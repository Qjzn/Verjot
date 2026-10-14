"""Small offline management commands for AI-assisted installation and support."""
from __future__ import annotations

import argparse
import importlib
import importlib.metadata
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import uuid

from tingji_config import load_config, ordinary_path

HOME = Path(__file__).resolve().parent
COMMON_PACKAGES = {"numpy": "numpy", "scipy": "scipy", "soundfile": "soundfile", "sherpa-onnx": "sherpa_onnx"}
LOCAL_PACKAGES = {"torch": "torch", "transformers": "transformers", "accelerate": "accelerate", "jinja2": "jinja2"}


def doctor(full=False):
    checks = []

    def add(name, ok, message, *, warning=False):
        checks.append({"id": name, "ok": bool(ok), "level": "warning" if warning else "check", "message": message})

    add("platform", os.name == "nt", "需要 Windows 10/11")
    add("python", sys.version_info[:2] == (3, 12) and struct.calcsize("P") == 8,
        "当前 Python " + sys.version.split()[0] + "；需要 Python 3.12 x64")
    try:
        config = load_config(HOME / "config.json")
        add("config", True, "配置字段、类型、路径和快捷键有效")
    except (OSError, ValueError) as error:
        add("config", False, str(error))
        return {"ok": False, "checks": checks, "mode": None, "audio_recorded": False, "api_called": False}

    mode = config["transcription_mode"]
    if mode == "api":
        key_exists = bool(os.environ.get(config["api_key_env"], "").strip())
        add("api_key", key_exists, "密钥环境变量已设置（不显示值）" if key_exists else "请设置环境变量 " + config["api_key_env"] + "，再重启听记")
        add("api_transport", config["api_base_url"].startswith("https://"),
            "API 使用 HTTPS" if config["api_base_url"].startswith("https://") else "API 使用 HTTP；适用于你信任的本机或内网服务", warning=True)
    packages = {**COMMON_PACKAGES, **(LOCAL_PACKAGES if mode == "local" else {})}
    for package, module in packages.items():
        try:
            version = importlib.metadata.version(package)
            if full:
                importlib.import_module(module)
            add("package:" + package, True, version + ("，导入成功" if full else "，已安装"))
        except Exception:
            add("package:" + package, False, "未安装或导入失败；请按当前模式运行 Install.ps1，不要改全局 Python")
    if full and mode == "local":
        try:
            import torch
            from transformers import AutoModelForMultimodalLM, AutoProcessor  # noqa: F401
            cuda = torch.cuda.is_available()
            detail = "CUDA 可用" if cuda else "使用 CPU；转写速度可能较慢"
            if cuda and not torch.cuda.is_bf16_supported():
                detail += "；GPU 不支持 BF16，本地识别可能回退 CPU"
            add("asr_runtime", True, detail)
        except Exception:
            add("asr_runtime", False, "本地模型接口导入失败，请恢复 requirements.txt 中的运行库版本")
    for name in ("ObsidianMeeting.exe", "NAudio.dll", "meeting_worker.py", "prepare_audio.py", "merge_audio.py",
                 "api_asr.py" if mode == "api" else "asr_engine.py"):
        add("file:" + name, (HOME / name).is_file(), "已就绪" if (HOME / name).is_file() else "文件缺失，请运行安装/构建脚本")
    models = HOME / "models"
    required_models = [models / "sherpa-onnx-pyannote-segmentation-3-0" / "model.onnx",
                       models / "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx"]
    if mode == "local":
        asr = models / "Qwen3-ASR-1.7B-hf"
        required_models.extend(asr / name for name in ("config.json", "processor_config.json", "tokenizer.json", "tokenizer_config.json", "model.safetensors"))
    for path in required_models:
        ready = path.is_file() and path.stat().st_size > 0
        add("model:" + path.relative_to(models).as_posix(), ready, "模型文件存在" if ready else "模型缺失；API 用 --speaker-only，本地用完整 download_models.py")
    for label, directory in (("程序目录", HOME), ("保存目录", Path(config["vault_root"]))):
        probe = directory / (".tingji-write-" + uuid.uuid4().hex + ".tmp")
        created = False
        try:
            with probe.open("xb") as stream:
                created = True
                stream.write(b"tingji-write-check")
                stream.flush()
            add("writable:" + label, True, "可写")
        except OSError:
            add("writable:" + label, False, "不可写；请使用当前用户有写权限的普通目录")
        finally:
            if created:
                probe.unlink()
        free = shutil.disk_usage(directory).free
        add("disk:" + label, free >= 1024 ** 3, f"剩余 {free / 1024**3:.1f} GiB；录音和转写需要可用磁盘空间", warning=True)
    if full:
        try:
            with tempfile.TemporaryDirectory(prefix="tingji-diagnose-", dir=HOME) as directory:
                report_path = Path(directory) / "devices.json"
                process = subprocess.run([str(HOME / "ObsidianMeeting.exe"), "--diagnose", str(report_path)],
                                         capture_output=True, timeout=20, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                report = json.loads(report_path.read_text(encoding="utf-8-sig"))
                for check in report.get("checks", []):
                    add("native:" + check["id"], check["ok"], check["message"])
                if process.returncode or not report.get("ok"):
                    add("native_audio", False, "原生设备诊断失败，未录制音频")
        except Exception:
            add("native_audio", False, "无法完成原生设备诊断；请构建完整新版工具后再检查")
    ok = all(row["ok"] for row in checks if row["level"] != "warning")
    return {"ok": ok, "mode": mode, "checks": checks, "audio_recorded": False, "api_called": False,
            "note": "文件存在不代表模型已成功推理；新机器仍需经用户同意完成一次真实录音和转写验证"}


def status():
    pointer = HOME / "last-session.json"
    if not pointer.is_file():
        return {"ok": True, "state": "idle", "session": None}
    saved = json.loads(pointer.read_text(encoding="utf-8-sig"))
    session = ordinary_path(Path(saved["session_dir"]))
    if session.resolve().parent != (HOME / "work").resolve():
        raise ValueError("最近会议指针不在当前程序的 work 直接子目录内")
    metadata = json.loads((session / "session.json").read_text(encoding="utf-8-sig"))
    progress_path = session / "status.json"
    progress = json.loads(progress_path.read_text(encoding="utf-8-sig")) if progress_path.is_file() else {}
    recording = metadata.get("state") == "recording"
    return {"ok": True, "session": session.name,
            "state": "paused" if recording and metadata.get("recording_paused") else "recording" if recording else progress.get("state", metadata.get("state")),
            "mode": metadata.get("transcription_mode", "local"), "message": progress.get("message", ""),
            "note_path": progress.get("note_path", ""), "microphone_paused": metadata.get("microphone_paused", False),
            "artifacts_compacted": progress.get("artifacts_compacted", False)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    health = commands.add_parser("doctor", help="离线自检；不录音、不调用 API")
    health.add_argument("--full", action="store_true", help="实际导入依赖并检查原生音频端点；不加载模型")
    health.add_argument("--json", action="store_true")
    recent = commands.add_parser("status", help="只读查看最近会议状态")
    recent.add_argument("--json", action="store_true")
    args = parser.parse_args()
    try:
        report = doctor(args.full) if args.command == "doctor" else status()
    except Exception:
        report = {"ok": False, "error": "自检/状态读取失败，请检查本机文件权限和 JSON；没有修改会议数据"}
    if args.json:
        print(json.dumps(report, ensure_ascii=False))
    elif "checks" in report:
        for row in report["checks"]:
            mark = "OK" if row["ok"] else "WARN" if row["level"] == "warning" else "FAIL"
            print(f"[{mark}] {row['id']}: {row['message']}")
    else:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["ok"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
