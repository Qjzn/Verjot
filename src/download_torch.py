"""Download the official Windows/Python 3.12 CUDA 12.8 PyTorch wheel.

Uses eight concurrent 64 MiB HTTP ranges, resumes incomplete ranges, and
verifies the SHA256 published by the official PyTorch package index before
publishing a wheel. Only Python's standard library is needed.
"""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
import hashlib
import html
import json
import os
from pathlib import Path
import re
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

from asr_engine import atomic_json


INDEX = "https://download.pytorch.org/whl/cu128/torch/"
CHUNK_SIZE = 64 * 1024 * 1024
BUFFER_SIZE = 256 * 1024


def official_wheel(version: str | None = None) -> dict:
    with urllib.request.urlopen(INDEX, timeout=30) as response:
        document = response.read().decode("utf-8")
    candidates = []
    for raw in re.findall(r'href="([^"]+)"', document):
        link = urllib.parse.urljoin(INDEX, html.unescape(raw))
        parsed = urllib.parse.urlsplit(link)
        filename = urllib.parse.unquote(parsed.path.rsplit("/", 1)[-1])
        match = re.fullmatch(r"torch-(\d+\.\d+\.\d+)\+cu128-cp312-cp312-win_amd64\.whl", filename)
        digest = urllib.parse.parse_qs(parsed.fragment).get("sha256", [""])[0]
        if match and re.fullmatch(r"[0-9a-f]{64}", digest):
            if version is None or match.group(1) == version:
                if parsed.hostname not in ("download.pytorch.org", "download-r2.pytorch.org"):
                    raise ValueError("Unexpected host in official PyTorch wheel index")
                url = urllib.parse.urlunsplit(("https", "download.pytorch.org", parsed.path, parsed.query, ""))
                candidates.append((tuple(map(int, match.group(1).split("."))), filename, digest, url))
    if not candidates:
        raise ValueError("No matching CUDA Windows cp312 wheel with an official SHA256")
    _, filename, digest, url = max(candidates)
    request = urllib.request.Request(url, method="HEAD")
    with urllib.request.urlopen(request, timeout=30) as response:
        size = int(response.headers["Content-Length"])
    if size <= 0:
        raise ValueError("Official wheel size is invalid")
    return {"filename": filename, "sha256": digest, "url": url, "size": size}


def hash_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def download_part(number, wheel, parts_dir, progress, lock, cancel) -> None:
    first = number * CHUNK_SIZE
    last = min(first + CHUNK_SIZE, wheel["size"]) - 1
    length = last - first + 1
    path = parts_dir / f"part-{number:04d}.bin"
    for attempt in range(6):
        if cancel.is_set():
            return
        existing = path.stat().st_size if path.exists() else 0
        if existing > length:
            raise ValueError(f"Cached part {number} is larger than its expected range")
        with lock:
            progress[number] = existing
        if existing == length:
            return
        start = first + existing
        request = urllib.request.Request(wheel["url"], headers={
            "Range": f"bytes={start}-{last}", "Accept-Encoding": "identity",
            "User-Agent": "ObsidianMeetingRuntime/1.0",
        })
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                expected = f"bytes {start}-{last}/{wheel['size']}"
                if response.status != 206 or response.headers.get("Content-Range") != expected:
                    # Do not read a 200 response: it could be a full 2.75 GB wheel per worker.
                    raise ValueError(f"Server ignored range {number}: HTTP {response.status}, "
                                     f"Content-Range={response.headers.get('Content-Range')!r}")
                if response.headers.get("Content-Encoding", "identity") != "identity":
                    raise ValueError("Range response unexpectedly uses content encoding")
                with path.open("ab") as stream:
                    written = existing
                    while written < length:
                        if cancel.is_set():
                            return
                        block = response.read(min(BUFFER_SIZE, length - written))
                        if not block:
                            raise OSError(f"Part {number} ended before its expected range")
                        stream.write(block)
                        written += len(block)
                        with lock:
                            progress[number] = written
                    stream.flush()
                    os.fsync(stream.fileno())
            return
        except (urllib.error.URLError, TimeoutError, OSError):
            if attempt == 5:
                raise
            if cancel.wait(0.5 * (attempt + 1)):
                return


