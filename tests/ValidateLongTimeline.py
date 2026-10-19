"""Offline duration stress test. No devices, playback, ASR or personal files.

Run explicitly with --output pointing at a new directory. This checks the actual
segmented reader, bounded mixer and MP3 encoder, not wall-clock capture reliability.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
import time

import numpy as np
import soundfile as sf
from scipy.signal import correlate, resample_poly

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from merge_audio import merge_session
from mp3_audio import encode_mp3

RATE = 16000


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def run_case(home, minutes):
    home.mkdir()
    seconds = minutes * 60
    cut = seconds // 2
    source_specs = [("mic", 0, seconds), ("system", 0, cut),
                    ("system_02", cut + .25, seconds - cut - .25)]
    # A unique band-limited pulse straddles each mic segment boundary. Other
    # pulses are time-separated so correlation cannot accidentally match a peer.
    rng = np.random.default_rng(20261019)
    pulse = rng.normal(0, .12, 2048).astype(np.float32)
    pulse *= np.hanning(len(pulse)).astype(np.float32)
    markers = []
    for source, offset, duration in source_specs:
        local_positions = [1., duration - 2.]
        local_positions += [t - .032 if source == "mic" else t + .5
                            for t in range(300, int(duration) - 1, 300)]
        for local in local_positions:
            if source != "mic":
                local += .5  # keep first/end pulses away from microphone ones
            markers.append({"source": source, "local_frame": round(local * RATE),
                            "expected_frame": round((offset + local) * 48000)})
    # These are input simulations; they do not exercise the UI or device driver.
    quiet_windows = [(60, 65), (120, 125), (seconds - 10, seconds - 5)]
    metadata = {"duration_seconds": seconds, "capture_offsets_ms": {}, "sources": []}
    originals = {}
    for source, offset, duration in source_specs:
        frames = round(duration * RATE)
        entry = {"source": source, "path": source + ".wav",
                 "kind": "microphone" if source == "mic" else "loopback",
                 "sample_rate": RATE, "channels": 1, "bits_per_sample": 16, "segments": []}
        metadata["capture_offsets_ms"][source] = offset * 1000
        for index, start in enumerate(range(0, frames, 300 * RATE), 1):
            count = min(300 * RATE, frames - start)
            data = np.zeros(count, dtype=np.float32)
            for marker in markers:
                if marker["source"] != source:
                    continue
                first = marker["local_frame"]
                lo, hi = max(start, first), min(start + count, first + len(pulse))
                if hi > lo:
                    data[lo - start:hi - start] = pulse[lo - first:hi - first]
            name = f"{source}.seg{index:06d}.wav"
            sf.write(home / name, data, RATE, subtype="PCM_16")
            entry["segments"].append({"path": name, "start_frame": start, "frames": count})
            originals[name] = digest(home / name)
        metadata["sources"].append(entry)
    (home / "session.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    started = time.perf_counter()
    merged = merge_session(home, metadata)
    assert merged["frames"] == seconds * 48000
    assert merged["gain"] == 1
    encoded = encode_mp3(home / "meeting.wav", home / "meeting.mp3",
                         frames=merged["frames"], bitrate_kbps=192)
    # Match against the quantized input actually written, independently of the
    # production reader. Streaming decode validation is performed by encode_mp3.
    quantized = np.floor(pulse * 32768) / 32768
    template = resample_poly(quantized, 3, 1)
    positions = []
    with sf.SoundFile(home / "meeting.mp3") as audio:
        for marker in markers:
            first = marker["expected_frame"] - 4800
            audio.seek(first)
            samples = audio.read(len(template) + 9600, dtype="float32", always_2d=True).mean(axis=1)
            actual = first + int(np.argmax(correlate(samples, template, mode="valid", method="fft")))
            delta = (actual - marker["expected_frame"]) / 48
            assert abs(delta) <= 1, (marker, delta)
            positions.append({**marker, "decoded_frame": actual, "error_ms": delta})
        for lo, hi in quiet_windows:
            audio.seek(round((lo + .1) * 48000))
            quiet = audio.read(round((hi - lo - .2) * 48000), dtype="float32")
            assert np.max(np.abs(quiet)) < 1e-5
    assert all(digest(home / name) == value for name, value in originals.items())
    result = {"passed": True, "synthetic_minutes": minutes,
              "elapsed_seconds": time.perf_counter() - started, "mp3": encoded,
              "source_segments": len(originals), "source_hashes_unchanged": True,
              "marker_positions": positions, "max_error_ms": max(abs(p["error_ms"]) for p in positions),
              "scope": "accelerated synthetic segmented merge and MP3; NOT live capture, device switching, ASR, publication or cleanup"}
    (home / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in result.items() if k not in ("marker_positions", "mp3")}), flush=True)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--minutes", nargs="+", type=int, default=[30, 60, 120])
    args = parser.parse_args()
    if args.output.exists() or len(set(args.minutes)) != len(args.minutes) or any(m <= 0 or m > 120 for m in args.minutes):
        parser.error("Use a new output directory and unique durations between 1 and 120 minutes.")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # Retain evidence and sources; never clean up a user-supplied directory.
    required = sum(args.minutes) * 60 * 300000 + 512 * 1024 * 1024
    if shutil.disk_usage(args.output.parent).free < required:
        parser.error("Insufficient space for retained synthetic WAV and MP3 evidence.")
    args.output.mkdir()
    try:
        cases = [run_case(args.output / f"{m:03d}min", m) for m in args.minutes]
        result = {"passed": True, "live_devices_tested": False, "cases": cases}
    except Exception as error:
        (args.output / "result.json").write_text(json.dumps({"passed": False, "error": str(error)}), encoding="utf-8")
        raise
    (args.output / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
