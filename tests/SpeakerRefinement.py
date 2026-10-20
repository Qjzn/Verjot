"""Experimental anonymous refinement; not imported by the installed worker.

The caller supplies within-turn embeddings and an existing anonymous clustering.
No human names, expected speaker count, or channel-to-person rule is used.
Cosine/margin gates are fixed diagnostics, not calibrated probabilities.
"""
import numpy as np

MIN_COSINE = .5
MIN_MARGIN = .1
MIN_SECONDS = .25


def unit(values):
    values = np.asarray(values, dtype=np.float32)
    norms = np.linalg.norm(values, axis=-1, keepdims=True)
    if not np.isfinite(values).all() or np.any(norms < 1e-8):
        raise ValueError("Finite nonzero voice vectors required")
    return values / norms


def core_centers(embeddings, labels):
    """One fixed trimming pass; never recruit outliers or create new identities."""
    vectors = unit(embeddings)
    labels = np.asarray(labels)
    if vectors.ndim != 2 or len(vectors) != len(labels):
        raise ValueError("One label per embedding required")
    names = sorted(set(labels.tolist()))
    if len(names) < 2:
        return {}, {}
    centers = unit(np.stack([vectors[labels == name].mean(0) for name in names]))
    scores = vectors @ centers.T
    result, counts = {}, {}
    for i, name in enumerate(names):
        others = np.delete(scores, i, axis=1).max(axis=1)
        keep = (labels == name) & (scores[:, i] >= MIN_COSINE) & (scores[:, i] - others >= MIN_MARGIN)
        counts[name] = int(keep.sum())
        if keep.sum() >= 2:
            result[name] = unit(vectors[keep].mean(0))
    # Removing a competing center must not artificially increase acceptance.
    return (result if len(result) == len(names) else {}), counts


def match(vector, centers):
    if len(centers) < 2:
        return {"label": None, "reason": "insufficient_evidence"}
    names = sorted(centers)
    scores = unit(vector) @ np.stack([centers[name] for name in names]).T
    order = np.argsort(-scores)
    best, margin = float(scores[order[0]]), float(scores[order[0]] - scores[order[1]])
    accepted = best >= MIN_COSINE and margin >= MIN_MARGIN
    return {"label": names[order[0]] if accepted else None, "cosine": best, "margin": margin,
            "reason": None if accepted else "ambiguous_voice" if best >= MIN_COSINE else "insufficient_evidence"}


def agree(views, centers):
    if len(views) != 2:
        return {"label": None, "reason": "insufficient_evidence"}
    votes = [match(view, centers) for view in views]
    if votes[0]["label"] is not None and votes[0]["label"] == votes[1]["label"]:
        return {"label": votes[0]["label"], "cosine": min(v["cosine"] for v in votes),
                "margin": min(v["margin"] for v in votes), "reason": None}
    return {"label": None, "reason": "ambiguous_voice"}


def refine(original, mix_views, centers, raw_views=(), raw_eligible=False):
    """Keep established labels unless fresh evidence passes every gate.

    A raw microphone is evidence about a voice, never proof of the owner's
    identity. Two raw views must agree, be internally consistent, and beat a
    conflicting mix decision by an additional margin. No unknown inflation.
    """
    row = dict(original)
    if row["end"] - row["start"] < MIN_SECONDS:
        return {**row, "speaker_reason": "short_utterance"}
    mixed = agree(mix_views, centers)
    winner = mixed
    evidence = "within_turn"
    if raw_eligible and len(raw_views) == 2 and float(unit(raw_views[0]) @ unit(raw_views[1])) >= MIN_COSINE:
        raw = agree(raw_views, centers)
        if raw["label"] is not None:
            if mixed["label"] is None or raw["label"] == mixed["label"] or raw["cosine"] >= mixed["cosine"] + MIN_MARGIN:
                winner, evidence = raw, "raw_voice"
            else:
                row["refinement_reason"] = "source_conflict"
    if winner["label"] is not None:
        row.update(speaker=winner["label"], speaker_reason=None, refinement_evidence=evidence)
    elif row.get("speaker") is None:
        row["speaker_reason"] = winner["reason"]
    return row
