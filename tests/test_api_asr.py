"""API transcription contract checks using synthetic WAVs and localhost HTTP only."""

from contextlib import nullcontext, redirect_stderr
from email import policy
from email.parser import BytesParser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch
import wave

import api_asr


class ApiTranscriptionTests(unittest.TestCase):
    def setUp(self):
        import tempfile

        self.temporary = tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent / "validation")
        self.addCleanup(self.temporary.cleanup)
        self.folder = Path(self.temporary.name)
        self.secret = "test-secret-never-log"
        self.responses = []
        self.requests = []
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                body = self.rfile.read(int(self.headers["Content-Length"]))
                owner.requests.append((self.path, dict(self.headers), body))
                code, payload, headers = owner.responses.pop(0)
                self.send_response(code)
                for key, value in headers.items():
                    self.send_header(key, value)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *_args):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.stop_server)
        self.base_url = f"http://127.0.0.1:{self.server.server_port}/v1"
        self.environment = patch.dict(os.environ, {"TINGJI_API_KEY": self.secret})
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.args = SimpleNamespace(segments=str(self.folder / "segments.json"), output=None,
                                    status=None, api_base_url=self.base_url, api_model="speech-model",
                                    api_key_env="TINGJI_API_KEY", api_timeout_seconds=10,
                                    context="挚达，充电桩")
        self.write_segments(2)

    def stop_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)

    def write_wav(self, name, rate=16000, channels=1, seconds=0.1):
        path = self.folder / name
        with wave.open(str(path), "wb") as stream:
            stream.setnchannels(channels)
            stream.setsampwidth(2)
            stream.setframerate(rate)
            stream.writeframes(b"\0\0" * int(rate * seconds) * channels)
        return path

    def write_segments(self, count):
        rows = [{"start": index, "end": index + 0.1, "speaker": f"mic_{index + 1:02d}",
                 "text_audio": self.write_wav(f"clip-{index}.wav").name}
                for index in range(count)]
        (self.folder / "segments.json").write_text(json.dumps(rows), encoding="utf-8")

    def reply(self, text="会议转写", code=200, headers=None, payload=None):
        if payload is None:
            payload = json.dumps({"text": text}, ensure_ascii=False).encode("utf-8")
        self.responses.append((code, payload, headers or {"Content-Type": "application/json"}))

    def run_job(self):
        self.stderr = io.StringIO()
        with redirect_stderr(self.stderr):
            return api_asr.run_job(self.args)

    def read(self, name):
        return json.loads((self.folder / name).read_text(encoding="utf-8"))

    def all_logged_content(self):
        return self.stderr.getvalue() + "".join(path.read_text(encoding="utf-8")
                                              for path in self.folder.glob("*.json"))

    def test_multipart_uploads_prepared_wav_and_keeps_result_contract(self):
        self.reply(" 第一段 ")
        self.reply("第二段")
        self.assertEqual(self.run_job(), 0)
        self.assertEqual(len(self.requests), 2)
        for index, (url, headers, body) in enumerate(self.requests):
            self.assertEqual(url, "/v1/audio/transcriptions")
            self.assertEqual(headers["Authorization"], f"Bearer {self.secret}")
            document = BytesParser(policy=policy.default).parsebytes(
                f"Content-Type: {headers['Content-Type']}\r\n\r\n".encode() + body)
            fields = {part.get_param("name", header="Content-Disposition"): part
                      for part in document.iter_parts()}
            self.assertEqual(set(fields), {"file", "model", "language", "prompt", "response_format"})
            self.assertEqual(fields["model"].get_payload(decode=True), b"speech-model")
            self.assertEqual(fields["language"].get_payload(decode=True), b"zh")
            self.assertEqual(fields["response_format"].get_payload(decode=True), b"json")
            self.assertEqual(fields["prompt"].get_payload(decode=True).decode("utf-8"), self.args.context)
            audio = fields["file"].get_payload(decode=True)
            self.assertEqual(audio, (self.folder / f"clip-{index}.wav").read_bytes())
            with wave.open(io.BytesIO(audio)) as stream:
                self.assertEqual((stream.getframerate(), stream.getnchannels()), (16000, 1))
                self.assertLessEqual(stream.getnframes() / stream.getframerate(), 25)
        result = self.read("result.json")
        self.assertTrue(result["complete"])
        self.assertEqual(result["schema_version"], 1)
        self.assertEqual([row["text"] for row in result["segments"]], ["第一段", "第二段"])
        self.assertEqual([row["speaker"] for row in result["segments"]], ["mic_01", "mic_02"])
        state = self.read("status.json")
        self.assertEqual((state["state"], state["completed"], state["total"]), ("completed", 2, 2))
        self.assertEqual(state["progress"], 1)
        self.assertNotIn(self.secret, self.all_logged_content())

    def test_http_failure_retains_successes_and_retry_uses_cache_without_leaking(self):
        self.reply("已经完成")
        self.reply(code=401, payload=f"Bearer {self.secret}: private-provider-body".encode(),
                   headers={"X-Private": self.secret})
        self.assertEqual(self.run_job(), 1)
        result = self.read("result.json")
        self.assertFalse(result["complete"])
        self.assertEqual(result["segments"][0]["status"], "done")
        self.assertEqual(result["segments"][1]["status"], "error")
        self.assertIn("HTTP 401", self.read("status.json")["error"])
        self.assertNotIn(self.secret, self.all_logged_content())
        self.assertNotIn("private-provider-body", self.all_logged_content())
        os.environ["TINGJI_API_KEY"] = "replacement-secret"
        self.reply("重试完成")
        self.assertEqual(self.run_job(), 0)
        self.assertEqual(len(self.requests), 3, "The successful first segment must not be uploaded again")
        self.assertEqual(self.read("result.json")["segments"][0]["text"], "已经完成")
        self.assertNotIn("replacement-secret", self.all_logged_content())

    def test_invalid_response_text_or_json_is_not_logged_or_checkpointed_as_success(self):
        self.write_segments(1)
        for payload in (json.dumps({"text": {"key": self.secret}}).encode(),
                        json.dumps({"text": "Bearer " + self.secret}).encode(),
                        f"invalid-json-{self.secret}".encode()):
            with self.subTest(payload_kind=payload[:1]):
                self.reply(payload=payload)
                self.assertEqual(self.run_job(), 1)
                self.assertEqual(self.read("result.json")["segments"][0]["status"], "error")
                self.assertNotIn(self.secret, self.all_logged_content())

    def test_invalid_audio_is_rejected_before_any_upload(self):
        self.write_segments(1)
        for rate, channels, seconds in ((8000, 1, 0.1), (16000, 2, 0.1), (16000, 1, 25.01)):
            with self.subTest(rate=rate, channels=channels, seconds=seconds):
                self.write_wav("clip-0.wav", rate, channels, seconds)
                (self.folder / "result.json").unlink(missing_ok=True)
                self.assertEqual(self.run_job(), 1)
                self.assertIn("16 kHz mono", self.read("status.json")["error"])
        self.assertEqual(self.requests, [])

    def test_redirect_does_not_forward_audio_or_authorization(self):
        self.write_segments(1)
        self.reply(code=307, payload=b"", headers={"Location": self.base_url + "/other"})
        self.assertEqual(self.run_job(), 1)
        self.assertEqual(len(self.requests), 1)
        self.assertIn("HTTP 307", self.read("status.json")["error"])
        self.assertNotIn(self.secret, self.all_logged_content())

    def test_validation_rejects_invalid_config_without_network_or_secret_leakage(self):
        for field, value in (("api_base_url", "ftp://host/v1"), ("api_base_url", "https://host/\n"),
                             ("api_base_url", "https://username:password@host/v1"),
                             ("api_base_url", "https://host/v1?api_key=hidden"),
                             ("api_model", ""), ("api_model", 3), ("api_key_env", ""),
                             ("api_timeout_seconds", 9), ("api_timeout_seconds", 601),
                             ("api_timeout_seconds", True)):
            with self.subTest(field=field, value=value):
                with patch.object(self.args, field, value):
                    self.assertEqual(self.run_job(), 1)
                self.assertNotIn(self.secret, self.all_logged_content())
        for key in ("", self.secret + "\r\nInjected: header", "密钥", self.secret + "\x7f"):
            with patch.dict(os.environ, {"TINGJI_API_KEY": key}):
                self.assertEqual(self.run_job(), 1)
                self.assertNotIn(self.secret, self.all_logged_content())
        self.assertEqual(self.requests, [])

    def test_fingerprint_includes_url_model_context_and_audio_identity_not_key(self):
        self.reply()
        self.reply()
        self.assertEqual(self.run_job(), 0)
        saved = (self.folder / "result.json").read_bytes()
        for field, value in (("api_base_url", self.base_url + "/changed"),
                             ("api_model", "different-model"), ("context", "不同术语")):
            with self.subTest(field=field):
                with patch.object(self.args, field, value):
                    self.assertEqual(self.run_job(), 1)
                self.assertEqual((self.folder / "result.json").read_bytes(), saved)
        os.environ["TINGJI_API_KEY"] = "other-test-key"
        self.assertEqual(self.run_job(), 0)
        self.assertEqual(len(self.requests), 2)
        self.write_wav("clip-0.wav", seconds=0.2)
        saved = (self.folder / "result.json").read_bytes()
        self.assertEqual(self.run_job(), 1)
        self.assertEqual((self.folder / "result.json").read_bytes(), saved)
        self.assertEqual(len(self.requests), 2)

    def test_empty_context_omits_optional_prompt_and_timeout_is_bounded(self):
        self.write_segments(1)
        self.args.context = ""
        self.reply()
        original = api_asr.urllib.request.OpenerDirector.open
        with patch.object(api_asr.urllib.request.OpenerDirector, "open", autospec=True,
                          side_effect=original) as opened:
            self.assertEqual(self.run_job(), 0)
        self.assertEqual(opened.call_args.kwargs["timeout"], 10)
        _url, headers, body = self.requests[0]
        document = BytesParser(policy=policy.default).parsebytes(
            f"Content-Type: {headers['Content-Type']}\r\n\r\n".encode() + body)
        self.assertNotIn("prompt", {part.get_param("name", header="Content-Disposition")
                                    for part in document.iter_parts()})

    def test_connection_error_and_timeout_do_not_echo_provider_details(self):
        self.write_segments(1)
        for error in (api_asr.urllib.error.URLError("Bearer " + self.secret),
                      TimeoutError("Bearer " + self.secret)):
            with self.subTest(error_type=type(error).__name__):
                with patch.object(api_asr.urllib.request.OpenerDirector, "open", side_effect=error):
                    self.assertEqual(self.run_job(), 1)
                self.assertNotIn(self.secret, self.all_logged_content())
                self.assertFalse(self.read("result.json")["complete"])
        self.assertEqual(self.requests, [])

    def test_cli_works_without_local_asr_or_audio_packages(self):
        self.write_segments(1)
        self.reply("仅API识别")
        # A process import guard proves API transcribing has only stdlib dependencies.
        code = """import builtins
original = builtins.__import__
def guarded(name, *args, **kwargs):
    if name.split('.')[0] in {'torch', 'transformers', 'accelerate', 'numpy', 'scipy', 'soundfile', 'sherpa_onnx', 'requests'}:
        raise AssertionError('API worker imported a local model/audio dependency')
    return original(name, *args, **kwargs)
builtins.__import__ = guarded
import api_asr
raise SystemExit(api_asr.main())
"""
        process = subprocess.run([sys.executable, "-c", code, "--segments", self.args.segments,
                                  "--api-base-url", self.base_url, "--api-model", "speech-model",
                                  "--api-timeout-seconds", "10"],
                                 cwd=Path(api_asr.__file__).resolve().parent, capture_output=True,
                                 text=True, encoding="utf-8", timeout=20)
        self.assertEqual(process.returncode, 0, process.stderr)
        self.assertEqual(self.read("result.json")["segments"][0]["text"], "仅API识别")
        self.assertNotIn(self.secret, process.stdout + process.stderr)


