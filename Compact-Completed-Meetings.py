"""Plan (or explicitly apply) cleanup of completed legacy meeting sessions.

Only recognized generated artifacts are removed. The published WAV and Markdown
remain in place; recorder control metadata moves outside the Obsidian vault.
"""
from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat

import psutil
import soundfile as sf

import meeting_worker as worker

HOME = Path(__file__).resolve().parent
SESSION_NAME = re.compile(r"^\d{8}-\d{6}(?:-[0-9a-f]{8})?$")
COMPLETED_STATES = {"completed"}


def guarded_path(path: Path, root: Path, *, required=False) -> Path:
    """Reject traversal and Windows junctions/reparse points before resolving."""
    path, root = Path(os.path.abspath(path)), Path(os.path.abspath(root))
    path.relative_to(root)
    chain = [path, *path.parents]
    for part in chain:
        try:
            attributes = part.lstat()
        except FileNotFoundError:
            attributes = None
        if attributes is not None:
            if stat.S_ISLNK(attributes.st_mode) or (
                getattr(attributes, "st_file_attributes", 0)
                & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
            ):
                raise ValueError(f"Refusing symlink/reparse path: {part}")
        if part == root:
            break
    resolved = path.resolve(strict=required)
    resolved.relative_to(root.resolve(strict=required))
    return resolved


def read_json_file(path: Path, root: Path) -> dict:
    guarded_path(path, root, required=True)
    if not path.is_file():
        raise ValueError(f"Expected regular JSON file: {path.name}")
    value = worker.read_json(path)
    if not isinstance(value, dict):
        raise ValueError(f"Invalid JSON object: {path.name}")
    return value


def active_sessions(home: Path = HOME, config: dict | None = None) -> set[Path]:
    """Protect workers identified by this tool's paths, PID record, or ancestry.

    Unreadable Python services from other applications are not recorder evidence.
    A worker identified as ours remains protected even if its command is denied.
    """
    home = Path(home).resolve()
    sessions = set()
    processes = {process.pid: process for process in psutil.process_iter(["pid", "ppid", "name"])}
    controller_ids = set()
    for process in processes.values():
        if str(process.info.get("name") or "").lower() != "obsidianmeeting.exe":
            continue
        try:
            if Path(process.exe()).resolve() == home / "ObsidianMeeting.exe":
                controller_ids.add(process.pid)
        except psutil.NoSuchProcess:
            continue
        except psutil.AccessDenied:
            # The native controller name is specific; an inaccessible instance
            # conservatively protects its children without affecting other apps.
            controller_ids.add(process.pid)

    def recorder_child(pid):
        visited = set()
        while pid and pid not in visited:
            if pid in controller_ids:
                return True
            visited.add(pid)
            process = processes.get(pid)
            if process is None:
                return False
            pid = process.info.get("ppid")
        return False

    recorded_worker = 0
    last_path = home / "last-session.json"
    if last_path.is_file():
        last = read_json_file(last_path, home)
        saved_session = last.get("session_dir")
        pid = int(last.get("worker_pid") or 0)
        process = processes.get(pid)
        if saved_session and process is not None:
            target = Path(saved_session)
            allowed = target.is_absolute() and target.parent == home / "work"
            if config is not None:
                vault = Path(config["vault_root"]).resolve()
                attachment_root = vault / config.get("meeting_folder", "会议记录") / "附件"
                allowed = allowed or target.parent == attachment_root
            if allowed:
                same_process = True
                try:
                    started = datetime.fromisoformat(last["worker_start_time_utc"].replace("Z", "+00:00"))
                    same_process = abs(process.create_time() - started.timestamp()) < 0.01
                except psutil.NoSuchProcess:
                    same_process = False
                except (psutil.AccessDenied, KeyError, TypeError, ValueError):
                    # A saved worker PID with unavailable start evidence only
                    # blocks its own session, never every completed meeting.
                    pass
                if same_process:
                    recorded_worker = pid
                    sessions.add(target.resolve())

    script_paths = {home / name for name in ("meeting_worker.py", "prepare_audio.py", "asr_engine.py")}
    script_names = {path.name.lower() for path in script_paths}
    for process in processes.values():
        if str(process.info.get("name") or "").lower() not in {
            "python.exe", "pythonw.exe", "python", "python3"
        }:
            continue
        try:
            command = process.cmdline()
            if not any(Path(argument).name.lower() in script_names
                       and Path(argument).is_absolute() and Path(argument).resolve() in script_paths
                       for argument in command):
                continue
            for index, argument in enumerate(command):
                if argument == "--session" and index + 1 < len(command):
                    sessions.add(Path(command[index + 1]).resolve())
                elif argument.startswith("--session="):
                    sessions.add(Path(argument.split("=", 1)[1]).resolve())
                elif argument == "--segments" and index + 1 < len(command):
                    sessions.add(Path(command[index + 1]).resolve().parent)
        except psutil.NoSuchProcess:
            continue
        except psutil.AccessDenied as error:
            if process.pid == recorded_worker:
                continue  # Its saved session was already protected above.
            if recorder_child(process.pid):
                raise RuntimeError(f"Cannot inspect recorder worker PID {process.pid}; cleanup deferred") from error
            continue
    return sessions


