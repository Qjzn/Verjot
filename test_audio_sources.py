import tempfile
import unittest
from pathlib import Path


class CaptureSourcesTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.session = Path(self.temporary.name)

    def test_legacy_detects_existing_tracks_without_inventing_missing_inputs(self):
        from audio_sources import capture_sources
        (self.session / "system.wav").touch()
        self.assertEqual(capture_sources(self.session, {}), [
            {"source": "system", "path": "system.wav", "kind": "loopback"}])

    def test_manifest_cannot_escape_or_alias_audio_files(self):
        from audio_sources import capture_sources
        for path in ("../system.wav", "sub/system.wav", "meeting.wav", "C:\\system.wav"):
            with self.subTest(path=path), self.assertRaises(ValueError):
                capture_sources(self.session, {"sources": [
                    {"source": "system", "path": path, "kind": "loopback"}]}, require_files=False)

    def test_duplicate_or_mismatched_source_kind_is_rejected(self):
        from audio_sources import capture_sources
        row = {"source": "system", "path": "system.wav", "kind": "loopback"}
        for rows in ([row, row], [{**row, "kind": "microphone"}], []):
            with self.subTest(rows=rows), self.assertRaises(ValueError):
                capture_sources(self.session, {"sources": rows}, require_files=False)


if __name__ == "__main__":
    unittest.main()
