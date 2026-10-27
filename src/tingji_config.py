"""Shared, dependency-free validation for Tingji's documented configuration."""
from __future__ import annotations

import json
import ntpath
import re
import stat
from pathlib import Path
from urllib.parse import urlsplit

SCHEMA = Path(__file__).with_name("config.schema.json")


def transcription_context(config: dict, metadata: dict | None = None) -> str:
    """Explicit vocabulary, frozen for new sessions; no inferred meeting facts."""
    values = metadata if metadata is not None and "asr_context" in metadata else config
    context, terms = values.get("asr_context", ""), values.get("asr_terms", "")
    if not isinstance(context, str) or not isinstance(terms, str):
        raise ValueError("asr_context 和 asr_terms 必须为字符串")
    vocabulary = list(dict.fromkeys(term.strip() for term in re.split(r"[,，;；\r\n]+", terms) if term.strip()))
    if len(vocabulary) > 64 or any(len(term) > 64 for term in vocabulary):
        raise ValueError("asr_terms 最多64个术语，每项最多64字符")
    prompt = context
    if vocabulary:
        prompt += ("\n" if prompt else "") + "术语：" + "、".join(vocabulary) + "。"
    if len(prompt) > 2000:
        raise ValueError("术语和上下文合计最多2000字符")
    return prompt


def parse_hotkey(value: str) -> tuple[int, int]:
    if not isinstance(value, str) or not re.fullmatch(
        r"(?:(?:Ctrl|Alt|Shift|Win)\+)+(?:[A-Z]|F(?:[1-9]|1[0-9]|2[0-4])|Space)", value
    ):
        raise ValueError("快捷键格式应为 Ctrl+Alt+L、Alt+Space 或 Ctrl+Shift+F9")
    parts = value.split("+")
    modifiers = {"Alt": 1, "Ctrl": 2, "Shift": 4, "Win": 8}
    if len(set(parts[:-1])) != len(parts) - 1:
        raise ValueError("快捷键不能重复修饰键")
    mask = sum(modifiers[item] for item in parts[:-1])
    key = parts[-1]
    return mask, 32 if key == "Space" else (111 + int(key[1:]) if key.startswith("F") else ord(key))


def ordinary_path(path: Path) -> Path:
    path = path.absolute()
    for part in [*reversed(path.parents), path]:
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("保存路径不能经过符号链接或 Windows 重解析目录")
    return path


def validate_config(config: dict, *, check_directory: bool = True) -> dict:
    schema = json.loads(SCHEMA.read_text(encoding="utf-8-sig"))
    if not isinstance(config, dict):
        raise ValueError("config.json 顶层必须是对象")
    properties = schema["properties"]
    unknown = sorted(set(config) - set(properties))
    if unknown:
        raise ValueError("未知配置字段：" + ", ".join(unknown))
    result = dict(config)
    for key in schema["required"]:
        if key not in result:
            raise ValueError(f"缺少必填配置：{key}")
    for key, rule in properties.items():
        if key not in result:
            if "default" in rule:
                result[key] = rule["default"]
            else:
                continue
        value = result[key]
        expected = {"string": str, "boolean": bool, "integer": int}[rule["type"]]
        if type(value) is not expected:
            raise ValueError(f"{key} 必须是 {rule['type']}，不能是 null 或其他类型")
        if isinstance(value, str):
            if len(value) < rule.get("minLength", 0) or len(value) > rule.get("maxLength", len(value)):
                raise ValueError(f"{key} 长度不符合配置说明")
            if "pattern" in rule and not re.fullmatch(rule["pattern"], value):
                raise ValueError(f"{key} 格式不符合配置说明")
        if "enum" in rule and value not in rule["enum"]:
            raise ValueError(f"{key} 必须为：" + ", ".join(map(str, rule["enum"])))
        if type(value) is int and not rule.get("minimum", value) <= value <= rule.get("maximum", value):
            raise ValueError(f"{key} 超出允许范围")
    transcription_context(result)
    drive, _ = ntpath.splitdrive(result["vault_root"])
    if not drive or not ntpath.isabs(result["vault_root"]):
        raise ValueError("vault_root 必须是完整 Windows 绝对目录，如 D:\\Notes\\MyNotes")
    root = ordinary_path(Path(result["vault_root"]))
    if check_directory and not root.is_dir():
        raise ValueError("vault_root 目录不存在；请先创建目录，或运行 Install.ps1 -NotesPath")
    for field in ("meeting_folder", "quick_note_folder"):
        folder = result[field]
        if ntpath.isabs(folder) or ntpath.splitdrive(folder)[0]:
            raise ValueError(f"{field} 必须为保存目录内的相对路径")
        for part in re.split(r"[\\/]", folder):
            if not part or part in (".", "..") or re.search(r'[<>:"|?*\x00-\x1f]', part) or part.endswith((".", " ")):
                raise ValueError(f"{field} 包含空段、路径穿越或 Windows 非法名称")
            if re.fullmatch(r"(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part, re.I):
                raise ValueError(f"{field} 不能使用 Windows 保留设备名称")
        output = ordinary_path(root / folder)
        if output.is_file():
            raise ValueError(f"{field} 已被同名文件占用，请改用目录")
    shortcuts = [parse_hotkey(result[key]) for key in ("hotkey", "pause_hotkey", "bookmark_hotkey", "quick_note_hotkey")]
    if len(set(shortcuts)) != len(shortcuts):
        raise ValueError("开始/结束、暂停、标记重点和速记快捷键必须各不相同")
    if result["transcription_mode"] == "api" and (result["auto_transcribe"] or result["api_base_url"].strip() or result["api_model"].strip()):
        try:
            url = urlsplit(result["api_base_url"])
            url.port
        except ValueError:
            raise ValueError("api_base_url 地址或端口格式不正确") from None
        if (any(character.isspace() for character in result["api_base_url"])
                or url.scheme not in ("http", "https") or not url.hostname
                or url.username is not None or url.password is not None or url.query or url.fragment):
            raise ValueError("api_base_url 必须是无密钥、无查询参数的 HTTP(S) API 前缀")
        if not result["api_model"].strip():
            raise ValueError("API 模式必须填写 api_model 音频转写模型名")
    return result


def load_config(path: Path) -> dict:
    try:
        return validate_config(json.loads(path.read_text(encoding="utf-8-sig")))
    except json.JSONDecodeError as error:
        raise ValueError(f"config.json JSON 错误：第 {error.lineno} 行、第 {error.colno} 列；不支持注释或尾逗号") from None
