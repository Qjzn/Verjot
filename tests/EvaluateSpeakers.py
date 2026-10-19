"""Score frozen human review windows, learning anonymous/name mapping on dev only.

Unknown references and overlapping reference speech are reported separately.
Predictions outside annotated speech are not counted: this is not full DER.
"""
import argparse
from collections import defaultdict
import json
import math
from pathlib import Path

import numpy as np
from scipy.optimize import linear_sum_assignment


def pieces(reference, predictions):
    points = sorted({r[k] for r in reference + predictions for k in ("start", "end")})
    for start, end in zip(points, points[1:]):
        middle = (start + end) / 2
        refs = {r["identity"] for r in reference if r["start"] <= middle < r["end"]}
        if not refs:
            continue
        labels = {r["speaker"] for r in predictions if r["start"] <= middle < r["end"]}
        yield end - start, refs, labels


def evaluate(review, predictions, own_identity="本人"):
    if review.get("human_verified") is not True or review.get("split_frozen") is not True:
        raise ValueError("Human-confirmed frozen review required; competitor output is not ground truth")
    clips = review["clips"]
    refs = review["segments"]
    if (not clips or len({c["id"] for c in clips}) != len(clips)
            or {c["split"] for c in clips} != {"dev", "holdout"}
            or any(not all(math.isfinite(c[k]) for k in ("start", "end"))
                   or not 0 <= c["start"] < c["end"] for c in clips)):
        raise ValueError("Unique finite development and held-out review windows required")
    ordered_clips = sorted(clips, key=lambda c: c["start"])
    if any(a["end"] > b["start"] for a, b in zip(ordered_clips, ordered_clips[1:])):
        raise ValueError("Frozen review windows must not overlap or leak between splits")
    if not refs or any(r.get("human_verified") is not True for r in refs):
        raise ValueError("Each reference segment must be manually confirmed")
    for r in refs:
        c = next((c for c in clips if c["id"] == r["clip_id"]), None)
        if (not c or r["split"] != c["split"] or not c["start"] <= r["start"] < r["end"] <= c["end"]
                or not isinstance(r["identity"], str) or not r["identity"]):
            raise ValueError("Reference segment escapes its frozen review window")
    for r in predictions:
        if not all(math.isfinite(r[k]) for k in ("start", "end")) or not 0 <= r["start"] < r["end"]:
            raise ValueError("Invalid prediction interval")
    # Clip independently: never bridge across unreviewed gaps or sample splits.
    material = {"dev": [], "holdout": []}
    for clip in clips:
        reference = [r for r in refs if r["clip_id"] == clip["id"]]
        predicted = [{**r, "start": max(r["start"], clip["start"]), "end": min(r["end"], clip["end"])}
                     for r in predictions if r["end"] > clip["start"] and r["start"] < clip["end"]]
        material[clip["split"]].extend(pieces(reference, predicted))
    matrix = defaultdict(lambda: defaultdict(float))
    for duration, identities, labels in material["dev"]:
        if len(identities) == len(labels) == 1 and "未知" not in identities and None not in labels:
            matrix[next(iter(labels))][next(iter(identities))] += duration
    labels = sorted(matrix)
    identities = sorted({i for values in matrix.values() for i in values})
    if not labels or not identities:
        raise ValueError("No usable development annotations; cannot map held-out identities")
    weights = np.array([[matrix[label][i] for i in identities] for label in labels])
    rows, cols = linear_sum_assignment(-weights)
    mapping = {labels[r]: identities[c] for r, c in zip(rows, cols) if weights[r, c] > 0}
    stats = {}
    for split, intervals in material.items():
        result = defaultdict(float)
        fragments = defaultdict(lambda: defaultdict(float))
        for duration, truth, predicted in intervals:
            if "未知" in truth:
                result["unknown_reference_seconds"] += duration
                continue
            if len(truth) > 1:
                result["reference_overlap_seconds"] += duration
                continue
            identity = next(iter(truth))
            result["annotated_nonoverlap_speech_seconds"] += duration
            if identity == own_identity:
                result["own_annotated_seconds"] += duration
            for label in predicted - {None}:
                fragments[identity][label] += duration
            if not predicted or predicted == {None}:
                outcome = "missing_or_unknown"
            elif len(predicted) > 1:
                outcome = "ambiguous_prediction"
            else:
                label = next(iter(predicted))
                outcome = "unmapped" if label not in mapping else "correct" if mapping[label] == identity else "wrong_identity"
            result[outcome + "_seconds"] += duration
            if identity == own_identity:
                result["own_" + outcome + "_seconds"] += duration
        result["labels_per_identity_over_200ms"] = {
            i: sorted(label for label, duration in values.items() if duration >= .2)
            for i, values in fragments.items()}
        stats[split] = dict(result)
    return {"mapping_learned_on": "dev", "identity_mapping": mapping, "splits": stats,
            "full_DER": False, "accuracy_verified": False,
            "scope": "Human-annotated non-overlap speech only. Unannotated speech, false alarms and overlap accuracy are not evaluated; no aggregate pass decision."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--review", type=Path, required=True)
    parser.add_argument("--prediction", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("Refusing to overwrite an existing evaluation")
    review = json.loads(args.review.read_text(encoding="utf-8-sig"))
    prediction = json.loads(args.prediction.read_text(encoding="utf-8-sig"))
    rows = prediction["turns"] if isinstance(prediction, dict) else prediction
    predictions = [{"start": r["start"], "end": r["end"],
                    "speaker": None if r.get("speaker_global") is False else str(r.get("global_speaker") or r["speaker"])} for r in rows]
    result = evaluate(review, predictions)
    result["review_source_sha256"] = review["source_sha256"]
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
