from pathlib import Path
import tempfile
import unittest

import numpy as np
import soundfile as sf
from scipy.signal import resample_poly


class MergeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.session = Path(self.temporary.name)
        self.metadata = {"duration_seconds": 1.0, "capture_offsets_ms": {"mic": 0, "system": 0}}

    def merge(self, **options):
        from merge_audio import merge_session
        return merge_session(self.session, self.metadata, **options)

    def write(self, source, samples, rate):
        sf.write(self.session / f"{source}.wav", samples, rate, subtype="FLOAT")

    def output(self):
        return sf.read(self.session / "meeting.wav", dtype="float32", always_2d=True)

    def test_silent_microphone_does_not_halve_media_and_keeps_stereo(self):
        timeline = np.arange(48000) / 48000
        media = np.column_stack([0.25 * np.sin(2 * np.pi * 440 * timeline),
                                 -0.4 * np.sin(2 * np.pi * 660 * timeline)])
        self.write("mic", np.zeros(48000), 48000)
        self.write("system", media, 48000)
        result = self.merge(block_frames=4096)
        actual, rate = self.output()
        self.assertEqual(rate, 48000)
        self.assertEqual(actual.shape, media.shape)
        np.testing.assert_allclose(actual, media, atol=1 / 32768)
        self.assertEqual(result["gain"], 1.0)

    def test_different_rates_offsets_and_block_boundaries_match_whole_resampling(self):
        count = 22050 + 137
        timeline = np.arange(count) / 44100
        microphone = 0.2 * np.sin(2 * np.pi * 330 * timeline)
        self.metadata["duration_seconds"] = 0.9
        self.metadata["capture_offsets_ms"]["mic"] = 125
        self.write("mic", microphone, 44100)
        self.write("system", np.zeros((32000, 2)), 32000)
        self.merge(block_frames=1024)
        actual, rate = self.output()
        expected_mic = resample_poly(microphone.astype(np.float32), 160, 147)
        expected = np.zeros(48000, dtype=np.float32)
        expected[6000:6000 + len(expected_mic)] = expected_mic
        np.testing.assert_allclose(actual[:, 0], expected, atol=1 / 32768)
        np.testing.assert_array_equal(actual[:, 0], actual[:, 1])
        self.assertEqual(len(actual), 48000)

    def test_peak_gain_is_global_safe_and_preserves_overlap_mix(self):
        mic = np.full(48000, 0.8, dtype=np.float32)
        media = np.zeros(48000, dtype=np.float32)
        media[12000:36000] = 0.8
        self.write("mic", mic, 48000)
        self.write("system", media, 48000)
        result = self.merge(block_frames=4096)
        actual, _ = self.output()
        self.assertAlmostEqual(result["gain"], 0.98 / 1.6, places=6)
        self.assertLessEqual(float(np.max(np.abs(actual))), 0.98001)
        self.assertAlmostEqual(float(actual[1000, 0]), 0.49, delta=1 / 32768)
        self.assertAlmostEqual(float(actual[20000, 0]), 0.98, delta=1 / 32768)

    def test_pause_silence_and_resume_stay_on_one_continuous_timeline(self):
        mic = np.full(48000, 0.3, dtype=np.float32)
        mic[12000:36000] = 0
        self.write("mic", mic, 48000)
        self.write("system", np.full(48000, 0.1, dtype=np.float32), 48000)
        self.merge(block_frames=1024)
        actual, _ = self.output()
        self.assertEqual(len(actual), 48000)
        np.testing.assert_allclose(actual[:12000], 0.4, atol=1 / 32768)
        np.testing.assert_allclose(actual[12000:36000], 0.1, atol=1 / 32768)
        np.testing.assert_allclose(actual[36000:], 0.4, atol=1 / 32768)

    def test_cached_success_is_reused_but_truncated_output_is_rebuilt(self):
        self.write("mic", np.full(48000, 0.2), 48000)
        first = self.merge()
        output = self.session / "meeting.wav"
        first_modified = output.stat().st_mtime_ns
        second = self.merge()
        self.assertTrue(second["reused"])
        self.assertEqual(output.stat().st_mtime_ns, first_modified)
        output.write_bytes(output.read_bytes()[:100])
        rebuilt = self.merge()
        self.assertFalse(rebuilt["reused"])
        self.assertEqual(len(self.output()[0]), 48000)
        self.assertEqual(first["frames"], rebuilt["frames"])

    def test_corrupt_audio_preserves_sources_and_has_no_complete_manifest(self):
        self.write("mic", np.full(48000, 0.2), 48000)
        system = self.session / "system.wav"
        system.write_bytes(b"not a WAV")
        before = (self.session / "mic.wav").read_bytes()
        with self.assertRaises(Exception):
            self.merge()
        self.assertEqual((self.session / "mic.wav").read_bytes(), before)
        self.assertEqual(system.read_bytes(), b"not a WAV")
        self.assertFalse((self.session / "merged_audio.json").exists())
        self.assertFalse((self.session / "meeting.wav").exists())

    def test_nonfinite_samples_fail_without_replacing_previous_output(self):
        self.write("mic", np.full(48000, 0.2), 48000)
        self.merge()
        original = (self.session / "meeting.wav").read_bytes()
        samples = np.zeros(48000, dtype=np.float32)
        samples[10] = np.nan
        self.write("mic", samples, 48000)
        with self.assertRaises(ValueError):
            self.merge()
        self.assertEqual((self.session / "meeting.wav").read_bytes(), original)

    def test_all_declared_outputs_and_switched_microphones_share_one_timeline(self):
        self.metadata["sources"] = [
            {"source": key, "path": key + ".wav", "kind": kind}
            for key, kind in (("mic", "microphone"), ("mic_02", "microphone"),
                              ("system", "loopback"), ("system_02", "loopback"))]
        expected = np.zeros(48000, dtype=np.float32)
        for index, row in enumerate(self.metadata["sources"]):
            samples = np.zeros(48000, dtype=np.float32)
            first = 1000 + index * 10000
            samples[first:first + 50] = 0.1 * (index + 1)
            self.write(row["source"], samples, 48000)
            expected += samples
        self.merge(block_frames=4096)
        actual, _ = self.output()
        np.testing.assert_allclose(actual[:, 0], expected, atol=1 / 32768)
        before = (self.session / "meeting.wav").read_bytes()
        self.write("system_02", np.zeros(48000), 48000)
        self.assertFalse(self.merge()["reused"])
        self.assertNotEqual((self.session / "meeting.wav").read_bytes(), before)

    def test_declared_missing_endpoint_cannot_silently_publish_partial_audio(self):
        self.write("system", np.full(48000, 0.1), 48000)
        self.metadata["sources"] = [
            {"source": "system", "path": "system.wav", "kind": "loopback"},
            {"source": "system_02", "path": "system_02.wav", "kind": "loopback"}]
        with self.assertRaisesRegex(ValueError, "system_02"):
            self.merge()
        self.assertFalse((self.session / "meeting.wav").exists())


if __name__ == "__main__":
    unittest.main()
