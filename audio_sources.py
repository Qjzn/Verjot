"""Read a capture manifest without guessing files or crossing the session boundary."""
from __future__ import annotations

from pathlib import Path
from bisect import bisect_right
import re

from tingji_config import ordinary_path


SOURCE_NAME = re.compile(r"(?:mic|system)(?:_\d{2,})?\Z")


def source_kind(source: str) -> str:
    if not isinstance(source, str) or not SOURCE_NAME.fullmatch(source):
        raise ValueError("无效的录音来源编号")
    return "microphone" if source == "mic" or source.startswith("mic_") else "loopback"


def capture_paths(session: Path, entry: dict) -> list[Path]:
    """Return only owned capture basenames, never the logical segmented path."""
    key = entry.get("source")
    source_kind(key)
    if entry.get("path") != key + ".wav":
        raise ValueError(f"录音来源文件名不安全：{key}")
    parts = entry.get("segments")
    if "segments" not in entry:
        names = [entry["path"]]
    else:
        if not isinstance(parts, list) or not parts:
            raise ValueError(f"录音分段必须是非空列表：{key}")
        for field in ("sample_rate", "channels", "bits_per_sample"):
            if type(entry.get(field)) is not int or entry[field] <= 0:
                raise ValueError(f"录音分段格式无效：{key}/{field}")
        names, next_frame = [], 0
        for index, part in enumerate(parts, 1):
            if (not isinstance(part, dict) or part.get("path") != f"{key}.seg{index:06d}.wav"
                    or type(part.get("start_frame")) is not int
                    or type(part.get("frames")) is not int
                    or part["start_frame"] != next_frame or part["frames"] < 0):
                raise ValueError(f"录音分段文件名或连续时间线无效：{key}")
            names.append(part["path"])
            next_frame += part["frames"]
    return [ordinary_path(Path(session) / name) for name in names]


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
        for path in capture_paths(session, entry):
            if path.exists() and not path.is_file():
                raise ValueError(f"录音来源不是普通文件：{path.name}")
            if require_files and not path.is_file():
                raise ValueError(f"录音来源文件缺失：{path.name}")
        result.append(dict(entry))
    return result


class SegmentedAudio:
    """A seekable continuous source, with at most one segment handle open.

    Capture boundaries never reset resampling phase or diarization identity.
    Headers are checked up front so a shortened or mismatched part cannot turn
    into a silently shortened complete meeting.
    """
    def __init__(self, session: Path, entry: dict):
        import soundfile as sf
        self.paths = capture_paths(session, entry)
        self.parts = entry["segments"]
        self.name = str(Path(session) / entry["path"])
        self.samplerate, self.channels = entry["sample_rate"], entry["channels"]
        self.frames = sum(part["frames"] for part in self.parts)
        self.starts = [part["start_frame"] for part in self.parts]
        self.position, self.audio, self.part_index = 0, None, None
        widths = {"PCM_U8": 8, "PCM_16": 16, "PCM_24": 24, "PCM_32": 32,
                  "FLOAT": 32, "DOUBLE": 64}
        subtype = None
        for path, part in zip(self.paths, self.parts):
            info = sf.info(path)
            if (info.samplerate != self.samplerate or info.channels != self.channels
                    or info.frames != part["frames"] or widths.get(info.subtype) != entry["bits_per_sample"]
                    or (subtype is not None and info.subtype != subtype)):
                raise ValueError(f"录音分段头或长度与清单不符：{path.name}")
            subtype = info.subtype

    def __enter__(self):
        return self

    def __exit__(self, *unused):
        self.close()

    def close(self):
        if self.audio is not None:
            self.audio.close()
            self.audio = None
            self.part_index = None

    def seek(self, frames: int, whence: int = 0) -> int:
        position = frames + (self.position if whence == 1 else self.frames if whence == 2 else 0)
        if whence not in (0, 1, 2) or not 0 <= position <= self.frames:
            raise ValueError("录音读取位置超出连续时间线")
        self.position = position
        return position

    def read(self, frames: int = -1, dtype="float64", always_2d=False):
        import numpy as np
        import soundfile as sf
        count = self.frames - self.position if frames < 0 else min(frames, self.frames - self.position)
        output = np.empty((count, self.channels), dtype=dtype)
        written = 0
        while written < count:
            index = bisect_right(self.starts, self.position) - 1
            if self.part_index != index:
                self.close()
                self.audio = sf.SoundFile(self.paths[index])
                self.part_index = index
            offset = self.position - self.starts[index]
            self.audio.seek(offset)
            requested = min(count - written, self.parts[index]["frames"] - offset)
            samples = self.audio.read(requested, dtype=dtype, always_2d=True)
            if len(samples) != requested or requested <= 0:
                raise ValueError(f"录音分段读取不完整：{self.paths[index].name}")
            output[written:written + requested] = samples
            self.position += requested
            written += requested
        return output if always_2d or self.channels != 1 else output[:, 0]


def open_source(session: Path, entry: dict):
    """Open either a legacy WAV or the manifest's virtual continuous source."""
    if "segments" in entry:
        return SegmentedAudio(session, entry)
    import soundfile as sf
    return sf.SoundFile(capture_paths(session, entry)[0])
