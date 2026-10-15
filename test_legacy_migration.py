"""Exercise the explicit legacy migration tools on disposable recordings only."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import numpy as np
import soundfile as sf

import meeting_worker as worker


def load_script(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).parent / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


preparation = load_script("legacy_preparation_test", "Prepare-Legacy-Merged-Meetings.py")
compaction = preparation.compaction


class LegacyMigrationTests(unittest.TestCase):
    def fixture(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        home, vault = root / "home", root / "notes"
        home.mkdir()
        session = vault / "会议记录" / "附件" / "20261014-150000"
        session.mkdir(parents=True)
        config = {"vault_root": str(vault), "meeting_folder": "会议记录"}
        metadata = {"state": "recorded", "start_time": "2026-10-14T15:00:00+08:00",
                    "duration_seconds": 0.12}
        worker.atomic_json(session / "session.json", metadata)
        worker.atomic_json(session / "result.json", {"complete": True, "segments": []})
        for name, level in (("mic", 0.1), ("system", 0.2)):
            sf.write(session / (name + ".wav"), np.full(5760, level), 48000, subtype="FLOAT")
        note = worker.note_location(session, config, metadata)[1]
        note.write_text("# 用户会议标题\n\n" + worker.BEGIN + "\n旧转写\n" + worker.END
                        + "\n\n## 我的笔记\n手写行动：周五确认预算。\n", encoding="utf-8")
        worker.atomic_json(session / "status.json", {"state": "completed", "note_path": str(note)})
        return home, session, config, note

    def test_preparation_refuses_existing_unowned_recording_without_changing_any_file(self):
        home, session, config, note = self.fixture()
        recording = session / "meeting.wav"
        recording.write_bytes(b"user recording must survive")
        before = {p.name: p.read_bytes() for p in session.iterdir()}
        note_before = note.read_bytes()
        error = None
        with patch.object(worker, "HOME", home):
            try:
                plan = preparation.eligible_session(session, config, home, running=set())
                preparation.apply_session(plan, config, home, running=set())
            except ValueError as caught:
                error = caught
        self.assertEqual(recording.read_bytes(), b"user recording must survive")
        self.assertIsNotNone(error)
        self.assertRegex(str(error), "already exists|already has")
        self.assertEqual(before, {p.name: p.read_bytes() for p in session.iterdir()})
        self.assertEqual(note_before, note.read_bytes())

    def test_prepare_then_compact_preserves_manual_notes_and_verified_recording(self):
        home, session, config, note = self.fixture()
        with patch.object(worker, "HOME", home):
            plan = preparation.eligible_session(session, config, home, running=set())
            prepared = preparation.apply_session(plan, config, home, running=set())
            self.assertTrue(prepared["prepared"])
            state = worker.read_json(session / "status.json")
            expected_audio = worker.file_sha256(session / "meeting.wav")
            self.assertEqual(state["merged_audio"]["output_sha256"], expected_audio)
            self.assertIn("managed_note", state)
            note_before = note.read_bytes()
            plan = compaction.evaluate_session(session, config, home, running=set())
            result = compaction.apply_session(plan, config, home, running=set())
        self.assertTrue(result["cleanup_complete"])
        self.assertEqual(worker.file_sha256(session / "meeting.wav"), expected_audio)
        self.assertEqual(note.read_bytes(), note_before)
        self.assertEqual({p.name for p in session.iterdir()}, {"meeting.wav"})
        migrated = worker.read_json(home / "work" / session.name / "status.json")
        self.assertIn("managed_note", migrated)
        worker.managed_note_range(note, note.read_bytes(), migrated["managed_note"])
        content = note.read_text(encoding="utf-8")
        self.assertIn("# 用户会议标题", content)
        self.assertIn("手写行动：周五确认预算。", content)
        self.assertNotIn(worker.BEGIN, content)
        self.assertNotIn(worker.END, content)
        self.assertNotIn("## 我的笔记", content)

    def test_compaction_rejects_old_unverified_checksum_before_any_changes(self):
        home, session, config, note = self.fixture()
        merged = worker.merge_session(session, worker.read_json(session / "session.json"))
        state = worker.read_json(session / "status.json")
        worker.atomic_json(session / "status.json", {**state, "merged_audio": merged})
        before = {p.name: p.read_bytes() for p in session.iterdir()}
        note_before = note.read_bytes()
        with patch.object(worker, "HOME", home):
            with self.assertRaisesRegex(ValueError, "no trusted content checksum"):
                compaction.evaluate_session(session, config, home, running=set())
        self.assertEqual(before, {p.name: p.read_bytes() for p in session.iterdir()})
        self.assertEqual(note_before, note.read_bytes())
        self.assertFalse((home / "work").exists())

    def test_compaction_rejects_same_size_replaced_audio_without_removing_originals(self):
        home, session, config, note = self.fixture()
        with patch.object(worker, "HOME", home):
            plan = preparation.eligible_session(session, config, home, running=set())
            preparation.apply_session(plan, config, home, running=set())
            recording = session / "meeting.wav"
            size = recording.stat().st_size
            original = recording.read_bytes()
            sf.write(recording, np.full((5760, 2), -0.4), 48000, subtype="PCM_16",
                     format=sf.info(recording).format)
            self.assertEqual(size, recording.stat().st_size)
            self.assertNotEqual(original, recording.read_bytes())
            before = {p.name: p.read_bytes() for p in session.iterdir()}
            note_before = note.read_bytes()
            with self.assertRaisesRegex(ValueError, "content changed"):
                compaction.evaluate_session(session, config, home, running=set())
        self.assertEqual(before, {p.name: p.read_bytes() for p in session.iterdir()})
        self.assertEqual(note_before, note.read_bytes())

    def test_renamed_meeting_keeps_its_note_identity_after_migration(self):
        home, session, config, note = self.fixture()
        renamed = note.with_name("周五预算会.md")
        note.rename(renamed)
        metadata = worker.read_json(session / "session.json")
        metadata.update(note_filename=renamed.name, title="周五预算会")
        worker.atomic_json(session / "session.json", metadata)
        worker.atomic_json(session / "status.json", {"state": "completed", "note_path": str(renamed)})
        with patch.object(worker, "HOME", home):
            plan = preparation.eligible_session(session, config, home, running=set())
            preparation.apply_session(plan, config, home, running=set())
            plan = compaction.evaluate_session(session, config, home, running=set())
            compaction.apply_session(plan, config, home, running=set())
            migrated = home / "work" / session.name
            metadata = worker.read_json(migrated / "session.json")
            self.assertEqual(metadata.get("title"), "周五预算会")
            self.assertEqual(worker.note_location(migrated, config, metadata)[1], renamed)
            worker.verified_artifacts(migrated, config, metadata, worker.read_json(migrated / "status.json"))

    def test_status_write_failure_restores_note_and_allows_non_destructive_retry(self):
        home, session, config, note = self.fixture()
        before = note.read_bytes()
        original_status = (session / "status.json").read_bytes()
        real_atomic_json = worker.atomic_json

        def fail_status(path, value):
            if path == session / "status.json":
                raise OSError("Injected status commit failure")
            real_atomic_json(path, value)

        with patch.object(worker, "HOME", home):
            plan = preparation.eligible_session(session, config, home, running=set())
            with patch.object(worker, "atomic_json", side_effect=fail_status):
                with self.assertRaisesRegex(OSError, "Injected status commit failure"):
                    preparation.apply_session(plan, config, home, running=set())
            self.assertEqual(note.read_bytes(), before)
            self.assertEqual((session / "status.json").read_bytes(), original_status)
            for name in ("mic.wav", "system.wav", "result.json", "meeting.wav"):
                self.assertTrue((session / name).is_file())
            recording = (session / "meeting.wav").read_bytes()
            plan = preparation.eligible_session(session, config, home, running=set())
            preparation.apply_session(plan, config, home, running=set())
            self.assertEqual((session / "meeting.wav").read_bytes(), recording)
            self.assertEqual(worker.read_json(session / "status.json")["state"], "completed")

    def test_status_failure_does_not_rollback_concurrent_user_edits(self):
        home, session, config, note = self.fixture()
        real_atomic_json = worker.atomic_json

        def fail_after_user_edit(path, value):
            if path == session / "status.json":
                with note.open("ab") as stream:
                    stream.write("用户刚添加的决定。\n".encode())
                raise OSError("Injected status commit failure after user edit")
            real_atomic_json(path, value)

        with patch.object(worker, "HOME", home):
            plan = preparation.eligible_session(session, config, home, running=set())
            with patch.object(worker, "atomic_json", side_effect=fail_after_user_edit):
                with self.assertRaisesRegex(OSError, "after user edit"):
                    preparation.apply_session(plan, config, home, running=set())
        self.assertIn("用户刚添加的决定。", note.read_text(encoding="utf-8"))
        for name in ("mic.wav", "system.wav", "result.json", "meeting.wav"):
            self.assertTrue((session / name).is_file())

    def test_post_commit_verification_failure_keeps_note_and_status_consistent(self):
        home, session, config, note = self.fixture()
        with patch.object(worker, "HOME", home):
            plan = preparation.eligible_session(session, config, home, running=set())
            with patch.object(worker, "verified_artifacts", side_effect=OSError("Injected post-commit check failure")):
                with self.assertRaisesRegex(OSError, "post-commit"):
                    preparation.apply_session(plan, config, home, running=set())
        state = worker.read_json(session / "status.json")
        worker.managed_note_range(note, note.read_bytes(), state["managed_note"])
        self.assertTrue((session / "mic.wav").is_file())


if __name__ == "__main__":
    unittest.main()
