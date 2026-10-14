"""Offline recording -> speaker turns -> Chinese transcript -> Obsidian."""
from __future__ import annotations

import argparse
from contextlib import contextmanager
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
from urllib.parse import quote

import soundfile as sf

from asr_engine import atomic_json
from merge_audio import merge_session
from tingji_config import load_config

HOME = Path(__file__).resolve().parent
BEGIN = "<!-- local-meeting-transcript:start -->"
END = "<!-- local-meeting-transcript:end -->"
COMPACT_FILES = frozenset({
    "mic.wav", "system.wav", "meeting.wav", "meeting.wav.tmp",
    "merged_audio.json", "merged_audio.json.tmp", "segments.json", "segments.json.tmp",
    "prepared_input.json", "prepared_input.json.tmp", "prepare_summary.json", "prepare_summary.json.tmp",
    "result.json", "result.json.tmp", "asr_status.json", "asr_status.json.tmp",
    "prepare.log", "asr.log", "merge.log", "worker.log",
})
CLIP_NAME = re.compile(r"(?:mic|system)_\d{5,}\.wav\Z")


def read_json(path: Path, default=None):
    path = safe_path(path)
    return json.loads(path.read_text(encoding="utf-8-sig")) if path.is_file() else default


def status(session: Path, message: str, state="processing", **extra):
    safe_path(session / "status.json")
    safe_path(session / "status.json.tmp")
    atomic_json(session / "status.json", {"state": state, "message": message,
                "updated_at": datetime.now().astimezone().isoformat(), **extra})


@contextmanager
def processing_slot(session: Path):
    """Serialize jobs; the OS releases the lock if a worker crashes."""
    import msvcrt
    with (HOME / "processing.lock").open("a+b") as lock:
        lock.seek(0, 2)
        if lock.tell() == 0:
            lock.write(b"0")
            lock.flush()
        while True:
            lock.seek(0)
            try:
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                break
            except OSError:
                status(session, "录音已保存，等待前一场会议转写完成", state="queued")
                time.sleep(2)
        try:
            yield
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


def timestamp(seconds: float):
    seconds = max(0, int(seconds))
    hours, remaining = divmod(seconds, 3600)
    minutes, seconds = divmod(remaining, 60)
    return f"{hours:02d}:{minutes:02d}:{seconds:02d}" if hours else f"{minutes:02d}:{seconds:02d}"


def safe_path(path: Path):
    """Reject symbolic links and Windows junctions before resolving a path."""
    path = Path(os.path.abspath(path))
    for part in [*reversed(path.parents), path]:
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError(f"不允许链接或重解析目录：{part}")
    return path.resolve()


def meeting_folder(config: dict):
    folder = Path(config.get("meeting_folder", "会议记录"))
    if folder.is_absolute() or ".." in folder.parts or str(folder) in ("", "."):
        raise ValueError("会议笔记目录必须是库内相对路径")
    return folder


def audio_output_path(session: Path, vault: Path, metadata: dict, config=None):
    if metadata.get("storage_mode") != "minimal":
        return safe_path(session / "meeting.wav")
    folder = meeting_folder(config or {})
    expected = safe_path(vault / folder / "附件" / session.name)
    supplied = metadata.get("audio_output_dir")
    if not supplied or not Path(supplied).is_absolute() or safe_path(Path(supplied)) != expected:
        raise ValueError("公开录音目录必须对应当前会议的库内附件目录")
    return safe_path(expected / "meeting.wav")


def note_location(session: Path, config: dict, metadata: dict):
    session = safe_path(session)
    vault = safe_path(Path(config["vault_root"]))
    work = safe_path(HOME / "work")
    if session == vault:
        raise ValueError("会议目录不能是整个笔记库")
    try:
        session.relative_to(vault)
    except ValueError:
        if session.parent != work or metadata.get("storage_mode") != "minimal":
            raise ValueError("会议必须位于笔记库或程序 work 的直接子目录")
    folder = meeting_folder(config)
    audio_output_path(session, vault, metadata, config)
    started = datetime.fromisoformat(metadata["start_time"])
    stamp = started.strftime("%Y%m%d-%H%M%S")
    suffix = session.name[len(stamp):] if session.name.startswith(stamp) else "-" + session.name[-6:]
    name = f"{started:%Y-%m-%d %H-%M-%S}{suffix}.md"
    return vault, safe_path(vault / folder / name)


