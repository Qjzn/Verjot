import importlib.util
from pathlib import Path
import tempfile
import unittest

import numpy as np

spec = importlib.util.spec_from_file_location("device_analysis", Path(__file__).with_name("AnalyzeDeviceRevalidation.py"))
analysis = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analysis)


class DeviceEvidenceTests(unittest.TestCase):
    def test_known_offset_ignores_silent_fft_windows(self):
        template = np.random.default_rng(2026).normal(0, .1, 8000)
        audio = np.zeros(16000 * 12)
        audio[16000 * 2 + 320:16000 * 2 + 320 + len(template)] = template
        result = analysis.match(audio, 16000, template, 2)
        self.assertTrue(result["matched"])
        self.assertAlmostEqual(result["capture_start_seconds"], 2.02, places=5)
        self.assertAlmostEqual(result["correlation"], 1, places=5)

    def test_requested_window_selects_corresponding_repeat(self):
        template = np.random.default_rng(7).normal(0, .1, 8000)
        audio = np.zeros(16000 * 15)
        for at in (2, 9):
            audio[at * 16000:at * 16000 + len(template)] = template
        self.assertAlmostEqual(analysis.match(audio, 16000, template, 9)["capture_start_seconds"], 9, places=5)

    def test_silence_and_inadequate_audio_are_not_matches(self):
        template = np.ones(8000) * .1
        self.assertFalse(analysis.match(np.zeros(160000), 16000, template, 2)["matched"])
        self.assertFalse(analysis.match(np.ones(100), 16000, template, 2)["matched"])

    def test_device_reset_estimate_and_real_gap_are_separate(self):
        header = "seq\traw_position\tframes\tqpc_100ns\traw_start_ms\tarrival_ms\tresolved_start_ms\tresolved_position\tflags\testimated\n"
        rows = "0\t0\t160\t100\t0\t10\t0\t0\t0\t0\n1\t320\t160\t200\t20\t30\t20\t320\t1\t0\n2\t0\t160\t0\t0\t40\t30\t480\t4\t1\n"
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "trace.tsv"
            path.write_text(header + rows, encoding="utf-8")
            result = analysis.packet_summary(path, 16000)
        self.assertEqual(result["packets"], 3)
        self.assertEqual(result["estimated_packets"], 1)
        self.assertEqual(result["raw_position_resets"], 1)
        self.assertEqual(result["resolved_start_backwards"], 0)
        self.assertEqual(result["device_position_gaps"][0]["milliseconds"], 10)


if __name__ == "__main__":
    unittest.main()
