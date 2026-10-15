"""Stream a lossless meeting mix to CBR MP3 and independently decode it."""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess

import numpy as np
import soundfile as sf


BITRATES = (128, 192, 256, 320)


def encoder_path():
    override = os.environ.get("TINGJI_FFMPEG_EXE")
    if override:
        path = Path(override)
        if not path.is_file():
            raise ValueError("TINGJI_FFMPEG_EXE 指定的编码器不存在")
        return str(path)
    try:
        import imageio_ffmpeg

        return imageio_ffmpeg.get_ffmpeg_exe()
    except (ImportError, RuntimeError):
        found = shutil.which("ffmpeg")
        if not found:
            raise RuntimeError("缺少 MP3 编码器，请安装 imageio-ffmpeg；原始无损录音已保留")
        return found


def _mpeg_frames(path: Path, bitrate_kbps: int):
    """Reject truncation or variable/wrong bitrate rather than trusting an extension."""
    bitrates = (0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320)
    size, count = path.stat().st_size, 0
    with path.open("rb") as stream:
        header = stream.read(10)
        offset = 0
        if header[:3] == b"ID3":
            if len(header) != 10 or any(byte & 0x80 for byte in header[6:10]):
                raise ValueError("MP3 标签损坏，原始无损录音已保留")
            offset = 10 + sum(byte << shift for byte, shift in zip(header[6:10], (21, 14, 7, 0)))
            if header[3] == 4 and header[5] & 0x10:
                offset += 10
        while offset < size:
            stream.seek(offset)
            raw = stream.read(4)
            if size - offset == 128 and raw[:3] == b"TAG":
                offset = size
                break
            if len(raw) != 4:
                raise ValueError("MP3 尾帧不完整，原始无损录音已保留")
            value = int.from_bytes(raw, "big")
            bitrate_index = (value >> 12) & 15
            if ((value >> 21) != 0x7FF or (value >> 19) & 3 != 3
                    or (value >> 17) & 3 != 1 or not 1 <= bitrate_index <= 14
                    or (value >> 10) & 3 != 1 or (value >> 6) & 3 == 3):
                raise ValueError("MP3 音频帧格式异常，原始无损录音已保留")
            bitrate = bitrates[bitrate_index]
            if bitrate != bitrate_kbps:
                raise ValueError("MP3 实际码率与配置不符，原始无损录音已保留")
            length = (144000 * bitrate // 48000) + ((value >> 9) & 1)
            if offset + length > size:
                raise ValueError("MP3 音频帧被截断，原始无损录音已保留")
            offset += length
            count += 1
    if offset != size or count < 2:
        raise ValueError("MP3 音频帧不完整，原始无损录音已保留")
    return count


def _tag_crc(data):
    """FFmpeg's AV_CRC_16_ANSI_LE, initialized to zero."""
    checksum = 0
    for value in data:
        checksum ^= value
        for _ in range(8):
            checksum = (checksum >> 1) ^ (0xA001 if checksum & 1 else 0)
    return checksum


def _repair_short_tail_padding(path: Path, *, frames: int, bitrate_kbps: int):
    """Correct only our just-encoded FFmpeg short-tail Info/LAME defect.

    FFmpeg 7.1 reports at least 47 samples in its last remainder, even when
    only 1..46 input samples remain. The audio payload is complete; its Info
    trailing-padding count is short by 47-remainder. Do not repair arbitrary
    files, missing audio frames, another layout, or mismatches in the header.
    Reference: FFmpeg n7.1 libavformat/mp3enc.c mp3_update_xing().
    """
    remainder = frames % 1152
    if not 1 <= remainder < 47:
        return
    count = _mpeg_frames(path, bitrate_kbps)
    with path.open("r+b") as stream:
        tag = stream.read(10)
        offset = 0
        if tag[:3] == b"ID3":
            if tag[3:6] != b"\x03\x00\x00" or any(value & 128 for value in tag[6:10]):
                raise ValueError("MP3 标签布局异常，不修正尾填充；已保留无损录音")
            offset = 10 + sum(value << shift for value, shift in zip(tag[6:10], (21, 14, 7, 0)))
        stream.seek(offset)
        header = bytearray(stream.read(192))
        if (len(header) != 192 or not header[1] & 1 or header[3] & 192
                or header[36:40] != b"Info" or int.from_bytes(header[40:44], "big") != 15
                or header[156:160] not in (b"Lavc", b"Lavf")
                or int.from_bytes(header[44:48], "big") != count - 1
                or int.from_bytes(header[48:52], "big") != path.stat().st_size - offset
                or _tag_crc(header[:190]) != int.from_bytes(header[190:192], "big")):
            raise ValueError("MP3 Info 布局不符合本机编码器约定，已保留无损录音")
        packed = int.from_bytes(header[177:180], "big")
        delay, padding = packed >> 12, packed & 4095
        decoded = (count - 1) * 1152 - delay - padding
        if decoded == frames:
            return  # A newer encoder may already provide the correct value.
        adjustment = decoded - frames
        if delay != 576 or padding != 1681 or adjustment != 47 - remainder:
            raise ValueError("MP3 尾填充差异不符合已验证的编码器边界，已保留无损录音")
        header[177:180] = ((delay << 12) | (padding + adjustment)).to_bytes(3, "big")
        header[190:192] = _tag_crc(header[:190]).to_bytes(2, "big")
        stream.seek(offset)
        stream.write(header)
        stream.flush()


def validate_mp3(path: Path, *, frames: int, bitrate_kbps: int = 192):
    if bitrate_kbps not in BITRATES or type(bitrate_kbps) is not int or frames <= 0:
        raise ValueError("MP3 码率或无损录音长度无效")
    count = _mpeg_frames(path, bitrate_kbps)
    try:
        with sf.SoundFile(path) as audio:
            if (audio.format != "MP3" or audio.subtype != "MPEG_LAYER_III"
                    or audio.samplerate != 48000 or audio.channels != 2):
                raise ValueError("MP3 采样率、声道或编码格式不符")
            decoded = 0
            for block in audio.blocks(blocksize=65536, dtype="float32", always_2d=True):
                if not np.isfinite(block).all():
                    raise ValueError("MP3 解码出现无效样本")
                decoded += len(block)
            if not decoded or decoded != audio.frames or abs(decoded - frames) > 1:
                raise ValueError("MP3 解码长度与无损录音不符，原始无损录音已保留")
    except sf.LibsndfileError as error:
        raise ValueError("MP3 无法完整解码，原始无损录音已保留") from error
    return {"format": "mp3", "bitrate_kbps": bitrate_kbps, "sample_rate": 48000,
            "channels": 2, "frames": frames, "decoded_frames": decoded,
            "duration_seconds": frames / 48000, "mpeg_frames": count,
            "output_size": path.stat().st_size, "complete": True}


def encode_mp3(source: Path, output: Path, *, frames: int, bitrate_kbps: int = 192):
    if bitrate_kbps not in BITRATES or type(bitrate_kbps) is not int:
        raise ValueError("MP3 码率必须为 128、192、256 或 320 kbps")
    info = sf.info(source)
    if (type(frames) is not int or info.frames != frames or frames <= 0 or info.samplerate != 48000
            or info.channels != 2 or info.subtype != "PCM_16" or info.format not in ("WAV", "RF64")):
        raise ValueError("MP3 输入与无损混音帧数或格式不符，已保留原始录音")
    command = [encoder_path(), "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
               "-i", str(source), "-map", "0:a:0", "-vn", "-c:a", "libmp3lame",
               "-b:a", f"{bitrate_kbps}k", "-ar", "48000", "-ac", "2",
               "-write_xing", "1", "-id3v2_version", "3", "-f", "mp3", str(output)]
    process = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                             creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if process.returncode:
        detail = process.stderr.decode("utf-8", errors="replace")[-2000:].strip()
        raise RuntimeError(f"MP3 编码失败，原始无损录音已保留：{detail}")
    _repair_short_tail_padding(output, frames=frames, bitrate_kbps=bitrate_kbps)
    return validate_mp3(output, frames=frames, bitrate_kbps=bitrate_kbps)
