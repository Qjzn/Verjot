"""Evaluator checks use fabricated intervals only, not real identities or audio."""
import unittest
from EvaluateSpeakers import evaluate


class SpeakerEvaluationTests(unittest.TestCase):
    def review(self):
        return {"human_verified": True, "split_frozen": True,
                "clips": [{"id": "a", "split": "dev", "start": 0, "end": 4},
                          {"id": "b", "split": "holdout", "start": 10, "end": 14}],
                "segments": [{"clip_id": key, "split": split, "start": start, "end": start + 2,
                              "identity": "本人", "human_verified": True}
                             for key, split, start in [("a", "dev", 0), ("b", "holdout", 10)]]}

    def test_does_not_relearn_mapping_on_holdout(self):
        rows = [{"start": 0, "end": 2, "speaker": "A"}, {"start": 10, "end": 12, "speaker": "B"}]
        result = evaluate(self.review(), rows)
        self.assertEqual(result["identity_mapping"], {"A": "本人"})
        self.assertEqual(result["splits"]["holdout"]["own_unmapped_seconds"], 2)
        self.assertFalse(result["full_DER"])

    def test_unknown_and_overlap_are_not_silently_scored_as_correct(self):
        review = self.review()
        review["segments"] += [{"clip_id": "b", "split": "holdout", "start": 10, "end": 11,
                                "identity": "他人", "human_verified": True},
                               {"clip_id": "b", "split": "holdout", "start": 12, "end": 13,
                                "identity": "未知", "human_verified": True}]
        result = evaluate(review, [{"start": 0, "end": 14, "speaker": "A"}])["splits"]["holdout"]
        self.assertEqual(result["reference_overlap_seconds"], 1)
        self.assertEqual(result["unknown_reference_seconds"], 1)
        self.assertEqual(result["own_correct_seconds"], 1)

    def test_no_prediction_counts_as_missing(self):
        result = evaluate(self.review(), [{"start": 0, "end": 2, "speaker": "A"}])
        self.assertEqual(result["splits"]["holdout"]["own_missing_or_unknown_seconds"], 2)

    def test_merged_people_cannot_both_become_the_same_identity(self):
        review = self.review()
        review["segments"][0]["end"] = 1
        review["segments"].append({"clip_id": "a", "split": "dev", "start": 1, "end": 4,
                                   "identity": "他人", "human_verified": True})
        result = evaluate(review, [{"start": 0, "end": 14, "speaker": "A"}])
        self.assertEqual(result["identity_mapping"], {"A": "他人"})
        self.assertEqual(result["splits"]["holdout"]["own_wrong_identity_seconds"], 2)

    def test_split_person_remains_visible_in_holdout(self):
        rows = [{"start": 0, "end": 2, "speaker": "A"},
                {"start": 10, "end": 11, "speaker": "A"},
                {"start": 11, "end": 12, "speaker": "B"}]
        result = evaluate(self.review(), rows)["splits"]["holdout"]
        self.assertEqual(result["labels_per_identity_over_200ms"]["本人"], ["A", "B"])
        self.assertEqual(result["own_correct_seconds"], 1)
        self.assertEqual(result["own_unmapped_seconds"], 1)

    def test_rejects_unverified_or_out_of_bounds_reference(self):
        review = self.review()
        review["human_verified"] = False
        with self.assertRaises(ValueError): evaluate(review, [])
        review["human_verified"] = True
        review["segments"][1]["end"] = 20
        with self.assertRaises(ValueError): evaluate(review, [])

    def test_rejects_overlapping_or_invalid_frozen_clips(self):
        rows = [{"start": 0, "end": 14, "speaker": "A"}]
        for clip in ({"id": "extra", "split": "holdout", "start": 1, "end": 3},
                     {"id": "a", "split": "dev", "start": 20, "end": 21},
                     {"id": "extra", "split": "dev", "start": float("nan"), "end": 21},
                     {"id": "extra", "split": "other", "start": 20, "end": 21}):
            review = self.review()
            review["clips"].append(clip)
            with self.assertRaises(ValueError): evaluate(review, rows)


if __name__ == "__main__": unittest.main()
