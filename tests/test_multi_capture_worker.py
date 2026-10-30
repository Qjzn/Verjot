import json
from contextlib import nullcontext
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import numpy as np
import soundfile as sf

import api_asr
import asr_engine
import meeting_worker as worker


class MultiCaptureWorkerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.session = Path(self.temporary.name)
        self.metadata = {"sources": [
            {"source": "system", "path": "system.wav", "kind": "loopback"},
            {"source": "system_02", "path": "system_02.wav", "kind": "loopback"}]}
        (self.session / "session.json").write_text(json.dumps(self.metadata), encoding="utf8")
        for row in self.metadata["sources"]:
            (self.session / row["path"]).write_bytes(b"raw")

    def test_changed_secondary_output_invalidates_prepared_input(self):
        before = worker.input_fingerprint(self.session)
        (self.session / "system_02.wav").write_bytes(b"changed raw")
        self.assertNotEqual(worker.input_fingerprint(self.session), before)

    def test_cleanup_only_lists_registered_raw_sources_and_known_clips(self):
        (self.session / "session.json").write_text(json.dumps({"sources": [self.metadata["sources"][1]]}),
                                                  encoding="utf8")
        clips = self.session / "clips"
        clips.mkdir()
        (clips / "system_02_00001.wav").write_bytes(b"generated")
        (clips / "customer.wav").write_bytes(b"user file")
        (self.session / "system_99.wav").write_bytes(b"unregistered user file")
        (self.session / "mic.wav").write_bytes(b"unregistered user microphone file")
        names = {str(x.relative_to(self.session)).replace("\\", "/")
                 for x in worker.cleanup_candidates(self.session)}
        self.assertIn("system_02.wav", names)
        self.assertIn("clips/system_02_00001.wav", names)
        self.assertNotIn("system_99.wav", names)
        self.assertNotIn("mic.wav", names)
        self.assertNotIn("system.wav", names)
        self.assertNotIn("clips/customer.wav", names)

    def test_legacy_cleanup_still_includes_its_existing_original_tracks(self):
        (self.session / "session.json").write_text("{}", encoding="utf8")
        (self.session / "mic.wav").write_bytes(b"legacy microphone")
        names = {path.name for path in worker.cleanup_candidates(self.session)}
        self.assertIn("mic.wav", names)
        self.assertIn("system.wav", names)
        self.assertNotIn("system_02.wav", names)

    def test_delayed_echo_requires_acoustic_evidence_and_matching_substantive_text(self):
        system = {"segment_id": "system_02_00001", "source": "system_02", "speaker": "system_02_01",
                  "start": 2.0, "end": 5.0, "status": "done", "text": "今天下午确认新的交付日期。"}
        microphone = {**system, "segment_id": "mic_02_00001", "source": "mic_02", "speaker": "mic_02_01",
                      "start": 2.12, "end": 5.12, "echo_candidate_of": system["segment_id"],
                      "echo_delay_ms": 120.0, "echo_similarity": 0.98, "echo_window_support": 1.0,
                      "echo_window_min_similarity": 0.95, "echo_residual_ratio": 0.04, "echo_window_count": 5}
        self.assertEqual(worker.transcript_rows({"segments": [microphone, system]}), [system])
        for change in ({"echo_similarity": 0.8}, {"echo_residual_ratio": 0.4},
                       {"echo_window_support": 0.5}, {"echo_window_count": 1},
                       {"text": "今天下午不要确认新的交付日期。"},
                       {"start": 8.0, "end": 11.0}):
            with self.subTest(change=change):
                self.assertEqual(len(worker.transcript_rows({"segments": [{**microphone, **change}, system]})), 2)
        short = {**system, "text": "收到。"}
        self.assertEqual(len(worker.transcript_rows({"segments": [{**microphone, "text": "收到。"}, short]})), 2)

    def test_echo_evidence_survives_asr_allowlist_and_only_changes_rendered_note(self):
        import asr_engine
        import prepare_audio

        # Frozen synthetic delayed waveform: this tests the plumbing without a
        # learned speaker/ASR model or a claim about human meeting accuracy.
        samples = np.random.default_rng(72105).normal(0, .025, 80000).astype(np.float32)
        delay = 1920
        sources = {"system_02": samples, "mic_02": np.pad(samples, (delay, 0))[:len(samples)] * .65}
        rows = [{"source": "system_02", "speaker": "system_02_01", "segment_id": "system_02_00001",
                 "start": .5, "end": 3.5},
                {"source": "mic_02", "speaker": "mic_02_01", "segment_id": "mic_02_00001",
                 "start": .62, "end": 3.62}]
        prepare_audio.mark_acoustic_echo(rows, sources, {})
        self.assertEqual(rows[1]["echo_candidate_of"], rows[0]["segment_id"])
        clips = self.session / "clips"
        clips.mkdir()
        for row in rows:
            clip = clips / (row["segment_id"] + ".wav")
            begin, end = round(row["start"] * 16000), round(row["end"] * 16000)
            sf.write(clip, sources[row["source"]][begin:end], 16000)
            row["text_audio"] = str(clip)
        prepared = self.session / "segments.json"
        prepared.write_text(json.dumps(rows), encoding="utf8")
        model = self.session / "unused-model"
        model.mkdir()
        args = SimpleNamespace(segments=str(prepared), output=None,
                               status=str(self.session / "asr_status.json"), model_dir=str(model),
                               context="", max_new_tokens=512, device="cpu", no_cpu_fallback=True)
        case = self

        class Backend:
            def __init__(self, *args):
                pass

            def transcribe(self, row):
                case.assertNotIn("echo_candidate_of", row)
                case.assertNotIn("echo_similarity", row)
                return "今天下午确认新的交付日期。"

            def close(self):
                pass

        with patch.object(asr_engine, "release_cuda"):
            self.assertEqual(asr_engine.run_job(args, backend_factory=Backend), 0)
        checkpoint = self.session / "result.json"
        original = checkpoint.read_bytes()
        result = worker.read_json(checkpoint)
        self.assertTrue(all("echo_candidate_of" not in row for row in result["segments"]))
        joined = worker.transcript_with_evidence(self.session, result)
        metadata = {"start_time": "2026-10-15T10:00:00+08:00"}
        note = worker.render_note(self.session, self.session, metadata, result=joined)
        self.assertEqual(note.count("今天下午确认新的交付日期。"), 1)
        self.assertEqual(len(result["segments"]), 2)
        self.assertEqual(checkpoint.read_bytes(), original)
        # Different concurrent content must survive the identical rendering path.
        joined["segments"][1]["text"] = "今天下午不要确认新的交付日期。"
        note = worker.render_note(self.session, self.session, metadata, result=joined)
        self.assertIn("今天下午确认新的交付日期。", note)
        self.assertIn("今天下午不要确认新的交付日期。", note)
        self.assertEqual(checkpoint.read_bytes(), original)


