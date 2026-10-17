"""Targeted invariants for meeting segmentation; all inputs are synthetic."""

import json
import math
from pathlib import Path
import tempfile
import unittest

import numpy as np

import prepare_audio as preparation


class PreparationTests(unittest.TestCase):
    def test_clean_anchor_recovers_unmixed_part_of_overlapping_turn(self):
        row = {"source": "mic", "speaker": "mic_01", "start": 10., "end": 20.}
        other = {"source": "mic", "speaker": "mic_02", "start": 10., "end": 14.}
        self.assertEqual(preparation.clean_anchor(row, [row, other]), (14., 20.))
        other["end"] = 19.
        self.assertIsNone(preparation.clean_anchor(row, [row, other]))

    def test_speech_windows_ignore_local_speaker_change_without_losing_turns(self):
        rows = [{"start": 0., "end": 3., "source": "mic", "speaker": "mic_01"},
                {"start": 2., "end": 5., "source": "mic", "speaker": "mic_02"}]
        windows = preparation.speech_windows(np.ones(80000, dtype=np.float32) * .02, rows)
        self.assertEqual(windows, [(0, 80000)])
        rows[1]["speaker"] = "mic_99"
        self.assertEqual(preparation.speech_windows(np.ones(80000, dtype=np.float32) * .02, rows), windows)

    def test_decoupled_mixed_speech_keeps_both_timelines_without_attributing_all_text(self):
        import soundfile as sf
        rows = [{"start": 10., "end": 13., "source": "mic", "speaker": "mic_01",
                 "global_speaker": "speaker_01", "speaker_global": True, "speaker_status": "matched"},
                {"start": 12., "end": 15., "source": "mic", "speaker": "mic_02",
                 "global_speaker": "speaker_02", "speaker_global": True, "speaker_status": "matched"}]
        samples = np.ones(80000, dtype=np.float32) * .02
        with tempfile.TemporaryDirectory() as temporary:
            output = preparation.decouple_asr_segments(rows, {"mic": samples}, {"mic": 10}, Path(temporary), "off")
            self.assertEqual(len(output), 1)
            self.assertEqual((output[0]["start"], output[0]["end"]), (10., 15.))
            self.assertEqual(output[0]["speaker_status"], "mixed")
            self.assertIsNone(output[0]["global_speaker"])
            self.assertEqual([span["global_speaker"] for span in output[0]["speaker_spans"]], ["speaker_01", "speaker_02"])
            self.assertEqual(sf.info(output[0]["text_audio"]).frames, 80000)
        np.testing.assert_array_equal(samples, np.full(80000, .02, dtype=np.float32))

    def test_clean_anchor_uses_only_unmixed_audio_with_capture_offset(self):
        streams = []
        class Stream:
            def accept_waveform(self, rate, samples): streams.append(samples.copy())
            def input_finished(self): pass
        class Extractor:
            def create_stream(self): return Stream()
            def is_ready(self, stream): return True
            def compute(self, stream): return [1., 0., 0.]
        rows = [{"source": "mic", "speaker": "mic_01", "start": 10., "end": 20.},
                {"source": "mic", "speaker": "mic_02", "start": 10., "end": 14.}]
        samples = np.arange(160000, dtype=np.float32) / 160000
        result = preparation.assign_global_speakers(rows, {"mic": samples}, {"mic": 10.}, Path("unused"),
                                                     embedding_extractor=Extractor(), anchor_mode="clean")
        self.assertEqual(result["eligible_segments"], 1)
        np.testing.assert_array_equal(streams[0], samples[64000:160000])
        self.assertEqual(rows[0]["speaker_status"], "matched")
        self.assertEqual(rows[1]["speaker_status"], "unknown")

    def test_speech_windows_cover_all_turn_samples_once_even_with_fragmented_labels(self):
        samples = np.ones(60 * preparation.SAMPLE_RATE, dtype=np.float32) * .02
        turns = [{"start": 1., "end": 35., "source": "mic", "speaker": "mic_01"},
                 {"start": 10., "end": 12., "source": "mic", "speaker": "mic_02"},
                 {"start": 35.2, "end": 55., "source": "mic", "speaker": "mic_03"}]
        windows = preparation.speech_windows(samples, turns)
        self.assertEqual(windows[0][0], 16000)
        self.assertEqual(windows[-1][1], 55 * 16000)
        self.assertTrue(all(left[1] == right[0] for left, right in zip(windows, windows[1:])))
        self.assertTrue(all(0 < end - start <= 24 * 16000 for start, end in windows))

    def test_cluster_failure_preserves_transcription_turns_as_unknown(self):
        class Stream:
            def accept_waveform(self, rate, samples): pass
            def input_finished(self): pass
        class Extractor:
            def create_stream(self): return Stream()
            def is_ready(self, stream): return True
            def compute(self, stream): return [1., 0., 0.]
        from unittest.mock import patch
        rows = [{"start": 0., "end": 3., "source": "mic", "speaker": "mic_01"}]
        with patch.object(preparation, "cluster_voice_identities", side_effect=RuntimeError("cluster unavailable")):
            result = preparation.assign_global_speakers(rows, {"mic": np.ones(48000, dtype=np.float32)}, {}, Path("unused"), embedding_extractor=Extractor())
        self.assertIn("cluster unavailable", result["error"])
        self.assertEqual(rows[0]["speaker_status"], "unknown")
        self.assertEqual(rows[0]["end"], 3.)

    def test_segmented_asr_source_preserves_resampling_and_speech_crossing_boundaries(self):
        import soundfile as sf
        for rate in (44100, 48000):
            with self.subTest(rate=rate), tempfile.TemporaryDirectory() as temporary:
                session = Path(temporary)
                timeline = np.arange(rate * 2 + 11) / rate
                voice = (.03 * np.sin(2 * np.pi * 311 * timeline)).astype(np.float32)
                samples = np.column_stack((voice, -voice))  # Preserve anti-phase fallback globally.
                path = session / "mic.wav"
                sf.write(path, samples, rate, subtype="FLOAT")
                expected, expected_info = preparation.read_audio(path)
                row = {"source": "mic", "path": path.name, "kind": "microphone",
                       "sample_rate": rate, "channels": 2, "bits_per_sample": 32, "segments": []}
                boundaries = [0, 1, rate // 2 - 1, rate // 2 + 1, rate + 7, len(samples)]
                for index, (first, last) in enumerate(zip(boundaries, boundaries[1:]), 1):
                    name = f"mic.seg{index:06d}.wav"
                    sf.write(session / name, samples[first:last], rate, subtype="FLOAT")
                    row["segments"].append({"path": name, "start_frame": first, "frames": last - first})
                path.unlink()
                actual, actual_info = preparation.read_audio(path, entry=row)
                np.testing.assert_array_equal(actual, expected)
                self.assertEqual(actual_info, expected_info)

    def test_capture_rotation_is_one_diarizer_call_and_does_not_create_speakers(self):
        import soundfile as sf
        from unittest.mock import patch
        class Turn:
            start, end, speaker = .3, 2.7, 4
        class Result:
            def sort_by_start_time(self): return [Turn()]
        class Diarizer:
            def __init__(self): self.calls = []
            def process(self, samples):
                self.calls.append(samples.copy())
                return Result()
        with tempfile.TemporaryDirectory() as temporary:
            session = Path(temporary)
            samples = (.03 * np.sin(np.arange(48000 * 3) * 2 * np.pi * 347 / 48000)).astype(np.float32)
            row = {"source": "mic", "path": "mic.wav", "kind": "microphone",
                   "sample_rate": 48000, "channels": 1, "bits_per_sample": 32, "segments": []}
            for index in range(3):
                path = f"mic.seg{index + 1:06d}.wav"
                sf.write(session / path, samples[index * 48000:(index + 1) * 48000], 48000, subtype="FLOAT")
                row["segments"].append({"path": path, "start_frame": index * 48000, "frames": 48000})
            (session / "session.json").write_text(json.dumps({"sources": [row],
                "capture_offsets_ms": {"mic": 500}}), encoding="utf-8")
            diarizer = Diarizer()
            with patch.object(preparation, "assign_global_speakers", return_value={}):
                rows = preparation.prepare_session(session, "unused", "unused", diarizer=diarizer)
            self.assertEqual(len(diarizer.calls), 1)
            self.assertEqual(len(diarizer.calls[0]), 48000)
            self.assertEqual(len(rows), 1)
            self.assertEqual((rows[0]["source"], rows[0]["speaker"]), ("mic", "mic_01"))
            self.assertEqual((rows[0]["start"], rows[0]["end"]), (.8, 3.2))
            clip, rate = sf.read(rows[0]["text_audio"])
            self.assertGreater(len(clip), int(2.4 * rate))
            self.assertFalse((session / "mic.wav").exists())

    def test_segmented_audio_rejects_nonfinite_samples_in_later_parts(self):
        import soundfile as sf
        with tempfile.TemporaryDirectory() as temporary:
            session = Path(temporary)
            row = {"source": "mic", "path": "mic.wav", "kind": "microphone",
                   "sample_rate": 48000, "channels": 1, "bits_per_sample": 32, "segments": []}
            for index, samples in enumerate((np.zeros(100), np.array([.1, np.nan])), 1):
                name = f"mic.seg{index:06d}.wav"
                sf.write(session / name, samples, 48000, subtype="FLOAT")
                row["segments"].append({"path": name, "start_frame": 0 if index == 1 else 100,
                                        "frames": len(samples)})
            with self.assertRaisesRegex(ValueError, "Non-finite audio samples"):
                preparation.read_audio(session / "mic.wav", entry=row)

    def test_segmented_stereo_downmix_matches_whole_source_at_statistics_block_edges(self):
        import soundfile as sf
        with tempfile.TemporaryDirectory() as temporary:
            session = Path(temporary)
            samples = np.random.default_rng(813).normal(0, .03, (131079, 2)).astype(np.float32)
            path = session / "system.wav"
            sf.write(path, samples, 44100, subtype="FLOAT")
            expected, _ = preparation.read_audio(path)
            row = {"source": "system", "path": path.name, "kind": "loopback",
                   "sample_rate": 44100, "channels": 2, "bits_per_sample": 32, "segments": []}
            boundaries = [0, 65535, 65536, 65537, len(samples)]
            for index, (first, last) in enumerate(zip(boundaries, boundaries[1:]), 1):
                name = f"system.seg{index:06d}.wav"
                sf.write(session / name, samples[first:last], 44100, subtype="FLOAT")
                row["segments"].append({"path": name, "start_frame": first, "frames": last - first})
            path.unlink()
            actual, _ = preparation.read_audio(path, entry=row)
            np.testing.assert_array_equal(actual, expected)

    def test_chunked_channel_rms_matches_reference_at_block_boundaries(self):
        random = np.random.default_rng(120)
        for length in (0, 1, 65535, 65536, 65537, 131079):
            for channels in (1, 2):
                with self.subTest(length=length, channels=channels):
                    samples = random.normal(0, .03, (length, channels)).astype(np.float32)
                    expected = (np.sqrt(np.mean(samples.astype(np.float64) ** 2, axis=0))
                                if length else np.zeros(channels))
                    np.testing.assert_allclose(preparation._channel_rms(samples), expected, rtol=1e-12, atol=1e-14)
                    if channels == 1:
                        np.testing.assert_allclose(preparation._channel_rms(samples[:, 0]), expected,
                                                   rtol=1e-12, atol=1e-14)

    def test_chunked_statistics_still_reject_nonfinite_audio(self):
        import soundfile as sf
        from unittest.mock import patch
        for invalid in (np.nan, np.inf, -np.inf):
            with self.subTest(invalid=invalid):
                samples = np.zeros((65537, 2), dtype=np.float32)
                samples[-1, 1] = invalid
                with patch.object(sf, "read", return_value=(samples, preparation.SAMPLE_RATE)):
                    with self.assertRaisesRegex(ValueError, "Non-finite audio samples: synthetic.wav"):
                        preparation.read_audio(Path("synthetic.wav"))

    def test_peak_frame_rms_pads_the_last_partial_frame_with_zero(self):
        samples = np.zeros(preparation.SAMPLE_RATE // 10 * 65 + 1, dtype=np.float32)
        samples[-1] = .8
        self.assertAlmostEqual(preparation.peak_frame_rms(samples),
                               float(samples[-1]) / math.sqrt(preparation.SAMPLE_RATE // 10), places=12)

    def test_read_audio_statistics_use_bounded_temporary_memory(self):
        import soundfile as sf
        from scipy.signal import resample_poly  # Warm imports outside measurement.
        import tracemalloc
        from unittest.mock import patch

        samples = np.full((preparation.SAMPLE_RATE * 30, 2), .03, dtype=np.float32)
        samples[:, 1] *= .5
        expected = samples.mean(axis=1)
        expected -= expected.mean()
        with patch.object(sf, "read", return_value=(samples, preparation.SAMPLE_RATE)):
            tracemalloc.start()
            try:
                actual, info = preparation.read_audio(Path("synthetic-memory-fixture.wav"))
                _, peak = tracemalloc.get_traced_memory()
            finally:
                tracemalloc.stop()
        np.testing.assert_array_equal(actual, expected)
        self.assertEqual(info["channels"], 2)
        self.assertLess(peak, 6 * 1024 * 1024, f"Statistics allocated {peak} bytes")

    def test_peak_frame_rms_uses_bounded_memory_and_keeps_tail_padding(self):
        import tracemalloc
        samples = np.random.default_rng(119).normal(0, .03, preparation.SAMPLE_RATE * 30 + 13).astype(np.float32)
        frame = preparation.SAMPLE_RATE // 10
        padded = np.pad(samples, (0, (-len(samples)) % frame))
        expected = float(np.sqrt(np.mean(padded.reshape(-1, frame).astype(np.float64) ** 2, axis=1)).max())
        del padded
        tracemalloc.start()
        try:
            actual = preparation.peak_frame_rms(samples)
            _, peak = tracemalloc.get_traced_memory()
        finally:
            tracemalloc.stop()
        self.assertAlmostEqual(actual, expected, places=12)
        self.assertLess(peak, 3 * 1024 * 1024, f"Frame statistics allocated {peak} bytes")

    def test_echo_evidence_handles_delay_channel_response_and_preserves_inputs(self):
        from scipy.signal import butter, sosfilt
        rate = preparation.SAMPLE_RATE
        random = np.random.default_rng(81)
        system = sosfilt(butter(3, [160, 3000], btype="bandpass", fs=rate, output="sos"),
                         random.normal(0, .03, 5 * rate)).astype(np.float32)
        delayed = np.pad(sosfilt(butter(2, [120, 3200], btype="bandpass", fs=rate, output="sos"),
                                system), (round(.12 * rate), 0))[:len(system)]
        mic = (delayed + random.normal(0, .0005, len(system))).astype(np.float32)
        before = mic.copy(), system.copy()
        result = preparation.echo_evidence(mic, system, .5, 4.5)
        self.assertIsNotNone(result)
        self.assertAlmostEqual(result["echo_delay_ms"], 120, delta=2)
        self.assertGreaterEqual(result["echo_similarity"], .95)
        self.assertGreaterEqual(result["echo_window_support"], .9)
        self.assertGreaterEqual(result["echo_window_min_similarity"], .85)
        self.assertLessEqual(result["echo_residual_ratio"], .10)
        self.assertGreaterEqual(result["echo_window_count"], 3)
        np.testing.assert_array_equal(mic, before[0])
        np.testing.assert_array_equal(system, before[1])

    def test_echo_evidence_rejects_different_simultaneous_voice_and_partial_near_voice(self):
        from scipy.signal import butter, sosfilt
        rate = preparation.SAMPLE_RATE
        random = np.random.default_rng(82)
        def voice():
            return sosfilt(butter(3, [160, 3000], btype="bandpass", fs=rate, output="sos"),
                           random.normal(0, .03, 5 * rate)).astype(np.float32)
        system, own = voice(), voice()
        different = preparation.echo_evidence(own, system, .5, 4.5)
        self.assertTrue(different is None or different["echo_similarity"] < .5)
        mixed = np.pad(system, (round(.1 * rate), 0))[:len(system)].copy()
        mixed[2 * rate:3 * rate] += own[2 * rate:3 * rate]
        result = preparation.echo_evidence(mixed, system, .5, 4.5)
        self.assertIsNotNone(result)
        self.assertTrue(result["echo_window_min_similarity"] < .85
                        or result["echo_residual_ratio"] > .10)
        self.assertIsNone(preparation.echo_evidence(own, system, 0, 1))
        self.assertIsNone(preparation.echo_evidence(np.zeros(rate * 2), np.zeros(rate * 2), 0, 2))

    def test_numbered_capture_sources_use_their_own_offsets_for_duplicate_and_echo(self):
        from scipy.signal import butter, sosfilt
        rate = preparation.SAMPLE_RATE
        voice = sosfilt(butter(3, [180, 3000], fs=rate, btype="bandpass", output="sos"),
                        np.random.default_rng(91).normal(0, .03, 4 * rate)).astype(np.float32)
        rows = [{"start": 5.5, "end": 8.5, "source": "mic_02", "segment_id": "mic_02_00001"},
                {"start": 5.5, "end": 8.5, "source": "system_02", "segment_id": "system_02_00001"}]
        sources = {"mic_02": voice.copy(), "system_02": voice.copy()}
        preparation.mark_digital_duplicates(rows, sources, {"mic_02": 5, "system_02": 5})
        self.assertEqual(rows[0]["possible_duplicate_of"], "system_02_00001")
        rows[0].pop("possible_duplicate_of")
        delay = round(.12 * rate)
        sources["mic_02"] = np.pad(voice, (delay, 0))[:len(voice)]
        preparation.mark_acoustic_echo(rows, sources, {"mic_02": 5, "system_02": 5})
        self.assertEqual(rows[0]["echo_candidate_of"], "system_02_00001")
        self.assertAlmostEqual(rows[0]["echo_delay_ms"], 120, delta=2)
        self.assertEqual(len(rows), 2, "Echo candidates must retain both source segments")

    def test_declared_multi_device_tracks_keep_each_timeline_and_source_kind(self):
        import soundfile as sf
        from unittest.mock import patch
        class Turn:
            start, end, speaker = .1, 1.3, 0
        class Result:
            def sort_by_start_time(self): return [Turn()]
        class Diarizer:
            def process(self, samples): return Result()
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent / "validation") as temporary:
            session = Path(temporary)
            entries = [{"source": key, "path": key + ".wav", "kind": kind}
                       for key, kind in (("mic", "microphone"), ("system", "loopback"),
                                         ("system_02", "loopback"))]
            for index, entry in enumerate(entries):
                samples = (.02 * np.sin(np.arange(32000) / 16000 * (200 + index * 80) * 2 * np.pi))
                sf.write(session / entry["path"], samples, 16000)
            (session / "session.json").write_text(json.dumps({"sources": entries,
                "capture_offsets_ms": {"system_02": 2000}}), encoding="utf-8")
            with patch.object(preparation, "mark_acoustic_echo") as detection:
                rows = preparation.prepare_session(session, "unused", "unused", diarizer=Diarizer(),
                                                   asr_echo_filter=False)
            self.assertFalse(detection.called, "Opt-out must not run echo inference")
            self.assertEqual({row["source"] for row in rows}, {"mic", "system", "system_02"})
            last = next(row for row in rows if row["source"] == "system_02")
            self.assertAlmostEqual(last["start"], 2.1)
            self.assertEqual(last["source_kind"], "loopback")
            summary = json.loads((session / "prepare_summary.json").read_text(encoding="utf-8"))
            self.assertEqual(set(summary["sources"]), {"mic", "system", "system_02"})
            self.assertFalse(summary["asr_echo_filter"])

    def test_light_denoise_removes_low_rumble_and_retains_speech_band(self):
        rate = preparation.SAMPLE_RATE
        time = np.arange(rate * 3) / rate
        rumble = .2 * np.sin(2 * np.pi * 20 * time)
        speech = .2 * np.sin(2 * np.pi * 300 * time)
        original = (rumble + speech).astype(np.float32)
        filtered = preparation.denoise_asr_clip(original, "light")
        center = slice(rate // 2, -rate // 2)
        def amplitude(samples, frequency):
            return abs(2 * np.mean(samples[center] * np.exp(-2j * np.pi * frequency * time[center])))
        self.assertLess(amplitude(filtered, 20) / .2, .02)
        self.assertGreater(amplitude(filtered, 300) / .2, .99)
        self.assertEqual(len(filtered), len(original))
        np.testing.assert_array_equal(original, (rumble + speech).astype(np.float32))

    def test_denoise_off_is_bit_exact_and_light_preserves_impulse_time(self):
        samples = np.zeros(preparation.SAMPLE_RATE * 2, dtype=np.float32)
        samples[preparation.SAMPLE_RATE] = .7
        off = preparation.denoise_asr_clip(samples, "off")
        np.testing.assert_array_equal(off, samples)
        light = preparation.denoise_asr_clip(samples, "light")
        self.assertEqual(len(light), len(samples))
        self.assertEqual(int(np.argmax(np.abs(light))), preparation.SAMPLE_RATE)
        self.assertTrue(np.isfinite(light).all())
        np.testing.assert_array_equal(preparation.denoise_asr_clip(samples[:8], "light"), samples[:8])
        with self.assertRaises(ValueError):
            preparation.denoise_asr_clip(samples, "aggressive")

    def test_cross_track_identity_uses_voice_vectors_not_local_number(self):
        groups = {
            "mic_01": {"source": "mic", "first_time": 5., "vector": np.array([1., 0., 0.])},
            "system_01": {"source": "system", "first_time": 0., "vector": np.array([0., 1., 0.])},
            "system_02": {"source": "system", "first_time": 10., "vector": np.array([.99, .01, 0.])},
        }
        mapped, uncertain = preparation.cluster_voice_identities(groups)
        self.assertEqual(mapped["mic_01"], mapped["system_02"])
        self.assertNotEqual(mapped["mic_01"], mapped["system_01"])
        self.assertEqual(mapped["system_01"], "speaker_01")
        self.assertEqual(mapped["mic_01"], "speaker_02")
        self.assertEqual(uncertain, set())

    def test_cross_track_identity_rejects_ambiguous_close_candidates(self):
        groups = {
            "mic_01": {"source": "mic", "first_time": 0., "vector": np.array([1., 0., 0.])},
            "system_01": {"source": "system", "first_time": 1., "vector": np.array([.9999, .01, 0.])},
            "mic_02": {"source": "mic", "first_time": 2., "vector": np.array([0., 1., 0.])},
            "system_02": {"source": "system", "first_time": 3., "vector": np.array([.7071, .7071, 0.])},
        }
        mapped, uncertain = preparation.cluster_voice_identities(groups)
        self.assertEqual(mapped["mic_01"], mapped["system_01"])
        self.assertNotEqual(mapped["mic_01"], mapped["mic_02"])
        self.assertIn("system_02", uncertain)

    def test_joint_turn_clustering_can_repair_a_wrong_local_cluster(self):
        groups = {
            "system_turn_1": {"source": "system", "first_time": 0., "vector": np.array([1., 0., 0.])},
            "system_turn_2": {"source": "system", "first_time": 1., "vector": np.array([0., 0., 1.])},
            "mic_turn_1": {"source": "mic", "first_time": 2., "vector": np.array([.5, .866, 0.])},
        }
        mapped, uncertain = preparation.cluster_voice_identities(groups)
        self.assertEqual(mapped["system_turn_1"], mapped["mic_turn_1"])
        self.assertNotEqual(mapped["system_turn_1"], mapped["system_turn_2"])
        self.assertEqual(uncertain, set())

    def test_global_identity_leaves_short_and_overlapping_turns_unknown(self):
        rows = [
            {"speaker": "mic_01", "source": "mic", "start": 0., "end": .5},
            {"speaker": "mic_02", "source": "mic", "start": 1., "end": 4.},
            {"speaker": "mic_03", "source": "mic", "start": 2., "end": 5.},
        ]
        summary = preparation.assign_global_speakers(rows, {}, {}, Path("unused"))
        self.assertEqual(summary["resolved_segments"], 0)
        self.assertEqual(summary["unknown_segments"], 3)
        self.assertTrue(all(row["speaker_status"] == "unknown" for row in rows))
        self.assertTrue(all(row["global_speaker"] is None for row in rows))

    def test_identity_anchor_uses_source_offset_without_modifying_audio(self):
        class Stream:
            def accept_waveform(self, rate, samples):
                self.samples = samples.copy()
            def input_finished(self):
                pass

        class Extractor:
            def __init__(self):
                self.streams = []
            def create_stream(self):
                stream = Stream()
                self.streams.append(stream)
                return stream
            def is_ready(self, stream):
                return True
            def compute(self, stream):
                return [1., 0., 0.]

        mic = np.linspace(-.2, .2, 32000, dtype=np.float32)
        system = np.linspace(.1, -.1, 32000, dtype=np.float32)
        sources = {"mic": mic.copy(), "system": system.copy()}
        rows = [{"start": 10., "end": 12., "source": "mic", "speaker": "mic_01"},
                {"start": 30., "end": 32., "source": "system", "speaker": "system_03"}]
        extractor = Extractor()
        summary = preparation.assign_global_speakers(rows, sources, {"mic": 10., "system": 30.},
                                                      Path("unused"), embedding_extractor=extractor)
        np.testing.assert_array_equal(extractor.streams[0].samples, mic)
        np.testing.assert_array_equal(extractor.streams[1].samples, system)
        np.testing.assert_array_equal(sources["mic"], mic)
        np.testing.assert_array_equal(sources["system"], system)
        self.assertEqual(summary["resolved_segments"], 2)
        self.assertEqual(rows[0]["global_speaker"], rows[1]["global_speaker"])
        self.assertEqual([row["speaker"] for row in rows], ["mic_01", "system_03"])

    def test_identity_backend_error_keeps_transcription_manifest_usable(self):
        class Extractor:
            def create_stream(self):
                raise RuntimeError("embedding fixture failure")

        rows = [{"start": 0., "end": 2., "source": "mic", "speaker": "mic_01"}]
        summary = preparation.assign_global_speakers(rows, {"mic": np.ones(32000, dtype=np.float32)}, {},
                                                      Path("unused"), embedding_extractor=Extractor())
        self.assertEqual(summary["unknown_segments"], 1)
        self.assertIn("embedding fixture failure", summary["error"])
        self.assertEqual(rows[0]["speaker"], "mic_01")

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

        class Stream:
            def accept_waveform(self, rate, samples):
                pass
            def input_finished(self):
                pass

        class Extractor:
            def create_stream(self):
                return Stream()
            def is_ready(self, stream):
                return True
            def compute(self, stream):
                return [1., 0., 0.]

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
            segments = preparation.prepare_session(session, "unused", "unused", diarizer=Diarizer(),
                                                     embedding_extractor=Extractor())
            self.assertEqual([item["speaker"] for item in segments], ["mic_01", "system_01"])
            self.assertEqual([item["global_speaker"] for item in segments], ["speaker_01", "speaker_01"])
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