class WorkerRoutingTests(unittest.TestCase):
    def test_only_explicit_api_mode_dispatches_api_worker_and_never_passes_key(self):
        import tempfile
        import meeting_worker as worker

        for mode in (None, "local", "api"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory(
                    dir=Path(__file__).resolve().parent / "validation") as temporary:
                vault = Path(temporary)
                session = vault / "audio"
                session.mkdir()
                metadata = {"start_time": "2026-10-14T15:00:00+08:00", "duration_seconds": 0.1}
                if mode == "api":
                    metadata.update(transcription_mode="api", transcription_model="speech-model",
                                    transcription_api_base_url="https://provider.example/v1")
                (session / "session.json").write_text(json.dumps(metadata), encoding="utf-8")
                config = {"vault_root": str(vault), "api_base_url": "https://provider.example/v1",
                          "api_model": "speech-model", "api_key_env": "TINGJI_API_KEY",
                          "api_timeout_seconds": 120, "asr_context": "业务术语"}
                if mode is not None:
                    config["transcription_mode"] = mode
                (vault / "config.json").write_text(json.dumps(config), encoding="utf-8")
                with wave.open(str(session / "mic.wav"), "wb") as audio:
                    audio.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
                    audio.writeframes(b"\0\0" * 1600)
                commands = []

                def stage(_session, command, name):
                    commands.append((name, command))
                    if name == "prepare":
                        api_asr.atomic_json(session / "segments.json", {"segments": []})
                    else:
                        api_asr.atomic_json(session / "result.json", {"complete": True, "segments": []})

                with patch.object(worker, "HOME", vault), \
                        patch.object(worker, "processing_slot", lambda _: nullcontext()), \
                        patch.object(worker, "run_stage", stage), \
                        patch.dict(os.environ, {"TINGJI_API_KEY": "routing-secret-never-pass"}):
                    self.assertEqual(worker.process_session(session), 0)
                command = next(command for name, command in commands if name == "asr")
                expected = "api_asr.py" if mode == "api" else "asr_engine.py"
                self.assertEqual(Path(command[1]).name, expected)
                self.assertNotIn("routing-secret-never-pass", " ".join(command))
                if mode == "api":
                    self.assertNotIn("--model-dir", command)
                    self.assertIn("--api-base-url", command)
                    self.assertEqual(command[command.index("--api-key-env") + 1], "TINGJI_API_KEY")
                else:
                    self.assertIn("--model-dir", command)


class DownloadRoutingTests(unittest.TestCase):
    def test_speaker_only_skips_all_hugging_face_calls_and_preserves_full_manifest(self):
        self.check_download(speaker_only=True)

    def test_default_still_downloads_local_asr_and_speaker_models(self):
        self.check_download(speaker_only=False)

    def check_download(self, speaker_only):
        import tempfile

        # Import the downloader with an isolated fake requests module: tests must
        # neither require a downloader dependency installation nor open the network.
        spec = importlib.util.spec_from_file_location(
            "download_models_test", Path(api_asr.__file__).resolve().parent / "download_models.py")
        downloader = importlib.util.module_from_spec(spec)
        with patch.dict(sys.modules, {"requests": SimpleNamespace(Session=lambda: MagicMock())}):
            spec.loader.exec_module(downloader)

        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent / "validation") as temporary:
            root = Path(temporary)
            previous_manifest = root / "model-manifest.json"
            previous_manifest.write_text("original model manifest", encoding="utf-8")
            calls = []

            def download(url, destination, *_args):
                calls.append(url)
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(b"synthetic-model")

            response = SimpleNamespace(raise_for_status=lambda: None,
                                       json=lambda: {"sha": downloader.QWEN_REVISION, "siblings": [
                                           {"rfilename": "config.json"}, {"rfilename": "model.safetensors"}]})
            package = MagicMock()
            package.__enter__.return_value.getmembers.return_value = []
            with patch.object(downloader, "ROOT", root), \
                    patch.object(downloader, "STATUS", root / "download-status.json"), \
                    patch.object(downloader, "download", side_effect=download), \
                    patch.object(downloader.HTTP, "get", return_value=response) as metadata_get, \
                    patch.object(downloader.tarfile, "open", return_value=package):
                downloader.main(["--speaker-only"] if speaker_only else [])
            if speaker_only:
                metadata_get.assert_not_called()
                self.assertEqual(len(calls), 2)
                self.assertTrue(all(url.startswith("https://github.com/k2-fsa/") for url in calls))
                self.assertFalse((root / "Qwen3-ASR-1.7B-hf").exists())
                self.assertEqual(previous_manifest.read_text(encoding="utf-8"), "original model manifest")
                self.assertTrue((root / "speaker-model-manifest.json").is_file())
            else:
                metadata_get.assert_called_once()
                self.assertEqual(len(calls), 4)
                self.assertEqual(json.loads(previous_manifest.read_text(encoding="utf-8"))["revision"], downloader.QWEN_REVISION)
                self.assertIn('/revision/' + downloader.QWEN_REVISION, metadata_get.call_args.args[0])
            self.assertEqual(json.loads((root / "download-status.json").read_text(encoding="utf-8"))["state"], "completed")