def audio_link(audio: Path, vault: Path, metadata: dict):
    if metadata.get("note_link_style", "wiki") == "wiki":
        return f"![[{audio.relative_to(vault).as_posix()}]]"
    if metadata.get("note_link_style") != "markdown" or metadata.get("storage_mode") != "minimal":
        raise ValueError("未知会议录音链接格式")
    # Minimal-storage notes live two folders above the recording's own folder.
    relative = os.path.relpath(audio, audio.parent.parent.parent).replace("\\", "/")
    return f"[会议录音]({quote(relative, safe='/')})"


def render_note(session: Path, vault: Path, metadata: dict, result=None, error=None,
                mixed_audio=None, merge_error=None):
    started = datetime.fromisoformat(metadata["start_time"])
    lines = [f"录制时间：{started:%Y-%m-%d %H:%M:%S}",
             f"时长：{timestamp(float(metadata.get('duration_seconds', 0)))}", "",
             ("转写引擎：API 音频转写（" + str(metadata.get("transcription_model", "已配置服务")) + "）"
              if metadata.get("transcription_mode") == "api" else "转写引擎：Qwen3-ASR-1.7B（本地，中文）"), ""]
    if mixed_audio is not None:
        default_audio = (Path(metadata["audio_output_dir"]) / "meeting.wav"
                         if metadata.get("storage_mode") == "minimal" else session / "meeting.wav")
        published = Path(mixed_audio.get("published_path", default_audio))
        lines.extend(["**会议录音**", audio_link(safe_path(published), safe_path(vault), metadata), ""])
    if metadata.get("recording_pauses"):
        lines.append("整场暂停区间（录音中保持静音，时间戳保持会议时间轴）：")
        for pause in metadata["recording_pauses"]:
            if pause.get("end_seconds") is not None:
                lines.append(f"- {timestamp(pause['start_seconds'])}–{timestamp(pause['end_seconds'])}")
        lines.append("")
    if merge_error:
        lines.extend([f"**合并录音暂未生成，原始分轨已保留。** {merge_error}", ""])
    if merge_error or error:
        location = "程序工作目录" if metadata.get("storage_mode") == "minimal" else "原会议目录"
        lines.extend([f"原始录音和处理数据保留在{location}，可通过录音工具重试。", ""])
    if metadata.get("capture_error"):
        lines.extend([f"录音异常：{metadata['capture_error']}", ""])
    if error:
        lines.extend(["**转写未完成，原始录音和已完成段已保留。**", str(error), ""])
    elif result is None:
        lines.extend(["录音已保存，正在转写处理。", ""])
    if result is not None:
        rows = [row for row in result.get("segments", []) if row.get("status") == "done"
                and str(row.get("text", "")).strip()]
        labels = {}
        if not rows:
            lines.extend(["未检测到可转写的讲话，请检查录音音量。", ""])
        else:
            lines.extend(["**逐段转写**", ""])
            for row in rows:
                speaker = str(row["speaker"])
                if speaker not in labels:
                    source = "麦克风" if speaker.startswith("mic_") else "电脑声音"
                    labels[speaker] = f"{source} · 说话人 {speaker.rsplit('_', 1)[-1]}"
                text = str(row.get("text", "")).strip()
                if text:
                    lines.extend([f"**[{timestamp(row['start'])}–{timestamp(row['end'])}] {labels[speaker]}**",
                                  text, ""])
            lines.extend(["说话人编号为每条音轨内的匿名分组。同一人在两条音轨上可能有不同编号；"
                          "重叠讲话与扬声器串音仍需核对原音频。", ""])
    return "\n".join(lines)


def save_note(path: Path, content: str):
    safe_path(path)
    block = f"{BEGIN}\n{content}\n{END}".encode("utf-8")
    if path.is_file():
        current = path.read_bytes()
        begin, end = BEGIN.encode(), END.encode()
        if current.count(begin) != 1 or current.count(end) != 1:
            raise ValueError("笔记自动转写标记被修改，已保留现有笔记；结果仍在录音目录 result.json")
        first, last = current.index(begin), current.index(end) + len(end)
        if first >= last:
            raise ValueError("笔记转写标记顺序无效，已保留现有笔记")
        text = current[:first] + block + current[last:]
    else:
        text = "# 会议记录\n\n".encode("utf-8") + block + "\n\n## 我的笔记\n\n".encode("utf-8")
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".md.tmp")
    safe_path(temporary)
    temporary.write_bytes(text)
    os.replace(temporary, path)