def run(args) -> int:
    output_dir = Path(args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    status_path = Path(args.status).resolve()
    started = time.monotonic()
    lock = threading.Lock()
    cancel = threading.Event()
    reporter_stop = threading.Event()
    progress = []
    wheel = None
    completed_path = None
    phase = "checking"
    baseline = 0

    def report(state="downloading", error=None):
        with lock:
            done = sum(progress)
        size = wheel["size"] if wheel else 0
        seconds = max(time.monotonic() - started, 0.001)
        value = {"state": state, "stage": "runtime", "phase": phase,
                 "pid": os.getpid(), "downloaded_bytes": done, "total_bytes": size,
                 "progress": done / size if size else 0,
                 "MiB_per_second": max(0, done - baseline) / 1048576 / seconds,
                 "workers": args.workers, "chunk_bytes": CHUNK_SIZE,
                 "source": wheel["url"] if wheel else INDEX,
                 "sha256": wheel["sha256"] if wheel else None,
                 "file_path": str(completed_path) if completed_path else None,
                 "updated_at": datetime.now(timezone.utc).isoformat()}
        if error:
            value["error"] = error
        atomic_json(status_path, value)

    def reporter():
        while not reporter_stop.wait(1):
            try:
                report()
            except OSError as error:
                print(f"Progress write deferred: {error}", flush=True)

    thread = None
    try:
        report()
        wheel = official_wheel(args.version)
        final_path = output_dir / wheel["filename"]
        count = (wheel["size"] + CHUNK_SIZE - 1) // CHUNK_SIZE
        progress[:] = [0] * count
        if final_path.is_file() and final_path.stat().st_size == wheel["size"]:
            phase = "verifying"
            report()
            if hash_file(final_path) == wheel["sha256"]:
                progress[:] = [min(CHUNK_SIZE, wheel["size"] - n * CHUNK_SIZE) for n in range(count)]
                completed_path = final_path
                phase = "completed"
                report("completed")
                return 0
        parts_dir = output_dir / ("." + wheel["filename"] + "." + wheel["sha256"][:12] + ".parts")
        parts_dir.mkdir(parents=True, exist_ok=True)
        for number in range(count):
            path = parts_dir / f"part-{number:04d}.bin"
            progress[number] = path.stat().st_size if path.exists() else 0
        baseline = sum(progress)
        started = time.monotonic()
        phase = "downloading"
        report()
        thread = threading.Thread(target=reporter, name="download-progress", daemon=True)
        thread.start()
        with ThreadPoolExecutor(max_workers=args.workers) as pool:
            futures = [pool.submit(download_part, n, wheel, parts_dir, progress, lock, cancel) for n in range(count)]
            try:
                for future in as_completed(futures):
                    future.result()
            except BaseException:
                cancel.set()
                raise
        phase = "assembling_and_verifying"
        report()
        assembling = output_dir / (wheel["filename"] + ".assembling")
        digest = hashlib.sha256()
        with assembling.open("wb") as destination:
            for number in range(count):
                with (parts_dir / f"part-{number:04d}.bin").open("rb") as source:
                    for block in iter(lambda: source.read(4 * 1024 * 1024), b""):
                        digest.update(block)
                        destination.write(block)
            destination.flush()
            os.fsync(destination.fileno())
        if assembling.stat().st_size != wheel["size"] or digest.hexdigest() != wheel["sha256"]:
            raise ValueError("Downloaded wheel failed the official SHA256 verification; not published")
        os.replace(assembling, final_path)
        completed_path = final_path
        phase = "completed"
        reporter_stop.set()
        if thread:
            thread.join()
        report("completed")
        print(json.dumps({"state": "completed", "file_path": str(final_path), "sha256": wheel["sha256"]}), flush=True)
        return 0
    except Exception as error:
        cancel.set()
        reporter_stop.set()
        if thread:
            thread.join()
        report("error", str(error))
        print(f"Torch wheel download failed: {error}", flush=True)
        return 1
    finally:
        reporter_stop.set()
        if thread:
            thread.join()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    root = Path(__file__).resolve().parent
    parser.add_argument("--version", help="Optional exact torch version; defaults to newest stable cu128 cp312")
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--output-dir", default=str(root / "runtime-wheels"))
    parser.add_argument("--status", default=str(root / "download-status-torch.json"))
    args = parser.parse_args()
    if not 1 <= args.workers <= 16:
        parser.error("workers must be between 1 and 16")
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