class DownloadResumeTests(unittest.TestCase):
    def setUp(self):
        import tempfile

        self.temporary = tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent / "validation")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        spec = importlib.util.spec_from_file_location(
            "download_resume_test", Path(api_asr.__file__).resolve().parent / "download_models.py")
        self.downloader = importlib.util.module_from_spec(spec)
        with patch.dict(sys.modules, {"requests": SimpleNamespace(Session=lambda: MagicMock())}):
            spec.loader.exec_module(self.downloader)
        self.downloader.ROOT = self.root
        self.downloader.STATUS = self.root / "download-status.json"
        self.destination = self.root / "speaker.onnx"
        self.partial = self.destination.with_suffix(".onnx.partial")
        self.payload = b"synthetic-complete-model"
        self.expected_hash = self.downloader.hashlib.sha256(self.payload).hexdigest()
        self.sentinel = self.root / "unrelated-model.onnx"
        self.sentinel.write_bytes(b"keep this other file")
        sleep = patch.object(self.downloader.time, "sleep")
        sleep.start()
        self.addCleanup(sleep.stop)

    def test_complete_partial_finishes_without_eof_range_request(self):
        for expected_hash in (None, self.expected_hash):
            with self.subTest(hash_provided=expected_hash is not None):
                self.destination.unlink(missing_ok=True)
                self.partial.write_bytes(self.payload)
                with patch.object(self.downloader.HTTP, "get", side_effect=AssertionError("must not request Range EOF")) as get:
                    self.downloader.download("https://model.example/file", self.destination,
                                             len(self.payload), expected_hash)
                get.assert_not_called()
                self.assertEqual(self.destination.read_bytes(), self.payload)
                self.assertFalse(self.partial.exists())
                self.assertEqual(self.sentinel.read_bytes(), b"keep this other file")

    def test_oversized_partial_restarts_from_zero(self):
        self.partial.write_bytes(b"x" * (len(self.payload) + 1))
        self.check_restarted_download()

    def test_complete_partial_with_wrong_hash_restarts_from_zero(self):
        self.partial.write_bytes(b"x" * len(self.payload))
        self.check_restarted_download(self.expected_hash)

    def check_restarted_download(self, expected_hash=None):
        response = MagicMock()
        response.__enter__.return_value = response
        response.status_code = 200
        response.headers = {"Content-Length": str(len(self.payload))}
        response.iter_content.return_value = [self.payload]
        with patch.object(self.downloader.HTTP, "get", return_value=response) as get:
            self.downloader.download("https://model.example/file", self.destination,
                                     len(self.payload), expected_hash)
        get.assert_called_once()
        self.assertEqual(get.call_args.kwargs["headers"], {})
        self.assertEqual(self.destination.read_bytes(), self.payload)
        self.assertFalse(self.partial.exists())
        self.assertEqual(self.sentinel.read_bytes(), b"keep this other file")


if __name__ == "__main__":
    unittest.main()