def validate_wave(path: Path, merged: dict):
    path = safe_path(path)
    info = sf.info(path)
    if (not merged.get("complete") or info.frames <= 0 or info.samplerate != 48000
            or info.channels != 2 or info.subtype != "PCM_16"
            or info.frames != merged.get("frames")
            or (merged.get("output_size") is not None and path.stat().st_size != merged["output_size"])):
        raise ValueError("完整合并录音验证失败，原始录音已保留")
    return path


def publish_audio(session: Path, config: dict, metadata: dict, merged: dict):
    vault, _ = note_location(session, config, metadata)
    source = validate_wave(session / "meeting.wav", merged)
    output = audio_output_path(session, vault, metadata, config)
    if output != source:
        safe_path(output.parent)
        output.parent.mkdir(parents=True, exist_ok=True)
        safe_path(output.parent)
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(prefix=".meeting-", suffix=".tmp", dir=output.parent,
                                             delete=False) as stream:
                temporary = Path(stream.name)
                with source.open("rb") as incoming:
                    shutil.copyfileobj(incoming, stream, length=1024 * 1024)
            validate_wave(temporary, merged)
            safe_path(output)
            os.replace(temporary, output)
        finally:
            if temporary is not None and temporary.exists():
                temporary.unlink()
    return {**merged, "published_path": str(output)}


def cleanup_candidates(session: Path, *, keep_meeting=False):
    """Plan only known generated files; callers must validate the session boundary."""
    session = safe_path(session)
    candidates = []
    for name in sorted(COMPACT_FILES):
        if name == "meeting.wav" and keep_meeting:
            continue
        path = session / name
        safe_path(path)
        if path.is_file():
            candidates.append(path)
        elif path.exists():
            raise ValueError(f"生成文件位置出现目录，已停止清理：{path}")
    clips = safe_path(session / "clips")
    if clips.is_dir():
        for path in sorted(clips.iterdir()):
            if CLIP_NAME.fullmatch(path.name):
                safe_path(path)
                if not path.is_file():
                    raise ValueError(f"分段录音位置出现目录，已停止清理：{path}")
                candidates.append(path)
    return candidates


def verified_artifacts(session: Path, config: dict, metadata: dict, verified_status: dict,
                       *, require_markers=True):
    vault, note = note_location(session, config, metadata)
    if read_json(session / "session.json") != metadata:
        raise ValueError("会议元数据已发生变化，已停止清理")
    actual = read_json(session / "status.json", {})
    if (actual.get("state") != "completed" or verified_status.get("state") != "completed"
            or actual.get("merge_error") or verified_status.get("merge_error")
            or actual.get("note_path") != str(note)
            or verified_status.get("note_path") != str(note)):
        raise ValueError("仅可清理已验证完成的会议")
    merged = verified_status.get("merged_audio") or {}
    if actual.get("merged_audio") != merged:
        raise ValueError("会议状态已发生变化，已停止清理")
    result_path = session / "result.json"
    result = read_json(result_path, {})
    complete = (result.get("complete") if result_path.is_file()
                else actual.get("result_complete") and verified_status.get("result_complete"))
    if not complete:
        raise ValueError("转写结果尚未完整，已保留原始录音")
    audio = audio_output_path(session, vault, metadata, config)
    validate_wave(audio, merged)
    if not note.is_file():
        raise ValueError("转写笔记不存在，已保留原始录音")
    content = note.read_bytes()
    link = audio_link(audio, vault, metadata).encode("utf-8")
    if not content or link not in content:
        raise ValueError("转写笔记缺少有效录音链接，已保留原始录音")
    if require_markers:
        begin, end = BEGIN.encode(), END.encode()
        if (content.count(begin) != 1 or content.count(end) != 1
                or content.index(begin) >= content.index(end)):
            raise ValueError("笔记自动转写标记被修改，已停止清理")
        if link not in content[content.index(begin):content.index(end)]:
            raise ValueError("自动转写区块缺少录音链接，已停止清理")
    return audio, note


def compact_session(session: Path, config: dict, metadata: dict, verified_status: dict):
    """Delete generated artifacts only after the published WAV and MD are verified.

    session.json/status.json and all unknown files remain. Return relative paths
    actually removed; marking artifacts_compacted is the caller's final action.
    """
    session = safe_path(session)
    vault, _ = note_location(session, config, metadata)
    if (metadata.get("storage_mode") != "minimal"
            and session.parent != safe_path(vault / meeting_folder(config) / "附件")):
        raise ValueError("旧会议清理仅限会议附件目录的直接子目录")
    audio, _ = verified_artifacts(session, config, metadata, verified_status)
    candidates = cleanup_candidates(session, keep_meeting=audio == session / "meeting.wav")
    # Remove recovery evidence last, so a interrupted cleanup can be resumed.
    candidates.sort(key=lambda path: (path.name in ("mic.wav", "system.wav", "result.json"),
                                      path.name == "result.json", str(path)))
    removed = []
    for path in candidates:
        safe_path(path)
        path.relative_to(session)
        path.unlink()
        removed.append(path.relative_to(session).as_posix())
    clips = safe_path(session / "clips")
    if clips.is_dir() and not any(clips.iterdir()):
        clips.rmdir()
        removed.append("clips/")
    return removed


