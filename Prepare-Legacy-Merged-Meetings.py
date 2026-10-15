"""Prepare a single WAV for verified completed legacy meetings; never delete files."""
from __future__ import annotations

import argparse
from datetime import datetime
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import tempfile

import numpy as np
import soundfile as sf

import meeting_worker as worker
from merge_audio import _fingerprint as merge_fingerprint

HOME = Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location("legacy_compaction", HOME / "Compact-Completed-Meetings.py")
compaction = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(compaction)


def eligible_session(session: Path, config: dict, home: Path = HOME,
                     running: set[Path] | None = None) -> dict:
    vault = worker.safe_path(Path(config["vault_root"]))
    folder = compaction.guarded_path(vault / worker.meeting_folder(config), vault)
    attachment_root = compaction.guarded_path(folder / "附件", vault)
    session = compaction.guarded_path(session, attachment_root, required=True)
    if session.parent != attachment_root or not compaction.SESSION_NAME.fullmatch(session.name):
        raise ValueError("Not a direct timestamped legacy meeting directory")
    running = compaction.active_sessions(home, config) if running is None else running
    if session in running:
        raise ValueError("Meeting worker is still active")
    metadata = compaction.read_json_file(session / "session.json", session)
    status = compaction.read_json_file(session / "status.json", session)
    result = compaction.read_json_file(session / "result.json", session)
    if metadata.get("state") != "recorded" or metadata.get("capture_error"):
        raise ValueError("Legacy recording is not successfully finished")
    if metadata.get("storage_mode") == "minimal":
        raise ValueError("Meeting already uses the current minimal storage format")
    if status.get("state") != "completed" or status.get("merge_error") or status.get("error"):
        raise ValueError("Legacy transcription is not successfully completed")
    segments = result.get("segments")
    if result.get("complete") is not True or not isinstance(segments, list):
        raise ValueError("Legacy transcription result is incomplete")
    if any(not isinstance(row, dict) or row.get("status") != "done" for row in segments):
        raise ValueError("Legacy transcription has unfinished segments")
    duration = float(metadata.get("duration_seconds", -1))
    if not math.isfinite(duration) or duration <= 0:
        raise ValueError("Recording duration is invalid")
    sources = []
    paths = [session / name for name in ("session.json", "status.json", "result.json")]
    for source in ("mic", "system"):
        audio = compaction.guarded_path(session / f"{source}.wav", session, required=True)
        if not audio.is_file():
            raise ValueError(f"Legacy source recording is missing: {source}")
        info = sf.info(audio)
        if (info.frames <= 0 or not 8000 <= info.samplerate <= 192000 or info.channels not in (1, 2)
                or info.format not in {"WAV", "WAVEX", "RF64"}
                or info.subtype not in {"FLOAT", "DOUBLE", "PCM_16", "PCM_24", "PCM_32"}):
            raise ValueError(f"Legacy source WAV header is invalid: {source}")
        offset = float(metadata.get("capture_offsets_ms", {}).get(source, 0)) / 1000
        if (not math.isfinite(offset) or offset < 0
                or abs(info.duration + offset - duration) > max(0.05, 2 / info.samplerate)):
            raise ValueError(f"Legacy source duration does not match its meeting: {source}")
        with sf.SoundFile(audio) as stream:
            first = stream.read(1, always_2d=True)
            stream.seek(info.frames - 1)
            last = stream.read(1, always_2d=True)
            if len(first) != 1 or len(last) != 1 or not np.isfinite(first).all() or not np.isfinite(last).all():
                raise ValueError(f"Legacy source WAV endpoints are unreadable: {source}")
        sources.append({"path": str(audio), "frames": info.frames, "sample_rate": info.samplerate,
                        "channels": info.channels, "duration_seconds": info.duration,
                        "bytes": audio.stat().st_size})
        paths.append(audio)
    expected_note = worker.note_location(session, config, metadata)[1].resolve()
    supplied_note = Path(status.get("note_path") or expected_note)
    if not supplied_note.is_absolute():
        supplied_note = vault / supplied_note
    note = compaction.guarded_path(supplied_note, folder, required=True)
    if note != expected_note or not note.is_file() or note.suffix.lower() != ".md":
        raise ValueError("Completed legacy Markdown note is missing or has an unexpected path")
    compaction.note_parts(note, status)
    paths.append(note)
    audio = compaction.guarded_path(session / "meeting.wav", session)
    manifest = compaction.guarded_path(session / "merged_audio.json", session)
    for generated in (audio, manifest):
        if generated.exists():
            if not generated.is_file():
                raise ValueError("Combined-recording output location is not a regular file")
            paths.append(generated)
    if status.get("merged_audio") and audio.is_file():
        compaction.validate_audio(audio, session, metadata, status)
        raise ValueError("Legacy meeting already has a verified combined recording")
    reusable = None
    if audio.exists():
        candidate = compaction.read_json_file(manifest, session) if manifest.is_file() else {}
        if (not re.fullmatch(r"[a-f0-9]{64}", str(candidate.get("output_sha256", "")))
                or candidate.get("fingerprint") != merge_fingerprint(session, metadata)):
            raise ValueError("Combined recording already exists without verified ownership; original files kept")
        compaction.validate_audio(audio, session, metadata, {"merged_audio": candidate})
        reusable = candidate
    return {"session": str(session), "note_path": str(note), "recording": str(audio),
            "duration_seconds": duration, "segments": len(segments), "sources": sources,
            "changes": [str(audio), str(manifest), str(note), str(session / "status.json")],
            "deletes_files": False, "_metadata": metadata, "_status": status, "_result": result,
            "_reusable_merge": reusable,
            "_identities": {str(path): compaction.file_identity(path) for path in paths}}