class CheckpointRetrySafetyTests(unittest.TestCase):
    mode = "local"

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.home, self.vault = self.folder / "program", self.folder / "vault"
        self.session = self.home / "work" / "20261015-100000-retry"
        self.session.mkdir(parents=True)
        self.vault.mkdir()
        output = self.vault / "会议记录" / "附件" / self.session.name
        self.config = {"vault_root": str(self.vault), "transcription_mode": self.mode}
        if self.mode == "api":
            self.config.update(transcription_mode="api", api_model="fixture-transcribe",
                               api_base_url="https://provider.example/v1")
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf8")
        self.metadata = {"start_time": "2026-10-15T10:00:00+08:00", "duration_seconds": .1,
                         "storage_mode": "minimal", "audio_output_dir": str(output),
                         "asr_denoise": "off", "asr_echo_filter": False,
                         "sources": [{"source": "system_02", "path": "system_02.wav", "kind": "loopback"}]}
        if self.mode == "api":
            self.metadata.update(transcription_mode="api", transcription_model=self.config["api_model"],
                                 transcription_api_base_url=self.config["api_base_url"])
        (self.session / "session.json").write_text(json.dumps(self.metadata), encoding="utf8")
        self.raw = self.session / "system_02.wav"
        sf.write(self.raw, np.full(4800, .1), 48000, subtype="FLOAT")
        self.patch_home = patch.object(worker, "HOME", self.home)
        self.patch_home.start()
        self.addCleanup(self.patch_home.stop)
        (self.session / "clips").mkdir()
        self.clip = self.session / "clips" / "system_02_00001.wav"
        sf.write(self.clip, np.full(1600, .1), 16000)
        worker.atomic_json(self.session / "segments.json", [{"start": 0, "end": .1,
                           "speaker": "system_02_01", "text_audio": str(self.clip)}])
        self.cache = {"fingerprint": worker.input_fingerprint(self.session),
                      "asr_denoise": "off", "asr_echo_filter": False}
        worker.atomic_json(self.session / "prepared_input.json", self.cache)
        canonical = asr_engine.read_segments(self.session / "segments.json")
        fingerprint = (api_asr.job_fingerprint(canonical, self.config["api_base_url"], self.config["api_model"], "")
                       if self.mode == "api" else asr_engine.job_fingerprint(
                           canonical, self.home / "models" / "Qwen3-ASR-1.7B-hf", "", 512))
        worker.atomic_json(self.session / "result.json", {"fingerprint": fingerprint, "complete": False,
                           "segments": [dict(canonical[0], status="done", text="旧录音需要保留。和手写区都要保留。") ]})
        merged = worker.publish_audio(self.session, self.config, self.metadata,
                                      worker.merge_session(self.session, self.metadata))
        self.audio = Path(merged["published_path"])
        self.note = worker.note_location(self.session, self.config, self.metadata)[1]
        worker.save_session_note(self.session, self.note,
                                 worker.render_note(self.session, self.vault, self.metadata,
                                                    result=worker.read_json(self.session / "result.json"),
                                                    mixed_audio=merged))
        worker.status(self.session, "fixture interrupted ASR", state="error", note_path=str(self.note))
        with self.note.open("a", encoding="utf8") as stream:
            stream.write("\n用户手写：这里不能被重试覆盖。\n")
        self.audio_before, self.note_before = self.audio.read_bytes(), self.note.read_bytes()
        self.result_before = (self.session / "result.json").read_bytes()

    def assert_rejected_without_changing_artifacts(self):
        with patch.object(worker, "processing_slot", lambda _: nullcontext()), \
                patch.object(worker, "run_stage", side_effect=AssertionError("must not run ASR or preparation")) as stage, \
                patch.object(worker, "publish_audio", wraps=worker.publish_audio) as publish, \
                patch.object(worker, "save_session_note", wraps=worker.save_session_note) as save:
            self.assertEqual(worker.process_session(self.session), 1)
        self.assertEqual(self.audio.read_bytes(), self.audio_before, "published recording was replaced")
        self.assertEqual(self.note.read_bytes(), self.note_before, "published transcript was rewritten")
        self.assertEqual((self.session / "result.json").read_bytes(), self.result_before)
        self.assertEqual(worker.read_json(self.session / "status.json")["state"], "error")
        self.assertEqual(worker.read_json(self.session / "status.json")["stage"], "input_validation")
        stage.assert_not_called()
        publish.assert_not_called()
        save.assert_not_called()

    def test_changed_raw_rejects_retry_before_publishing_new_audio_or_md(self):
        sf.write(self.raw, np.full(4800, .35), 48000, subtype="FLOAT")
        self.assert_rejected_without_changing_artifacts()

    def test_missing_registered_raw_preserves_existing_audio_and_md(self):
        self.raw.unlink()
        self.assert_rejected_without_changing_artifacts()

    def test_changed_denoise_checkpoint_preserves_existing_audio_and_md(self):
        worker.atomic_json(self.session / "prepared_input.json", {**self.cache, "asr_denoise": "light"})
        self.assert_rejected_without_changing_artifacts()

    def test_changed_echo_checkpoint_preserves_existing_audio_and_md(self):
        worker.atomic_json(self.session / "prepared_input.json", {**self.cache, "asr_echo_filter": True})
        self.assert_rejected_without_changing_artifacts()

    def test_config_context_change_rejects_before_publishing_or_saving(self):
        worker.atomic_json(self.home / "config.json", {**self.config, "asr_context": "挚达，充电桩"})
        self.assert_rejected_without_changing_artifacts()

    def test_changed_prepared_clip_rejects_before_publishing_or_saving(self):
        sf.write(self.clip, np.full(3200, .2), 16000)
        self.assert_rejected_without_changing_artifacts()

    def test_missing_prepared_clip_rejects_before_publishing_or_saving(self):
        self.clip.unlink()
        self.assert_rejected_without_changing_artifacts()

    def test_changed_prepared_time_range_rejects_before_publishing_or_saving(self):
        rows = worker.read_json(self.session / "segments.json")
        rows[0]["end"] = .09
        worker.atomic_json(self.session / "segments.json", rows)
        self.assert_rejected_without_changing_artifacts()

    def test_changed_prepared_speaker_rejects_before_publishing_or_saving(self):
        rows = worker.read_json(self.session / "segments.json")
        rows[0]["speaker"] = "system_02_02"
        worker.atomic_json(self.session / "segments.json", rows)
        self.assert_rejected_without_changing_artifacts()

    def test_checkpoint_done_row_mismatch_rejects_before_publishing_or_saving(self):
        result = worker.read_json(self.session / "result.json")
        result["segments"][0]["speaker"] = "system_02_99"
        worker.atomic_json(self.session / "result.json", result)
        self.result_before = (self.session / "result.json").read_bytes()
        self.assert_rejected_without_changing_artifacts()

    def test_invalid_checkpoint_rows_reject_before_publishing_or_saving(self):
        result = worker.read_json(self.session / "result.json")
        result["segments"] = {"not": "a checkpoint array"}
        worker.atomic_json(self.session / "result.json", result)
        self.result_before = (self.session / "result.json").read_bytes()
        self.assert_rejected_without_changing_artifacts()

    def test_unchanged_checkpoint_resumes_without_preparing_or_replacing_clip_bytes(self):
        calls = []

        def stage(session, command, name):
            calls.append(name)
            self.assertEqual(name, "asr")
            result = worker.read_json(session / "result.json")
            worker.atomic_json(session / "result.json", {**result, "complete": True})

        with patch.object(worker, "processing_slot", lambda _: nullcontext()), \
                patch.object(worker, "run_stage", stage):
            self.assertEqual(worker.process_session(self.session), 0)
        self.assertEqual(calls, ["asr"])
        self.assertEqual(self.audio.read_bytes(), self.audio_before)
        self.assertIn("旧录音需要保留。", self.note.read_text(encoding="utf8"))
        self.assertIn("用户手写：这里不能被重试覆盖。", self.note.read_text(encoding="utf8"))


