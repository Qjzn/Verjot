"""Offline recording -> speaker turns -> Chinese transcript -> Obsidian."""
from __future__ import annotations

import argparse
import copy
from contextlib import contextmanager
from datetime import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile
import time
from urllib.parse import quote

import soundfile as sf

from asr_engine import atomic_json, job_fingerprint as local_job_fingerprint, read_segments
from audio_sources import capture_paths, capture_sources, source_kind, SOURCE_NAME
from merge_audio import merge_session
from mp3_audio import encode_mp3, validate_mp3
from tingji_config import load_config, transcription_context

HOME = Path(__file__).resolve().parent
BEGIN = "<!-- local-meeting-transcript:start -->"
END = "<!-- local-meeting-transcript:end -->"
COMPACT_FILES = frozenset({
    "meeting.wav", "meeting.wav.tmp",
    "merged_audio.json", "merged_audio.json.tmp", "segments.json", "segments.json.tmp",
    "prepared_input.json", "prepared_input.json.tmp", "prepare_summary.json", "prepare_summary.json.tmp",
    "speaker_turns.json",
    "result.json", "result.json.tmp", "asr_status.json", "asr_status.json.tmp",
    "prepare.log", "asr.log", "merge.log", "worker.log",
})
CLIP_NAME = re.compile(r"(?:mic|system)(?:_\d{2,})?(?:_speech)?_\d{5,}\.wav\Z")
SPEAKER_REASONS = {
    "short_utterance": "发言过短，声纹证据不足",
    "overlapping_speech": "多人声音重叠，无法可靠区分",
    "missing_source": "原始音轨不可用",
    "insufficient_audio": "有效音频不足",
    "embedding_unavailable": "未能提取有效声纹",
    "identity_processing_error": "声纹处理失败，可重试",
    "ambiguous_voice": "与多个说话人相似，无法可靠区分",
    "multiple_speakers": "本段包含多个说话人",
    "source_conflict": "麦克风与电脑声音的身份判断冲突",
    "insufficient_evidence": "尚无足够的身份判定证据",
}


def speaker_reason_text(row):
    reason = row.get("speaker_reason")
    if not isinstance(reason, str):
        return SPEAKER_REASONS["insufficient_evidence"]
    return SPEAKER_REASONS.get(reason, SPEAKER_REASONS["insufficient_evidence"])


def read_json(path: Path, default=None):
    path = safe_path(path)
    return json.loads(path.read_text(encoding="utf-8-sig")) if path.is_file() else default