def public_plan(plan: dict) -> dict:
    return {key: value for key, value in plan.items() if not key.startswith("_")}


def make_plan(config: dict, only_sessions: set[str] | None = None, home: Path = HOME,
              running: set[Path] | None = None) -> dict:
    vault = worker.safe_path(Path(config["vault_root"]))
    root = compaction.guarded_path(vault / worker.meeting_folder(config) / "附件", vault)
    running = compaction.active_sessions(home, config) if running is None else running
    ready, skipped = [], []
    if root.exists():
        for session in sorted(root.iterdir()):
            if not compaction.SESSION_NAME.fullmatch(session.name):
                continue
            if only_sessions and session.name not in only_sessions:
                continue
            try:
                ready.append(eligible_session(session, config, home, running))
            except Exception as error:
                skipped.append({"session": str(session), "reason": str(error)})
    return {"ready": ready, "skipped": skipped, "attachments_root": str(root)}


def apply_session(plan: dict, config: dict, home: Path = HOME,
                  running: set[Path] | None = None) -> dict:
    session = Path(plan["session"])
    fresh = eligible_session(session, config, home, running)
    if fresh["_identities"] != plan["_identities"]:
        raise ValueError("Legacy meeting changed after planning; no preparation performed")
    note = Path(fresh["note_path"])
    protected = [session / "session.json", session / "result.json",
                 session / "mic.wav", session / "system.wav"]
    before = {str(path): compaction.digest(path) for path in protected}
    original_note = note.read_bytes()
    manual_bytes = compaction.note_parts(note, fresh["_status"])
    metadata, previous = fresh["_metadata"], fresh["_status"]
    merged = fresh["_reusable_merge"] or worker.merge_session(session, metadata)
    audio = Path(fresh["recording"])
    merged["output_sha256"] = compaction.digest(audio)
    worker.atomic_json(session / "merged_audio.json", merged)
    completed = {**previous, "merged_audio": merged, "merge_error": None,
                 "updated_at": datetime.now().astimezone().isoformat()}
    info = compaction.validate_audio(audio, session, metadata, completed)
    content = worker.render_note(session, Path(config["vault_root"]).resolve(), metadata,
                                 result=fresh["_result"], mixed_audio=merged)
    # Check identities again before updating existing user-visible artifacts.
    for path in [note, session / "session.json", session / "status.json", session / "result.json",
                 session / "mic.wav", session / "system.wav"]:
        if compaction.file_identity(path) != fresh["_identities"][str(path)]:
            raise ValueError("Legacy inputs changed during merge; original files kept")
    generated_note = None
    try:
        completed["managed_note"] = worker.save_note(note, content, previous.get("managed_note"))
        generated_note = note.read_bytes()
        if compaction.note_parts(note, completed) != manual_bytes:
            raise ValueError("Handwritten Markdown bytes changed; status not updated")
        link = worker.audio_link(audio, Path(config["vault_root"]).resolve(), metadata).encode()
        if link not in generated_note:
            raise ValueError("Prepared Markdown is missing its combined-recording embed")
        for path in protected:
            if compaction.digest(path) != before[str(path)]:
                raise ValueError("Legacy source or complete transcription changed; status not updated")
        worker.atomic_json(session / "status.json", completed)
    except Exception:
        # Ordinary commit failures are retryable. Preserve any concurrent user
        # edit and never roll back just the note after status already committed.
        if (generated_note is not None and worker.read_json(session / "status.json") == previous
                and note.read_bytes() == generated_note):
            temporary = None
            try:
                with tempfile.NamedTemporaryFile(prefix=".tingji-rollback-", suffix=".tmp",
                                                 dir=note.parent, delete=False) as stream:
                    temporary = Path(stream.name)
                    stream.write(original_note)
                worker.safe_path(note)
                if note.read_bytes() == generated_note:
                    os.replace(temporary, note)
            finally:
                if temporary is not None and temporary.exists():
                    temporary.unlink()
        raise
    worker.verified_artifacts(session, config, metadata, completed)
    return {**public_plan(fresh), "prepared": True, "audio": info,
            "source_and_result_bytes_unchanged": True, "handwritten_bytes_preserved": True,
            "combined_recording_sha256": compaction.digest(audio), "deletes_files": False}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--only-session", action="append", default=[], help="Exact timestamped folder; repeat to select multiple")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    if args.apply and not args.only_session:
        parser.error("--apply requires an explicit --only-session allowlist")
    if any(not compaction.SESSION_NAME.fullmatch(name) for name in args.only_session):
        parser.error("--only-session must be a timestamped folder name")
    mode = "apply" if args.apply else "dry-run"
    report_path = compaction.guarded_path(args.report or HOME / "validation" / f"legacy-merge-{mode}.json", HOME)
    started = datetime.now().astimezone().isoformat()
    try:
        config = worker.read_json(HOME / "config.json")
        plan = make_plan(config, set(args.only_session))
        results, failures = [], []
        for session in plan["ready"]:
            if args.apply:
                try:
                    results.append(apply_session(session, config))
                except Exception as error:
                    failures.append({"session": session["session"], "error": str(error)})
            else:
                results.append(public_plan(session))
        report = {"mode": mode, "started_at": started, "allowlist": args.only_session,
                  "attachments_root": plan["attachments_root"], "ready_sessions": len(plan["ready"]),
                  "prepared_sessions": len(results) if args.apply else 0,
                  "sessions": results, "skipped": plan["skipped"], "failures": failures,
                  "deletes_files": False}
        worker.atomic_json(report_path, report)
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 1 if failures else 0
    except Exception as error:
        report = {"mode": mode, "started_at": started, "error": str(error), "deletes_files": False}
        worker.atomic_json(report_path, report)
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
