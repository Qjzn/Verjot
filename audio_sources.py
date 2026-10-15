"""Read a capture manifest without guessing files or crossing the session boundary."""
from __future__ import annotations

from pathlib import Path
import re

from tingji_config import ordinary_path


SOURCE_NAME = re.compile(r"(?:mic|system)(?:_\d{2,})?\Z")


def source_kind(source: str) -> str:
    if not isinstance(source, str) or not SOURCE_NAME.fullmatch(source):
        raise ValueError("无效的录音来源编号")
    return "microphone" if source == "mic" or source.startswith("mic_") else "loopback"


def capture_sources(session: Path, metadata: dict, *, require_files: bool = True) -> list[dict]:
    """Legacy sessions use existing mic/system WAVs; manifests are authoritative.

    Only numbered capture basenames are accepted, never arbitrary paths or the
    published meeting.wav. A declared missing track is an error during processing,
    so a partial multi-device recording cannot silently become a complete result.
    """
    session = ordinary_path(Path(session))
    if "sources" not in metadata:
        entries = [{"source": key, "path": key + ".wav", "kind": source_kind(key)}
                   for key in ("mic", "system") if (session / (key + ".wav")).exists()]
    else:
        entries = metadata["sources"]
        if not isinstance(entries, list) or not entries:
            raise ValueError("录音来源 sources 必须是非空列表")
    result, seen = [], set()
    for entry in entries:
        if not isinstance(entry, dict):
            raise ValueError("录音来源条目必须是对象")
        key = entry.get("source")
        kind = source_kind(key)
        if entry.get("kind") != kind or entry.get("path") != key + ".wav" or key in seen:
            raise ValueError(f"录音来源重复、类型不符或文件名不安全：{key}")
        seen.add(key)
        path = ordinary_path(session / entry["path"])
        if path.exists() and not path.is_file():
            raise ValueError(f"录音来源不是普通文件：{entry['path']}")
        if require_files and not path.is_file():
            raise ValueError(f"录音来源文件缺失：{entry['path']}")
        result.append(dict(entry))
    return result
