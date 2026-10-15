"""Prepare local dual-track recordings for ASR; no network calls.

Sources are segmented independently, then reliable voice embeddings are compared
across sources. Global anonymous labels are not real-world identity recognition.
Short, overlapping or ambiguous turns stay unassigned. Original WAVs are untouched.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Callable

import numpy as np


SAMPLE_RATE = 16000
MAX_CLIP_SECONDS = 24.0
MIN_TURN_SECONDS = 0.15
MERGE_GAP_SECONDS = 0.45
SILENCE_RMS = 0.0003
CONTEXT_SECONDS = 0.18
# Official Mandarin four-speaker example recommends 0.9 for automatic clustering.
# https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/models.html#d-speaker-model-onnx
DEFAULT_CLUSTER_THRESHOLD = 0.9
IDENTITY_MIN_SECONDS = 1.5
IDENTITY_MAX_ANCHOR_SECONDS = 6.0
IDENTITY_CLUSTER_DISTANCE = 0.70
IDENTITY_MARGIN = 0.08


def read_audio(path: Path) -> tuple[np.ndarray, dict]:
    """Read/resample without denoising or mixing separate recording sources."""
    import soundfile as sf
    from scipy.signal import resample_poly

    samples, rate = sf.read(path, dtype="float32", always_2d=True)
    if samples.size == 0:
        return np.empty(0, dtype=np.float32), {"sample_rate": rate, "channels": samples.shape[1]}
    if not np.isfinite(samples).all():
        raise ValueError(f"Non-finite audio samples: {path}")
    channel_rms = np.sqrt(np.mean(samples.astype(np.float64) ** 2, axis=0))
    mono = samples.mean(axis=1)
    # Stereo averaging can cancel opposite-polarity channels. Use the strongest
    # channel in that case, instead of silently producing a near-empty recording.
    if np.sqrt(np.mean(mono.astype(np.float64) ** 2)) < channel_rms.max() * 0.25:
        mono = samples[:, int(channel_rms.argmax())]
    mono = mono - mono.mean()
    if rate != SAMPLE_RATE:
        divisor = math.gcd(int(rate), SAMPLE_RATE)
        mono = resample_poly(mono, SAMPLE_RATE // divisor, int(rate) // divisor)
    return np.ascontiguousarray(mono, dtype=np.float32), {
        "sample_rate": rate,
        "channels": samples.shape[1],
        "duration": round(len(mono) / SAMPLE_RATE, 3),
    }


def peak_frame_rms(samples: np.ndarray) -> float:
    """Use the loudest 100 ms frame, so a short turn in a long file survives."""
    if len(samples) == 0:
        return 0.0
    frame = SAMPLE_RATE // 10
    count = (len(samples) + frame - 1) // frame
    padded = np.pad(samples, (0, count * frame - len(samples)))
    return float(np.sqrt(np.mean(padded.reshape(count, frame).astype(np.float64) ** 2, axis=1)).max())


def make_diarizer(segmentation_model: Path, embedding_model: Path, threads: int = 4,
                  threshold: float = DEFAULT_CLUSTER_THRESHOLD):
    """Use the official sherpa-onnx CPU Python API (>= 1.10.28)."""
    import sherpa_onnx

    for model in (segmentation_model, embedding_model):
        if not model.is_file():
            raise FileNotFoundError(f"Speaker model is missing: {model}")
    config = sherpa_onnx.OfflineSpeakerDiarizationConfig(
        segmentation=sherpa_onnx.OfflineSpeakerSegmentationModelConfig(
            pyannote=sherpa_onnx.OfflineSpeakerSegmentationPyannoteModelConfig(
                model=str(segmentation_model), window_shift_ratio=0.1),
            num_threads=threads, provider="cpu"),
        embedding=sherpa_onnx.SpeakerEmbeddingExtractorConfig(
            model=str(embedding_model), num_threads=threads, provider="cpu"),
        clustering=sherpa_onnx.FastClusteringConfig(num_clusters=-1, threshold=threshold),
        min_duration_on=0.3, min_duration_off=0.5)
    if not config.validate():
        raise RuntimeError("Invalid sherpa-onnx speaker model configuration")
    return sherpa_onnx.OfflineSpeakerDiarization(config)


def merge_turns(turns: list[dict], duration: float) -> list[dict]:
    """Merge consecutive same-speaker turns; never absorb another speaker."""
    cleaned = []
    for turn in sorted(turns, key=lambda item: (item["start"], item["end"])):
        start = max(0.0, min(float(turn["start"]), duration))
        end = max(start, min(float(turn["end"]), duration))
        if end - start < MIN_TURN_SECONDS:
            continue
        current = {"start": start, "end": end, "speaker": int(turn["speaker"])}
        if (cleaned and cleaned[-1]["speaker"] == current["speaker"]
                and current["start"] - cleaned[-1]["end"] <= MERGE_GAP_SECONDS):
            # Do not bridge over a speaker that overlaps the previous turn.
            previous = cleaned[-1]
            intervening = any(t["speaker"] != current["speaker"]
                              and t["start"] < current["start"]
                              and t["end"] > previous["start"] for t in cleaned[:-1])
            if not intervening:
                previous["end"] = max(previous["end"], current["end"])
                continue
        cleaned.append(current)
    return cleaned


def split_turn(samples: np.ndarray, start: float, end: float) -> list[tuple[int, int]]:
    """Cut long turns near a quiet boundary, staying strictly below 25 seconds."""
    left = max(0, int(round(start * SAMPLE_RATE)))
    right = min(len(samples), int(round(end * SAMPLE_RATE)))
    maximum = int(MAX_CLIP_SECONDS * SAMPLE_RATE)
    pieces = []
    while right - left > maximum:
        latest = min(left + maximum, right - SAMPLE_RATE)
        earliest = max(left + 12 * SAMPLE_RATE, latest - 4 * SAMPLE_RATE)
        frame = SAMPLE_RATE // 20
        boundary = latest
        lowest_energy = float("inf")
        for candidate in range(earliest, latest + 1, frame):
            context = samples[max(left, candidate - frame):min(right, candidate + frame)]
            energy = float(np.mean(context.astype(np.float64) ** 2))
            if energy < lowest_energy:
                lowest_energy, boundary = energy, candidate
        pieces.append((left, boundary))
        left = boundary
    if right > left:
        pieces.append((left, right))
    return pieces


def context_bounds(turns: list[dict], index: int, duration: float) -> tuple[float, float]:
    """Protect word onsets/ends using adjacent unlabelled audio only.

    The timeline keeps the diarizer's boundaries; only ASR audio receives this
    context. Half of a gap belongs to each neighbour, so padding does not overlap.
    """
    turn = turns[index]
    others = turns[:index] + turns[index + 1:]
    start, end = turn["start"], turn["end"]
    previous = [other for other in others if other["start"] <= start]
    following = [other for other in others if other["start"] >= end]
    if not any(other["start"] <= start < other["end"] for other in others):
        gap = start - max((other["end"] for other in previous), default=0)
        start -= min(CONTEXT_SECONDS, max(0, gap / 2 if previous else gap))
    if not any(other["start"] < end <= other["end"] for other in others):
        gap = min((other["start"] for other in following), default=duration) - end
        end += min(CONTEXT_SECONDS, max(0, gap / 2 if following else gap))
    return start, end


def normalize_clip(samples: np.ndarray) -> np.ndarray:
    """Limited gain only; raw recordings remain unchanged."""
    rms = float(np.sqrt(np.mean(samples.astype(np.float64) ** 2)))
    peak = float(np.max(np.abs(samples)))
    gain = min(10.0, 0.08 / max(rms, 1e-8), 0.98 / max(peak, 1e-8))
    return np.asarray(samples * gain, dtype=np.float32)


def denoise_asr_clip(samples: np.ndarray, mode: str = "light") -> np.ndarray:
    """Optional low-frequency rumble reduction for the ASR copy only.

    A zero-phase 70 Hz, second-order high-pass preserves sample count and avoids
    shifting timestamps. This is not general speech enhancement/echo separation.
    The original tracks, playback mix and speaker embeddings do not use it.
    """
    if mode not in ("light", "off"):
        raise ValueError("asr_denoise must be light or off")
    if mode == "off" or len(samples) <= 12:
        return np.asarray(samples, dtype=np.float32).copy()
    from scipy.signal import butter, sosfiltfilt
    sections = butter(2, 70, btype="highpass", fs=SAMPLE_RATE, output="sos")
    return np.asarray(sosfiltfilt(sections, samples), dtype=np.float32)


def mark_digital_duplicates(segments: list[dict], sources: dict[str, np.ndarray],
                            offsets: dict[str, float]) -> None:
    """Flag only near-identical simultaneous waveforms; do not delete audio.

    Ordinary acoustic echo is deliberately left for transcript review. A high
    waveform similarity is evidence of duplication, not speaker identification.
    """
    media = [item for item in segments if item["source"] == "system"]
    for mic in (item for item in segments if item["source"] == "mic"):
        for system in media:
            begin, finish = max(mic["start"], system["start"]), min(mic["end"], system["end"])
            if finish - begin < max(0.8, (mic["end"] - mic["start"]) * 0.9):
                continue
            count = int((finish - begin) * SAMPLE_RATE)
            mic_start = max(0, int(round((begin - offsets["mic"]) * SAMPLE_RATE)))
            system_start = max(0, int(round((begin - offsets["system"]) * SAMPLE_RATE)))
            # Subsampling is sufficient for this conservative same-waveform check.
            a = sources["mic"][mic_start:mic_start + count:4].astype(np.float64)
            b = sources["system"][system_start:system_start + count:4].astype(np.float64)
            length = min(len(a), len(b))
            if length < SAMPLE_RATE // 5:
                continue
            a, b = a[:length], b[:length]
            denominator = float(np.linalg.norm(a) * np.linalg.norm(b))
            similarity = abs(float(np.dot(a, b))) / max(denominator, 1e-12)
            if denominator > 1e-8 and similarity >= 0.998:
                mic["possible_duplicate_of"] = system["segment_id"]
                mic["waveform_similarity"] = round(similarity, 6)
                break


def cluster_voice_identities(groups: dict) -> tuple[dict[str, str], set[str]]:
    """Joint complete-link clustering of turn voice vectors across both sources.

    Local cluster numbers are never identity evidence. A leave-one-out comparison
    marks turns near competing cluster centroids uncertain instead of force fitting.
    The distance threshold and cosine scores are not calibrated probabilities.
    """
    names = sorted(groups, key=lambda name: (groups[name]["first_time"], name))
    vectors = {}
    for name in names:
        raw = groups[name].get("vector")
        if raw is None:
            continue
        vector = np.asarray(raw, dtype=np.float32)
        norm = float(np.linalg.norm(vector))
        if vector.ndim != 1 or not np.isfinite(vector).all() or norm < 1e-8:
            raise ValueError("Invalid speaker embedding")
        vectors[name] = vector / norm
    if not names:
        return {}, set()
    if len(vectors) != len(names):
        raise ValueError("Every global identity candidate needs a voice embedding")
    if len(names) == 1:
        return {names[0]: "speaker_01"}, set()
    import sherpa_onnx
    features = np.ascontiguousarray([vectors[name] for name in names], dtype=np.float32)
    config = sherpa_onnx.FastClusteringConfig(num_clusters=-1, threshold=IDENTITY_CLUSTER_DISTANCE)
    labels = sherpa_onnx.FastClustering(config)(features)
    members = {}
    for name, label in zip(names, labels):
        members.setdefault(label, []).append(name)
    uncertain = set()
    for name, label in zip(names, labels):
        peers = [vectors[other] for other in members[label] if other != name]
        if not peers:
            continue
        own = np.mean(peers, axis=0)
        own /= max(float(np.linalg.norm(own)), 1e-8)
        own_score = float(np.dot(vectors[name], own))
        for other_label, others in members.items():
            if other_label == label:
                continue
            other = np.mean([vectors[key] for key in others], axis=0)
            other /= max(float(np.linalg.norm(other)), 1e-8)
            if float(np.dot(vectors[name], other)) >= own_score - IDENTITY_MARGIN:
                uncertain.add(name)
                break
    mapped = {}
    ids = {}
    for name, label in zip(names, labels):
        ids.setdefault(label, f"speaker_{len(ids) + 1:02d}")
        mapped[name] = ids[label]
    return mapped, uncertain


def assign_global_speakers(segments: list[dict], sources: dict[str, np.ndarray],
                           offsets: dict[str, float], embedding_model: Path,
                           threads: int = 4, embedding_extractor=None) -> dict:
    """Attach meeting-wide anonymous IDs using clean anchors from both sources.

    Only the ASR manifest changes; no audio is filtered, normalized or overwritten.
    Existing `speaker` values remain stable for old ASR checkpoints/fingerprints.
    """
    eligible, groups = [], {}
    for row_index, row in enumerate(segments):
        row.update(global_speaker=None, speaker_global=False, speaker_status="unknown")
        duration = row["end"] - row["start"]
        overlap = any(other is not row and other["source"] == row["source"]
                      and other["speaker"] != row["speaker"]
                      and min(row["end"], other["end"]) - max(row["start"], other["start"]) > 0.08
                      for other in segments)
        if duration < IDENTITY_MIN_SECONDS or overlap or row["source"] not in sources:
            continue
        key = str(row_index)
        eligible.append((key, row))
        groups[key] = {"source": row["source"], "first_time": row["start"], "row": row, "vector": None}
    identity_error = None
    multiple_sources = len({group["source"] for group in groups.values()}) > 1
    if groups:
        try:
            if embedding_extractor is None:
                import sherpa_onnx
                config = sherpa_onnx.SpeakerEmbeddingExtractorConfig(
                    model=str(embedding_model), num_threads=threads, provider="cpu")
                if not config.validate():
                    raise RuntimeError("Invalid cross-source speaker model configuration")
                embedding_extractor = sherpa_onnx.SpeakerEmbeddingExtractor(config)
            for group in groups.values():
                row = group["row"]
                center = (row["start"] + row["end"]) / 2 - offsets.get(row["source"], 0)
                half = min(IDENTITY_MAX_ANCHOR_SECONDS, row["end"] - row["start"]) / 2
                first, last = round((center - half) * SAMPLE_RATE), round((center + half) * SAMPLE_RATE)
                samples = sources[row["source"]][max(0, first):last]
                if len(samples) < IDENTITY_MIN_SECONDS * SAMPLE_RATE:
                    continue
                stream = embedding_extractor.create_stream()
                stream.accept_waveform(SAMPLE_RATE, np.ascontiguousarray(samples, dtype=np.float32))
                stream.input_finished()
                if not embedding_extractor.is_ready(stream):
                    continue
                vector = np.asarray(embedding_extractor.compute(stream), dtype=np.float32)
                norm = float(np.linalg.norm(vector))
                if vector.ndim != 1 or not np.isfinite(vector).all() or norm < 1e-8:
                    continue
                group["vector"] = vector / norm
        except Exception as error:
            identity_error = str(error)
    valid = {name: group for name, group in groups.items() if group["vector"] is not None}
    mapped, uncertain = cluster_voice_identities(valid)
    for name, row in eligible:
        if name in mapped and name not in uncertain:
            row.update(global_speaker=mapped[name], speaker_global=True, speaker_status="matched")
    resolved = sum(row["speaker_global"] for row in segments)
    return {
        "method": "joint_turn_voice_embeddings_complete_link_v1",
        "minimum_anchor_seconds": IDENTITY_MIN_SECONDS,
        "maximum_cluster_cosine_distance": IDENTITY_CLUSTER_DISTANCE, "minimum_margin": IDENTITY_MARGIN,
        "resolved_segments": resolved, "unknown_segments": len(segments) - resolved,
        "global_speakers": len({row["global_speaker"] for row in segments if row["speaker_global"]}),
        "cross_source_embedding_comparison": multiple_sources,
        "error": identity_error,
    }


def prepare_session(session_dir: str | Path, segmentation_model: str | Path,
                    embedding_model: str | Path, progress: Callable[[dict], None] | None = None,
                    threads: int = 4, threshold: float = DEFAULT_CLUSTER_THRESHOLD, diarizer=None,
                    embedding_extractor=None, asr_denoise: str = "light") -> list[dict]:
    import soundfile as sf

    if asr_denoise not in ("light", "off"):
        raise ValueError("asr_denoise must be light or off")
    session_dir = Path(session_dir).resolve()
    metadata_path = session_dir / "session.json"
    metadata = json.loads(metadata_path.read_text(encoding="utf-8-sig")) if metadata_path.is_file() else {}
    raw_offsets = metadata.get("capture_offsets_ms", {})
    offsets = {source: float(raw_offsets.get(source, 0)) / 1000 for source in ("mic", "system")}
    if any(not math.isfinite(value) or value < 0 for value in offsets.values()):
        raise ValueError("capture_offsets_ms must contain non-negative finite numbers")
    clip_dir = session_dir / "clips"
    clip_dir.mkdir(exist_ok=True)
    sources, segments, summaries = {}, [], {}
    for source in ("mic", "system"):
        audio_path = session_dir / f"{source}.wav"
        if not audio_path.is_file():
            summaries[source] = {"status": "missing"}
            continue
        samples, info = read_audio(audio_path)
        sources[source] = samples
        info["peak_frame_rms"] = round(peak_frame_rms(samples), 6)
        summaries[source] = info
        if info["peak_frame_rms"] < SILENCE_RMS:
            info["status"] = "near_silent"
            continue
        if diarizer is None:
            diarizer = make_diarizer(Path(segmentation_model), Path(embedding_model), threads, threshold)
        if progress:
            progress({"stage": "speaker_segmentation", "source": source})
        result = diarizer.process(samples).sort_by_start_time()
        turns = merge_turns([{"start": item.start, "end": item.end, "speaker": item.speaker}
                            for item in result], len(samples) / SAMPLE_RATE)
        labels, count = {}, 0
        for index, turn in enumerate(turns):
            if turn["speaker"] not in labels:
                labels[turn["speaker"]] = f"{source}_{len(labels) + 1:02d}"
            pieces = split_turn(samples, turn["start"], turn["end"])
            context_start, context_end = context_bounds(turns, index, len(samples) / SAMPLE_RATE)
            for piece_index, (begin, finish) in enumerate(pieces):
                audio_begin = int(round(context_start * SAMPLE_RATE)) if piece_index == 0 else begin
                audio_finish = int(round(context_end * SAMPLE_RATE)) if piece_index == len(pieces) - 1 else finish
                clip = samples[audio_begin:audio_finish]
                if peak_frame_rms(clip) < SILENCE_RMS:
                    continue
                count += 1
                segment_id = f"{source}_{count:05d}"
                audio_output = clip_dir / f"{segment_id}.wav"
                sf.write(audio_output, normalize_clip(denoise_asr_clip(clip, asr_denoise)),
                         SAMPLE_RATE, subtype="PCM_16")
                segments.append({
                    "start": round(begin / SAMPLE_RATE + offsets[source], 4),
                    "end": round(finish / SAMPLE_RATE + offsets[source], 4),
                    "audio_start": round(audio_begin / SAMPLE_RATE + offsets[source], 4),
                    "audio_end": round(audio_finish / SAMPLE_RATE + offsets[source], 4),
                    "speaker": labels[turn["speaker"]],
                    "source": source,
                    "segment_id": segment_id,
                    "text_audio": str(audio_output),
                })
        info.update(status="prepared" if count else "no_speech", speakers=len(labels), segments=count)
    segments.sort(key=lambda item: (item["start"], item["end"], item["source"]))
    if progress:
        progress({"stage": "speaker_identity", "message": "正在比较声纹，统一跨音轨说话人"})
    identities = assign_global_speakers(segments, sources, offsets, Path(embedding_model),
                                       threads, embedding_extractor)
    if "mic" in sources and "system" in sources:
        mark_digital_duplicates(segments, sources, offsets)
    summary = {
        "sample_rate": SAMPLE_RATE,
        "speaker_cluster_threshold": threshold,
        "capture_offsets_ms": {key: value * 1000 for key, value in offsets.items()},
        "sources": summaries,
        "segments": len(segments),
        "global_speaker_identity": identities,
        "asr_denoise": asr_denoise,
        "limitations": [
            "Global speaker labels use conservative cross-source voice comparisons, not identified people.",
            "Short, overlapping or ambiguous speech remains unassigned; real device/channel differences may affect matches.",
            "Possible duplicate audio is flagged and retained; acoustic echo may remain.",
        ],
    }
    # Replace result manifests only after all model processing and clip writes succeed.
    for filename, value in (("segments.json", segments), ("prepare_summary.json", summary)):
        destination = session_dir / filename
        temporary = destination.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
        temporary.replace(destination)
    if progress:
        progress({"stage": "audio_prepared", "segments": len(segments)})
    return segments


def find_models(models_dir: Path) -> tuple[Path, Path]:
    names = [models_dir / "segmentation" / "model.onnx",
             models_dir / "sherpa-onnx-pyannote-segmentation-3-0" / "model.onnx",
             models_dir / "segmentation" / "model.int8.onnx",
             models_dir / "sherpa-onnx-pyannote-segmentation-3-0" / "model.int8.onnx"]
    segmentation = next((path for path in names if path.is_file()), names[0])
    embedding = models_dir / "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx"
    return segmentation, embedding


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", required=True, type=Path)
    parser.add_argument("--models", required=True, type=Path)
    parser.add_argument("--threads", default=4, type=int)
    parser.add_argument("--threshold", default=DEFAULT_CLUSTER_THRESHOLD, type=float)
    parser.add_argument("--asr-denoise", choices=("light", "off"), default="light",
                        help="Optional 70 Hz low-frequency reduction on ASR copies; originals/mix unchanged")
    args = parser.parse_args()
    segmentation, embedding = find_models(args.models)
    prepare_session(args.session, segmentation, embedding, threads=args.threads,
                    threshold=args.threshold, asr_denoise=args.asr_denoise,
                    progress=lambda event: print(json.dumps(event), flush=True))


if __name__ == "__main__":
    main()
