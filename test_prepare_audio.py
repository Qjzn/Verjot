"""Targeted invariants for meeting segmentation; all inputs are synthetic."""

import json
from pathlib import Path
import tempfile
import unittest

import numpy as np

import prepare_audio as preparation


class PreparationTests(unittest.TestCase):
    def test_short_turn_survives_in_long_silence(self):
        samples = np.zeros(60 * preparation.SAMPLE_RATE, dtype=np.float32)
        samples[30 * preparation.SAMPLE_RATE:30 * preparation.SAMPLE_RATE + 1600] = 0.02
        self.assertGreater(preparation.peak_frame_rms(samples), preparation.SILENCE_RMS)
        self.assertEqual(preparation.peak_frame_rms(np.zeros(16000)), 0)

    def test_turn_merge_preserves_other_speakers_and_overlap(self):
        turns = [{"start": 0, "end": 1, "speaker": 0},
                 {"start": 1.2, "end": 1.6, "speaker": 0},
                 {"start": 1.5, "end": 2, "speaker": 1},
                 {"start": 1.7, "end": 3, "speaker": 0}]
        merged = preparation.merge_turns(turns, 3)
        self.assertEqual(len(merged), 3)
        self.assertEqual(merged[0]["end"], 1.6)
        self.assertEqual(merged[1]["speaker"], 1)
        self.assertEqual(merged[2]["start"], 1.7)

    def test_long_turn_splits_without_gaps_or_duplicate_samples(self):
        rate = preparation.SAMPLE_RATE
        samples = np.full(70 * rate, 0.1, dtype=np.float32)
        samples[21 * rate:21 * rate + 1600] = 0
        pieces = preparation.split_turn(samples, 0, 70)
        self.assertEqual(pieces[0][0], 0)
        self.assertEqual(pieces[-1][1], len(samples))
        self.assertTrue(all(end - start < 25 * rate for start, end in pieces))
        self.assertTrue(all(pieces[i][1] == pieces[i + 1][0] for i in range(len(pieces) - 1)))
        self.assertAlmostEqual(pieces[0][1] / rate, 21.05, delta=0.1)

    def test_context_padding_stays_in_unlabelled_gaps(self):
        turns = [{"start": 0.3, "end": 1, "speaker": 0},
                 {"start": 1.2, "end": 2, "speaker": 1},
                 {"start": 1.8, "end": 2.5, "speaker": 0}]
        padded = preparation.context_bounds(turns, 0, 3)
        self.assertAlmostEqual(padded[0], 0.12)
        self.assertAlmostEqual(padded[1], 1.1)
        self.assertEqual(preparation.context_bounds(turns, 1, 3), (1.1, 2))
        self.assertEqual(preparation.context_bounds(turns, 2, 3), (1.8, 2.68))

    def test_duplicate_detection_uses_global_capture_offset(self):
        rate = preparation.SAMPLE_RATE
        random = np.random.default_rng(7)
        voice = random.normal(0, 0.02, 2 * rate).astype(np.float32)
        mic = np.concatenate((np.zeros(rate, dtype=np.float32), voice))
        segments = [{"start": 1, "end": 3, "source": "mic", "segment_id": "mic_00001"},
                    {"start": 1, "end": 3, "source": "system", "segment_id": "system_00001"}]
        preparation.mark_digital_duplicates(segments, {"mic": mic, "system": voice},
                                            {"mic": 0, "system": 1})
        self.assertEqual(segments[0]["possible_duplicate_of"], "system_00001")
        self.assertEqual(len(segments), 2, "Possible duplicates must remain reviewable")
        different = random.normal(0, 0.02, 2 * rate).astype(np.float32)
        segments[0].pop("possible_duplicate_of")
        preparation.mark_digital_duplicates(segments, {"mic": mic, "system": different},
                                            {"mic": 0, "system": 1})
        self.assertNotIn("possible_duplicate_of", segments[0])

    def test_full_manifest_keeps_sources_separate_and_inputs_unchanged(self):
        import soundfile as sf

        class Turn:
            start, end, speaker = 0.1, 1.9, 0

        class Result:
            def sort_by_start_time(self):
                return [Turn()]

        class Diarizer:
            def process(self, samples):
                return Result()

        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent / "validation") as temporary:
            session = Path(temporary)
            rate = 48000
            time = np.arange(rate * 2) / rate
            # Opposite-polarity stereo should still yield audible clips.
            voice = (0.03 * np.sin(time * 2 * np.pi * 300)).astype(np.float32)
            sf.write(session / "mic.wav", np.stack((voice, -voice), axis=1), rate)
            sf.write(session / "system.wav", voice, rate)
            original = {source: (session / f"{source}.wav").read_bytes() for source in ("mic", "system")}
            (session / "session.json").write_text(json.dumps({"capture_offsets_ms": {"system": 1000}}))
            segments = preparation.prepare_session(session, "unused", "unused", diarizer=Diarizer())
            self.assertEqual([item["speaker"] for item in segments], ["mic_01", "system_01"])
            self.assertAlmostEqual(segments[1]["start"], 1.1)
            for item in segments:
                audio, audio_rate = sf.read(item["text_audio"])
                self.assertEqual(audio_rate, 16000)
                self.assertEqual(audio.ndim, 1)
                self.assertGreater(np.max(np.abs(audio)), 0.01)
            for source, content in original.items():
                self.assertEqual((session / f"{source}.wav").read_bytes(), content)
            self.assertEqual(json.loads((session / "segments.json").read_text()), segments)


if __name__ == "__main__":
    unittest.main()
