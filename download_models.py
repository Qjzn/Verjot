"""Download official public model artifacts; this never reads meeting audio."""
import argparse
from pathlib import Path
import hashlib
import json
import tarfile
import time
import requests

ROOT = Path(__file__).resolve().parent / "models"
ROOT.mkdir(exist_ok=True)
STATUS = ROOT / "download-status.json"
HTTP = requests.Session()


def report(**data):
    data["updated_at"] = time.time()
    temp = STATUS.with_suffix(".tmp")
    temp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(STATUS)


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def download(url, destination, expected_size=None, expected_hash=None):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.is_file() and (expected_size is None or destination.stat().st_size == expected_size):
        if not expected_hash or sha256(destination) == expected_hash:
            return
    partial = destination.with_suffix(destination.suffix + ".partial")
    for attempt in range(5):
        received = partial.stat().st_size if partial.is_file() else 0
        try:
            if expected_size is not None and partial.is_file() and received >= expected_size:
                if received == expected_size and (not expected_hash or sha256(partial) == expected_hash):
                    partial.replace(destination)
                    return
                # Only reset this downloader's own oversized or invalid partial.
                partial.unlink()
                received = 0
            headers = {"Range": f"bytes={received}-"} if received else {}
            with HTTP.get(url, headers=headers, stream=True, timeout=(30, 60)) as response:
                response.raise_for_status()
                if response.status_code != 206:
                    received = 0
                total = expected_size or (int(response.headers.get("Content-Length", 0)) + received)
                mode = "ab" if received else "wb"
                began, previous, initial = time.monotonic(), 0, received
                with partial.open(mode) as stream:
                    for block in response.iter_content(1024 * 1024):
                        stream.write(block)
                        received += len(block)
                        now = time.monotonic()
                        if now - previous >= 2:
                            report(state="downloading", file=destination.name, received=received,
                                   total=total, bytes_per_second=(received-initial)/max(now-began,.01))
                            previous = now
            if expected_size and received != expected_size:
                raise IOError(f"Incomplete file: {destination.name} {received}/{expected_size}")
            if expected_hash and sha256(partial) != expected_hash:
                partial.unlink()
                raise IOError(f"SHA256 mismatch: {destination.name}")
            partial.replace(destination)
            return
        except Exception:
            if attempt == 4:
                raise
            time.sleep(2 * (attempt + 1))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--speaker-only", action="store_true",
                        help="Download only local speaker models for API transcription mode")
    args = parser.parse_args(argv)
    repo = "Qwen/Qwen3-ASR-1.7B-hf"
    revision = None
    manifest = []
    if not args.speaker_only:
        response = HTTP.get(f"https://huggingface.co/api/models/{repo}?blobs=true", timeout=30)
        response.raise_for_status()
        metadata = response.json()
        revision = metadata["sha"]
        for item in metadata["siblings"]:
            name = item["rfilename"]
            if name.endswith((".json", ".jinja", ".safetensors")):
                lfs = item.get("lfs", {})
                destination = ROOT / "Qwen3-ASR-1.7B-hf" / name
                expected_size = item.get("size") or lfs.get("size")
                expected_hash = lfs.get("sha256")
                download(f"https://huggingface.co/{repo}/resolve/{revision}/{name}", destination,
                         expected_size, expected_hash)
                manifest.append({"file": name, "size": destination.stat().st_size,
                                 "sha256": sha256(destination)})
    archive = ROOT / "segmentation.tar.bz2"
    download("https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/"
             "sherpa-onnx-pyannote-segmentation-3-0.tar.bz2", archive, 6958444)
    with tarfile.open(archive) as package:
        for member in package.getmembers():
            (ROOT / member.name).resolve().relative_to(ROOT.resolve())
        package.extractall(ROOT, filter="data")
    speaker = ROOT / "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx"
    download("https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/"
             + speaker.name, speaker, 39593761)
    if args.speaker_only:
        (ROOT / "speaker-model-manifest.json").write_text(json.dumps({
                 "speaker_sha256": sha256(speaker), "segmentation_sha256": sha256(archive)},
                 indent=2), encoding="utf-8")
        report(state="completed", message="官方说话人模型下载完成并验证（API转写模式）")
    else:
        (ROOT / "model-manifest.json").write_text(json.dumps({"repo": repo, "revision": revision,
                 "files": manifest, "speaker_sha256": sha256(speaker)}, indent=2), encoding="utf-8")
        report(state="completed", message="官方中文识别和说话人模型下载完成并验证", revision=revision)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        report(state="error", error=str(error))
        raise
