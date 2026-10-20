"""Offline capture transport checks, not physical provenance or accuracy verification."""

import argparse
from collections import Counter
import json
import math
from pathlib import Path
import sys


SCHEMA = "questx.nt.capture.v1"
VIO = "/QuestX/v2/vio"
V4 = "/QuestX/v4/apriltag"
V3 = "/QuestX/v3/apriltag"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def number(value):
    return type(value) in (int, float) and math.isfinite(value)


def uint(value, maximum=0xFFFFFFFF):
    return number(value) and 0 <= value <= maximum and value == int(value)


def words(data, offset):
    return int(data[offset]) | (int(data[offset + 1]) << 32)


def validate_common(data):
    require(isinstance(data, list) and len(data) >= 12, "missing common header")
    require(all(number(v) for v in data), "all values must be finite numbers")
    require(all(uint(v) for v in data[:4]), "sequence/capture words must be uint32")
    require(0 < words(data, 2) < 2**63, "capture timestamp must be positive int64")
    require(data[4] >= 0, "app uptime must be nonnegative")
    norm = sum(v * v for v in data[8:12])
    # Matches Java: accepts nonzero finite quaternion and normalizes when constructing rotation.
    require(math.isfinite(norm) and norm > 1e-12, "quaternion must have finite nonzero norm")


def positive_definite(upper, size):
    matrix = [[0.0] * size for _ in range(size)]
    index = 0
    for row in range(size):
        for col in range(row, size):
            matrix[row][col] = matrix[col][row] = upper[index]
            index += 1
    factor = [[0.0] * size for _ in range(size)]
    for row in range(size):
        for col in range(row + 1):
            residual = matrix[row][col] - sum(
                factor[row][k] * factor[col][k] for k in range(col)
            )
            if not math.isfinite(residual):
                return False
            if row == col:
                if residual <= 0:
                    return False
                factor[row][col] = math.sqrt(residual)
            else:
                factor[row][col] = residual / factor[col][col]
    return True


def validate_apriltag(data, v4=True):
    """Wire checks only, following MeasurementWireFormat.java and measurement-v4.md."""
    header = 53 if v4 else 26
    require(isinstance(data, list) and len(data) >= header, f"requires {header}-value header")
    validate_common(data)
    require(uint(data[12]), "tag count must be uint32")
    count = int(data[12])
    require(len(data) == header + 2 * count, f"length must be {header} + 2 * tagCount")
    require(uint(data[13]) and uint(data[14]) and data[13] <= data[14], "invalid corner counts")
    require(0 <= data[15] <= 1, "inlier ratio out of range")
    require(all(v >= 0 for v in data[16:23]), "negative distance or standard deviation")
    require(0 <= data[23] <= 1 and data[24] >= 0 and data[25] >= 0, "invalid quality fields")
    require(all(uint(v, 0x7FFFFFFF) for v in data[header:header + count]), "invalid tag ID")
    require(all(0 <= v <= 1 for v in data[header + count:]), "invalid per-tag ambiguity")
    unavailable = False
    if v4:
        require(positive_definite(data[26:47], 6), "6D covariance must be positive definite")
        unavailable = all(v == 0 for v in data[47:53])
        require(unavailable or positive_definite(data[47:53], 3),
                "planar covariance must be positive definite or exactly six zeros")
    return {"sequence": words(data, 0), "capture_us": words(data, 2),
            "planar_unavailable": unavailable}


def validate_vio(data):
    require(isinstance(data, list) and len(data) == 14, "VIO requires exactly 14 values")
    validate_common(data)
    require(data[12] in (0, 1), "VIO tracking flag must be 0 or 1")
    require(uint(data[13]), "VIO epoch must be uint32")
    return {"sequence": words(data, 0), "capture_us": words(data, 2),
            "tracking": bool(data[12]), "epoch": int(data[13])}


def progression(samples):
    """Only compare adjacent stamps within one topic's publisher clock."""
    captures = [entry["capture_us"] for entry in samples]
    sequences = [entry["sequence"] for entry in samples]
    return {
        "capture_advancing_pairs": sum(b > a for a, b in zip(captures, captures[1:])),
        "capture_repeated_pairs": sum(b == a for a, b in zip(captures, captures[1:])),
        "capture_regressions": sum(b < a for a, b in zip(captures, captures[1:])),
        "sequence_repeated_pairs": sum(b == a for a, b in zip(sequences, sequences[1:])),
        "sequence_regressions": sum(b < a for a, b in zip(sequences, sequences[1:])),
        "sequence_gap_pairs": sum(b > a + 1 for a, b in zip(sequences, sequences[1:])),
    }


