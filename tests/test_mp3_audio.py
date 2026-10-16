from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import numpy as np
import soundfile as sf

import mp3_audio
from mp3_audio import encode_mp3, encoder_path, validate_mp3


class Mp3AudioTests(unittest.TestCase):
    def test_gapless_tail_shorter_than_47_samples_keeps_exact_source_length(self):
        # FFmpeg 7.1 incorrectly reports a 47-sample last remainder for these
        # lengths, despite valid RF64 input; both mpg123 and FFmpeg decode long.
        with tempfile.TemporaryDirectory() as folder:
            source, output = Path(folder) / "lossless.wav", Path(folder) / "meeting.mp3"
            for frames in (1153, 1160, 1176, 1185, 1198, 1199, 1200, 2304, 2305, 1789089):
                with self.subTest(frames=frames):
                    samples = np.zeros((frames, 2), dtype=np.float32)
                    times = np.arange(min(frames, 8192)) / 48000
                    tail = .2 * np.sin(2 * np.pi * 1933 * times)
                    samples[-len(tail):, 0] = tail
                    samples[-len(tail):, 1] = tail * .6
                    sf.write(source, samples, 48000, subtype="PCM_16", format="RF64")
                    details = encode_mp3(source, output, frames=frames, bitrate_kbps=192)
                    self.assertEqual(details["decoded_frames"], frames)
                    decoded = sf.read(output, dtype="float32", always_2d=True)[0]
                    self.assertEqual(len(decoded), frames)
                    self.assertGreater(np.corrcoef(decoded[-1024:, 0], samples[-1024:, 0])[0, 1], .995)
                    independently_decoded = subprocess.run(
                        [encoder_path(), "-v", "error", "-i", str(output),
                         "-f", "f32le", "-acodec", "pcm_f32le", "-"], capture_output=True, check=True)
                    self.assertEqual(len(independently_decoded.stdout) // 8, frames)

    def test_short_tail_repair_only_changes_verified_padding_and_tag_crc(self):
        with tempfile.TemporaryDirectory() as folder:
            source, output = Path(folder) / "lossless.wav", Path(folder) / "meeting.mp3"
            sf.write(source, np.zeros((1789089, 2), dtype=np.float32), 48000, subtype="PCM_16", format="RF64")
            original = {}
            run = subprocess.run

            def capture_before_repair(command, **options):
                result = run(command, **options)
                original["bytes"] = output.read_bytes()
                return result

            with patch.object(mp3_audio.subprocess, "run", capture_before_repair):
                encode_mp3(source, output, frames=1789089)
            before, after = original["bytes"], output.read_bytes()
            self.assertEqual(len(before), len(after))
            offset = after.index(b"Info") - 36
            changed = {index - offset for index, (a, b) in enumerate(zip(before, after)) if a != b}
            self.assertTrue(changed)
            self.assertTrue(changed <= {177, 178, 179, 190, 191})
            self.assertEqual(before[offset + 192:], after[offset + 192:])

            for changed_index in (offset + 190, offset + 36, offset + 156):
                with self.subTest(changed_index=changed_index):
                    invalid = bytearray(before)
                    invalid[changed_index] ^= 1
                    output.write_bytes(invalid)
                    with self.assertRaises(ValueError):
                        mp3_audio._repair_short_tail_padding(output, frames=1789089, bitrate_kbps=192)
                    self.assertEqual(output.read_bytes(), invalid)
            output.write_bytes(before[:-576])
            truncated = output.read_bytes()
            with self.assertRaises(ValueError):
                mp3_audio._repair_short_tail_padding(output, frames=1789089, bitrate_kbps=192)
            self.assertEqual(output.read_bytes(), truncated)

    def test_encoder_rejects_wrong_input_length_before_output_is_created(self):
        with tempfile.TemporaryDirectory() as folder:
            source, output = Path(folder) / "lossless.wav", Path(folder) / "meeting.mp3"
            sf.write(source, np.zeros((1199, 2)), 48000, subtype="PCM_16")
            with self.assertRaises(ValueError):
                encode_mp3(source, output, frames=1153)
            self.assertFalse(output.exists())

    def test_cbr_mp3_decodes_with_gapless_duration_and_expected_channels(self):
        with tempfile.TemporaryDirectory() as folder:
            source, output = Path(folder) / "lossless.wav", Path(folder) / "meeting.mp3"
            rate, frames = 48000, 125123
            times = np.arange(frames) / rate
            samples = np.column_stack((.2 * np.sin(2 * np.pi * 440 * times),
                                       .15 * np.sin(2 * np.pi * 880 * times)))
            sf.write(source, samples, rate, subtype="PCM_16", format="RF64")
            details = encode_mp3(source, output, frames=frames, bitrate_kbps=192)
            self.assertEqual(details["format"], "mp3")
            self.assertEqual(details["bitrate_kbps"], 192)
            self.assertEqual(details["sample_rate"], rate)
            self.assertEqual(details["channels"], 2)
            self.assertLessEqual(abs(details["decoded_frames"] - frames), 1)
            decoded, actual_rate = sf.read(output, dtype="float32", always_2d=True)
            self.assertEqual(actual_rate, rate)
            self.assertGreater(np.corrcoef(decoded[:, 0], samples[:len(decoded), 0])[0, 1], .995)
            self.assertGreater(np.corrcoef(decoded[:, 1], samples[:len(decoded), 1])[0, 1], .995)
            self.assertLess(output.stat().st_size, source.stat().st_size / 4)
            self.assertEqual(validate_mp3(output, frames=frames, bitrate_kbps=192), details)

    def test_truncated_or_wrong_duration_mp3_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            source, output = Path(folder) / "lossless.wav", Path(folder) / "meeting.mp3"
            sf.write(source, np.zeros((48000, 2)), 48000, subtype="PCM_16")
            encode_mp3(source, output, frames=48000, bitrate_kbps=192)
            with self.assertRaises(ValueError):
                validate_mp3(output, frames=96000, bitrate_kbps=192)
            output.write_bytes(output.read_bytes()[:-40])
            with self.assertRaises(ValueError):
                validate_mp3(output, frames=48000, bitrate_kbps=192)

    def test_wrong_bitrate_or_non_mp3_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            source, output = Path(folder) / "lossless.wav", Path(folder) / "meeting.mp3"
            sf.write(source, np.zeros((48000, 2)), 48000, subtype="PCM_16")
            encode_mp3(source, output, frames=48000, bitrate_kbps=192)
            with self.assertRaises(ValueError):
                validate_mp3(output, frames=48000, bitrate_kbps=128)
            with self.assertRaises(ValueError):
                validate_mp3(source, frames=48000, bitrate_kbps=192)


if __name__ == "__main__":
    unittest.main()
