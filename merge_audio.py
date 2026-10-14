"""Make one local playback WAV; source tracks remain intact for transcription."""
from __future__ import annotations

from contextlib import ExitStack
import hashlib
import json
import math
import os
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy.signal import resample_poly


SAMPLE_RATE = 48000
CHANNELS = 2
VERSION = 1


def _fingerprint(session: Path, metadata: dict) -> str:
    files = []
    for source in ("mic", "system"):
        path = session / f"{source}.wav"
        if path.is_file():
            info = path.stat()
            files.append([source, info.st_size, info.st_mtime_ns])
    value = {"version": VERSION, "files": files,
             "duration_seconds": metadata.get("duration_seconds", 0),
             "capture_offsets_ms": metadata.get("capture_offsets_ms", {})}
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


def _read_block(audio: sf.SoundFile, offset: int, start: int, count: int) -> np.ndarray:
    """Resample a bounded window with FIR context and a globally aligned phase."""
    output = np.zeros((count, CHANNELS), dtype=np.float32)
    track_length = (audio.frames * SAMPLE_RATE + audio.samplerate - 1) // audio.samplerate
    first = max(start, offset)
    last = min(start + count, offset + track_length)
    if first >= last:
        return output
    local_first, local_last = first - offset, last - offset
    divisor = math.gcd(audio.samplerate, SAMPLE_RATE)
    up, down = SAMPLE_RATE // divisor, audio.samplerate // divisor
    # scipy's default polyphase FIR has a half-length of 10*max(up, down).
    # Retain more context than that, and start on a complete rational period.
    context = math.ceil(16 * max(up, down) / up) + 2
    input_first = max(0, local_first * down // up - context)
    input_first -= input_first % down
    input_last = min(audio.frames, math.ceil(local_last * down / up) + context)
    audio.seek(input_first)
    samples = audio.read(input_last - input_first, dtype="float32", always_2d=True)
    if not np.isfinite(samples).all():
        raise ValueError(f"录音含无效音频样本：{Path(audio.name).name}")
    if samples.shape[1] == 1:
        samples = np.repeat(samples, CHANNELS, axis=1)
    elif samples.shape[1] > CHANNELS:
        # Multi-channel capture is unusual here; keep a centered downmix.
        samples = np.repeat(samples.mean(axis=1, keepdims=True), CHANNELS, axis=1)
    if up != down:
        samples = resample_poly(samples, up, down, axis=0)
    origin = input_first * up // down
    selected = samples[local_first - origin:local_last - origin]
    if len(selected) != last - first:
        raise ValueError(f"录音读取不完整：{Path(audio.name).name}")
    output[first - start:last - start] = selected
    return output


def merge_session(session: Path, metadata: dict, block_frames: int = SAMPLE_RATE * 2) -> dict:
    """Two bounded passes: peak scan, then write with one safe global gain.

    Silent sources add zeros, not a fixed 50% reduction. Stereo media is retained;
    mono microphones are centered. A single gain prevents block-wise pumping.
    Publish the WAV atomically and reuse it only with a complete matching manifest.
    """
    session = Path(session)
    if block_frames <= 0:
        raise ValueError("block_frames must be positive")
    duration = float(metadata.get("duration_seconds", 0))
    if not math.isfinite(duration) or duration < 0:
        raise ValueError("duration_seconds must be non-negative and finite")
    fingerprint = _fingerprint(session, metadata)
    destination = session / "meeting.wav"
    manifest = session / "merged_audio.json"
    try:
        cached = json.loads(manifest.read_text(encoding="utf-8"))
        info = destination.stat()
        header = sf.info(destination)
        if (cached.get("complete") and cached.get("fingerprint") == fingerprint
                and cached.get("output_size") == info.st_size
                and cached.get("output_mtime_ns") == info.st_mtime_ns
                and header.frames == cached.get("frames")
                and header.samplerate == SAMPLE_RATE and header.channels == CHANNELS
                and header.subtype == "PCM_16"):
            return {**cached, "reused": True}
    except (OSError, ValueError, sf.LibsndfileError):
        pass

    temporary = destination.with_suffix(".wav.tmp")
    try:
        with ExitStack() as resources:
            tracks = []
            frames = int(round(duration * SAMPLE_RATE))
            for source in ("mic", "system"):
                path = session / f"{source}.wav"
                if not path.is_file():
                    continue
                offset_seconds = float(metadata.get("capture_offsets_ms", {}).get(source, 0)) / 1000
                if not math.isfinite(offset_seconds) or offset_seconds < 0:
                    raise ValueError("capture_offsets_ms must contain non-negative finite numbers")
                offset = int(round(offset_seconds * SAMPLE_RATE))
                audio = resources.enter_context(sf.SoundFile(path))
                tracks.append((audio, offset))
                frames = max(frames, offset + (audio.frames * SAMPLE_RATE + audio.samplerate - 1)
                             // audio.samplerate)
            if not tracks or frames <= 0:
                raise ValueError("没有可合并的录音，原始文件已保留")

            def blocks():
                for start in range(0, frames, block_frames):
                    count = min(block_frames, frames - start)
                    mixed = np.zeros((count, CHANNELS), dtype=np.float32)
                    for audio, offset in tracks:
                        mixed += _read_block(audio, offset, start, count)
                    yield mixed

            peak = max(float(np.max(np.abs(block))) for block in blocks())
            gain = min(1.0, 0.98 / peak) if peak > 0 else 1.0
            with sf.SoundFile(temporary, "w", samplerate=SAMPLE_RATE, channels=CHANNELS,
                              subtype="PCM_16", format="WAV") as output:
                for block in blocks():
                    output.write(block * gain)
            header = sf.info(temporary)
            if header.frames != frames:
                raise ValueError("合并录音写入不完整，原始分轨已保留")
            if _fingerprint(session, metadata) != fingerprint:
                raise ValueError("合并期间原始录音发生变化，请停止录音后重新处理")
            os.replace(temporary, destination)
            info = destination.stat()
            result = {"complete": True, "fingerprint": fingerprint, "path": destination.name,
                      "sample_rate": SAMPLE_RATE, "channels": CHANNELS, "subtype": "PCM_16",
                      "frames": frames, "duration_seconds": frames / SAMPLE_RATE,
                      "gain": gain, "mixed_peak_before_gain": peak,
                      "output_size": info.st_size, "output_mtime_ns": info.st_mtime_ns,
                      "reused": False}
            manifest_temporary = manifest.with_suffix(".json.tmp")
            manifest_temporary.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
            os.replace(manifest_temporary, manifest)
            return result
    finally:
        if temporary.exists():
            temporary.unlink()
