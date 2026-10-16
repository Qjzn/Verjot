"""Offline, one-job Qwen3-ASR worker for prepared meeting segments.

Requires the official Qwen/Qwen3-ASR-1.7B-hf snapshot, Transformers >=5.13,
CUDA-enabled PyTorch, accelerate, numpy, soundfile and jinja2 >=3.1. No qwen-asr, vLLM,
FlashAttention or forced aligner is used. Audio must be local 16 kHz mono,
with each segment at most 25 seconds. Speaker labels come from preparation;
this worker transcribes them and does not perform diarization.

Example: python asr_engine.py --segments prepared.json --model-dir models/asr
Both result.json and status.json are atomically updated beside prepared.json.
Re-running the same command resumes successful segments from result.json.
"""

from __future__ import annotations

import argparse
import gc
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import tempfile
import time
from datetime import datetime, timezone


def atomic_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", dir=path.parent, suffix=".tmp", delete=False
        ) as stream:
            temporary = Path(stream.name)
            json.dump(value, stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        for attempt in range(8):
            try:
                os.replace(temporary, path)
                break
            except PermissionError:
                if attempt == 7:
                    raise
                time.sleep(0.05 * (attempt + 1))
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def read_segments(path: Path) -> list[dict]:
    with path.open(encoding="utf-8-sig") as stream:
        document = json.load(stream)
    rows = document.get("segments") if isinstance(document, dict) else document
    if not isinstance(rows, list):
        raise ValueError("Prepared JSON must be an array or an object with segments[]")
    segments = []
    for index, row in enumerate(rows):
        if not isinstance(row, dict):
            raise ValueError(f"Segment {index} must be an object")
        start, end = row.get("start"), row.get("end")
        if any(isinstance(x, bool) or not isinstance(x, (int, float)) for x in (start, end)):
            raise ValueError(f"Segment {index}: start/end must be numbers")
        if not all(math.isfinite(x) for x in (start, end)) or not 0 <= start < end:
            raise ValueError(f"Segment {index}: invalid time range")
        if end - start > 25.001:
            raise ValueError(f"Segment {index}: split audio into at most 25 seconds")
        audio = row.get("text_audio")
        if not isinstance(audio, str) or not audio.strip() or "://" in audio or audio.startswith("data:"):
            raise ValueError(f"Segment {index}: text_audio must be a local file path")
        audio_path = Path(audio)
        if not audio_path.is_absolute():
            audio_path = path.parent / audio_path
        audio_path = audio_path.resolve(strict=True)
        if not audio_path.is_file():
            raise ValueError(f"Segment {index}: text_audio is not a file")
        speaker = row.get("speaker", "Unknown")
        if not isinstance(speaker, (str, int)) or isinstance(speaker, bool):
            raise ValueError(f"Segment {index}: speaker must be a string or integer label")
        segments.append({"index": index, "start": start, "end": end,
                         "speaker": speaker, "text_audio": str(audio_path)})
    return segments


def job_fingerprint(segments: list[dict], model_dir: Path, context: str, max_tokens: int) -> str:
    files = []
    for row in segments:
        stat = Path(row["text_audio"]).stat()
        files.append((stat.st_size, stat.st_mtime_ns))
    key = [segments, files, str(model_dir), "Chinese", context, max_tokens, "hf-native-v1"]
    return hashlib.sha256(json.dumps(key, ensure_ascii=False, sort_keys=True).encode()).hexdigest()


class QwenBackend:
    def __init__(self, model_dir: Path, device: str, context: str, max_tokens: int):
        # Set before importing Hugging Face, including its processor audio helpers.
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
        import torch
        from transformers import AutoModelForMultimodalLM, AutoProcessor

        self.torch = torch
        self.device = device
        self.context = context
        self.max_tokens = max_tokens
        self.model = None
        self.processor = None
        try:
            self.processor = AutoProcessor.from_pretrained(str(model_dir), local_files_only=True)
            self.model = AutoModelForMultimodalLM.from_pretrained(
                str(model_dir), local_files_only=True, trust_remote_code=False,
                dtype=torch.bfloat16 if device == "cuda" else torch.float32,
                device_map={"": "cuda:0" if device == "cuda" else "cpu"},
                attn_implementation="sdpa", low_cpu_mem_usage=True,
            )
            self.model.eval()
        except BaseException:
            self.close()
            raise

    def transcribe(self, row: dict) -> str:
        import soundfile as sf

        info = sf.info(row["text_audio"])
        if info.samplerate != 16000 or info.channels != 1 or not 0 < info.duration <= 25.001:
            raise ValueError("Prepared audio must be 16 kHz mono and at most 25 seconds")
        audio, _ = sf.read(row["text_audio"], dtype="float32", always_2d=False)
        # Arrays avoid URL handling and external media decoders entirely.
        with self.torch.inference_mode():
            inputs = self.processor.apply_transcription_request(
                audio=audio, language="Chinese", prompt=self.context or None,
                sampling_rate=16000,
            ).to(self.model.device, self.model.dtype)
            outputs = self.model.generate(**inputs, max_new_tokens=self.max_tokens, do_sample=False)
            sequences = getattr(outputs, "sequences", outputs)
            generated = sequences[0, inputs["input_ids"].shape[1]:]
            eos = self.model.generation_config.eos_token_id
            eos = eos if isinstance(eos, list) else [eos]
            if len(generated) >= self.max_tokens and int(generated[-1]) not in eos:
                raise RuntimeError("ASR reached its token limit; shorten the prepared segment")
            text = self.processor.decode(generated, return_format="transcription_only")
        if not isinstance(text, str):
            raise RuntimeError("Unexpected Qwen3-ASR transcription response")
        return text.strip()

    def close(self) -> None:
        self.model = None
        self.processor = None
        gc.collect()
        if self.torch.cuda.is_available():
            self.torch.cuda.empty_cache()


def is_cuda_memory_error(error: Exception) -> bool:
    message = str(error).lower()
    return isinstance(error, MemoryError) or any(
        phrase in message for phrase in ("cuda out of memory", "cuda error: out of memory", "cublas_status_alloc_failed")
    )


def cuda_available() -> bool:
    import torch
    return torch.cuda.is_available()


def release_cuda() -> None:
    gc.collect()
    torch = sys.modules.get("torch")
    if torch is not None and torch.cuda.is_available():
        torch.cuda.empty_cache()


def discard_error_frames(error: BaseException) -> None:
    # A failed HF constructor's traceback can retain partially loaded CUDA tensors.
    error.__traceback__ = None
    error.__cause__ = None
    error.__context__ = None


def run_job(args, backend_factory=QwenBackend, gpu_available=cuda_available) -> int:
    source = Path(args.segments).resolve()
    output = Path(args.output).resolve() if args.output else source.parent / "result.json"
    status_path = Path(args.status).resolve() if args.status else source.parent / "status.json"
    model_dir = Path(args.model_dir).resolve()
    if len({source, output, status_path}) != 3:
        raise ValueError("Prepared JSON, result JSON and status JSON must have distinct paths")
    backend = None
    rows = []
    result = None
    device = "pending"
    fallback_reason = None
    current = None

    def status(state, message, **extra):
        done = sum(row.get("status") == "done" for row in rows)
        value = {"state": state, "stage": "asr", "message": message, "pid": os.getpid(),
                 "completed": done, "total": len(rows), "progress": done / len(rows) if rows else 0,
                 "current_segment": current, "device": device, "fallback_reason": fallback_reason,
                 "updated_at": datetime.now(timezone.utc).isoformat()}
        value.update(extra)
        atomic_json(status_path, value)

    def checkpoint(state):
        if result is not None:
            result.update(state=state, complete=state == "completed", segments=rows)
            atomic_json(output, result)

    try:
        status("validating", "正在检查本地录音分段")
        if not model_dir.is_dir():
            raise ValueError("model-dir must be an existing local model directory")
        segments = read_segments(source)
        fingerprint = job_fingerprint(segments, model_dir, args.context, args.max_new_tokens)
        rows = [dict(row, status="pending") for row in segments]
        if output.exists():
            with output.open(encoding="utf-8-sig") as stream:
                saved = json.load(stream)
            if saved.get("fingerprint") != fingerprint:
                raise ValueError("Existing result belongs to different input/model/settings; use a new --output")
            cached = saved.get("segments", [])
            for index, row in enumerate(rows):
                if index < len(cached) and cached[index].get("status") == "done":
                    old = cached[index]
                    if any(old.get(key) != row[key] for key in segments[index]) or not isinstance(old.get("text"), str):
                        raise ValueError("Checkpoint segment does not match prepared input")
                    rows[index] = old
        result = {"schema_version": 1, "engine": "Qwen3-ASR-1.7B-hf", "model_dir": str(model_dir),
                  "fingerprint": fingerprint, "language": "Chinese", "segments": rows}
        checkpoint("processing")
        if all(row.get("status") == "done" for row in rows):
            checkpoint("completed")
            status("completed", "转写完成", progress=1)
            return 0
        device = "cpu" if args.device == "cpu" else "cuda"
        if device == "cuda" and not gpu_available():
            if args.no_cpu_fallback:
                raise RuntimeError("CUDA is unavailable and CPU fallback is disabled")
            device, fallback_reason = "cpu", "CUDA不可用，使用CPU"
        status("loading", "正在加载中文识别模型" + ("（CPU）" if device == "cpu" else "（GPU）"))
        try:
            backend = backend_factory(model_dir, device, args.context, args.max_new_tokens)
        except Exception as error:
            if device != "cuda" or args.no_cpu_fallback or not is_cuda_memory_error(error):
                raise
            fallback_reason = str(error)
            discard_error_frames(error)
            release_cuda()
            device = "cpu"
            status("loading", "显存不足，正在重新加载CPU模型")
            backend = backend_factory(model_dir, device, args.context, args.max_new_tokens)
        for current, row in enumerate(rows):
            if row.get("status") == "done":
                continue
            row.pop("error", None)
            status("transcribing", f"正在转写第 {current + 1}/{len(rows)} 段（{device.upper()}）")
            try:
                text = backend.transcribe(row)
            except Exception as error:
                if device != "cuda" or args.no_cpu_fallback or not is_cuda_memory_error(error):
                    raise
                fallback_reason = str(error)
                discard_error_frames(error)
                backend.close()
                backend = None
                release_cuda()
                device = "cpu"
                status("loading", "显存不足，正在切换CPU后继续当前段")
                backend = backend_factory(model_dir, device, args.context, args.max_new_tokens)
                status("transcribing", f"正在转写第 {current + 1}/{len(rows)} 段（CPU）")
                text = backend.transcribe(row)
            row.update(status="done", text=text, language="Chinese", device=device)
            checkpoint("processing")
            status("transcribing", f"已完成 {current + 1}/{len(rows)} 段")
        checkpoint("completed")
        status("completed", "中文转写完成", progress=1)
        return 0
    except KeyboardInterrupt:
        checkpoint("cancelled")
        status("cancelled", "转写已停止，已完成分段可继续")
        return 130
    except Exception as error:
        message = str(error)
        discard_error_frames(error)
        if current is not None and rows[current].get("status") != "done":
            rows[current].update(status="error", error=message)
        checkpoint("error")
        status("error", "转写失败，已完成分段已保留", error=message)
        print(f"ASR worker: {message}", file=sys.stderr, flush=True)
        return 1
    finally:
        try:
            if backend is not None:
                backend.close()
        finally:
            release_cuda()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--segments", required=True)
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--output")
    parser.add_argument("--status")
    parser.add_argument("--device", choices=("auto", "cuda", "cpu"), default="auto")
    parser.add_argument("--no-cpu-fallback", action="store_true")
    parser.add_argument("--context", default="", help="Optional terminology context, not transcript history")
    parser.add_argument("--max-new-tokens", type=int, default=512)
    args = parser.parse_args()
    if args.max_new_tokens <= 0:
        parser.error("--max-new-tokens must be positive")
    return run_job(args)


if __name__ == "__main__":
    raise SystemExit(main())
