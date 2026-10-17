"""verbit：AI 安装、自检与明确授权的首次使用验收。"""
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
import time
import uuid
import wave

from tingji_config import load_config, ordinary_path

HOME = Path(__file__).resolve().parent
COMMON_PACKAGES = {"numpy": "numpy", "scipy": "scipy", "soundfile": "soundfile", "sherpa-onnx": "sherpa_onnx",
                   "imageio-ffmpeg": "imageio_ffmpeg"}
LOCAL_PACKAGES = {"torch": "torch", "transformers": "transformers", "accelerate": "accelerate", "jinja2": "jinja2"}


def runtime_import_advice(error):
    detail = str(error).lower()
    if getattr(error, "winerror", None) in (126, 127, 193, 1114, 14001) or any(
            token in detail for token in (".dll", "dll load", "winerror 126", "winerror 1114")):
        return ("本地运行库加载失败；先从 Microsoft 官方安装 Visual C++ v14 Redistributable x64，"
                "若提示重启请先重启，再重跑 doctor；仍失败时核对 Python 3.12 x64 与固定依赖版本。"
                "不要从 DLL 下载站复制文件；详见 documentation/install-windows.md")
    return "未安装或导入失败；请按当前模式运行 Install.ps1，不要改全局 Python"


def doctor(full=False, check_devices=True):
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
        add("api_key", key_exists, "密钥环境变量已设置（不显示值）" if key_exists else "请设置环境变量 " + config["api_key_env"] + "，再重启 verbit")
        add("api_transport", config["api_base_url"].startswith("https://"),
            "API 使用 HTTPS" if config["api_base_url"].startswith("https://") else "API 使用 HTTP；适用于你信任的本机或内网服务", warning=True)
    packages = {**COMMON_PACKAGES, **(LOCAL_PACKAGES if mode == "local" else {})}
    for package, module in packages.items():
        try:
            version = importlib.metadata.version(package)
            if full:
                importlib.import_module(module)
            add("package:" + package, True, version + ("，导入成功" if full else "，已安装"))
        except Exception as error:
            add("package:" + package, False, runtime_import_advice(error))
    if full:
        try:
            from mp3_audio import encoder_path
            executable = encoder_path()
            probe = subprocess.run([str(executable), "-hide_banner", "-encoders"],
                                   capture_output=True, text=True, timeout=15,
                                   creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            add("mp3_encoder", probe.returncode == 0 and "libmp3lame" in probe.stdout,
                "本地 MP3 编码器可用，不上传音频" if probe.returncode == 0 and "libmp3lame" in probe.stdout
                else "MP3 编码器不可用；请按当前模式运行 Install.ps1")
        except Exception:
            add("mp3_encoder", False, "MP3 编码器缺失或无法执行；请按当前模式运行 Install.ps1")
    if full and mode == "local":
        try:
            import torch
            from transformers import AutoModelForMultimodalLM, AutoProcessor  # noqa: F401
            cuda = torch.cuda.is_available()
            detail = "CUDA 可用" if cuda else "使用 CPU；转写速度可能较慢"
            if cuda and not torch.cuda.is_bf16_supported():
                detail += "；GPU 不支持 BF16，本地识别可能回退 CPU"
            add("asr_runtime", True, detail)
        except Exception as error:
            add("asr_runtime", False, runtime_import_advice(error))
    for name in ("ObsidianMeeting.exe", "NAudio.dll", "meeting_worker.py", "prepare_audio.py", "merge_audio.py", "audio_sources.py", "mp3_audio.py",
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
    if full and check_devices:
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
    if full and not check_devices:
        add("native_audio_skipped", True, "已显式跳过声卡检查；只验运行环境，不代表真实录音可用", warning=True)
    return {"ok": ok, "mode": mode, "checks": checks, "audio_recorded": False, "api_called": False,
            "devices_checked": bool(full and check_devices),
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


# The bridge runs beside a private copy of the production executable. Its
# AppDomain base directory therefore isolates RecordingSession's work directory,
# configuration and logs without adding test branches to the recorder itself.
VERIFY_BRIDGE = r'''using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
class VerbitFirstUse {
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    [STAThread]
    static int Main(string[] args) {
        string home = AppDomain.CurrentDomain.BaseDirectory;
        string report = Path.Combine(home, "capture-report.json");
        object session = null; SoundPlayer player = null;
        bool failed = false, played = false; string path = "";
        try {
            if (args.Length != 1) return 2;
            var spec = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Encoding.UTF8));
            var config = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(home, "config.json"), Encoding.UTF8));
            var assembly = Assembly.LoadFrom(Path.Combine(home, "ObsidianMeeting.exe"));
            var settings = assembly.GetType("MeetingConfig", true);
            config = (Dictionary<string, object>)settings.GetMethod("Validate").Invoke(null, new object[] {config});
            var type = assembly.GetType("RecordingSession", true);
            string sample = Convert.ToString(spec["play_test"]);
            if (sample.Length != 0) { player = new SoundPlayer(sample); player.Load(); }
            session = Activator.CreateInstance(type, new object[] {config, new Action<Exception>(delegate {failed = true;})});
            path = (string)type.GetField("DirectoryPath").GetValue(session);
            File.WriteAllText(report, Json.Serialize(new {state="recording", session=path, pid=Process.GetCurrentProcess().Id}), new UTF8Encoding(false));
            var clock = Stopwatch.StartNew();
            int seconds = Convert.ToInt32(spec["seconds"]);
            while (clock.Elapsed.TotalSeconds < seconds && !File.Exists(Path.Combine(home, "cancel")) && !failed) {
                if (player != null && !played && clock.Elapsed.TotalMilliseconds >= 500) { player.Play(); played = true; }
                Thread.Sleep(50);
            }
            if (player != null) player.Stop();
            var stopped = new ManualResetEvent(false);
            type.GetMethod("StopAsync").Invoke(session, new object[] {new Action<Exception>(delegate(Exception error) {if (error != null) failed = true; stopped.Set();})});
            if (!stopped.WaitOne(30000)) return 3;
            stopped.Dispose();
            ((IDisposable)session).Dispose(); session = null;
            File.WriteAllText(report, Json.Serialize(new {state=failed?"capture_error":"recorded", session=path, playback_requested=played}), new UTF8Encoding(false));
            return failed ? 2 : 0;
        } catch {
            File.WriteAllText(report, Json.Serialize(new {state="error", session=path, message="Isolated capture failed; raw files were retained."}), new UTF8Encoding(false));
            return 2;
        } finally { if (player != null) player.Dispose(); if (session != null) try {((IDisposable)session).Dispose();} catch {} }
    }
}'''


def verify_plan(*, transcribe=False, play_test=None):
    """Describe effects without creating files, querying devices or loading models."""
    return {"ok": True, "product": "verbit", "plan_only": True,
            "audio_recorded": False, "api_called": False,
            "stages": ["隔离目录采集麦克风和电脑声音", "检查来源、分段和信号能量", "合并并完整校验 MP3"]
                      + (["通过当前配置进行真实转写，生成 Markdown"] if transcribe else []),
            "playback_requested": bool(play_test),
            "output": "程序目录/validation/first-use-<随机编号>/",
            "consent": "必须显式 --consent-record；播放另需 --consent-playback；API 转写另需 --consent-upload",
            "limits": ["不会修改用户笔记、剪贴板、Windows 音量或默认设备",
                       "信号能量不能证明录音内容完整、时间线准确或识别准确；必须人工回放核对"]}


def _verify_flags(args, config):
    if not args.consent_record:
        raise ValueError("真实采集必须显式提供 --consent-record；查看方案请使用 verify --plan")
    if not 10 <= args.seconds <= 60:
        raise ValueError("首次验收时长必须为 10–60 秒；长会议验收按 release-readiness.md 单独安排")
    if not 30 <= args.timeout_seconds <= 3600:
        raise ValueError("转写阶段超时必须为 30–3600 秒")
    if args.play_test and not args.consent_playback:
        raise ValueError("播放指定测试 WAV 必须显式提供 --consent-playback")
    if args.transcribe and config["transcription_mode"] == "api" and not args.consent_upload:
        raise ValueError("API 验收会上传测试录音，必须另行提供 --consent-upload")
    if args.transcribe and config["transcription_mode"] == "api" and not os.environ.get(config["api_key_env"], "").strip():
        raise ValueError("API 密钥环境变量未设置；不会显示或保存密钥")
    if args.play_test:
        sample = ordinary_path(Path(args.play_test))
        if not sample.is_file() or sample.stat().st_size > 32 * 1024 * 1024:
            raise ValueError("测试音必须是小于 32 MiB 的普通 PCM WAV 文件")
        with wave.open(str(sample), "rb") as audio:
            if (audio.getcomptype() != "NONE" or audio.getsampwidth() not in (1, 2, 3, 4)
                    or not 1 <= audio.getnchannels() <= 2 or audio.getframerate() <= 0
                    or not 0 < audio.getnframes() / audio.getframerate() <= args.seconds - 1):
                raise ValueError("测试音必须是单/双声道 PCM WAV，长度至少比采集时长短 1 秒")


def _verify_capture(directory, config, args):
    """Only this explicitly invoked path opens audio devices or plays a sample."""
    runtime = directory / "runtime"
    runtime.mkdir()
    for name in ("ObsidianMeeting.exe", "ObsidianMeeting.exe.config", "NAudio.dll", "config.schema.json"):
        source = ordinary_path(HOME / name)
        if not source.is_file():
            raise ValueError("原生采集文件缺失，请先完成 Build.ps1：" + name)
        shutil.copyfile(source, runtime / name)
    # Keep the selected mode/devices but never publish into the user's note tree.
    notes = directory / "output"
    notes.mkdir()
    private_config = {**config, "vault_root": str(notes), "open_in_obsidian": False,
                      "auto_open_completed_note": False, "recording_segment_seconds": 10}
    (runtime / "config.json").write_text(json.dumps(private_config, ensure_ascii=False), encoding="utf-8")
    sample = ""
    if args.play_test:
        destination = runtime / "test-input.wav"
        shutil.copyfile(args.play_test, destination)
        sample = str(destination)
    spec = runtime / "verify.json"
    spec.write_text(json.dumps({"seconds": args.seconds, "play_test": sample}), encoding="utf-8")
    source = runtime / "FirstUse.cs"
    source.write_text(VERIFY_BRIDGE, encoding="utf-8-sig")
    compiler = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    executable = runtime / "VerbitFirstUse.exe"
    compilation = subprocess.run([str(compiler), "/nologo", "/target:exe", "/reference:System.Web.Extensions.dll",
                                  "/out:" + str(executable), str(source)],
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30,
                                 creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if compilation.returncode:
        raise ValueError("隔离采集桥编译失败，请检查 .NET Framework 4.8 和 C# 编译器")
    shutil.copyfile(runtime / "ObsidianMeeting.exe.config", Path(str(executable) + ".config"))
    process = subprocess.Popen([str(executable), str(spec)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    try:
        process.wait(timeout=args.seconds + 45)
    except (KeyboardInterrupt, subprocess.TimeoutExpired):
        (runtime / "cancel").touch()
        try:
            process.wait(timeout=35)
        except subprocess.TimeoutExpired:
            process.kill()  # This isolated child only; never the installed controller.
            process.wait(timeout=10)
        raise
    report = json.loads((runtime / "capture-report.json").read_text(encoding="utf-8-sig"))
    session = ordinary_path(Path(report.get("session", "")))
    if session.parent != runtime / "work" or not session.is_dir():
        raise ValueError("隔离采集没有返回有效会话；不会读取正式会议")
    if process.returncode or report.get("state") != "recorded":
        raise ValueError("隔离采集报告异常，原始资料已保留；请检查验收目录")
    return session


def _verify_signals(session, metadata):
    import numpy as np
    from audio_sources import capture_paths, capture_sources, open_source
    rows = []
    for entry in capture_sources(session, metadata):
        peak, square, samples = 0.0, 0.0, 0
        with open_source(session, entry) as audio:
            while True:
                block = audio.read(65536, dtype="float32", always_2d=True)
                if not len(block):
                    break
                if not np.isfinite(block).all():
                    raise ValueError("录音包含无效样本")
                peak = max(peak, float(np.max(np.abs(block))))
                square += float(np.sum(block.astype(np.float64) ** 2))
                samples += block.size
            frames, rate = audio.frames, audio.samplerate
        rms = (square / samples) ** .5 if samples else 0.0
        rows.append({"source": entry["source"], "kind": entry["kind"], "frames": frames,
                     "sample_rate": rate, "duration_seconds": frames / rate,
                     "segments": len(capture_paths(session, entry)), "peak": peak, "rms": rms,
                     "signal_detected": peak >= .0005 and rms >= .0001})
    return rows


def _verify_transcribe(session, config, args, api_started):
    commands = [[sys.executable, str(HOME / "prepare_audio.py"), "--session", str(session),
                 "--models", str(HOME / "models"), "--asr-denoise", config["asr_denoise"],
                 "--echo-filter", "on" if config["asr_echo_filter"] else "off"]]
    common = ["--segments", str(session / "segments.json"), "--context", config["asr_context"],
              "--output", str(session / "result.json"), "--status", str(session / "asr_status.json")]
    if config["transcription_mode"] == "api":
        commands.append([sys.executable, str(HOME / "api_asr.py"), *common,
                         "--api-base-url", config["api_base_url"], "--api-model", config["api_model"],
                         "--api-key-env", config["api_key_env"],
                         "--api-timeout-seconds", str(config["api_timeout_seconds"])])
    else:
        commands.append([sys.executable, str(HOME / "asr_engine.py"), *common,
                         "--model-dir", str(HOME / "models/Qwen3-ASR-1.7B-hf")])
    for index, command in enumerate(commands):
        if index == 1 and config["transcription_mode"] == "api":
            api_started()
        # Do not put provider error bodies, prompts or speech in the safe report.
        process = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                 timeout=args.timeout_seconds, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        if process.returncode:
            raise ValueError("说话人准备失败，原始资料已保留" if index == 0 else "转写失败，已完成分段已保留；请检查环境和模型/API 配置")
    return json.loads((session / "result.json").read_text(encoding="utf-8-sig"))


def verify(args):
    """Run a real, opt-in recording without publishing into user note folders."""
    if args.plan:
        return verify_plan(transcribe=args.transcribe, play_test=args.play_test)
    if not args.consent_record:
        raise ValueError("真实采集必须显式提供 --consent-record；查看方案请使用 verify --plan")
    config = load_config(HOME / "config.json")
    _verify_flags(args, config)
    if status().get("state") in ("recording", "paused", "processing", "queued"):
        raise ValueError("最近会议仍在录音或转写；请完成后再单独验收，避免测试音混入会议")
    directory = ordinary_path(HOME / "validation") / ("first-use-" + uuid.uuid4().hex)
    directory.mkdir(parents=True)
    started = time.monotonic()
    report = {"ok": False, "product": "verbit", "plan_only": False,
              "scope": "recording_and_transcription" if args.transcribe else "recording_only",
              "recording_attempted": False, "audio_recorded": False, "api_called": False,
              "playback_requested": bool(args.play_test),
              "listening_review": "required", "speaker_quality_verified": False,
              "timeline_quality_verified": False, "checks": [], "report_path": str(directory / "report.json")}
    try:
        report["stage"] = "recording"
        report["recording_attempted"] = True
        report["audio_recorded"] = None  # A failed start can still leave partial audio.
        session = _verify_capture(directory, config, args)
        report["audio_recorded"] = True
        report["stage"] = "signal_check"
        metadata = json.loads((session / "session.json").read_text(encoding="utf-8-sig"))
        if metadata.get("state") != "recorded" or metadata.get("capture_error"):
            raise ValueError("采集存在警告；请回听原始分段，不能作为通过的首次验收")
        rows = _verify_signals(session, metadata)
        report["sources"] = rows
        for kind, label in (("microphone", "麦克风"), ("loopback", "电脑声音")):
            detected = any(row["kind"] == kind and row["signal_detected"] for row in rows)
            report["checks"].append({"id": kind, "ok": detected,
                                     "message": label + ("存在可检测信号；仍需人工回放确认内容" if detected
                                                          else "未检测到足够信号；请检查设备和原始分段，再重新验收")})
        from merge_audio import merge_session
        from mp3_audio import encode_mp3
        report["stage"] = "mix_and_mp3"
        details = merge_session(session, metadata)
        output = directory / "output"
        output.mkdir(exist_ok=True)
        audio = output / "first-use.mp3"
        report["mp3"] = encode_mp3(session / "meeting.wav", audio, frames=details["frames"],
                                  bitrate_kbps=config["mp3_bitrate_kbps"])
        report["audio_path"] = str(audio)
        report["checks"].append({"id": "mp3", "ok": report["mp3"]["complete"],
                                 "message": "MP3 已完整解码并核对无损混音长度；不等于真实双路对齐已验收"})
        if args.transcribe:
            report["stage"] = "transcription"
            result = _verify_transcribe(session, config, args, lambda: report.update(api_called=True))
            from meeting_worker import clean_transcript_text, timestamp, transcript_rows, transcript_with_evidence
            transcript = transcript_rows(transcript_with_evidence(session, result))
            complete = bool(result.get("complete")) and bool(transcript)
            report["checks"].append({"id": "transcription", "ok": complete,
                                     "message": "实际转写返回非空完整正文；准确率及说话人归属需人工对照"})
            note = output / "first-use.md"
            text = "# verbit 首次使用验收\n\n[测试录音](first-use.mp3)\n\n"
            text += "\n\n".join(f"[{timestamp(row['start'])}] {row['speaker']}：{clean_transcript_text(row['text'])}" for row in transcript)
            note.write_text(text + "\n", encoding="utf-8")
            report["note_path"] = str(note)
        report["ok"] = all(row["ok"] for row in report["checks"])
        report["stage"] = "finished"
    except KeyboardInterrupt:
        report["error"] = "验收已取消；隔离目录中的已录内容已保留"
    except Exception:
        report["error"] = "验收未通过；请检查隔离目录中的来源及阶段文件，再按 first-use.md 排查；未修改正式会议"
    report["elapsed_seconds"] = round(time.monotonic() - started, 3)
    report["note"] = "通过只代表本轮所列技术检查。必须回听、核对文字和说话人；短录音不能证明长会议稳定性。"
    (directory / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return report


def main():
    # The public CLI emits UTF-8 even when redirected on an English Windows host.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    health = commands.add_parser("doctor", help="离线自检；不录音、不调用 API")
    health.add_argument("--full", action="store_true", help="实际导入依赖并检查原生音频端点；不加载模型")
    health.add_argument("--skip-devices", action="store_true", help="无声卡沙箱中显式跳过端点检查；不代表采集可用")
    health.add_argument("--json", action="store_true")
    recent = commands.add_parser("status", help="只读查看最近会议状态")
    recent.add_argument("--json", action="store_true")
    first = commands.add_parser("verify", help="明确授权的首次实际录音验收；结果独立保存，不写正式笔记")
    first.add_argument("--plan", action="store_true", help="只列方案；不录音、不播放、不调用 API、不写文件")
    first.add_argument("--seconds", type=int, default=20, help="采集 10–60 秒，默认 20 秒")
    first.add_argument("--consent-record", action="store_true", help="用户已明确同意录制测试期间的麦克风和电脑声")
    first.add_argument("--play-test", metavar="PCM_WAV", help="用户选定的已知中文 PCM WAV；不会自动下载测试音")
    first.add_argument("--consent-playback", action="store_true", help="用户已明确同意播放指定测试 WAV")
    first.add_argument("--transcribe", action="store_true", help="实际加载本地模型或使用当前配置的 API 进行转写")
    first.add_argument("--consent-upload", action="store_true", help="用户已明确同意将本次测试录音上传到配置的转写 API")
    first.add_argument("--timeout-seconds", type=int, default=600, help="每个准备/转写子阶段的超时，30–3600 秒")
    first.add_argument("--json", action="store_true")
    args = parser.parse_args()
    try:
        report = doctor(args.full, not args.skip_devices) if args.command == "doctor" else verify(args) if args.command == "verify" else status()
    except ValueError as error:
        report = {"ok": False, "error": str(error)}
    except Exception:
        report = {"ok": False, "error": "自检/状态读取失败，请检查本机文件权限和 JSON；没有修改会议数据"}
    if args.json:
        print(json.dumps(report, ensure_ascii=False))
    elif args.command == "verify":
        print(json.dumps(report, ensure_ascii=False, indent=2))
    elif "checks" in report:
        for row in report["checks"]:
            mark = "OK" if row["ok"] else "WARN" if row["level"] == "warning" else "FAIL"
            print(f"[{mark}] {row['id']}: {row['message']}")
    else:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["ok"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
