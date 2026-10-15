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

    def segmented(self):
        return {"source": "mic", "path": "mic.wav", "kind": "microphone",
                "sample_rate": 48000, "channels": 1, "bits_per_sample": 32,
                "segments": [{"path": "mic.seg000001.wav", "start_frame": 0, "frames": 3},
                             {"path": "mic.seg000002.wav", "start_frame": 3, "frames": 4}]}

    def test_segmented_source_uses_real_segments_without_a_logical_wav(self):
        import numpy as np
        import soundfile as sf
        from audio_sources import capture_paths, capture_sources, open_source
        row = self.segmented()
        expected = np.arange(7, dtype=np.float32) / 16
        for part in row["segments"]:
            first = part["start_frame"]
            sf.write(self.session / part["path"], expected[first:first + part["frames"]],
                     48000, subtype="FLOAT")
        self.assertEqual(capture_sources(self.session, {"sources": [row]}), [row])
        self.assertEqual([path.name for path in capture_paths(self.session, row)],
                         [part["path"] for part in row["segments"]])
        with open_source(self.session, row) as audio:
            self.assertEqual((audio.frames, audio.channels, audio.samplerate), (7, 1, 48000))
            np.testing.assert_array_equal(audio.read(5, dtype="float32", always_2d=True)[:, 0], expected[:5])
            audio.seek(2)
            np.testing.assert_array_equal(audio.read(5, dtype="float32", always_2d=True)[:, 0], expected[2:])
            audio.seek(7)
            self.assertEqual(len(audio.read(3)), 0)

    def test_segment_manifest_rejects_gaps_overlap_aliases_and_missing_parts(self):
        import copy
        from audio_sources import capture_sources
        for change in ({"start_frame": 4}, {"start_frame": 2}, {"path": "../mic.seg000002.wav"},
                       {"path": "system.seg000002.wav"}, {"path": "mic.seg000001.wav"},
                       {"frames": -1}, {"frames": True}):
            row = copy.deepcopy(self.segmented())
            row["segments"][1].update(change)
            with self.subTest(change=change), self.assertRaises(ValueError):
                capture_sources(self.session, {"sources": [row]}, require_files=False)
        with self.assertRaisesRegex(ValueError, "mic.seg000001"):
            capture_sources(self.session, {"sources": [self.segmented()]})
        for parts in (None, [], "mic.seg000001.wav"):
            row = {**self.segmented(), "segments": parts}
            with self.subTest(parts=parts), self.assertRaises(ValueError):
                capture_sources(self.session, {"sources": [row]}, require_files=False)

    def test_reader_rejects_header_length_or_format_disagreement(self):
        import soundfile as sf
        import numpy as np
        from audio_sources import open_source
        for rate, channels, count, subtype in ((44100, 1, 3, "FLOAT"), (48000, 2, 3, "FLOAT"),
                                              (48000, 1, 2, "FLOAT"), (48000, 1, 3, "PCM_16")):
            row = self.segmented()
            row["segments"] = row["segments"][:1]
            sf.write(self.session / row["segments"][0]["path"], np.zeros((count, channels)),
                     rate, subtype=subtype)
            with self.subTest(rate=rate, channels=channels, count=count, subtype=subtype):
                with self.assertRaises(ValueError):
                    with open_source(self.session, row):
                        pass

    def test_missing_middle_segment_cannot_shorten_the_continuous_source(self):
        import numpy as np
        import soundfile as sf
        from audio_sources import capture_sources
        row = self.segmented()
        sf.write(self.session / row["segments"][0]["path"], np.zeros(3), 48000, subtype="FLOAT")
        with self.assertRaisesRegex(ValueError, "mic.seg000002"):
            capture_sources(self.session, {"sources": [row]})

    def test_reader_keeps_only_one_segment_handle_open_during_bounded_reads(self):
        import numpy as np
        import soundfile as sf
        from unittest.mock import patch
        from audio_sources import open_source
        row = {**self.segmented(), "segments": []}
        for index in range(23):
            name = f"mic.seg{index + 1:06d}.wav"
            sf.write(self.session / name, np.full(101, index / 64), 48000, subtype="FLOAT")
            row["segments"].append({"path": name, "start_frame": 101 * index, "frames": 101})
        with open_source(self.session, row) as audio:
            opened, factory = [], sf.SoundFile
            def tracked(*args, **kwargs):
                self.assertTrue(all(item.closed for item in opened))
                item = factory(*args, **kwargs)
                opened.append(item)
                return item
            with patch.object(sf, "SoundFile", side_effect=tracked):
                blocks = [audio.read(min(73, audio.frames - first), dtype="float32")
                          for first in range(0, audio.frames, 73)]
            expected = np.repeat(np.arange(23, dtype=np.float32) / 64, 101)
            np.testing.assert_array_equal(np.concatenate(blocks), expected)
            self.assertEqual(len(opened), 23)
        self.assertTrue(all(item.closed for item in opened))


if __name__ == "__main__":
    unittest.main()