def cleanup_candidates(session: Path) -> list[Path]:
    return list(worker.cleanup_candidates(session, keep_meeting=True))


def marker_parts(content: bytes) -> tuple[bytes, bytes]:
    begin, end = worker.BEGIN.encode(), worker.END.encode()
    if content.count(begin) != 1 or content.count(end) != 1:
        raise ValueError("Markdown auto-transcript markers are missing or ambiguous")
    first, last = content.index(begin), content.index(end)
    if first >= last:
        raise ValueError("Markdown auto-transcript markers are reversed")
    return content[:first], content[last + len(end):]


def file_identity(path: Path) -> tuple[int, int]:
    info = path.stat()
    return info.st_size, info.st_mtime_ns


def validate_audio(audio: Path, session: Path, metadata: dict, status: dict) -> dict:
    guarded_path(audio, session, required=True)
    if not audio.is_file():
        raise ValueError("Combined recording is missing")
    info = sf.info(audio)
    if (info.frames <= 0 or info.samplerate != 48000 or info.channels != 2
            or info.subtype != "PCM_16" or info.format not in {"WAV", "WAVEX"}):
        raise ValueError("Combined recording has an invalid WAV header")
    merged = status.get("merged_audio")
    if not isinstance(merged, dict) or not merged.get("complete"):
        raise ValueError("Completed status has no verified combined recording manifest")
    if not merged.get("complete") or int(merged.get("frames", -1)) != info.frames:
        raise ValueError("Combined recording does not match its completed manifest")
    duration = float(metadata.get("duration_seconds", -1))
    if (not math.isfinite(duration) or duration < 0
            or abs(info.duration - duration) > max(0.25, 2 / info.samplerate)):
        raise ValueError("Combined recording duration does not match the meeting")
    if abs(float(merged.get("duration_seconds", -1)) - info.duration) > 2 / info.samplerate:
        raise ValueError("Combined recording duration does not match its manifest")
    with sf.SoundFile(audio) as stream:
        if len(stream.read(1, always_2d=True)) != 1:
            raise ValueError("Combined recording contains no readable audio")
        stream.seek(info.frames - 1)
        if len(stream.read(1, always_2d=True)) != 1:
            raise ValueError("Combined recording ends before its declared last frame")
    return {"frames": info.frames, "sample_rate": info.samplerate,
            "channels": info.channels, "duration_seconds": info.duration,
            "bytes": audio.stat().st_size}