class ApiCheckpointRetrySafetyTests(CheckpointRetrySafetyTests):
    mode = "api"


class CaptureFailureRetentionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        self.home, self.vault = root / "program", root / "vault"
        self.session = self.home / "work" / "20261015-120231-synthetic"
        self.session.mkdir(parents=True)
        self.vault.mkdir()
        self.config = {"vault_root": str(self.vault), "transcription_mode": "local"}
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf8")
        self.metadata = {"start_time": "2026-10-15T12:02:31+08:00", "duration_seconds": .1,
                         "state": "recorded_with_error", "capture_error": "fixture endpoint stopped early",
                         "storage_mode": "minimal",
                         "audio_output_dir": str(self.vault / "会议记录" / "附件" / self.session.name),
                         "sources": [{"source": "mic", "path": "mic.wav", "kind": "microphone"},
                                     {"source": "system_02", "path": "system_02.wav", "kind": "loopback"}]}
        self.originals = {}
        for source in self.metadata["sources"]:
            path = self.session / source["path"]
            sf.write(path, np.full(4800, .1), 48000, subtype="FLOAT")
            self.originals[path] = path.read_bytes()
        self.patch_home = patch.object(worker, "HOME", self.home)
        self.patch_home.start()
        self.addCleanup(self.patch_home.stop)

    def stage(self, session, command, name):
        if name == "prepare":
            clips = session / "clips"
            clips.mkdir()
            sf.write(clips / "mic_00001.wav", np.full(1600, .1), 16000)
            worker.atomic_json(session / "segments.json", [])
            worker.atomic_json(session / "prepare_summary.json", {"sources": {}})
        else:
            self.assertEqual(name, "asr")
            worker.atomic_json(session / "result.json", {"complete": True, "segments": []})

    def process(self):
        (self.session / "session.json").write_text(json.dumps(self.metadata), encoding="utf8")
        with patch.object(worker, "processing_slot", lambda _: nullcontext()), \
                patch.object(worker, "run_stage", self.stage):
            return worker.process_session(self.session)

    def assert_evidence_retained(self):
        for path, content in self.originals.items():
            self.assertTrue(path.is_file(), "original capture track was removed")
            self.assertEqual(path.read_bytes(), content)
        state = worker.read_json(self.session / "status.json")
        self.assertEqual(state["state"], "completed", "finished ASR must not become a retry loop")
        self.assertIn("采集异常", state["message"])
        self.assertTrue(state.get("capture_needs_review"))
        self.assertFalse(state.get("artifacts_compacted"))
        for name in ("segments.json", "result.json", "prepared_input.json", "prepare_summary.json"):
            self.assertTrue((self.session / name).is_file(), "recovery evidence was removed: " + name)
        self.assertTrue((self.session / "clips" / "mic_00001.wav").is_file())
        self.assertTrue(Path(state["merged_audio"]["published_path"]).is_file())
        self.assertTrue(Path(state["note_path"]).is_file())
        self.assertIn("录音异常", Path(state["note_path"]).read_text(encoding="utf8"))

    def test_confirmed_capture_failure_keeps_processing_result_and_recovery_evidence(self):
        self.assertEqual(self.process(), 0)
        self.assert_evidence_retained()

    def test_capture_error_without_error_state_still_retains_recovery_evidence(self):
        self.metadata["state"] = "recorded"
        self.assertEqual(self.process(), 0)
        self.assert_evidence_retained()

    def test_error_state_without_error_text_still_retains_recovery_evidence(self):
        self.metadata["capture_error"] = ""
        self.assertEqual(self.process(), 0)
        self.assert_evidence_retained()

    def test_diagnostics_alone_do_not_turn_successful_capture_into_a_failure(self):
        self.metadata.update(state="recorded", capture_error="",
                             capture_problems=["timestamp fallback diagnostic"])
        self.metadata["sources"][0]["diagnostics"] = [{"message": "timestamp fallback diagnostic"}]
        self.assertEqual(self.process(), 0)
        state = worker.read_json(self.session / "status.json")
        self.assertEqual(state["state"], "completed")
        self.assertTrue(state["artifacts_compacted"])
        self.assertFalse(state.get("capture_needs_review"))
        for path in self.originals:
            self.assertFalse(path.exists())

    def test_completed_capture_failure_retry_does_not_rerun_asr_or_change_saved_evidence(self):
        self.assertEqual(self.process(), 0)
        self.assert_evidence_retained()
        before = {path: path.read_bytes() for path in self.session.rglob("*") if path.is_file()}
        with patch.object(worker, "run_stage", side_effect=AssertionError("must not reload a model")):
            self.assertEqual(worker.process_session(self.session), 0)
        self.assert_evidence_retained()
        for path, content in before.items():
            self.assertEqual(path.read_bytes(), content)

    def test_explicit_compaction_cannot_override_confirmed_capture_failure(self):
        with patch.object(worker, "finish_compaction"):
            self.assertEqual(self.process(), 0)
        state = worker.read_json(self.session / "status.json")
        with self.assertRaisesRegex(ValueError, "采集异常"):
            worker.compact_session(self.session, self.config, self.metadata, state)
        for path, content in self.originals.items():
            self.assertEqual(path.read_bytes(), content)


if __name__ == "__main__":
    unittest.main()