def verify_capture(path):
    counts = Counter()
    errors = []
    valid = {VIO: [], V4: [], V3: []}
    received = {}
    metadata = None
    end = None
    with Path(path).open(encoding="utf-8") as source:
        for line_number, line in enumerate(source, 1):
            try:
                record = json.loads(line)
                require(isinstance(record, dict), "record must be an object")
                kind = record.get("type")
                require(end is None, "record after end")
                if kind == "metadata":
                    require(line_number == 1, "metadata must be first and unique")
                    require(record.get("schema") == SCHEMA, "unsupported capture schema")
                    require(record.get("mode") in ("server", "client"), "invalid capture mode")
                    topics = record.get("topics")
                    require(isinstance(topics, list) and all(isinstance(p, str) for p in topics),
                            "metadata topics must be a list of paths")
                    require(isinstance(record.get("clocks"), dict) and all(
                        isinstance(record["clocks"].get(key), str) for key in (
                            "receive_monotonic_ns", "receive_unix_ns", "nt_local_now_us",
                            "nt_local_us", "nt_server_us", "nt_server_offset_us",
                            "value_capture_words", "value_app_seconds")), "clock metadata missing")
                    metadata = record
                elif kind == "sample":
                    require(metadata is not None, "sample before valid metadata")
                    topic = record.get("path")
                    require(isinstance(topic, str) and topic in metadata.get("topics", []),
                            "sample path absent from metadata topics")
                    counts[topic] += 1
                    for field in ("receive_monotonic_ns", "receive_unix_ns", "nt_local_now_us", "nt_local_us"):
                        require(type(record.get(field)) is int and record[field] > 0,
                                f"{field} must be positive integer")
                    require(type(record.get("nt_server_us")) is int and record["nt_server_us"] >= 0,
                            "nt_server_us must be nonnegative integer")
                    offset = record.get("nt_server_offset_us")
                    require(offset is None or type(offset) is int, "invalid NT server offset")
                    stamp = record["receive_monotonic_ns"]
                    stamps = received.setdefault(topic, [])
                    require(not stamps or stamp >= stamps[-1], "receive monotonic time regressed")
                    stamps.append(stamp)
                    data = record.get("value")
                    require(isinstance(data, list) and all(number(v) for v in data),
                            "all sample values must be finite numbers")
                    if topic == VIO:
                        valid[topic].append(validate_vio(data))
                    elif topic in (V4, V3):
                        valid[topic].append(validate_apriltag(data, topic == V4))
                    # Limelight topics are preserved, not correlated with an independent latest frame.
                elif kind == "end":
                    end = record
                    require(record.get("complete") is True and record.get("errors") == [],
                            "capture reported incomplete/runtime/shutdown failure")
                    require(record.get("reason") in ("duration", "interrupt"), "invalid end reason")
                    require(number(record.get("elapsed_seconds")) and record["elapsed_seconds"] >= 0,
                            "invalid elapsed duration")
                    require(record.get("counts") == dict(counts), "end counts differ from sample records")
                else:
                    raise ValueError("unknown record type")
            except (ValueError, TypeError, OverflowError) as exc:
                errors.append(f"line {line_number}: {exc}")
    if metadata is None:
        errors.append("missing valid metadata")
    if end is None:
        errors.append("missing end record (incomplete capture)")
    if not valid[V4]:
        errors.append("no valid /QuestX/v4/apriltag sample")
    if not valid[VIO]:
        errors.append("no valid /QuestX/v2/vio sample")
    vio = valid[VIO]
    epochs = list(dict.fromkeys(s["epoch"] for s in vio))
    timing = {}
    for topic, stamps in received.items():
        gaps = [b - a for a, b in zip(stamps, stamps[1:])]
        timing[topic] = {
            "receive_dequeue_span_seconds": (stamps[-1] - stamps[0]) / 1e9,
            "max_receive_dequeue_gap_seconds": max(gaps, default=0) / 1e9,
        }
        if topic in valid:
            timing[topic].update(progression(valid[topic]))
    return {
        "check": "capture transport checks", "passed": not errors,
        "accuracy": "unverified", "hardware_provenance": "unverified",
        "samples": dict(counts), "valid_atomic_samples": {k: len(v) for k, v in valid.items()},
        "planar_unavailable_samples": sum(s["planar_unavailable"] for s in valid[V4]),
        "vio": {
            "epochs_observed": epochs,
            "epoch_changes": sum(a["epoch"] != b["epoch"] for a, b in zip(vio, vio[1:])),
            "not_tracking_samples": sum(not s["tracking"] for s in vio),
            "tracking_loss_events": sum(a["tracking"] and not b["tracking"] for a, b in zip(vio, vio[1:])),
            "tracking_resume_events": sum(not a["tracking"] and b["tracking"] for a, b in zip(vio, vio[1:])),
        },
        "timing": timing,
        "limitations": [
            "Receive times measure queue draining; finite NT queues may overflow. Counts do not prove lossless delivery.",
            "Publisher capture words, NT server time, app uptime and host wall time are separate clock domains; no cross-domain latency computed.",
            "No cross-stream association, ground truth, accuracy, NEES or robot reset acknowledgment checked.",
            "Fresh timestamps or a frozen pose do not prove live tracking; tracking flags and epochs are reported as received.",
        ],
        "errors": errors,
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path, help="JSONL file from quest_nt_capture.py")
    args = parser.parse_args(argv)
    try:
        report = verify_capture(args.capture)
    except (OSError, UnicodeError) as exc:
        print(f"verification failed: {exc}", file=sys.stderr)
        return 1
    print(json.dumps(report, indent=2, allow_nan=False))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