def evaluate_session(session: Path, config: dict, home: Path,
                     running: set[Path]) -> dict:
    vault = Path(config["vault_root"]).resolve()
    folder = guarded_path(vault / config.get("meeting_folder", "会议记录"), vault)
    attachment_root = guarded_path(folder / "附件", vault)
    session = guarded_path(session, attachment_root, required=True)
    if session.parent != attachment_root or not SESSION_NAME.fullmatch(session.name):
        raise ValueError("Not a direct timestamped meeting session")
    if session in running:
        raise ValueError("Meeting worker is still active")
    metadata = read_json_file(session / "session.json", session)
    state = read_json_file(session / "status.json", session)
    if metadata.get("state") == "recording":
        raise ValueError("Meeting is still recording")
    if state.get("state") not in COMPLETED_STATES or state.get("merge_error"):
        raise ValueError("Transcription or recording merge is not successfully completed")
    result = read_json_file(session / "result.json", session)
    if result.get("complete") is not True:
        raise ValueError("Transcription result is incomplete")
    if not isinstance(result.get("segments"), list):
        raise ValueError("Transcription result has no segment list")
    audio = session / "meeting.wav"
    audio_info = validate_audio(audio, session, metadata, state)
    note = Path(state.get("note_path") or worker.note_location(session, config, metadata)[1])
    if not note.is_absolute():
        note = vault / note
    note = guarded_path(note, folder, required=True)
    expected_note = worker.note_location(session, config, metadata)[1].resolve()
    if note != expected_note:
        raise ValueError("Completed note path differs from this meeting's generated note")
    if not note.is_file() or note.suffix.lower() != ".md":
        raise ValueError("Completed Markdown note is missing")
    content = note.read_bytes()
    marker_parts(content)
    relative_audio = audio.relative_to(vault).as_posix()
    if f"![[{relative_audio}]]".encode() not in content:
        raise ValueError("Markdown does not embed the combined recording")
    work_root = guarded_path(home / "work", home)
    work_session = guarded_path(work_root / session.name, home)
    if work_session.exists():
        existing = read_json_file(work_session / "session.json", home)
        if (existing.get("storage_mode") != "minimal"
                or existing.get("state") == "recording"
                or Path(existing.get("audio_output_dir", "")).resolve() != session
                or existing.get("start_time") != metadata.get("start_time")):
            raise ValueError("A different work session already uses this timestamp")
        if any(path.name not in {"session.json", "status.json"} for path in work_session.iterdir()):
            raise ValueError("Work session contains other artifacts; refusing to overwrite it")
        for name in ("session.json", "status.json"):
            guarded_path(work_session / name, home)
        existing_status = read_json_file(work_session / "status.json", home)
        if existing_status.get("state") != "completed" or not existing_status.get("result_complete"):
            raise ValueError("Work session is not a previously staged completed migration")
    removal = [*cleanup_candidates(session), session / "session.json", session / "status.json"]
    for path in removal:
        guarded_path(path, session, required=True)
        if not path.is_file():
            raise ValueError(f"Generated artifact is not a regular file: {path.name}")
    known = set(removal) | {audio, session / "clips"}
    unknown = [path.relative_to(session).as_posix()
               for path in session.iterdir() if path not in known]
    clips = session / "clips"
    if clips.exists():
        known_clips = set(removal)
        unknown.extend(path.relative_to(session).as_posix()
                       for path in clips.iterdir() if path not in known_clips)
    return {
        "session": str(session), "work_session": str(work_session), "note_path": str(note),
        "recording": str(audio), "audio": audio_info,
        "remove": [{"path": str(path), "bytes": path.stat().st_size} for path in removal],
        "preserved_unknown": sorted(unknown),
        "_metadata": metadata, "_status": state, "_result": result,
        "_identities": {str(path): file_identity(path) for path in [audio, note, *removal]},
    }


def public_plan(plan: dict) -> dict:
    return {key: value for key, value in plan.items() if not key.startswith("_")}


def make_plan(config: dict, home: Path = HOME, running: set[Path] | None = None,
              only_sessions: set[str] | None = None) -> dict:
    home = Path(home).resolve()
    vault = Path(config["vault_root"]).resolve()
    attachment_root = guarded_path(vault / config.get("meeting_folder", "会议记录") / "附件", vault)
    running = active_sessions(home, config) if running is None else running
    ready, skipped = [], []
    if attachment_root.exists():
        for session in sorted(attachment_root.iterdir()):
            if not SESSION_NAME.fullmatch(session.name):
                continue
            if only_sessions and session.name not in only_sessions:
                continue
            try:
                ready.append(evaluate_session(session, config, home, running))
            except Exception as error:
                skipped.append({"session": str(session), "reason": str(error)})
    return {"ready": ready, "skipped": skipped,
            "attachments_root": str(attachment_root), "vault": str(vault)}