def finish_compaction(session: Path, config: dict, metadata: dict, completed: dict):
    """Keep transcription completion distinct from an interrupted cleanup."""
    if metadata.get("storage_mode") != "minimal" or completed.get("merge_error"):
        return
    if completed.get("artifact_error"):
        count = completed.get("segments", 0)
        completed = {**completed, "message": (f"已完成中文转写，共 {count} 段" if count
                                              else "录音已保存，未检测到讲话")}
        completed.pop("artifact_error", None)
    try:
        compact_session(session, config, metadata, completed)
        completed = {**completed, "artifacts_compacted": True}
        completed.pop("cleanup_error", None)
    except Exception as error:
        completed = {**completed, "artifacts_compacted": False, "cleanup_error": str(error)}
    safe_path(session / "status.json")
    safe_path(session / "status.json.tmp")
    atomic_json(session / "status.json", completed)


def input_fingerprint(session: Path):
    entries = []
    for name in ("session.json", "mic.wav", "system.wav"):
        path = session / name
        if path.is_file():
            info = path.stat()
            entries.append([name, info.st_size, info.st_mtime_ns])
    return hashlib.sha256(json.dumps(entries).encode()).hexdigest()


def run_stage(session: Path, command: list[str], stage: str):
    env = dict(os.environ, PYTHONUTF8="1", PYTHONIOENCODING="utf-8",
               HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1")
    with (session / f"{stage}.log").open("a", encoding="utf-8") as log:
        process = subprocess.Popen(command, cwd=HOME, env=env, stdout=log, stderr=log,
                                   creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        while process.poll() is None:
            if stage == "asr":
                progress = read_json(session / "asr_status.json", {})
                if progress:
                    status(session, progress.get("message", "正在本地转写"), stage=stage,
                           completed=progress.get("completed", 0), total=progress.get("total", 0),
                           device=progress.get("device"), fallback_reason=progress.get("fallback_reason"))
            time.sleep(0.5)
        if process.returncode:
            detail = read_json(session / "asr_status.json", {}) if stage == "asr" else {}
            raise RuntimeError(detail.get("error") or f"{stage} 处理失败，请查看 {stage}.log")


def process_session(session: Path):
    session = safe_path(session)
    config = load_config(HOME / "config.json")
    metadata = read_json(session / "session.json")
    vault, note = note_location(session, config, metadata)
    for name in COMPACT_FILES:
        safe_path(session / name)
    safe_path(session / "clips")
    previous = read_json(session / "status.json", {})
    if previous.get("artifacts_compacted"):
        try:
            verified_artifacts(session, config, metadata, previous, require_markers=False)
            return 0
        except Exception as error:
            print(f"已完成会议的录音或笔记需要检查：{error}", file=sys.stderr)
            return 1
    if (metadata.get("storage_mode") == "minimal" and previous.get("state") == "completed"
            and not previous.get("merge_error") and previous.get("result_complete")):
        # A cleanup can be retried even when its earlier steps already removed
        # the source WAVs. Never try to load a model again for a finished result.
        try:
            verified_artifacts(session, config, metadata, previous)
        except Exception as error:
            detail = str(error)
            failure = {**previous, "artifacts_compacted": False, "artifact_error": detail,
                       "cleanup_error": detail,
                       "message": "录音或转写成品需要检查，已保留处理数据；请恢复成品后重试",
                       "updated_at": datetime.now().astimezone().isoformat()}
            atomic_json(session / "status.json", failure)
            print(failure["message"] + "：" + detail, file=sys.stderr)
            return 1
        finish_compaction(session, config, metadata, previous)
        return 0
    recorded_mode = metadata.get("transcription_mode", "local")
    transcription_mode = config.get("transcription_mode", "local")
    recorded_model = metadata.get("transcription_model", "Qwen3-ASR-1.7B" if recorded_mode == "local" else None)
    transcription_model = config["api_model"] if transcription_mode == "api" else "Qwen3-ASR-1.7B"
    if recorded_mode != transcription_mode or recorded_model != transcription_model or (
            recorded_mode == "api" and metadata.get("transcription_api_base_url") != config["api_base_url"].rstrip("/")):
        message = "转写设置已改变，录音和现有成品已保留；请恢复录制时的模式、模型和服务配置后重试"
        status(session, message, state="error", stage="configuration", error=message,
               note_path=str(note))
        return 1
    status(session, "录音已保存，正在准备中文转写", note_path=str(note))
    result = None
    mixed_audio, merge_error = None, None
    try:
        status(session, "正在生成单条会议录音", stage="merge", note_path=str(note))
        try:
            mixed_audio = merge_session(session, metadata)
            mixed_audio = publish_audio(session, config, metadata, mixed_audio)
        except Exception as error:
            mixed_audio = None
            merge_error = str(error)
        with (session / "merge.log").open("a", encoding="utf-8") as log:
            log.write(json.dumps({"updated_at": datetime.now().astimezone().isoformat(),
                                  "audio": mixed_audio, "error": merge_error}, ensure_ascii=False) + "\n")
        save_note(note, render_note(session, vault, metadata, mixed_audio=mixed_audio,
                                   merge_error=merge_error))
        with processing_slot(session):
            fingerprint = input_fingerprint(session)
            cache = read_json(session / "prepared_input.json", {})
            if cache.get("fingerprint") != fingerprint or not (session / "segments.json").is_file():
                if (session / "result.json").exists():
                    raise RuntimeError("原始录音或分段已改变，已保留旧结果，请使用新的录音目录重新处理")
                status(session, "正在分析麦克风和电脑声音，区分说话人", stage="speakers")
                run_stage(session, [sys.executable, str(HOME / "prepare_audio.py"),
                          "--session", str(session), "--models", str(HOME / "models")], "prepare")
                atomic_json(session / "prepared_input.json", {"fingerprint": fingerprint})
            if transcription_mode == "api":
                status(session, "正在通过API进行中文会议转写", stage="asr")
                command = [sys.executable, str(HOME / "api_asr.py"),
                           "--segments", str(session / "segments.json"),
                           "--api-base-url", config["api_base_url"], "--api-model", config["api_model"],
                           "--api-key-env", config.get("api_key_env", "TINGJI_API_KEY"),
                           "--api-timeout-seconds", str(config.get("api_timeout_seconds", 120)),
                           "--status", str(session / "asr_status.json"),
                           "--context", config.get("asr_context", "")]
            elif transcription_mode == "local":
                status(session, "正在加载中文会议识别模型", stage="asr")
                command = [sys.executable, str(HOME / "asr_engine.py"),
                           "--segments", str(session / "segments.json"),
                           "--model-dir", str(HOME / "models" / "Qwen3-ASR-1.7B-hf"),
                           "--status", str(session / "asr_status.json"),
                           "--context", config.get("asr_context", "")]
            else:
                raise ValueError("transcription_mode must be local or api")
            run_stage(session, command, "asr")
            result = read_json(session / "result.json")
            if not result.get("complete"):
                raise RuntimeError("转写结果不完整，已保留完成段")
            save_note(note, render_note(session, vault, metadata, result=result,
                                       mixed_audio=mixed_audio, merge_error=merge_error))
            summary = read_json(session / "prepare_summary.json", {})
            count = sum(bool(str(row.get("text", "")).strip()) for row in result.get("segments", []))
            message = f"已完成中文转写，共 {count} 段" if count else "录音已保存，未检测到讲话"
            if merge_error:
                message += "；合并录音失败，原始分轨已保留"
            status(session, message,
                   state="completed", note_path=str(note), segments=count,
                   sources=summary.get("sources", {}), merged_audio=mixed_audio, merge_error=merge_error,
                   result_complete=True, artifacts_compacted=False)
            finish_compaction(session, config, metadata, read_json(session / "status.json"))
            return 0
    except Exception as error:
        result = read_json(session / "result.json", result)
        try:
            save_note(note, render_note(session, vault, metadata, result=result, error=str(error),
                                       mixed_audio=mixed_audio, merge_error=merge_error))
        except Exception as note_error:
            error = RuntimeError(f"{error}；{note_error}")
        status(session, "转写未完成，录音和已完成段已保留", state="error",
               error=str(error), note_path=str(note))
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", required=True, type=Path)
    args = parser.parse_args()
    raise SystemExit(process_session(args.session))
