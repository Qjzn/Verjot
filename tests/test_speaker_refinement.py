"""Synthetic guards for the independent speaker candidate, no recordings."""
import unittest
import numpy as np
from SpeakerRefinement import core_centers, match, refine


class RefinementTests(unittest.TestCase):
    def setUp(self):
        self.bank = {"a": np.array([1., 0., 0.]), "b": np.array([0., 1., 0.])}

    def test_short_span_can_be_filled_without_adjacent_speech_or_new_identity(self):
        row = {"start": 0., "end": .7, "speaker": None}
        out = refine(row, [[1, 0, 0], [1, .1, 0]], self.bank)
        self.assertEqual(out["speaker"], "a")
        self.assertIsNone(row["speaker"])

    def test_repeated_padding_is_not_permission_to_skip_similarity_margin(self):
        self.assertIsNone(match([1, 1, 0], self.bank)["label"])
        row = {"start": 0., "end": .7, "speaker": None}
        self.assertIsNone(refine(row, [[1, 1, 0]] * 2, self.bank)["speaker"])

    def test_conflicting_views_do_not_fill_short_turn(self):
        row = {"start": 0., "end": .7, "speaker": None}
        self.assertIsNone(refine(row, [[1, 0, 0], [0, 1, 0]], self.bank)["speaker"])

    def test_raw_mic_is_not_a_rule_for_assigning_owner(self):
        row = {"start": 0., "end": 3., "speaker": "a"}
        out = refine(row, [], self.bank, [[0, 1, 0]] * 2, raw_eligible=True)
        self.assertEqual(out["speaker"], "b")
        self.assertEqual(out["refinement_evidence"], "raw_voice")

    def test_echo_or_inconsistent_raw_evidence_cannot_override_label(self):
        row = {"start": 0., "end": 3., "speaker": "a"}
        self.assertEqual(refine(row, [], self.bank, [[0, 1, 0]] * 2, False)["speaker"], "a")
        self.assertEqual(refine(row, [], self.bank, [[1, 0, 0], [0, 1, 0]], True)["speaker"], "a")

    def test_conflicting_source_needs_extra_margin_not_just_channel_priority(self):
        row = {"start": 0., "end": 3., "speaker": "a"}
        out = refine(row, [[1, 0, 0]] * 2, self.bank, [[0, 1, 0]] * 2, True)
        self.assertEqual(out["speaker"], "a")
        self.assertEqual(out["refinement_reason"], "source_conflict")

    def test_low_confidence_does_not_hide_existing_errors_as_unknown(self):
        row = {"start": 0., "end": 3., "speaker": "b"}
        self.assertEqual(refine(row, [[0, 0, 1]] * 2, self.bank)["speaker"], "b")

    def test_core_center_trims_outlier_without_removing_competing_identity(self):
        bank, counts = core_centers([[1, 0], [1, 0], [0, 1], [0, 1], [0, 1]], ["a", "a", "a", "b", "b"])
        self.assertEqual(counts, {"a": 2, "b": 2})
        np.testing.assert_allclose(bank["a"], [1, 0])
        bank, _ = core_centers([[1, 0], [1, 0], [0, 1]], ["a", "a", "b"])
        self.assertEqual(bank, {})

    def test_invalid_vectors_fail_instead_of_creating_confident_label(self):
        with self.assertRaises(ValueError):
            match([float("nan"), 0, 0], self.bank)
        with self.assertRaises(ValueError):
            match([0, 0, 0], self.bank)