def digest(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def apply_session(plan: dict, config: dict, home: Path = HOME,
                  running: set[Path] | None = None) -> dict:
    home = Path(home).resolve()
    session = Path(plan["session"])
    fresh = evaluate_session(session, config, home,
                             active_sessions(home, config) if running is None else running)
    if fresh["_identities"] != plan["_identities"]:
        raise ValueError("Session files changed after planning; no cleanup performed")
    note, audio = Path(fresh["note_path"]), Path(fresh["recording"])
    before_note, before_audio = note.read_bytes(), digest(audio)
    before_outside = marker_parts(before_note)
    metadata, state = fresh["_metadata"], fresh["_status"]
    mixed_audio = dict(state.get("merged_audio") or worker.read_json(session / "merged_audio.json"))
    mixed_audio["published_path"] = str(audio)
    content = worker.render_note(session, Path(config["vault_root"]).resolve(), metadata,
                                 result=fresh["_result"], mixed_audio=mixed_audio)
    worker.save_note(note, content)
    if marker_parts(note.read_bytes()) != before_outside:
        raise ValueError("Handwritten Markdown bytes changed; cleanup aborted")
    work_session = Path(fresh["work_session"])
    guarded_path(work_session, home)
    work_session.mkdir(parents=True, exist_ok=True)
    minimal_metadata = {
        key: metadata[key] for key in (
            "schema_version", "start_time", "end_time", "state", "duration_seconds",
            "capture_error", "device_names", "microphone_pauses", "microphone_paused",
        ) if key in metadata
    }
    minimal_metadata.update(session_dir=str(work_session), storage_mode="minimal",
                            audio_output_dir=str(session))
    minimal_status = {key: state[key] for key in (
        "state", "message", "updated_at", "note_path", "segments", "merged_audio", "merge_error"
    ) if key in state}
    minimal_status.update(note_path=str(note), storage_mode="minimal", result_complete=True,
                          cleanup_complete=False, artifacts_compacted=False)
    worker.atomic_json(work_session / "session.json", minimal_metadata)
    worker.atomic_json(work_session / "status.json", minimal_status)
    # Persist proof before deleting result.json so interrupted cleanup is recoverable.
    state = dict(state, result_complete=True)
    worker.atomic_json(session / "status.json", state)
    removed = worker.compact_session(session, config, metadata, state)
    remaining = cleanup_candidates(session)
    if remaining:
        raise ValueError("Recognized processing artifacts remain; control metadata kept")
    if digest(audio) != before_audio:
        raise ValueError("Combined recording changed during cleanup")
    if marker_parts(note.read_bytes()) != before_outside:
        raise ValueError("Handwritten Markdown bytes changed during cleanup")
    validate_audio(audio, session, metadata, state)
    for name in ("session.json", "status.json"):
        path = guarded_path(session / name, session, required=True)
        if not path.is_file():
            raise ValueError(f"Refusing non-file control metadata: {name}")
    minimal_status["cleanup_complete"] = True
    minimal_status["artifacts_compacted"] = True
    worker.atomic_json(work_session / "status.json", minimal_status)
    for name in ("session.json", "status.json"):
        guarded_path(session / name, session, required=True).unlink()
        removed.append(name)
    last_path = home / "last-session.json"
    if last_path.is_file():
        last = read_json_file(last_path, home)
        if Path(last.get("session_dir", "")).resolve() == session:
            last["session_dir"] = str(work_session)
            worker.atomic_json(last_path, last)
    return {**public_plan(fresh), "removed": removed,
            "removed_files": sum(not name.endswith("/") for name in removed),
            "removed_bytes": sum(row["bytes"] for row in fresh["remove"]),
            "handwritten_bytes_preserved": True, "recording_sha256": before_audio,
            "recording_unchanged": True, "cleanup_complete": True}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="Actually compact verified completed sessions")
    parser.add_argument("--config", type=Path, default=HOME / "config.json")
    parser.add_argument("--only-session", action="append", default=[], help="Limit to an exact timestamped folder name")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    config = worker.read_json(args.config.resolve())
    mode = "apply" if args.apply else "dry_run"
    report_path = args.report or HOME / "validation" / f"minimal-storage-{mode}.json"
    report_path = guarded_path(report_path, HOME)
    started = datetime.now().astimezone().isoformat()
    try:
        plan = make_plan(config, only_sessions=set(args.only_session))
        results, failures = [], []
        for session in plan["ready"]:
            if args.apply:
                try:
                    results.append(apply_session(session, config))
                except Exception as error:
                    failures.append({"session": session["session"], "error": str(error)})
            else:
                results.append(public_plan(session))
        report = {"mode": mode, "started_at": started, "vault": plan["vault"],
                  "attachments_root": plan["attachments_root"],
                  "sessions": results, "skipped": plan["skipped"], "failures": failures,
                  "ready_sessions": len(plan["ready"]), "applied_sessions": len(results) if args.apply else 0,
                  "files_to_remove": sum(len(row["remove"]) for row in plan["ready"]),
                  "bytes_to_remove": sum(item["bytes"] for row in plan["ready"] for item in row["remove"])}
        worker.atomic_json(report_path, report)
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 1 if failures else 0
    except Exception as error:
        report = {"mode": mode, "started_at": started, "error": str(error), "applied_sessions": 0}
        worker.atomic_json(report_path, report)
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
