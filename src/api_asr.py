"""Optional OpenAI-compatible transcription of locally prepared speaker segments.

Only this explicitly selected API worker uploads audio. It uses the same atomic
result/status files and successful-segment resume contract as the local worker.
The API key is read from a process environment variable and is never persisted.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import re
import socket
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid
import wave

from asr_engine import atomic_json, discard_error_frames, read_segments


class ApiAsrError(Exception):
    """A safe public message containing no provider response or credentials."""


def validate_settings(args):
    base_url = args.api_base_url
    if (not isinstance(base_url, str) or not base_url.strip()
            or any(character.isspace() for character in base_url)):
        raise ApiAsrError("api-base-url must be an HTTP(S) provider prefix")
    try:
        parsed = urllib.parse.urlsplit(base_url)
        valid = (parsed.scheme in ("http", "https") and parsed.hostname
                 and parsed.username is None and parsed.password is None
                 and not parsed.query and not parsed.fragment)
        parsed.port  # Validate an explicitly configured port without logging it.
    except ValueError:
        valid = False
    if not valid:
        raise ApiAsrError("api-base-url must be HTTP(S), without credentials, query or fragment")
    model = args.api_model
    if not isinstance(model, str) or not model.strip():
        raise ApiAsrError("api-model must be a non-empty string")
    key_env = args.api_key_env
    if not isinstance(key_env, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key_env):
        raise ApiAsrError("api-key-env must name a process environment variable")
    key = os.environ.get(key_env, "")
    if not key or not key.isascii() or any(not 33 <= ord(character) <= 126 for character in key):
        raise ApiAsrError("The configured API key environment variable is missing or invalid")
    timeout = args.api_timeout_seconds
    if type(timeout) is not int or not 10 <= timeout <= 600:
        raise ApiAsrError("api-timeout-seconds must be an integer from 10 to 600")
    if not isinstance(args.context, str):
        raise ApiAsrError("context must be a string")
    return base_url.rstrip("/"), model.strip(), key, timeout


def job_fingerprint(segments: list[dict], base_url: str, model: str, context: str) -> str:
    files = []
    for row in segments:
        info = Path(row["text_audio"]).stat()
        files.append((info.st_size, info.st_mtime_ns))
    # Credentials do not determine transcription identity and must not enter this key.
    identity = [segments, files, base_url, model, "zh", context, "api-transcriptions-v1"]
    return hashlib.sha256(json.dumps(identity, ensure_ascii=False, sort_keys=True).encode()).hexdigest()


class NoRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        # Never forward a recording or Bearer token to a redirect target.
        return None


class ApiBackend:
    def __init__(self, base_url: str, model: str, key: str, context: str, timeout: int):
        self.endpoint = base_url + "/audio/transcriptions"
        self.model, self.key, self.context, self.timeout = model, key, context, timeout
        self.opener = urllib.request.build_opener(NoRedirects())

    def transcribe(self, row: dict) -> str:
        try:
            audio = Path(row["text_audio"]).read_bytes()
            with wave.open(io.BytesIO(audio), "rb") as stream:
                valid = (stream.getframerate() == 16000 and stream.getnchannels() == 1
                         and stream.getcomptype() == "NONE" and 0 < stream.getnframes() <= 25 * 16000)
                if valid:
                    frames = stream.readframes(stream.getnframes())
                    valid = len(frames) == stream.getnframes() * stream.getsampwidth()
        except (OSError, EOFError, wave.Error):
            valid = False
        if not valid:
            raise ApiAsrError("Prepared audio must be a valid 16 kHz mono WAV of at most 25 seconds")

        boundary = "tingji-" + uuid.uuid4().hex
        fields = {"model": self.model, "language": "zh", "response_format": "json"}
        if self.context:
            fields["prompt"] = self.context
        parts = []
        for name, value in fields.items():
            parts.append((f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n"
                          "\r\n").encode() + value.encode("utf-8") + b"\r\n")
        parts.append((f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; "
                      f"filename=\"segment-{row['index']:05d}.wav\"\r\n"
                      "Content-Type: audio/wav\r\n\r\n").encode() + audio + b"\r\n")
        parts.append(f"--{boundary}--\r\n".encode())
        request = urllib.request.Request(self.endpoint, data=b"".join(parts), method="POST",
                                        headers={"Authorization": "Bearer " + self.key,
                                                 "Content-Type": "multipart/form-data; boundary=" + boundary,
                                                 "Accept": "application/json"})
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                payload = response.read(1024 * 1024 + 1)
        except urllib.error.HTTPError as error:
            code = int(error.code)
            error.close()
            raise ApiAsrError(f"API transcription request failed (HTTP {code})") from None
        except (TimeoutError, socket.timeout):
            raise ApiAsrError("API transcription request timed out") from None
        except (urllib.error.URLError, OSError, ValueError):
            # URL errors can echo URL credentials; never emit their reason or repr.
            raise ApiAsrError("API transcription request could not be completed") from None
        if len(payload) > 1024 * 1024:
            raise ApiAsrError("API transcription response exceeds the size limit")
        try:
            document = json.loads(payload)
        except (ValueError, UnicodeError):
            raise ApiAsrError("API transcription response must be JSON with a text string") from None
        if not isinstance(document, dict) or not isinstance(document.get("text"), str):
            raise ApiAsrError("API transcription response must be JSON with a text string")
        if self.key in document["text"]:
            raise ApiAsrError("API transcription response contains credential material")
        return document["text"].strip()


def run_job(args, backend_factory=ApiBackend) -> int:
    source = Path(args.segments).resolve()
    output = Path(args.output).resolve() if args.output else source.parent / "result.json"
    status_path = Path(args.status).resolve() if args.status else source.parent / "status.json"
    if len({source, output, status_path}) != 3:
        raise ValueError("Prepared JSON, result JSON and status JSON must have distinct paths")
    rows, result, current = [], None, None

    def status(state, message, **extra):
        done = sum(row.get("status") == "done" for row in rows)
        value = {"state": state, "stage": "asr", "message": message, "pid": os.getpid(),
                 "completed": done, "total": len(rows), "progress": done / len(rows) if rows else 0,
                 "current_segment": current, "device": "api", "fallback_reason": None,
                 "updated_at": datetime.now(timezone.utc).isoformat()}
        value.update(extra)
        atomic_json(status_path, value)

    def checkpoint(state):
        if result is not None:
            result.update(state=state, complete=state == "completed", segments=rows)
            atomic_json(output, result)

    try:
        status("validating", "正在检查API转写设置和本地录音分段")
        base_url, model, key, timeout = validate_settings(args)
        try:
            segments = read_segments(source)
            fingerprint = job_fingerprint(segments, base_url, model, args.context)
        except (OSError, ValueError):
            raise ApiAsrError("Prepared segments are missing or invalid") from None
        rows = [dict(row, status="pending") for row in segments]
        if output.exists():
            try:
                with output.open(encoding="utf-8-sig") as stream:
                    saved = json.load(stream)
                if not isinstance(saved, dict) or saved.get("fingerprint") != fingerprint:
                    raise ApiAsrError("Existing result belongs to different input/provider/model/settings; use a new output")
                cached = saved.get("segments", [])
                if not isinstance(cached, list):
                    raise ApiAsrError("Checkpoint segments are invalid")
                for index, row in enumerate(rows):
                    if index < len(cached) and isinstance(cached[index], dict) and cached[index].get("status") == "done":
                        old = cached[index]
                        if any(old.get(field) != row[field] for field in segments[index]) or not isinstance(old.get("text"), str):
                            raise ApiAsrError("Checkpoint segment does not match prepared input")
                        rows[index] = old
            except (OSError, ValueError):
                raise ApiAsrError("Existing transcription checkpoint could not be read") from None
        result = {"schema_version": 1, "engine": "OpenAI-compatible API", "api_base_url": base_url,
                  "model": model, "fingerprint": fingerprint, "language": "Chinese", "segments": rows}
        checkpoint("processing")
        if all(row.get("status") == "done" for row in rows):
            checkpoint("completed")
            status("completed", "中文转写完成", progress=1)
            return 0
        backend = backend_factory(base_url, model, key, args.context, timeout)
        for current, row in enumerate(rows):
            if row.get("status") == "done":
                continue
            row.pop("error", None)
            status("transcribing", f"正在通过API转写第 {current + 1}/{len(rows)} 段")
            text = backend.transcribe(row)
            if not isinstance(text, str):
                raise ApiAsrError("API transcription response must contain a text string")
            row.update(status="done", text=text, language="Chinese", device="api")
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
        message = str(error) if isinstance(error, ApiAsrError) else "API transcription failed; completed segments were retained"
        discard_error_frames(error)
        if current is not None and rows[current].get("status") != "done":
            rows[current].update(status="error", error=message)
        checkpoint("error")
        status("error", "API转写失败，已完成分段已保留", error=message)
        print(f"API ASR worker: {message}", file=sys.stderr, flush=True)
        return 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--segments", required=True)
    parser.add_argument("--api-base-url", required=True)
    parser.add_argument("--api-model", required=True)
    parser.add_argument("--api-key-env", default="TINGJI_API_KEY")
    parser.add_argument("--api-timeout-seconds", type=int, default=120)
    parser.add_argument("--context", default="")
    parser.add_argument("--output")
    parser.add_argument("--status")
    return run_job(parser.parse_args())


if __name__ == "__main__":
    raise SystemExit(main())
