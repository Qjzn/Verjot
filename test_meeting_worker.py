import json
from contextlib import nullcontext
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import numpy as np
import soundfile as sf

import meeting_worker as worker


class NoteTests(unittest.TestCase):
    def process_fixture(self, merge_fails=False):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        vault = Path(temporary.name)
        session = vault / "audio"
        session.mkdir()
        metadata = {"start_time": "2026-10-14T15:00:00+08:00", "duration_seconds": 0.1}
        (session / "session.json").write_text(json.dumps(metadata), encoding="utf-8")
        (vault / "config.json").write_text(json.dumps({"vault_root": str(vault)}), encoding="utf-8")
        sf.write(session / "mic.wav", np.full(4800, 0.2), 48000, subtype="FLOAT")
        if merge_fails:
            (session / "system.wav").write_bytes(b"bad WAV")

        def stage(_session, _command, name):
            if name == "prepare":
                (session / "segments.json").write_text("[]", encoding="utf-8")
            else:
                (session / "result.json").write_text(json.dumps({"complete": True, "segments": []}),
                                                      encoding="utf-8")

        with patch.object(worker, "HOME", vault), patch.object(worker, "processing_slot", lambda _: nullcontext()), \
                patch.object(worker, "run_stage", stage):
            self.assertEqual(worker.process_session(session), 0)
        # note_location derives a suffix when a session has a non-timestamp name.
        note = worker.note_location(session, {"vault_root": str(vault)}, metadata)[1]
        return session, note.read_text(encoding="utf-8")

    def test_worker_writes_single_playback_and_matching_complete_status(self):
        session, content = self.process_fixture()
        self.assertEqual(content.count("![["), 1)
        self.assertIn("![[audio/meeting.wav]]", content)
        state = worker.read_json(session / "status.json")
        self.assertEqual(state["state"], "completed")
        self.assertTrue(state["merged_audio"]["complete"])
        self.assertIsNone(state["merge_error"])
        self.assertTrue(worker.read_json(session / "merged_audio.json")["complete"])

    def test_worker_keeps_transcribing_when_merge_fails_and_preserves_warning(self):
        session, content = self.process_fixture(merge_fails=True)
        self.assertIn("合并录音暂未生成", content)
        self.assertNotIn("![[", content)
        self.assertTrue((session / "mic.wav").is_file())
        state = worker.read_json(session / "status.json")
        self.assertEqual(state["state"], "completed")
        self.assertTrue(state["merge_error"])
        self.assertIn("合并录音失败", state["message"])
        self.assertTrue(json.loads((session / "merge.log").read_text(encoding="utf-8"))["error"])

    def test_merged_recording_is_the_only_player_without_raw_track_links(self):
        with tempfile.TemporaryDirectory() as folder:
            vault = Path(folder)
            session = vault / "audio"
            session.mkdir()
            for name in ("mic.wav", "system.wav", "meeting.wav"):
                (session / name).write_bytes(b"audio")
            metadata = {"start_time": "2026-10-14T15:00:00+08:00", "duration_seconds": 60}
            content = worker.render_note(session, vault, metadata,
                                         mixed_audio={"path": "meeting.wav", "gain": 1.0})
            self.assertEqual(content.count("![["), 1)
            self.assertIn("![[audio/meeting.wav]]", content)
            self.assertNotIn("<details>", content)
            self.assertNotIn("mic.wav", content)
            self.assertNotIn("system.wav", content)

    def test_merge_error_is_visible_without_embedding_an_unverified_mix(self):
        with tempfile.TemporaryDirectory() as folder:
            vault = Path(folder)
            session = vault / "audio"
            session.mkdir()
            (session / "mic.wav").write_bytes(b"original")
            (session / "meeting.wav").write_bytes(b"incomplete")
            metadata = {"start_time": "2026-10-14T15:00:00+08:00"}
            content = worker.render_note(session, vault, metadata, merge_error="损坏的 WAV")
            self.assertIn("损坏的 WAV", content)
            self.assertNotIn("[[audio/mic.wav|麦克风分轨]]", content)
            self.assertNotIn("![[", content)

    def test_retry_preserves_user_notes_and_single_transcript(self):
        with tempfile.TemporaryDirectory() as folder:
            note = Path(folder) / "note.md"
            ownership = worker.save_note(note, "第一次转写")
            with note.open("a", encoding="utf-8") as stream:
                stream.write("用户手写：周五确认预算。\n")
            worker.save_note(note, "恢复后的完整转写", ownership)
            content = note.read_text(encoding="utf-8")
            self.assertIn("用户手写：周五确认预算。", content)
            self.assertIn("恢复后的完整转写", content)
            self.assertNotIn("第一次转写", content)
            self.assertNotIn(worker.BEGIN, content)
            self.assertNotIn(worker.END, content)
            self.assertNotIn("我的笔记", content)

    def test_generated_text_edit_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            note = Path(folder) / "note.md"
            ownership = worker.save_note(note, "转写正文")
            note.write_bytes(note.read_bytes().replace("正文".encode(), "正文的手写修订".encode()))
            original = note.read_bytes()
            with self.assertRaises(ValueError):
                worker.save_note(note, "新正文", ownership)
            self.assertEqual(note.read_bytes(), original)

    def test_new_note_is_compact_and_bookmarks_use_meeting_timeline(self):
        metadata = {"start_time": "2026-10-14T15:00:00+08:00", "duration_seconds": 80,
                    "bookmarks": [{"time_seconds": 62.1, "label": "预算确认"},
                                  {"time_seconds": 8, "label": "重点 1"}]}
        result = {"segments": [{"start": 1, "end": 3, "speaker": "mic_01", "status": "done",
                                "text": "会议\u200b  记录"}]}
        content = worker.render_note(Path("audio"), Path("vault"), metadata, result=result)
        for unwanted in ("录制时间", "时长：", "转写引擎", "我的笔记", worker.BEGIN, worker.END):
            self.assertNotIn(unwanted, content)
        self.assertIn("## 重点时刻", content)
        self.assertIn("[01:02] 预算确认", content)
        self.assertLess(content.index("[00:08]"), content.index("[01:02]"))
        self.assertNotIn("\u200b", content)

    def test_duplicate_suppression_requires_waveform_text_and_overlap_evidence(self):
        system = {"segment_id": "system_00000", "source": "system", "start": 1, "end": 4,
                  "speaker": "system_01", "status": "done", "text": "确认预算。"}
        mic = {**system, "segment_id": "mic_00000", "source": "mic", "speaker": "mic_01",
               "possible_duplicate_of": "system_00000", "waveform_similarity": 0.999}
        def render(rows):
            return worker.render_note(Path("audio"), Path("vault"),
                                      {"start_time": "2026-10-14T15:00:00+08:00"},
                                      result={"segments": rows})
        self.assertEqual(render([mic, system]).count("确认预算。"), 1)
        self.assertEqual(render([{**mic, "waveform_similarity": 0.8}, system]).count("确认预算。"), 2)
        self.assertIn("重新确认预算。", render([{**mic, "text": "重新确认预算。"}, system]))
        self.assertEqual(render([{**mic, "start": 4, "end": 7}, system]).count("确认预算。"), 2)
        self.assertEqual(render([{key: value for key, value in mic.items()
                                  if key != "possible_duplicate_of"}, system]).count("确认预算。"), 2)

    def test_global_speaker_uses_one_label_across_sources_and_unknown_stays_unassigned(self):
        base = {"start": 1, "end": 3, "status": "done", "speaker_global": True,
                "global_speaker": "speaker_01", "speaker_status": "matched"}
        rows = [{**base, "speaker": "mic_02", "text": "确认预算。"},
                {**base, "start": 4, "end": 6, "speaker": "system_01", "text": "周五完成。"},
                {**base, "start": 6, "end": 7, "speaker": "system_03", "text": "收到。",
                 "speaker_global": False, "speaker_status": "unknown", "global_speaker": None}]
        content = worker.render_note(Path("audio"), Path("vault"),
                                     {"start_time": "2026-10-14T15:00:00+08:00"}, result={"segments": rows})
        self.assertEqual(content.count("] 说话人 01**"), 2)
        self.assertNotIn("麦克风 · 说话人", content)
        self.assertIn("说话人待确认 · 电脑声音", content)
        self.assertNotIn("每条音轨内", content)

    def test_session_note_ownership_survives_progress_status_updates(self):
        with tempfile.TemporaryDirectory() as folder:
            session = Path(folder) / "work"
            session.mkdir()
            note = Path(folder) / "note.md"
            worker.status(session, "正在转写")
            worker.save_session_note(session, note, "第一次转写")
            worker.status(session, "正在处理", stage="asr")
            worker.save_session_note(session, note, "完整转写")
            self.assertIn("完整转写", note.read_text(encoding="utf-8"))
            self.assertNotIn("第一次转写", note.read_text(encoding="utf-8"))
            self.assertIn("managed_note", worker.read_json(session / "status.json"))

    def test_prepared_waveform_evidence_joins_results_without_changing_checkpoint(self):
        from asr_engine import read_segments
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "segments.json"
            clip = Path(folder) / "clip.wav"
            sf.write(clip, np.zeros(1600), 16000)
            row = {"start": 0, "end": 0.1, "speaker": "mic_01", "text_audio": "clip.wav",
                   "segment_id": "mic_00000", "possible_duplicate_of": "system_00000",
                   "waveform_similarity": 0.999, "speaker_global": True,
                   "global_speaker": "speaker_01", "speaker_status": "matched", "source": "mic"}
            source.write_text(json.dumps([row]), encoding="utf-8")
            actual = {**read_segments(source)[0], "status": "done", "text": "确认预算。"}
            result = {"segments": [actual]}
            merged = worker.transcript_with_evidence(Path(folder), result)["segments"][0]
            for key in ("segment_id", "possible_duplicate_of", "waveform_similarity", "speaker_global",
                        "global_speaker", "speaker_status", "source"):
                self.assertEqual(merged[key], row[key])
                self.assertNotIn(key, actual)
            for invalid in (-0.1, 1.01, "0.999", True, float("nan")):
                with self.subTest(invalid=invalid):
                    source.write_text(json.dumps([{**row, "waveform_similarity": invalid}]), encoding="utf-8")
                    merged = worker.transcript_with_evidence(Path(folder), result)["segments"][0]
                    self.assertNotIn("waveform_similarity", merged)
            source.write_text(json.dumps([{**row, "speaker": "mic_02"}]), encoding="utf-8")
            self.assertEqual(worker.transcript_with_evidence(Path(folder), result), result)

    def test_manifest_to_asr_checkpoint_to_note_retains_only_verified_digital_duplicate(self):
        import asr_engine
        import prepare_audio

        class Turn:
            start, end, speaker = 0.1, 1.9, 0

        class Diarizer:
            def process(self, samples):
                return SimpleNamespace(sort_by_start_time=lambda: [Turn()])

        class Backend:
            def __init__(self, *args):
                pass

            def transcribe(self, row):
                return "预算确认在周五完成。"

            def close(self):
                pass

        with tempfile.TemporaryDirectory() as folder:
            session = Path(folder)
            samples = (np.sin(np.arange(96000) * 2 * np.pi * 300 / 48000) * 0.03).astype(np.float32)
            for source in ("mic", "system"):
                sf.write(session / (source + ".wav"), samples, 48000)
            metadata = {"start_time": "2026-10-14T15:00:00+08:00"}
            (session / "session.json").write_text(json.dumps(metadata), encoding="utf-8")
            prepared = prepare_audio.prepare_session(session, "unused", "unused", diarizer=Diarizer())
            self.assertEqual(len(prepared), 2)
            self.assertIn("possible_duplicate_of", prepared[0])
            model = session / "model"
            model.mkdir()
            args = SimpleNamespace(segments=str(session / "segments.json"), output=None,
                                   status=str(session / "asr_status.json"), model_dir=str(model),
                                   context="", max_new_tokens=512, device="cpu", no_cpu_fallback=True)
            with patch.object(asr_engine, "release_cuda"):
                self.assertEqual(asr_engine.run_job(args, backend_factory=Backend), 0)
            before = (session / "result.json").read_bytes()
            result = worker.transcript_with_evidence(session, worker.read_json(session / "result.json"))
            note = worker.render_note(session, session, metadata, result=result)
            self.assertEqual(note.count("预算确认在周五完成。"), 1)
            self.assertEqual((session / "result.json").read_bytes(), before)
            with patch.object(asr_engine, "release_cuda"):
                self.assertEqual(asr_engine.run_job(args, backend_factory=lambda *args: self.fail("cached result")), 0)

    def test_damaged_markers_do_not_overwrite_note(self):
        with tempfile.TemporaryDirectory() as folder:
            note = Path(folder) / "note.md"
            note.write_text("只有用户自己的内容", encoding="utf-8")
            original = note.read_bytes()
            with self.assertRaises(ValueError):
                worker.save_note(note, "自动结果")
            self.assertEqual(note.read_bytes(), original)

    def test_retry_preserves_handwritten_bytes_including_bom_and_crlf(self):
        with tempfile.TemporaryDirectory() as folder:
            note = Path(folder) / "note.md"
            prefix = b"\xef\xbb\xbf# Personal title\r\n\r\n"
            suffix = "\r\n\r\n用户手写：周五确认预算。\r\n".encode("utf-8")
            note.write_bytes(prefix + worker.BEGIN.encode() + b"\r\nold text\r\n"
                             + worker.END.encode() + suffix)
            worker.save_note(note, "新的转写")
            current = note.read_bytes()
            self.assertTrue(current.startswith(prefix))
            self.assertTrue(current.endswith(suffix))
            self.assertNotIn(b"old text", current)

    def test_same_second_sessions_have_distinct_note_paths(self):
        with tempfile.TemporaryDirectory() as folder:
            vault = Path(folder)
            metadata = {"start_time": "2026-10-14T15:00:00+08:00"}
            config = {"vault_root": str(vault)}
            first = worker.note_location(vault / "附件" / "20261014-150000-aaaaaa", config, metadata)[1]
            second = worker.note_location(vault / "附件" / "20261014-150000-bbbbbb", config, metadata)[1]
            self.assertNotEqual(first, second)
            with self.assertRaises(ValueError):
                worker.note_location(vault.parent / "outside", config, metadata)

    def test_named_note_path_is_local_and_completed_retry_uses_it(self):
        with tempfile.TemporaryDirectory() as folder:
            vault = Path(folder)
            metadata = {"start_time": "2026-10-14T15:00:00+08:00", "note_filename": "预算确认.md"}
            config = {"vault_root": str(vault)}
            session = vault / "audio"
            self.assertEqual(worker.note_location(session, config, metadata)[1], vault / "会议记录" / "预算确认.md")
            for invalid in ("../other.md", "..\\other.md", "other.txt", ".md", "C:\\other.md"):
                with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                    worker.note_location(session, config, {**metadata, "note_filename": invalid})


class MinimalStorageTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.folder = Path(temporary.name)
        self.home = self.folder / "program"
        self.vault = self.folder / "vault"
        self.session = self.home / "work" / "20261014-150000-aaaaaa"
        self.session.mkdir(parents=True)
        self.vault.mkdir()
        self.output = self.vault / "会议记录" / "附件" / self.session.name
        self.metadata = {"start_time": "2026-10-14T15:00:00+08:00",
                         "duration_seconds": 0.1, "storage_mode": "minimal",
                         "audio_output_dir": str(self.output)}
        self.config = {"vault_root": str(self.vault)}
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf-8")
        (self.session / "session.json").write_text(json.dumps(self.metadata), encoding="utf-8")
        sf.write(self.session / "mic.wav", np.full(4800, 0.2), 48000, subtype="FLOAT")
        sf.write(self.session / "system.wav", np.zeros((4800, 2)), 48000, subtype="FLOAT")
        self.patch_home = patch.object(worker, "HOME", self.home)
        self.patch_home.start()
        self.addCleanup(self.patch_home.stop)

    def stage(self, _session, _command, name):
        if name == "prepare":
            clips = self.session / "clips"
            clips.mkdir(exist_ok=True)
            sf.write(clips / "mic_00000.wav", np.full(1600, 0.2), 16000)
            (self.session / "segments.json").write_text("[]", encoding="utf-8")
            (self.session / "prepare_summary.json").write_text("{}", encoding="utf-8")
        else:
            (self.session / "result.json").write_text(json.dumps({"complete": True, "segments": []}),
                                                     encoding="utf-8")
            (self.session / "asr.log").write_text("test log", encoding="utf-8")

    def process(self, stage=None):
        with patch.object(worker, "processing_slot", lambda _: nullcontext()), \
                patch.object(worker, "run_stage", stage or self.stage):
            return worker.process_session(self.session)

    def test_success_publishes_one_audio_and_md_and_compacts_work(self):
        self.assertEqual(self.process(), 0)
        note = worker.note_location(self.session, self.config, self.metadata)[1]
        content = note.read_text(encoding="utf-8")
        relative = (self.output / "meeting.wav").relative_to(self.vault).as_posix()
        self.assertIn(f"![[{relative}]]", content)
        self.assertEqual(content.count("![["), 1)
        self.assertNotIn("mic.wav", content)
        self.assertEqual(sorted(path.suffix for path in self.vault.rglob("*") if path.is_file()),
                         [".md", ".wav"])
        self.assertEqual(sf.info(self.output / "meeting.wav").frames, 4800)
        self.assertEqual(sorted(path.name for path in self.session.iterdir()),
                         ["session.json", "status.json"])
        self.assertTrue(worker.read_json(self.session / "status.json")["artifacts_compacted"])

    def test_recorded_denoise_mode_is_used_after_configuration_changes(self):
        self.metadata["asr_denoise"] = "off"
        (self.session / "session.json").write_text(json.dumps(self.metadata), encoding="utf-8")
        commands = []

        def stage(session, command, name):
            if name == "prepare":
                commands.append(command)
            self.stage(session, command, name)

        self.assertEqual(self.process(stage), 0)
        self.assertEqual(commands[0][commands[0].index("--asr-denoise") + 1], "off")

    def test_legacy_retry_keeps_prepared_audio_mode_without_recreating_clips(self):
        self.config["asr_denoise"] = "off"
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf-8")

        def fail_asr(session, command, name):
            if name == "asr":
                raise RuntimeError("fixture interrupted ASR")
            self.stage(session, command, name)

        self.assertEqual(self.process(fail_asr), 1)
        cache = worker.read_json(self.session / "prepared_input.json")
        self.assertEqual(cache["asr_denoise"], "off")
        # Simulate a cache from a release predating the optional filter.
        cache.pop("asr_denoise")
        worker.atomic_json(self.session / "prepared_input.json", cache)
        self.config["asr_denoise"] = "light"
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf-8")
        names = []

        def resume(session, command, name):
            names.append(name)
            self.stage(session, command, name)

        self.assertEqual(self.process(resume), 0)
        self.assertEqual(names, ["asr"])

    def test_completed_retry_keeps_handwritten_md_without_reloading_model(self):
        self.assertEqual(self.process(), 0)
        note = worker.note_location(self.session, self.config, self.metadata)[1]
        with note.open("a", encoding="utf-8") as stream:
            stream.write("用户手写：周五确认预算。\n")
        content = note.read_bytes()
        audio_modified = (self.output / "meeting.wav").stat().st_mtime_ns
        self.config.update(transcription_mode="api", api_base_url="https://provider.example/v1",
                           api_model="new-api-model")
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf-8")
        with patch.object(worker, "run_stage", side_effect=AssertionError("must not load a model")):
            self.assertEqual(worker.process_session(self.session), 0)
        self.assertEqual(note.read_bytes(), content)
        self.assertEqual((self.output / "meeting.wav").stat().st_mtime_ns, audio_modified)

    def test_completed_named_meeting_retries_without_recreating_old_filename(self):
        self.assertEqual(self.process(), 0)
        old_note = worker.note_location(self.session, self.config, self.metadata)[1]
        self.metadata["note_filename"] = "预算确认.md"
        (self.session / "session.json").write_text(json.dumps(self.metadata), encoding="utf-8")
        new_note = worker.note_location(self.session, self.config, self.metadata)[1]
        old_note.replace(new_note)
        state = worker.read_json(self.session / "status.json")
        state["note_path"] = str(new_note)
        state["managed_note"]["path"] = str(new_note)
        worker.atomic_json(self.session / "status.json", state)
        before = new_note.read_bytes()
        with patch.object(worker, "run_stage", side_effect=AssertionError("completed meeting needs no model")):
            self.assertEqual(worker.process_session(self.session), 0)
        self.assertFalse(old_note.exists())
        self.assertEqual(new_note.read_bytes(), before)

    def test_legacy_local_session_cannot_be_uploaded_after_config_switches_to_api(self):
        self.config.update(transcription_mode="api", api_base_url="https://provider.example/v1",
                           api_model="api-model")
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf-8")
        note = worker.note_location(self.session, self.config, self.metadata)[1]
        worker.save_session_note(self.session, note, "已有转写内容，不能因配置改变而覆盖")
        self.output.mkdir(parents=True)
        sf.write(self.output / "meeting.wav", np.zeros((48, 2)), 48000, subtype="PCM_16")
        note_before = note.read_bytes()
        audio_before = (self.output / "meeting.wav").read_bytes()
        original = (self.session / "mic.wav").read_bytes()
        commands = []

        def record_stage(session, command, name):
            commands.append(command)
            self.stage(session, command, name)

        self.assertEqual(self.process(record_stage), 1)
        self.assertEqual(commands, [], "旧本地场次不得启动准备或 API 转写进程")
        self.assertEqual(note.read_bytes(), note_before)
        self.assertEqual((self.output / "meeting.wav").read_bytes(), audio_before)
        self.assertEqual((self.session / "mic.wav").read_bytes(), original)
        state = worker.read_json(self.session / "status.json")
        self.assertEqual(state["state"], "error")
        self.assertIn("恢复", state["message"])
        self.config["transcription_mode"] = "local"
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf-8")
        self.assertEqual(self.process(), 0)

    def test_api_session_cannot_use_a_different_model_from_its_recorded_settings(self):
        self.metadata.update(transcription_mode="api", transcription_model="recorded-model",
                             transcription_api_base_url="https://provider.example/v1")
        self.config.update(transcription_mode="api", api_base_url="https://provider.example/v1",
                           api_model="different-model")
        (self.session / "session.json").write_text(json.dumps(self.metadata), encoding="utf-8")
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf-8")
        commands = []

        def record_stage(session, command, name):
            commands.append(command)
            self.stage(session, command, name)

        self.assertEqual(self.process(record_stage), 1)
        self.assertEqual(commands, [])
        self.assertFalse(self.output.exists())
        self.assertTrue((self.session / "mic.wav").is_file())
        self.assertIn("恢复", worker.read_json(self.session / "status.json")["message"])

    def test_api_session_cannot_change_or_omit_its_recorded_service(self):
        self.metadata.update(transcription_mode="api", transcription_model="api-model")
        self.config.update(transcription_mode="api", api_base_url="https://current-provider.example/v1/",
                           api_model="api-model")
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf-8")
        original = (self.session / "mic.wav").read_bytes()
        for recorded_service in ("https://recorded-provider.example/v1", None):
            with self.subTest(recorded_service=recorded_service):
                if recorded_service is None:
                    self.metadata.pop("transcription_api_base_url", None)
                else:
                    self.metadata["transcription_api_base_url"] = recorded_service
                (self.session / "session.json").write_text(json.dumps(self.metadata), encoding="utf-8")
                commands = []

                def record_stage(session, command, name):
                    commands.append(command)
                    self.stage(session, command, name)

                self.assertEqual(self.process(record_stage), 1)
                self.assertEqual(commands, [], "服务快照缺失或改变时不得上传")
                self.assertFalse(self.output.exists())
                self.assertEqual((self.session / "mic.wav").read_bytes(), original)
                state = worker.read_json(self.session / "status.json")
                self.assertEqual(state["state"], "error")
                self.assertIn("恢复", state["message"])
                self.assertNotIn("provider.example", json.dumps(state))

    def test_api_session_accepts_its_recorded_service_with_a_trailing_slash_in_configuration(self):
        self.metadata.update(transcription_mode="api", transcription_model="api-model",
                             transcription_api_base_url="https://provider.example/v1")
        self.config.update(transcription_mode="api", api_base_url="https://provider.example/v1/",
                           api_model="api-model")
        (self.session / "session.json").write_text(json.dumps(self.metadata), encoding="utf-8")
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf-8")
        commands = []

        def record_stage(session, command, name):
            commands.append(command)
            self.stage(session, command, name)

        self.assertEqual(self.process(record_stage), 0)
        self.assertEqual(Path(commands[-1][1]).name, "api_asr.py")
        self.assertTrue(worker.read_json(self.session / "status.json")["artifacts_compacted"])

    def test_plain_markdown_success_publishes_relative_audio_link_and_compacts_work(self):
        self.metadata["note_link_style"] = "markdown"
        (self.session / "session.json").write_text(json.dumps(self.metadata), encoding="utf-8")
        self.assertEqual(self.process(), 0)
        note = worker.note_location(self.session, self.config, self.metadata)[1]
        content = note.read_text(encoding="utf-8")
        self.assertIn(f"[会议录音](%E9%99%84%E4%BB%B6/{self.session.name}/meeting.wav)", content)
        self.assertNotIn("![[", content)
        self.assertEqual(sf.info(self.output / "meeting.wav").frames, 4800)
        self.assertEqual(sorted(path.suffix for path in self.vault.rglob("*") if path.is_file()),
                         [".md", ".wav"])
        self.assertEqual(sorted(path.name for path in self.session.iterdir()),
                         ["session.json", "status.json"])
        self.assertTrue(worker.read_json(self.session / "status.json")["artifacts_compacted"])

    def test_asr_failure_preserves_original_and_cache(self):
        original = (self.session / "mic.wav").read_bytes()

        def failed_stage(session, command, name):
            if name == "asr":
                raise RuntimeError("fixture ASR failed")
            self.stage(session, command, name)

        self.assertEqual(self.process(failed_stage), 1)
        self.assertEqual((self.session / "mic.wav").read_bytes(), original)
        self.assertTrue((self.session / "clips" / "mic_00000.wav").is_file())
        self.assertFalse(worker.read_json(self.session / "status.json").get("artifacts_compacted", False))

    def test_asr_failure_keeps_public_audio_ownership_and_allows_unchanged_retry(self):
        def failed_stage(session, command, name):
            if name == "asr":
                raise RuntimeError("fixture interrupted after publishing")
            self.stage(session, command, name)

        self.assertEqual(self.process(failed_stage), 1)
        ownership = worker.read_json(self.session / "status.json")["published_audio"]
        audio = self.output / "meeting.wav"
        original = audio.read_bytes()
        self.assertEqual(ownership["path"], str(audio))
        self.assertEqual(ownership["sha256"], worker.file_sha256(audio))
        self.assertEqual(self.process(), 0)
        self.assertEqual(audio.read_bytes(), original)
        self.assertTrue(worker.read_json(self.session / "status.json")["artifacts_compacted"])

    def test_merge_failure_keeps_original_even_if_transcript_completed(self):
        (self.session / "system.wav").write_bytes(b"bad WAV")
        original = (self.session / "mic.wav").read_bytes()
        self.assertEqual(self.process(), 0)
        self.assertEqual((self.session / "mic.wav").read_bytes(), original)
        self.assertEqual((self.session / "system.wav").read_bytes(), b"bad WAV")
        self.assertTrue((self.session / "result.json").is_file())
        self.assertFalse(worker.read_json(self.session / "status.json").get("artifacts_compacted", False))

    def test_damaged_user_note_blocks_cleanup_and_is_never_overwritten(self):
        note = worker.note_location(self.session, self.config, self.metadata)[1]
        note.parent.mkdir(parents=True)
        note.write_text("只有用户自己的内容", encoding="utf-8")
        original_note = note.read_bytes()
        original_audio = (self.session / "mic.wav").read_bytes()
        self.assertEqual(self.process(), 1)
        self.assertEqual(note.read_bytes(), original_note)
        self.assertEqual((self.session / "mic.wav").read_bytes(), original_audio)

    def test_unknown_files_and_unknown_clip_names_survive_compaction(self):
        (self.session / "keep.txt").write_text("manual", encoding="utf-8")
        clips = self.session / "clips"
        clips.mkdir()
        (clips / "manual.wav").write_bytes(b"unknown audio")
        self.assertEqual(self.process(), 0)
        self.assertEqual((self.session / "keep.txt").read_text(encoding="utf-8"), "manual")
        self.assertEqual((clips / "manual.wav").read_bytes(), b"unknown audio")
        self.assertFalse((clips / "mic_00000.wav").exists())

    def test_arbitrary_output_path_and_non_direct_work_child_are_rejected(self):
        self.metadata["audio_output_dir"] = str(self.folder / "outside")
        with self.assertRaises(ValueError):
            worker.note_location(self.session, self.config, self.metadata)
        self.metadata["audio_output_dir"] = str(self.output)
        with self.assertRaises(ValueError):
            worker.note_location(self.home / "work" / "nested" / self.session.name,
                                 self.config, self.metadata)

    def test_output_symlink_is_rejected_before_publication(self):
        outside = self.folder / "outside"
        outside.mkdir()
        self.output.parent.mkdir(parents=True)
        try:
            self.output.symlink_to(outside, target_is_directory=True)
        except OSError as error:
            self.skipTest(f"symlinks unavailable: {error}")
        with self.assertRaises(ValueError):
            worker.note_location(self.session, self.config, self.metadata)
        self.assertEqual(list(outside.iterdir()), [])

    def test_windows_reparse_attribute_is_rejected(self):
        original_lstat = Path.lstat

        def reparse_lstat(path, *args, **kwargs):
            info = original_lstat(path, *args, **kwargs)
            if path == self.session.parent:
                return SimpleNamespace(st_mode=info.st_mode, st_file_attributes=0x400)
            return info

        with patch.object(Path, "lstat", reparse_lstat):
            with self.assertRaises(ValueError):
                worker.note_location(self.session, self.config, self.metadata)
        self.assertTrue((self.session / "mic.wav").is_file())

    def test_interrupted_cleanup_can_resume_without_sources_or_model_loading(self):
        original_unlink = Path.unlink

        def fail_last_cleanup(path, *args, **kwargs):
            if path == self.session / "result.json":
                raise PermissionError("fixture cleanup blocked")
            return original_unlink(path, *args, **kwargs)

        with patch.object(Path, "unlink", fail_last_cleanup):
            self.assertEqual(self.process(), 0)
        status = worker.read_json(self.session / "status.json")
        self.assertEqual(status["state"], "completed")
        self.assertFalse(status["artifacts_compacted"])
        self.assertIn("fixture cleanup blocked", status["cleanup_error"])
        self.assertFalse((self.session / "mic.wav").exists())
        with patch.object(worker, "run_stage", side_effect=AssertionError("must not load a model")):
            self.assertEqual(worker.process_session(self.session), 0)
        self.assertTrue(worker.read_json(self.session / "status.json")["artifacts_compacted"])
        self.assertFalse((self.session / "result.json").exists())

    def interrupt_cleanup_before_sources(self):
        original_unlink = Path.unlink

        def locked_log(path, *args, **kwargs):
            if path == self.session / "asr.log":
                raise PermissionError("fixture log remains open")
            return original_unlink(path, *args, **kwargs)

        with patch.object(Path, "unlink", locked_log):
            self.assertEqual(self.process(), 0)
        self.assertFalse(worker.read_json(self.session / "status.json")["artifacts_compacted"])
        self.assertTrue((self.session / "mic.wav").is_file())

    def test_cleanup_retry_reports_invalid_audio_and_recovers_without_model_loading(self):
        self.interrupt_cleanup_before_sources()
        audio = self.output / "meeting.wav"
        before_audio = audio.read_bytes()
        before_source = (self.session / "mic.wav").read_bytes()
        audio.write_bytes(b"corrupted")
        with patch.object(worker, "run_stage", side_effect=AssertionError("must not load a model")):
            self.assertEqual(worker.process_session(self.session), 1)
        state = worker.read_json(self.session / "status.json")
        self.assertEqual(state["state"], "completed")
        self.assertTrue(state["result_complete"])
        self.assertTrue(state["artifact_error"])
        self.assertTrue(state["cleanup_error"])
        self.assertIn("成品", state["message"])
        self.assertEqual((self.session / "mic.wav").read_bytes(), before_source)
        audio.write_bytes(before_audio)
        with patch.object(worker, "run_stage", side_effect=AssertionError("must not load a model")):
            self.assertEqual(worker.process_session(self.session), 0)
        state = worker.read_json(self.session / "status.json")
        self.assertTrue(state["artifacts_compacted"])
        self.assertNotIn("artifact_error", state)
        self.assertNotIn("cleanup_error", state)
        self.assertNotIn("需要检查", state["message"])

    def test_cleanup_retry_rejects_valid_same_size_replaced_audio_before_deleting_sources(self):
        self.interrupt_cleanup_before_sources()
        audio = self.output / "meeting.wav"
        before_source = (self.session / "mic.wav").read_bytes()
        original_size = audio.stat().st_size
        sf.write(audio, np.full((4800, 2), .7), 48000, subtype="PCM_16")
        replaced = audio.read_bytes()
        self.assertEqual(audio.stat().st_size, original_size)
        with patch.object(worker, "run_stage", side_effect=AssertionError("must not load a model")):
            self.assertEqual(worker.process_session(self.session), 1)
        self.assertEqual((self.session / "mic.wav").read_bytes(), before_source)
        self.assertEqual(audio.read_bytes(), replaced)
        self.assertTrue((self.session / "result.json").is_file())
        self.assertFalse(worker.read_json(self.session / "status.json")["artifacts_compacted"])

    def test_preexisting_unowned_public_wav_is_not_overwritten(self):
        self.output.mkdir(parents=True)
        audio = self.output / "meeting.wav"
        sf.write(audio, np.full((4800, 2), .7), 48000, subtype="PCM_16")
        original = audio.read_bytes()
        before_source = (self.session / "mic.wav").read_bytes()
        self.process()
        self.assertEqual(audio.read_bytes(), original)
        self.assertEqual((self.session / "mic.wav").read_bytes(), before_source)
        state = worker.read_json(self.session / "status.json")
        self.assertFalse(state.get("artifacts_compacted"))
        self.assertTrue(state.get("merge_error"))

    def test_legacy_completed_status_without_content_hash_cannot_delete_recovery_tracks(self):
        self.interrupt_cleanup_before_sources()
        state = worker.read_json(self.session / "status.json")
        state["merged_audio"].pop("output_sha256")
        worker.atomic_json(self.session / "status.json", state)
        original = (self.session / "mic.wav").read_bytes()
        with patch.object(worker, "run_stage", side_effect=AssertionError("must not load a model")):
            self.assertEqual(worker.process_session(self.session), 0)
        state = worker.read_json(self.session / "status.json")
        self.assertFalse(state["artifacts_compacted"])
        self.assertIn("内容校验", state["cleanup_error"])
        self.assertEqual((self.session / "mic.wav").read_bytes(), original)
        self.assertTrue((self.session / "result.json").is_file())

    def test_completed_cleanup_retry_accepts_changed_transcription_configuration(self):
        self.interrupt_cleanup_before_sources()
        note = worker.note_location(self.session, self.config, self.metadata)[1]
        note_before = note.read_bytes()
        audio_before = (self.output / "meeting.wav").read_bytes()
        self.config.update(transcription_mode="api", api_base_url="https://provider.example/v1",
                           api_model="new-api-model")
        (self.home / "config.json").write_text(json.dumps(self.config), encoding="utf-8")
        with patch.object(worker, "run_stage", side_effect=AssertionError("must not load a model")):
            self.assertEqual(worker.process_session(self.session), 0)
        self.assertEqual(note.read_bytes(), note_before)
        self.assertEqual((self.output / "meeting.wav").read_bytes(), audio_before)
        self.assertFalse((self.session / "mic.wav").exists())
        self.assertTrue(worker.read_json(self.session / "status.json")["artifacts_compacted"])

    def test_cleanup_retry_reports_damaged_md_without_overwriting_or_deleting_sources(self):
        self.interrupt_cleanup_before_sources()
        note = worker.note_location(self.session, self.config, self.metadata)[1]
        before_note = note.read_bytes()
        before_source = (self.session / "mic.wav").read_bytes()
        modified_note = before_note.replace("未检测到可转写的讲话".encode(), "用户修订：没有讲话".encode())
        modified_note += "\r\n手写内容保持原样。\r\n".encode("utf-8")
        note.write_bytes(modified_note)
        with patch.object(worker, "run_stage", side_effect=AssertionError("must not load a model")):
            self.assertEqual(worker.process_session(self.session), 1)
        state = worker.read_json(self.session / "status.json")
        self.assertEqual(state["state"], "completed")
        self.assertTrue(state["result_complete"])
        self.assertTrue(state["artifact_error"])
        self.assertEqual(note.read_bytes(), modified_note)
        self.assertEqual((self.session / "mic.wav").read_bytes(), before_source)
        note.write_bytes(before_note)
        with patch.object(worker, "run_stage", side_effect=AssertionError("must not load a model")):
            self.assertEqual(worker.process_session(self.session), 0)
        state = worker.read_json(self.session / "status.json")
        self.assertTrue(state["artifacts_compacted"])
        self.assertNotIn("artifact_error", state)
        self.assertEqual(note.read_bytes(), before_note)

    def test_missing_completed_audio_is_reported_without_retranscribing(self):
        self.assertEqual(self.process(), 0)
        (self.output / "meeting.wav").write_bytes(b"corrupted")
        with patch.object(worker, "run_stage", side_effect=AssertionError("must not load a model")):
            self.assertEqual(worker.process_session(self.session), 1)
        note = worker.note_location(self.session, self.config, self.metadata)[1]
        self.assertTrue(note.is_file())

    def legacy_fixture(self):
        legacy = self.vault / "会议记录" / "附件" / self.session.name
        legacy.mkdir(parents=True)
        (self.session / "mic.wav").replace(legacy / "mic.wav")
        self.session = legacy
        self.output = legacy
        self.metadata.pop("storage_mode")
        self.metadata.pop("audio_output_dir")
        (legacy / "session.json").write_text(json.dumps(self.metadata), encoding="utf-8")

    def test_public_compaction_preserves_legacy_mix_handwritten_note_and_unknown_files(self):
        self.legacy_fixture()
        (self.session / "keep.txt").write_text("manual", encoding="utf-8")
        self.assertEqual(self.process(), 0)
        note = worker.note_location(self.session, self.config, self.metadata)[1]
        with note.open("ab") as stream:
            stream.write("\r\n手写笔记。\r\n".encode("utf-8"))
        before = note.read_bytes()
        state = worker.read_json(self.session / "status.json")
        removed = worker.compact_session(self.session, self.config, self.metadata, state)
        self.assertIn("mic.wav", removed)
        self.assertIn("result.json", removed)
        self.assertNotIn("meeting.wav", removed)
        self.assertEqual(note.read_bytes(), before)
        self.assertTrue((self.session / "meeting.wav").is_file())
        self.assertTrue((self.session / "session.json").is_file())
        self.assertTrue((self.session / "status.json").is_file())
        self.assertEqual((self.session / "keep.txt").read_text(encoding="utf-8"), "manual")

    def test_public_compaction_requires_completed_result_and_unchanged_status(self):
        self.legacy_fixture()
        self.assertEqual(self.process(), 0)
        state = worker.read_json(self.session / "status.json")
        original_audio = (self.session / "mic.wav").read_bytes()
        # A stale completion flag must not override an existing incomplete result.
        worker.atomic_json(self.session / "result.json", {"complete": False})
        with self.assertRaises(ValueError):
            worker.compact_session(self.session, self.config, self.metadata, state)
        self.assertEqual((self.session / "mic.wav").read_bytes(), original_audio)
        incomplete = {**state, "result_complete": False}
        worker.atomic_json(self.session / "status.json", incomplete)
        worker.atomic_json(self.session / "result.json", {"complete": False})
        with self.assertRaises(ValueError):
            worker.compact_session(self.session, self.config, self.metadata, incomplete)
        self.assertEqual((self.session / "mic.wav").read_bytes(), original_audio)
        worker.atomic_json(self.session / "status.json", {**state, "state": "processing"})
        with self.assertRaises(ValueError):
            worker.compact_session(self.session, self.config, self.metadata, state)
        self.assertEqual((self.session / "mic.wav").read_bytes(), original_audio)


if __name__ == "__main__":
    unittest.main()
