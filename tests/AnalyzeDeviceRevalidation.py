"""Read-only device evidence; never starts capture, playback or cloud inference."""
import argparse
import csv
import hashlib
import json
import sys
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy.signal import correlate, resample_poly

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
from audio_sources import open_source


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as reader:
        for block in iter(lambda: reader.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def match(samples, sample_rate, template, at):
    rate = 16000
    audio = resample_poly(samples, rate, sample_rate)
    first = max(0, round((at - 1) * rate))
    last = min(len(audio), round((at + 4) * rate) + len(template))
    region = audio[first:last].astype(np.float64)
    if len(region) < len(template) or np.max(np.abs(region), initial=0) < 1e-5:
        return {"matched": False, "reason": "silent_or_short"}
    energy = np.r_[0, np.cumsum(region * region)]
    energy = energy[len(template):] - energy[:-len(template)]
    scores = correlate(region, template, mode="valid", method="fft")
    scores /= np.sqrt(np.maximum(energy * np.dot(template, template), 1e-20))
    scores[energy < energy.max(initial=0) * .01] = 0
    best = int(np.argmax(np.abs(scores)))
    score = float(scores[best])
    return {"matched": abs(score) >= .80, "correlation": score,
            "capture_start_seconds": (first + best) / rate,
            "request_to_signal_ms": ((first + best) / rate - at) * 1000}


def packet_summary(path, rate):
    count = estimates = position_resets = backwards_resolved = 0
    gaps, previous = [], None
    with path.open(encoding="utf-8-sig", newline="") as reader:
        for row in csv.DictReader(reader, delimiter="\t"):
            current = {key: float(value) for key, value in row.items()}
            count += 1
            estimates += int(current["estimated"])
            if previous:
                position_resets += current["raw_position"] < previous["raw_position"]
                backwards_resolved += current["resolved_start_ms"] < previous["resolved_start_ms"]
                frame_gap = current["raw_position"] - previous["raw_position"] - previous["frames"]
                if frame_gap > 0:
                    gaps.append({"seq": int(current["seq"]), "frames": int(frame_gap),
                                 "milliseconds": frame_gap * 1000 / rate,
                                 "arrival_ms": current["arrival_ms"],
                                 "flags": int(current["flags"])})
            previous = current
    return {"packets": count, "estimated_packets": estimates, "raw_position_resets": position_resets,
            "resolved_start_backwards": backwards_resolved, "device_position_gaps": gaps,
            "interpretation": "Position gaps/flags are device evidence, not proof of audible speech loss."}


def analyze(runtime):
    runtime = runtime.resolve(strict=True)
    review = json.loads((runtime / "device-review.json").read_text(encoding="utf-8-sig"))
    session = Path(review["session_dir"]).resolve(strict=True)
    if session.parent != runtime / "work":
        raise ValueError("Only this isolated runtime's completed session is allowed")
    metadata = json.loads((session / "session.json").read_text(encoding="utf-8-sig"))
    if metadata["state"] == "recording":
        raise ValueError("Do not analyze an active recording")
    reference, rate = sf.read(runtime / "test-input.wav", dtype="float64", always_2d=True)
    template = resample_poly(reference.mean(axis=1), 16000, rate)
    plays = [event for event in review["events"] if event["action"] == "play_requested"]
    report = {"scope": "actual local devices; no cloud; not a long-meeting or speaker-accuracy acceptance",
              "source_commit": json.loads((runtime.parent / "preparation.json").read_text(encoding="utf-8-sig"))["source_commit"],
              "capture_state": metadata["state"], "capture_error": metadata.get("capture_error", ""),
              "stop_reason": review["reason"], "harness_errors": review["errors"],
              "human_heard_playback": review.get("human_heard_playback"),
              "human_completed_speech": review.get("human_completed_speech"),
              "human_completed_device_switches": review.get("human_completed_device_switches"),
              "sources": [], "cross_source_relative_delays": [], "raw_sha256": {}}
    for entry in metadata["sources"]:
        parts = entry.get("segments", [])
        end = 0
        for part in parts:
            if part["start_frame"] != end:
                raise ValueError("Non-contiguous lossless manifest: " + entry["source"])
            path = session / part["path"]
            if path.resolve().parent != session:
                raise ValueError("Source outside this isolated session")
            info = sf.info(path)
            if info.frames != part["frames"] or info.samplerate != entry["sample_rate"]:
                raise ValueError("WAV/manifest mismatch: " + part["path"])
            end += info.frames
            report["raw_sha256"][part["path"]] = digest(path)
        with open_source(session, entry) as audio:
            samples = audio.read(dtype="float32", always_2d=True)
            source_rate = audio.samplerate
        mono = samples.mean(axis=1)
        row = {"source": entry["source"], "kind": entry["kind"], "frames": len(samples),
               "sample_rate": source_rate, "segments": len(parts),
               "opened_at_seconds": entry.get("opened_at_seconds"),
               "closed_at_seconds": entry.get("closed_at_seconds"), "end_reason": entry.get("end_reason"),
               "error": entry.get("error"), "diagnostics": entry.get("diagnostics", []),
               "peak": float(np.max(np.abs(samples), initial=0)), "play_matches": []}
        for event in plays:
            row["play_matches"].append({"target_seconds": event["details"]["target_seconds"],
                                        **match(mono, source_rate, template, event["at_seconds"])})
        if entry["kind"] == "microphone":
            row["mute_interiors"] = []
            for interval in metadata.get("microphone_pauses", []):
                first = round((interval["start_seconds"] + .15) * source_rate)
                last = round((interval["end_seconds"] - .15) * source_rate)
                inside = samples[first:last]
                row["mute_interiors"].append({"start_frame": first, "end_frame": last,
                    "frames_checked": len(inside), "exact_zero": bool(len(inside) and np.all(inside == 0))})
        packet_file = session / (entry["path"] + ".packets.tsv")
        row["packet_trace"] = packet_summary(packet_file, source_rate) if packet_file.is_file() else {"missing": True}
        report["sources"].append(row)
    for mic in [row for row in report["sources"] if row["kind"] == "microphone"]:
        for system in [row for row in report["sources"] if row["kind"] == "loopback"]:
            for left, right in zip(mic["play_matches"], system["play_matches"]):
                if left["matched"] and right["matched"]:
                    report["cross_source_relative_delays"].append({"microphone": mic["source"], "system": system["source"],
                        "target_seconds": left["target_seconds"],
                        "mic_minus_system_ms": (left["capture_start_seconds"] - right["capture_start_seconds"]) * 1000,
                        "interpretation": "Includes acoustic propagation and device latency; not absolute software error."})
    report["device_snapshots"] = [event for event in review["events"] if event["action"] == "default_devices"]
    report["cross_source_real_alignment"] = "measured_with_acoustic_delay" if report["cross_source_relative_delays"] else "not_measured_no_shared_confident_marker"
    report["overall_acceptance"] = "pending_manual_review_and_pipeline_checks"
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runtime", type=Path)
    args = parser.parse_args()
    result = analyze(args.runtime)
    destination = args.runtime.parent / "device-analysis.json"
    destination.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(destination), "capture_state": result["capture_state"],
                      "sources": len(result["sources"]), "real_alignment": result["cross_source_real_alignment"],
                      "overall_acceptance": result["overall_acceptance"]}, ensure_ascii=False))