def status(session: Path, message: str, state="processing", **extra):
    safe_path(session / "status.json")
    safe_path(session / "status.json.tmp")
    previous = read_json(session / "status.json", {})
    managed = {key: previous[key] for key in ("managed_note", "published_audio") if previous.get(key)}
    atomic_json(session / "status.json", {**managed, "state": state, "message": message,
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


def playback_timestamp(session: Path, seconds: float, label=None, *, enabled=False):
    """Owned meeting links are handled locally; no arbitrary audio path is encoded.

    Plain media #t fragments are not used because Markdown readers and external
    default players do not consistently implement their seek semantics.
    """
    text = timestamp(seconds) if label is None else label
    if (not enabled or type(seconds) not in (int, float) or not math.isfinite(seconds)
            or not 0 <= seconds <= 604800
            or not re.fullmatch(r"[0-9]{8}-[0-9]{6}(?:-[0-9a-f]{8})?", session.name)):
        return f"[{text}]"
    position = f"{seconds:.3f}".rstrip("0").rstrip(".")
    return f"[{text}](pianyan://play?session={session.name}&seconds={position})"


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
    # Absence identifies pre-MP3 recordings; never migrate their finished audio.
    audio_format = metadata.get("audio_format", "wav")
    if audio_format not in ("wav", "mp3"):
        raise ValueError("会议录音格式必须为 wav 或 mp3")
    filename = "meeting." + audio_format
    if metadata.get("storage_mode") != "minimal":
        return safe_path(session / filename)
    folder = meeting_folder(config or {})
    expected = safe_path(vault / folder / "附件" / session.name)
    supplied = metadata.get("audio_output_dir")
    if not supplied or not Path(supplied).is_absolute() or safe_path(Path(supplied)) != expected:
        raise ValueError("公开录音目录必须对应当前会议的库内附件目录")
    return safe_path(expected / filename)


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
    if metadata.get("note_filename"):
        name = metadata["note_filename"]
        if (not isinstance(name, str) or not name.lower().endswith(".md")
                or not name[:-3].strip() or name != name.strip()
                or re.search(r'[<>:"/\\|?*\x00-\x1f]', name)):
            raise ValueError("会议笔记文件名必须是有效的 Markdown 文件名，不能包含目录")
    return vault, safe_path(vault / folder / name)


def audio_link(audio: Path, vault: Path, metadata: dict):
    if metadata.get("note_link_style", "wiki") == "wiki":
        return f"![[{audio.relative_to(vault).as_posix()}]]"
    if metadata.get("note_link_style") != "markdown" or metadata.get("storage_mode") != "minimal":
        raise ValueError("未知会议录音链接格式")
    # Minimal-storage notes live two folders above the recording's own folder.
    relative = os.path.relpath(audio, audio.parent.parent.parent).replace("\\", "/")
    return f"[会议录音]({quote(relative, safe='/')})"


def clean_transcript_text(value):
    """Clean transport whitespace without rewriting speech or guessing punctuation."""
    text = str(value).replace("\r\n", "\n").replace("\r", "\n")
    text = re.sub(r"[\u200b\ufeff]", "", text)
    return "\n".join(re.sub(r"[^\S\n]+", " ", line).strip()
                     for line in text.split("\n")).strip()


def capture_failure(metadata: dict):
    """Only recorder-confirmed errors affect retention; diagnostics are advisory."""
    error = metadata.get("capture_error")
    if isinstance(error, str) and error.strip():
        # Older drivers exposed repeated localized E_NOTIMPL text, sometimes
        # containing blank lines which split the generated Markdown paragraph.
        detail = " ".join(error.split())
        detail = re.sub(r"(?:尚未实现\s*)+", "设备报告不支持的音频操作；请检查设备连接与录音完整性", detail)
        return detail
    if metadata.get("state") == "recorded_with_error":
        return "录音设备报告采集异常，部分声音可能缺失"
    return ""


def transcript_with_evidence(session: Path, result):
    """Join verified preparation evidence for display, leaving ASR checkpoints intact."""
    if result is None:
        return None
    prepared = read_json(session / "segments.json", [])
    if not isinstance(prepared, list):
        return result
    rows = []
    for row in result.get("segments", []):
        current = dict(row)
        index = row.get("index")
        if type(index) is int and 0 <= index < len(prepared):
            original = prepared[index]
            if (isinstance(original, dict)
                    and all(original.get(key) == row.get(key) for key in ("start", "end", "speaker"))
                    and isinstance(original.get("text_audio"), str)):
                path = Path(original["text_audio"])
                path = path if path.is_absolute() else session / path
                if str(path.resolve()) == row.get("text_audio"):
                    for key in ("segment_id", "possible_duplicate_of", "echo_candidate_of"):
                        if isinstance(original.get(key), str) and original[key].strip():
                            current[key] = original[key]
                    similarity = original.get("waveform_similarity")
                    if (type(similarity) in (int, float) and math.isfinite(similarity)
                            and 0 <= similarity <= 1):
                        current["waveform_similarity"] = similarity
                    for key in ("echo_similarity", "echo_window_support", "echo_window_min_similarity",
                                "echo_residual_ratio", "echo_delay_ms"):
                        value = original.get(key)
                        maximum = 300 if key == "echo_delay_ms" else 1
                        if type(value) in (int, float) and math.isfinite(value) and 0 <= value <= maximum:
                            current[key] = value
                    count = original.get("echo_window_count")
                    if type(count) is int and count >= 0:
                        current["echo_window_count"] = count
                    if isinstance(original.get("source"), str) and SOURCE_NAME.fullmatch(original["source"]):
                        current["source"] = original["source"]
                        current["source_kind"] = source_kind(original["source"])
                    if type(original.get("speaker_global")) is bool:
                        current["speaker_global"] = original["speaker_global"]
                    if original.get("speaker_status") in ("matched", "unknown", "mixed"):
                        current["speaker_status"] = original["speaker_status"]
                    if isinstance(original.get("speaker_reason"), str) and original["speaker_reason"] in SPEAKER_REASONS:
                        current["speaker_reason"] = original["speaker_reason"]
                    if isinstance(original.get("speaker_spans"), list):
                        current["speaker_spans"] = [span for span in original["speaker_spans"]
                            if isinstance(span, dict) and type(span.get("start")) in (int, float)
                            and type(span.get("end")) in (int, float)
                            and math.isfinite(span["start"]) and math.isfinite(span["end"])
                            and row["start"] <= span["start"] < span["end"] <= row["end"] + .001
                            and (span.get("global_speaker") is None or
                                 re.fullmatch(r"speaker_\d+", str(span["global_speaker"])))]
                    identity = original.get("global_speaker")
                    if isinstance(identity, str) and re.fullmatch(r"speaker_\d+", identity):
                        current["global_speaker"] = identity
        rows.append(current)
    return {**result, "segments": rows}


def _verified_echo(row: dict, original: dict | None) -> bool:
    """Suppress text only with independent acoustic evidence and exact content.

    This does not alter the recording or equate a fitted echo score with digital
    identity. Short acknowledgements, different words and real repeats remain.
    """
    if (not original or not str(row.get("speaker", "")).startswith("mic_")
            or not str(original.get("speaker", "")).startswith("system_")):
        return False
    text = clean_transcript_text(row.get("text", ""))
    if text != clean_transcript_text(original.get("text", "")) or len(re.sub(r"[\W_]", "", text)) < 8:
        return False
    rules = {"echo_similarity": (0.95, 1), "echo_window_support": (0.9, 1),
             "echo_window_min_similarity": (0.85, 1), "echo_residual_ratio": (0, 0.10),
             "echo_delay_ms": (0, 300)}
    for key, (minimum, maximum) in rules.items():
        value = row.get(key)
        if type(value) not in (int, float) or not math.isfinite(value) or not minimum <= value <= maximum:
            return False
    if type(row.get("echo_window_count")) is not int or row["echo_window_count"] < 3:
        return False
    duration, other_duration = row["end"] - row["start"], original["end"] - original["start"]
    delay = row["echo_delay_ms"] / 1000
    overlap = min(row["end"] - delay, original["end"]) - max(row["start"] - delay, original["start"])
    return min(duration, other_duration) >= 1.5 and overlap >= 0.9 * max(duration, other_duration)


def transcript_rows(result):
    rows = [row for row in result.get("segments", []) if row.get("status") == "done"
            and clean_transcript_text(row.get("text", ""))]
    by_id = {row["segment_id"]: row for row in rows if row.get("segment_id")}
    retained = []
    for row in sorted(rows, key=lambda item: (item["start"], item["end"], str(item["speaker"]))):
        if _verified_echo(row, by_id.get(row.get("echo_candidate_of"))):
            continue
        original = by_id.get(row.get("possible_duplicate_of"))
        if (original and str(row["speaker"]).startswith("mic_")
                and str(original["speaker"]).startswith("system_")
                and float(row.get("waveform_similarity", 0)) >= 0.998
                and clean_transcript_text(row["text"]) == clean_transcript_text(original["text"])):
            overlap = min(row["end"], original["end"]) - max(row["start"], original["start"])
            # Require matching text AND simultaneous, near-identical waveform evidence.
            # True repeats, ordinary echo and different recognition results are retained.
            if overlap >= 0.9 * max(row["end"] - row["start"], original["end"] - original["start"]):
                continue
        retained.append(row)
    return retained


def render_note(session: Path, vault: Path, metadata: dict, result=None, error=None,
                mixed_audio=None, merge_error=None):
    lines = []
    playback_enabled = mixed_audio is not None and not merge_error
    if mixed_audio is not None:
        default_audio = audio_output_path(session, vault, metadata)
        published = Path(mixed_audio.get("published_path", default_audio))
        lines.extend(["**会议录音**", audio_link(safe_path(published), safe_path(vault), metadata), ""])
    bookmarks = [item for item in metadata.get("bookmarks", []) if isinstance(item, dict)
                 and isinstance(item.get("time_seconds"), (int, float))
                 and not isinstance(item["time_seconds"], bool)
                 and math.isfinite(item["time_seconds"]) and item["time_seconds"] >= 0]
    if bookmarks:
        lines.extend(["## 重点时刻", ""])
        for item in sorted(bookmarks, key=lambda item: item["time_seconds"]):
            label = " ".join(clean_transcript_text(item.get("label", "重点")).splitlines()) or "重点"
            lines.append(f"- {playback_timestamp(session, item['time_seconds'], enabled=playback_enabled)} {label}")
        lines.append("")
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
    if capture_failure(metadata):
        lines.extend([f"录音异常：{capture_failure(metadata)}", ""])
    if error:
        lines.extend(["**转写未完成，原始录音和已完成段已保留。**", str(error), ""])
    elif result is None:
        lines.extend(["录音已保存，正在转写处理。", ""])
    if result is not None:
        rows = transcript_rows(result)
        if not rows:
            lines.extend(["未检测到可转写的讲话，请检查录音音量。", ""])
        else:
            lines.extend(["**逐段转写**", ""])
            for row in rows:
                speaker = str(row["speaker"])
                source = "麦克风" if row.get("source_kind") == "microphone" or row.get("source") == "mic" or speaker.startswith("mic_") else "电脑声音" if row.get("source_kind") == "loopback" or row.get("source") == "system" or speaker.startswith("system_") else "未知音轨"
                identity = row.get("global_speaker")
                if (row.get("speaker_global") is True and isinstance(identity, str)
                        and re.fullmatch(r"speaker_\d+", identity)):
                    label = "说话人 " + identity.rsplit("_", 1)[-1]
                elif row.get("speaker_status") == "mixed":
                    label = "本段多人 · " + source
                elif row.get("speaker_status") == "unknown" or row.get("speaker_global") is False:
                    label = "说话人待确认 · " + source + "（" + speaker_reason_text(row) + "）"
                else:
                    label = f"{source} · 说话人 {speaker.rsplit('_', 1)[-1]}"
                text = clean_transcript_text(row.get("text", ""))
                if text:
                    time = playback_timestamp(session, row['start'],
                                              f"{timestamp(row['start'])}–{timestamp(row['end'])}",
                                              enabled=playback_enabled)
                    lines.extend([f"**{time} {label}**",
                                  text, ""])
                    if row.get("speaker_status") == "mixed":
                        spans = row.get("speaker_spans", [])
                        details = [f"{timestamp(span['start'])}–{timestamp(span['end'])} " +
                                   ("说话人 " + span["global_speaker"].rsplit("_", 1)[-1]
                                    if span.get("speaker_global") and span.get("global_speaker") else
                                    "待确认（" + speaker_reason_text(span) + "）")
                                   for span in spans]
                        if details:
                            lines.extend(["说话时间（未逐字对齐）：" + "；".join(details), ""])
            caveat = ("说话人按跨音轨声纹匿名分组，待确认段不作身份归并；重叠讲话与串音仍需核对原音频。"
                      if any("speaker_global" in row for row in rows) else
                      "说话人编号为每条音轨内的匿名分组。同一人在两条音轨上可能有不同编号；重叠讲话与扬声器串音仍需核对原音频。")
            lines.extend([caveat, ""])
    return "\n".join(lines)


def managed_note_range(path: Path, current: bytes, ownership: dict):
    if (not isinstance(ownership, dict) or ownership.get("path") != str(path)
            or type(ownership.get("start")) is not int or type(ownership.get("length")) is not int):
        raise ValueError("缺少笔记生成区校验信息，已保留现有笔记")
    first, length = ownership["start"], ownership["length"]
    if first < 0 or length <= 0 or first + length > len(current):
        raise ValueError("笔记生成区位置已改变，已保留现有笔记")
    last = first + length
    if hashlib.sha256(current[first:last]).hexdigest() != ownership.get("sha256"):
        raise ValueError("笔记生成区已被编辑，已保留现有笔记；请另存转写结果")
    return first, last


def save_note(path: Path, content: str, ownership=None):
    safe_path(path)
    block = (content.rstrip() + "\n").encode("utf-8")
    if path.is_file():
        current = path.read_bytes()
        begin, end = BEGIN.encode(), END.encode()
        if ownership is not None:
            first, last = managed_note_range(path, current, ownership)
            suffix = current[last:]
        elif begin in current or end in current:
            if current.count(begin) != 1 or current.count(end) != 1 or current.index(begin) >= current.index(end):
                raise ValueError("笔记自动转写标记被修改，已保留现有笔记；结果仍在录音目录 result.json")
            first, last = current.index(begin), current.index(end) + len(end)
            suffix = current[last:]
            # Remove only the former template heading; retain all handwritten bytes.
            suffix = re.sub(rb"\A(?:\r?\n)*## " + "我的笔记".encode() + rb"\r?\n", b"\n", suffix, count=1)
        else:
            raise ValueError("缺少笔记生成区校验信息，已保留现有笔记")
        text = current[:first] + block + suffix
    else:
        prefix = "# 会议记录\n\n".encode("utf-8")
        first = len(prefix)
        text = prefix + block + b"\n"
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".md.tmp")
    safe_path(temporary)
    temporary.write_bytes(text)
    os.replace(temporary, path)
    return {"path": str(path), "start": first, "length": len(block),
            "sha256": hashlib.sha256(block).hexdigest()}


def save_session_note(session: Path, path: Path, content: str):
    state_path = safe_path(session / "status.json")
    previous = read_json(state_path, {})
    ownership = save_note(path, content, previous.get("managed_note"))
    atomic_json(state_path, {**previous, "managed_note": ownership})


def validate_wave(path: Path, merged: dict):
    path = safe_path(path)
    info = sf.info(path)
    if (not merged.get("complete") or info.frames <= 0 or info.samplerate != 48000
            or info.channels != 2 or info.subtype != "PCM_16"
            or info.frames != merged.get("frames")
            or (merged.get("output_size") is not None and path.stat().st_size != merged["output_size"])):
        raise ValueError("完整合并录音验证失败，原始录音已保留")
    if merged.get("output_sha256") and file_sha256(path) != merged["output_sha256"]:
        raise ValueError("合并录音内容已改变，已保留原始录音和转写结果")
    return path


def file_sha256(path: Path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def verify_audio_destination(output: Path, ownership):
    if not output.exists():
        return
    if (not isinstance(ownership, dict) or ownership.get("path") != str(output)
            or ownership.get("size") != output.stat().st_size
            or not isinstance(ownership.get("sha256"), str)
            or file_sha256(output) != ownership["sha256"]):
        raise ValueError("目标录音已存在且无法确认属于本场，或已被修改；已保留该文件及原始录音")


def publish_audio(session: Path, config: dict, metadata: dict, merged: dict):
    vault, _ = note_location(session, config, metadata)
    source = validate_wave(session / "meeting.wav", merged)
    output = audio_output_path(session, vault, metadata, config)
    ownership = read_json(session / "status.json", {}).get("published_audio")
    if metadata.get("audio_format") == "mp3":
        status(session, "正在生成并校验 MP3 会议录音", stage="encode")
        source_sha256 = file_sha256(source)
        verify_audio_destination(output, ownership)
        safe_path(output.parent)
        output.parent.mkdir(parents=True, exist_ok=True)
        safe_path(output.parent)
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(prefix=".meeting-", suffix=".tmp", dir=output.parent,
                                             delete=False) as stream:
                temporary = Path(stream.name)
            details = encode_mp3(source, temporary, frames=merged["frames"],
                                 bitrate_kbps=metadata.get("mp3_bitrate_kbps", 192))
            if file_sha256(source) != source_sha256:
                raise ValueError("编码期间无损合并录音发生变化，已保留所有原始录音")
            published_sha256 = file_sha256(temporary)
            safe_path(output)
            verify_audio_destination(output, ownership)
            os.replace(temporary, output)
        finally:
            if temporary is not None and temporary.exists():
                temporary.unlink()
        previous = read_json(session / "status.json", {})
        atomic_json(session / "status.json", {**previous, "published_audio": {
            "path": str(output), "size": output.stat().st_size, "sha256": published_sha256}})
        return {**merged, **details, "path": "meeting.mp3", "published_path": str(output),
                "container": "MP3", "subtype": "MPEG_LAYER_III",
                "output_mtime_ns": output.stat().st_mtime_ns,
                "lossless_container": merged.get("container", "WAV"),
                "lossless_sha256": source_sha256,
                "lossless_output_size": merged.get("output_size"), "output_sha256": published_sha256}
    if output != source:
        verify_audio_destination(output, ownership)
        safe_path(output.parent)
        output.parent.mkdir(parents=True, exist_ok=True)
        safe_path(output.parent)
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(prefix=".meeting-", suffix=".tmp", dir=output.parent,
                                             delete=False) as stream:
                temporary = Path(stream.name)
                digest = hashlib.sha256()
                with source.open("rb") as incoming:
                    for block in iter(lambda: incoming.read(1024 * 1024), b""):
                        stream.write(block)
                        digest.update(block)
            validate_wave(temporary, merged)
            safe_path(output)
            verify_audio_destination(output, ownership)
            os.replace(temporary, output)
        finally:
            if temporary is not None and temporary.exists():
                temporary.unlink()
        published_sha256 = digest.hexdigest()
    else:
        published_sha256 = file_sha256(source)
    previous = read_json(session / "status.json", {})
    atomic_json(session / "status.json", {**previous, "published_audio": {
        "path": str(output), "size": output.stat().st_size, "sha256": published_sha256}})
    return {**merged, "published_path": str(output), "output_sha256": published_sha256}


def validate_published_audio(path: Path, metadata: dict, merged: dict):
    if metadata.get("audio_format") != "mp3":
        return validate_wave(path, merged)
    path = safe_path(path)
    if (not merged.get("complete") or merged.get("format") != "mp3"
            or path.stat().st_size != merged.get("output_size")
            or not re.fullmatch(r"[a-f0-9]{64}", str(merged.get("output_sha256", "")))
            or file_sha256(path) != merged["output_sha256"]):
        raise ValueError("MP3 成品内容已改变或缺少校验信息，已保留原始无损录音")
    details = validate_mp3(path, frames=merged.get("frames", 0),
                           bitrate_kbps=metadata.get("mp3_bitrate_kbps", 192))
    if any(details.get(key) != merged.get(key) for key in details):
        raise ValueError("MP3 成品参数与完成状态不一致，已保留原始无损录音")
    return path


def cleanup_metadata_token(session: Path, metadata: dict):
    """Bind cleanup to one unchanged metadata file and its validated contents."""
    path = safe_path(session / "session.json")

    def identity(info):
        # Windows Python 3.12 stat/fstat expose different ctime meanings.
        # File ID detects replacement; the digest below binds exact contents.
        return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)

    with path.open("rb") as stream:
        before = identity(os.fstat(stream.fileno()))
        content = stream.read()
        after = identity(os.fstat(stream.fileno()))
    if (before != after or after != identity(safe_path(path).stat())
            or json.loads(content.decode("utf-8-sig")) != metadata):
        raise ValueError("会议元数据已发生变化，已停止清理")
    return after, hashlib.sha256(content).digest()


def cleanup_candidates(session: Path, *, keep_meeting=False, metadata=None):
    """Plan only known generated files; callers must validate the session boundary."""
    session = safe_path(session)
    candidates = []
    if metadata is None:
        metadata = read_json(session / "session.json", {})
    generated = set(COMPACT_FILES)
    generated.update(path.relative_to(session).as_posix()
                     for row in capture_sources(session, metadata, require_files=False)
                     for path in capture_paths(session, row))
    for name in sorted(generated):
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
    validate_published_audio(audio, metadata, merged)
    if not note.is_file():
        raise ValueError("转写笔记不存在，已保留原始录音")
    content = note.read_bytes()
    link = audio_link(audio, vault, metadata).encode("utf-8")
    if not content or link not in content:
        raise ValueError("转写笔记缺少有效录音链接，已保留原始录音")
    if require_markers:
        begin, end = BEGIN.encode(), END.encode()
        if actual.get("managed_note"):
            first, last = managed_note_range(note, content, actual["managed_note"])
        elif (content.count(begin) == 1 and content.count(end) == 1
              and content.index(begin) < content.index(end)):
            first, last = content.index(begin), content.index(end)
        else:
            raise ValueError("笔记生成区校验失败，已停止清理")
        if link not in content[first:last]:
            raise ValueError("自动转写区块缺少录音链接，已停止清理")
    return audio, note


def compact_session(session: Path, config: dict, metadata: dict, verified_status: dict):
    """Delete generated artifacts only after the published audio and MD are verified.

    session.json/status.json and all unknown files remain. Return relative paths
    actually removed; marking artifacts_compacted is the caller's final action.
    """
    session = safe_path(session)
    # Never let a later edit enroll additional files for deletion.
    metadata = copy.deepcopy(metadata)
    metadata_token = cleanup_metadata_token(session, metadata)

    def check_metadata():
        if cleanup_metadata_token(session, metadata) != metadata_token:
            raise ValueError("会议元数据已发生变化，已停止清理")

    if capture_failure(metadata) or capture_failure(verified_status):
        raise ValueError("采集异常，已保留原始录音、分段和转写结果供检查，不能清理恢复资料")
    vault, _ = note_location(session, config, metadata)
    if (metadata.get("storage_mode") != "minimal"
            and session.parent != safe_path(vault / meeting_folder(config) / "附件")):
        raise ValueError("旧会议清理仅限会议附件目录的直接子目录")
    audio, _ = verified_artifacts(session, config, metadata, verified_status)
    if not re.fullmatch(r"[a-f0-9]{64}", str(verified_status.get("merged_audio", {}).get("output_sha256", ""))):
        raise ValueError("旧录音缺少内容校验信息，已保留原始录音和处理资料，请先核对成品")
    candidates = cleanup_candidates(session, keep_meeting=audio == session / "meeting.wav",
                                    metadata=metadata)
    # Remove recovery evidence last, so a interrupted cleanup can be resumed.
    raw_names = {path.relative_to(session).as_posix()
                 for row in capture_sources(session, metadata, require_files=False)
                 for path in capture_paths(session, row)}
    candidates.sort(key=lambda path: (path.name in raw_names or path.name == "result.json",
                                      path.name == "result.json", str(path)))
    removed = []
    for path in candidates:
        safe_path(path)
        path.relative_to(session)
        check_metadata()
        path.unlink()
        removed.append(path.relative_to(session).as_posix())
    clips = safe_path(session / "clips")
    if clips.is_dir() and not any(clips.iterdir()):
        check_metadata()
        clips.rmdir()
        removed.append("clips/")
    check_metadata()
    return removed


def finish_compaction(session: Path, config: dict, metadata: dict, completed: dict):
    """Keep transcription completion distinct from an interrupted cleanup."""
    if metadata.get("storage_mode") != "minimal" or completed.get("merge_error"):
        return
    failure = capture_failure(metadata) or capture_failure(completed)
    if failure:
        message = completed.get("message", "转写处理已结束")
        if not completed.get("capture_needs_review"):
            message += "；采集异常，请检查录音；原始资料已保留"
        # Processing can finish on an incomplete recording. Preserve the
        # existing completed/retry path while keeping every recovery artifact.
        completed = {**completed, "message": message, "capture_error": failure,
                     "capture_needs_review": True, "artifacts_compacted": False}
        atomic_json(safe_path(session / "status.json"), completed)
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
    metadata = read_json(session / "session.json", {})
    names = ["session.json", *(path.relative_to(session).as_posix()
             for row in capture_sources(session, metadata) for path in capture_paths(session, row))]
    for name in names:
        path = session / name
        if path.is_file():
            info = path.stat()
            entries.append([name, info.st_size, info.st_mtime_ns])
    return hashlib.sha256(json.dumps(entries).encode()).hexdigest()


def validate_asr_checkpoint(session: Path, config: dict):
    """Validate the same ASR identity before touching either public artifact."""
    segments = read_segments(session / "segments.json")
    context = transcription_context(config, read_json(session / "session.json", {}))
    if config.get("transcription_mode", "local") == "api":
        from api_asr import job_fingerprint as api_job_fingerprint

        fingerprint = api_job_fingerprint(segments, config["api_base_url"].rstrip("/"),
                                          config["api_model"].strip(), context)
    else:
        fingerprint = local_job_fingerprint(segments, (HOME / "models" / "Qwen3-ASR-1.7B-hf").resolve(),
                                            context, 512)
    saved = read_json(session / "result.json")
    if not isinstance(saved, dict) or saved.get("fingerprint") != fingerprint:
        raise RuntimeError("转写输入、模型或术语设置已改变，已保留现有成品和旧结果；请恢复设置或使用新的录音目录重新处理")
    cached = saved.get("segments", [])
    if not isinstance(cached, list):
        raise RuntimeError("转写断点的分段资料无效，已保留现有成品和旧结果")
    for index, row in enumerate(segments):
        if index >= len(cached):
            break
        old = cached[index]
        if not isinstance(old, dict) or (old.get("status") == "done" and (
                any(old.get(key) != row[key] for key in row) or not isinstance(old.get("text"), str))):
            raise RuntimeError("已完成转写段与录音分段不一致，已保留现有成品和旧结果")


def preparation_state(session: Path, metadata: dict, config: dict):
    fingerprint = input_fingerprint(session)
    cache = read_json(session / "prepared_input.json", {})
    has_prepared = (session / "segments.json").is_file()
    # A retry must keep the same clip bytes as its ASR checkpoints. Old caches
    # predate the filters and therefore represent unfiltered audio.
    denoise = metadata.get("asr_denoise", cache.get("asr_denoise", "off")
                           if has_prepared and cache.get("fingerprint") else config["asr_denoise"])
    echo_filter = metadata.get("asr_echo_filter", cache.get("asr_echo_filter", False)
                               if has_prepared and cache.get("fingerprint") else config["asr_echo_filter"])
    if denoise not in ("light", "off"):
        raise ValueError("会议的 asr_denoise 配置必须为 light 或 off")
    if type(echo_filter) is not bool:
        raise ValueError("会议的 asr_echo_filter 配置必须为布尔值")
    needs_preparation = (cache.get("fingerprint") != fingerprint or not has_prepared
                         or cache.get("asr_denoise", "off") != denoise
                         or cache.get("asr_echo_filter", False) != echo_filter)
    options = preparation_options(metadata, config, cache if has_prepared else {})
    needs_preparation |= any(cache.get(key, default) != options[key] for key, default in
                             (("speaker_anchor_mode", "legacy"), ("asr_segmentation", "speaker")))
    if (session / "result.json").exists():
        if needs_preparation:
            raise RuntimeError("原始录音或分段已改变，已保留旧结果，请使用新的录音目录重新处理")
        validate_asr_checkpoint(session, config)
    return fingerprint, denoise, echo_filter, needs_preparation


def preparation_options(metadata: dict, config: dict, cache=None):
    cache = cache or {}
    options = {key: metadata.get(key, cache.get(key, default if cache.get("fingerprint") else config.get(key, default)))
               for key, default in (("speaker_anchor_mode", "legacy"), ("asr_segmentation", "speaker"))}
    if options["speaker_anchor_mode"] not in ("legacy", "clean") or options["asr_segmentation"] not in ("speaker", "speech"):
        raise ValueError("会议的声纹锚点或转写分段设置无效")
    return options


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
    if (session / "result.json").exists():
        # Reject incompatible checkpoints before replacing either public file.
        # The ordinary processing error handler renders partial results, which
        # would pair old text with changed audio and overwrite the existing MD.
        try:
            preparation_state(session, metadata, config)
        except Exception as error:
            status(session, "输入检查未通过，录音和现有成品已保留", state="error",
                   stage="input_validation", error=str(error), note_path=str(note))
            print(error, file=sys.stderr)
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
        save_session_note(session, note, render_note(session, vault, metadata, mixed_audio=mixed_audio,
                                   merge_error=merge_error))
        with processing_slot(session):
            fingerprint, denoise, echo_filter, needs_preparation = preparation_state(session, metadata, config)
            options = preparation_options(metadata, config, read_json(session / "prepared_input.json", {}))
            context = transcription_context(config, metadata)
            if needs_preparation:
                status(session, "正在分析麦克风和电脑声音，区分说话人", stage="speakers")
                run_stage(session, [sys.executable, str(HOME / "prepare_audio.py"),
                          "--session", str(session), "--models", str(HOME / "models"),
                          "--asr-denoise", denoise, "--echo-filter", "on" if echo_filter else "off",
                          "--anchor-mode", options["speaker_anchor_mode"],
                          "--asr-segmentation", options["asr_segmentation"]], "prepare")
                atomic_json(session / "prepared_input.json", {"fingerprint": fingerprint,
                            "asr_denoise": denoise, "asr_echo_filter": echo_filter, **options})
            if transcription_mode == "api":
                status(session, "正在通过API进行中文会议转写", stage="asr")
                command = [sys.executable, str(HOME / "api_asr.py"),
                           "--segments", str(session / "segments.json"),
                           "--api-base-url", config["api_base_url"], "--api-model", config["api_model"],
                           "--api-key-env", config.get("api_key_env", "TINGJI_API_KEY"),
                           "--api-timeout-seconds", str(config.get("api_timeout_seconds", 120)),
                           "--status", str(session / "asr_status.json"),
                           "--context", context]
            elif transcription_mode == "local":
                status(session, "正在加载中文会议识别模型", stage="asr")
                command = [sys.executable, str(HOME / "asr_engine.py"),
                           "--segments", str(session / "segments.json"),
                           "--model-dir", str(HOME / "models" / "Qwen3-ASR-1.7B-hf"),
                           "--status", str(session / "asr_status.json"),
                           "--context", context]
            else:
                raise ValueError("transcription_mode must be local or api")
            run_stage(session, command, "asr")
            result = read_json(session / "result.json")
            if not result.get("complete"):
                raise RuntimeError("转写结果不完整，已保留完成段")
            save_session_note(session, note, render_note(session, vault, metadata,
                                       result=transcript_with_evidence(session, result),
                                       mixed_audio=mixed_audio, merge_error=merge_error))
            summary = read_json(session / "prepare_summary.json", {})
            count = sum(bool(str(row.get("text", "")).strip()) for row in result.get("segments", []))
            message = f"已完成中文转写，共 {count} 段" if count else "录音已保存，未检测到讲话"
            if merge_error:
                message += "；合并录音失败，原始分轨已保留"
            failure = capture_failure(metadata)
            if failure:
                message += "；采集异常，请检查录音；原始资料已保留"
            status(session, message,
                   state="completed", note_path=str(note), segments=count,
                   sources=summary.get("sources", {}), merged_audio=mixed_audio, merge_error=merge_error,
                   capture_error=failure, capture_needs_review=bool(failure),
                   result_complete=True, artifacts_compacted=False)
            finish_compaction(session, config, metadata, read_json(session / "status.json"))
            return 0
    except Exception as error:
        result = read_json(session / "result.json", result)
        try:
            save_session_note(session, note, render_note(session, vault, metadata,
                                       result=transcript_with_evidence(session, result), error=str(error),
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
