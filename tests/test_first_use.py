"""Synthetic first-use checks: no devices, playback, clipboard or live API."""
from argparse import Namespace
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch
import wave

import numpy as np
import soundfile as sf

import tingji
from tingji_config import validate_config


def options():
    return Namespace(plan=False, seconds=20, consent_record=True, play_test=None,
                     consent_playback=False, transcribe=False, consent_upload=False,
                     timeout_seconds=600)


class FirstUseTests(unittest.TestCase):
    def test_native_dependency_failure_has_specific_safe_remedy(self):
        advice = tingji.runtime_import_advice(OSError("[WinError 126] DLL not found"))
        self.assertIn("Microsoft", advice)
        self.assertIn("x64", advice)
        self.assertIn("不要从 DLL 下载站", advice)
        self.assertNotIn("Redistributable", tingji.runtime_import_advice(ModuleNotFoundError("torch")))
        self.assertNotIn("Redistributable", tingji.runtime_import_advice(PermissionError("Access denied")))

    def test_explicit_device_skip_keeps_the_boundary_in_report(self):
        fake_torch = SimpleNamespace(cuda=SimpleNamespace(is_available=lambda: False))
        fake_transformers = SimpleNamespace(AutoModelForMultimodalLM=object, AutoProcessor=object)
        with patch.object(tingji, "load_config", return_value=self.config), \
                patch.object(tingji.importlib.metadata, "version", return_value="fixture"), \
                patch.object(tingji.importlib, "import_module"), \
                patch.dict(sys.modules, torch=fake_torch, transformers=fake_transformers), \
                patch.object(tingji.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "libmp3lame")) as run:
            report = tingji.doctor(full=True, check_devices=False)
        self.assertFalse(report["devices_checked"])
        self.assertTrue(any(row["id"] == "native_audio_skipped" and row["level"] == "warning" for row in report["checks"]))
        self.assertTrue(all("--diagnose" not in call.args[0] for call in run.call_args_list))

    def setUp(self):
        scratch = Path(__file__).resolve().parent / "validation"
        scratch.mkdir(exist_ok=True)
        self.folder = tempfile.TemporaryDirectory(prefix="first-use-test-", dir=scratch)
        self.home = Path(self.folder.name)
        self.config = validate_config({"vault_root": str(self.home), "transcription_mode": "local"}, check_directory=False)
        self.home_patch = patch.object(tingji, "HOME", self.home)
        self.home_patch.start()

    def tearDown(self):
        self.home_patch.stop()
        self.folder.cleanup()

    def args(self, **changes):
        args = options()
        for key, value in changes.items():
            setattr(args, key, value)
        return args

    def sample(self, name="known.wav", seconds=1):
        path = self.home / name
        with wave.open(str(path), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(16000)
            audio.writeframes(b"\0\0" * int(seconds * 16000))
        return path

    def fixture(self, directory, config, args, *, silent=False, segmented=False):
        session = directory / "runtime/work/isolated-session"
        session.mkdir(parents=True)
        rate, frames = 16000, 4000
        samples = np.zeros(frames) if silent else .1 * np.sin(2 * np.pi * 360 * np.arange(frames) / rate)
        sources = []
        for key, kind in (("mic", "microphone"), ("system", "loopback")):
            entry = {"source": key, "path": key + ".wav", "kind": kind}
            if segmented:
                entry.update(sample_rate=rate, channels=1, bits_per_sample=16, segments=[])
                for index in range(2):
                    name = f"{key}.seg{index + 1:06d}.wav"
                    sf.write(session / name, samples[index * 2000:(index + 1) * 2000], rate, subtype="PCM_16")
                    entry["segments"].append({"path": name, "start_frame": index * 2000, "frames": 2000})
            else:
                sf.write(session / entry["path"], samples, rate, subtype="PCM_16")
            sources.append(entry)
        metadata = {"state": "recorded", "duration_seconds": frames / rate, "capture_error": "", "sources": sources}
        (session / "session.json").write_text(json.dumps(metadata), encoding="utf-8")
        return session

    def run_fixture(self, args=None, capture=None, transcribe=None):
        with patch.object(tingji, "load_config", return_value=self.config), \
                patch.object(tingji, "status", return_value={"state": "idle"}), \
                patch.object(tingji, "_verify_capture", side_effect=capture or self.fixture), \
                patch.object(tingji, "_verify_transcribe", side_effect=transcribe):
            return tingji.verify(args or self.args())

    def test_plan_does_not_load_config_create_files_or_start_processes(self):
        with patch.object(tingji, "load_config", side_effect=AssertionError("no config read")), \
                patch.object(tingji.subprocess, "Popen", side_effect=AssertionError("no process")):
            report = tingji.verify(self.args(plan=True, transcribe=True, play_test="not-read.wav"))
        self.assertTrue(report["plan_only"])
        self.assertFalse(report["audio_recorded"])
        self.assertFalse(report["api_called"])
        self.assertEqual(list(self.home.iterdir()), [])

    def test_public_cli_plan_and_missing_consent_have_real_exit_codes(self):
        script = Path(tingji.__file__).resolve()
        for arguments, expected in ((["--plan", "--transcribe", "--json"], 0), (["--json"], 2)):
            with self.subTest(arguments=arguments):
                process = subprocess.run([tingji.sys.executable, "-B", str(script), "verify", *arguments],
                                         capture_output=True, encoding="utf-8", timeout=15,
                                         env={**tingji.os.environ, "PYTHONIOENCODING": "cp1252"},
                                         creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                self.assertEqual(process.returncode, expected, process.stderr)
                report = json.loads(process.stdout)
                self.assertEqual(report["ok"], expected == 0)

    def test_isolated_native_bridge_compiles_without_starting_capture(self):
        compiler = Path(tingji.os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        if not compiler.is_file():
            self.skipTest("Windows Framework C# compiler is unavailable")
        source, executable = self.home / "FirstUse.cs", self.home / "VerjotFirstUse.exe"
        source.write_text(tingji.VERIFY_BRIDGE, encoding="utf-8-sig")
        process = subprocess.run([str(compiler), "/nologo", "/target:exe", "/reference:System.Web.Extensions.dll",
                                  "/out:" + str(executable), str(source)], capture_output=True, timeout=30,
                                 creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.assertEqual(process.returncode, 0, process.stdout.decode(errors="replace"))
        self.assertTrue(executable.is_file())

    def test_missing_recording_consent_fails_before_configuration_or_files(self):
        with patch.object(tingji, "load_config", side_effect=AssertionError("no config read")):
            with self.assertRaisesRegex(ValueError, "consent-record"):
                tingji.verify(self.args(consent_record=False))
        self.assertEqual(list(self.home.iterdir()), [])

    def test_playback_requires_separate_consent(self):
        with self.assertRaisesRegex(ValueError, "consent-playback"):
            tingji._verify_flags(self.args(play_test="not-read.wav"), self.config)

    def test_pcm_playback_length_and_safe_path_are_checked(self):
        sample = self.sample()
        tingji._verify_flags(self.args(play_test=str(sample), consent_playback=True), self.config)
        with self.assertRaisesRegex(ValueError, "短 1 秒"):
            tingji._verify_flags(self.args(seconds=10, play_test=str(self.sample("too-long.wav", 10)),
                                          consent_playback=True), self.config)

    def test_api_upload_requires_its_own_consent(self):
        config = {**self.config, "transcription_mode": "api", "api_key_env": "VERJOT_TEST_ONLY_KEY"}
        with patch.dict(tingji.os.environ, {"VERJOT_TEST_ONLY_KEY": "test-value"}):
            with self.assertRaisesRegex(ValueError, "consent-upload"):
                tingji._verify_flags(self.args(transcribe=True), config)
            tingji._verify_flags(self.args(transcribe=True, consent_upload=True), config)
            tingji._verify_flags(self.args(), config)  # Record-only never needs an API key/upload.

    def test_missing_api_key_fails_before_recording(self):
        config = {**self.config, "transcription_mode": "api", "api_key_env": "VERJOT_TEST_UNSET_KEY"}
        with patch.dict(tingji.os.environ, {}, clear=True), self.assertRaisesRegex(ValueError, "未设置"):
            tingji._verify_flags(self.args(transcribe=True, consent_upload=True), config)

    def test_duration_and_stage_timeout_are_bounded(self):
        for values in ({"seconds": 9}, {"seconds": 61}, {"timeout_seconds": 29}, {"timeout_seconds": 3601}):
            with self.subTest(values=values), self.assertRaises(ValueError):
                tingji._verify_flags(self.args(**values), self.config)

    def test_active_meeting_blocks_before_creating_validation_directory(self):
        for state in ("recording", "paused", "processing", "queued"):
            with self.subTest(state=state), patch.object(tingji, "load_config", return_value=self.config), \
                    patch.object(tingji, "status", return_value={"state": state}), self.assertRaises(ValueError):
                tingji.verify(self.args())
        self.assertEqual(list(self.home.iterdir()), [])

    def test_recording_only_uses_real_merge_and_complete_mp3_without_transcription(self):
        report = self.run_fixture()
        self.assertTrue(report["ok"], report)
        self.assertEqual(report["scope"], "recording_only")
        self.assertTrue(report["audio_recorded"])
        self.assertFalse(report["api_called"])
        self.assertNotIn("note_path", report)
        self.assertFalse(report["timeline_quality_verified"])
        self.assertFalse(report["speaker_quality_verified"])
        self.assertEqual(report["listening_review"], "required")
        self.assertEqual(report["mp3"]["decoded_frames"], 12000)
        self.assertTrue(Path(report["audio_path"]).is_file())
        saved = json.loads(Path(report["report_path"]).read_text(encoding="utf-8"))
        self.assertEqual(saved, report)
        self.assertNotIn("vault_root", json.dumps(saved))

    def test_segmented_sources_are_read_continuously(self):
        report = self.run_fixture(capture=lambda *args: self.fixture(*args, segmented=True))
        self.assertTrue(report["ok"], report)
        self.assertEqual([row["segments"] for row in report["sources"]], [2, 2])
        self.assertEqual([row["frames"] for row in report["sources"]], [4000, 4000])

    def test_silence_cannot_pass_as_successful_two_source_recording(self):
        report = self.run_fixture(capture=lambda *args: self.fixture(*args, silent=True))
        self.assertFalse(report["ok"])
        self.assertEqual([row["ok"] for row in report["checks"][:2]], [False, False])
        for row in report["checks"][:2]:
            self.assertIn("未检测到", row["message"])
        self.assertTrue(report["mp3"]["complete"])

    def test_invalid_segment_continuity_fails_at_signal_check(self):
        def capture(*args):
            session = self.fixture(*args, segmented=True)
            path = session / "session.json"
            metadata = json.loads(path.read_text())
            metadata["sources"][0]["segments"][1]["start_frame"] += 1
            path.write_text(json.dumps(metadata))
            return session
        report = self.run_fixture(capture=capture)
        self.assertFalse(report["ok"])
        self.assertEqual(report["stage"], "signal_check")

    def test_capture_warning_does_not_become_success(self):
        def capture(*args):
            session = self.fixture(*args)
            path = session / "session.json"
            metadata = json.loads(path.read_text())
            metadata.update(state="recorded_with_error", capture_error="fixture capture warning")
            path.write_text(json.dumps(metadata))
            return session
        self.assertFalse(self.run_fixture(capture=capture)["ok"])

    def test_partial_start_error_preserves_unknown_audio_state_and_safe_report(self):
        report = self.run_fixture(capture=lambda *args: (_ for _ in ()).throw(RuntimeError("private-content-secret")))
        self.assertFalse(report["ok"])
        self.assertIsNone(report["audio_recorded"])
        self.assertTrue(report["recording_attempted"])
        self.assertNotIn("private-content-secret", Path(report["report_path"]).read_text(encoding="utf-8"))

    def test_transcript_output_has_real_words_but_safe_report_has_no_body(self):
        content = "这是独立测试正文。"
        def transcribe(session, config, args, api_started):
            return {"complete": True, "segments": [{"start": .02, "end": .2, "speaker": "mic_01",
                                                     "status": "done", "text": content}]}
        report = self.run_fixture(self.args(transcribe=True), transcribe=transcribe)
        self.assertTrue(report["ok"], report)
        self.assertIn(content, Path(report["note_path"]).read_text(encoding="utf-8"))
        self.assertNotIn(content, Path(report["report_path"]).read_text(encoding="utf-8"))
        self.assertFalse(report["api_called"])

    def test_empty_complete_result_does_not_pass_transcription(self):
        report = self.run_fixture(self.args(transcribe=True), transcribe=lambda *args: {"complete": True, "segments": []})
        self.assertFalse(report["ok"])
        self.assertFalse(report["checks"][-1]["ok"])

    def test_api_attempt_flag_is_not_set_when_preparation_fails(self):
        config = {**self.config, "transcription_mode": "api", "api_base_url": "https://example.invalid/v1",
                  "api_model": "test-only-model"}
        calls = []
        with patch.object(tingji.subprocess, "run", return_value=subprocess.CompletedProcess([], 1)):
            with self.assertRaises(ValueError):
                tingji._verify_transcribe(self.home, config, self.args(), lambda: calls.append(True))
        self.assertEqual(calls, [])

    def test_api_invocation_uses_environment_name_without_persisting_key(self):
        config = {**self.config, "transcription_mode": "api", "api_base_url": "https://example.invalid/v1",
                  "api_model": "test-only-model", "api_key_env": "VERJOT_TEST_ONLY_KEY"}
        (self.home / "result.json").write_text('{"complete": true, "segments": []}')
        attempted = []
        with patch.object(tingji.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as run:
            tingji._verify_transcribe(self.home, config, self.args(), lambda: attempted.append(True))
        self.assertEqual(attempted, [True])
        command = run.call_args_list[-1].args[0]
        self.assertIn("VERJOT_TEST_ONLY_KEY", command)
        self.assertEqual(command[command.index("--api-model") + 1], "test-only-model")
        self.assertEqual(run.call_args.kwargs["stdout"], subprocess.DEVNULL)


if __name__ == "__main__":
    unittest.main()
