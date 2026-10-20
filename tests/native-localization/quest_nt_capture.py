"""Read-only NT4 capture. Requires pyntcore; offline verification does not."""

import argparse
from collections import Counter
from importlib.metadata import version
import json
import math
from pathlib import Path
import sys
import time


SCHEMA = "questx.nt.capture.v1"
TOPICS = (
    "/QuestX/v2/vio",
    "/QuestX/v4/apriltag",
    "/QuestX/v3/apriltag",
    "/QuestX/limelight/botpose_wpiblue",
    "/QuestX/limelight/headsetpose_wpiblue",
    "/QuestX/limelight/stddevs",
)
CLOCKS = {
    "receive_monotonic_ns": "capture host Python monotonic; queue-drain time, not packet arrival",
    "receive_unix_ns": "capture host Unix wall clock; may jump",
    "nt_local_now_us": "capture host ntcore clock at queue drain",
    "nt_local_us": "value timestamp translated by NT into capture host ntcore clock",
    "nt_server_us": "value timestamp in NT server clock; 0/1 may mean unavailable",
    "nt_server_offset_us": "server minus capture host ntcore clock at drain; null if unsynchronized",
    "value_capture_words": "indices 2/3 of atomic VIO/AprilTag: publisher Quest ntcore clock, NOT server clock",
    "value_app_seconds": "index 4: publisher app uptime; diagnostic only",
}


def metadata(args):
    return {
        "type": "metadata", "schema": SCHEMA,
        "mode": "client" if args.server else "server",
        "server": args.server, "listen": args.listen,
        "port4": args.port4, "port3": args.port3,
        "duration_seconds": args.duration, "topics": list(TOPICS),
        "queue_capacity": 4096, "poll_seconds": 0.01,
        "clocks": CLOCKS,
        "nonfinite_encoding": "NaN, +Infinity, -Infinity strings preserve invalid received values",
        "claims": "capture transport only; provenance and accuracy unverified",
    }


def json_value(value):
    # Preserve invalid samples without emitting nonstandard JSON numeric literals.
    if math.isfinite(value):
        return value
    return "NaN" if math.isnan(value) else ("+Infinity" if value > 0 else "-Infinity")


def capture(args, ntcore):
    """Persist each queued update, including duplicates; never publish or write entries."""
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # Refuse to overwrite evidence from an earlier run.
    with args.output.open("x", encoding="utf-8") as output:
        def write(record):
            output.write(json.dumps(record, allow_nan=False) + "\n")

        info = metadata(args)
        info["pyntcore_version"] = version("pyntcore")
        write(info)
        output.flush()
        inst = None
        subscribers = {}
        counts = Counter()
        errors = []
        reason = "duration"
        started = time.monotonic()

        def drain():
            for path, sub in subscribers.items():
                for sample in sub.readQueue():
                    write({
                        "type": "sample", "path": path,
                        "value": [json_value(v) for v in sample.value],
                        "receive_monotonic_ns": time.monotonic_ns(),
                        "receive_unix_ns": time.time_ns(),
                        "nt_local_now_us": ntcore._now(),
                        "nt_local_us": sample.time,
                        "nt_server_us": sample.serverTime,
                        "nt_server_offset_us": inst.getServerTimeOffset(),
                    })
                    counts[path] += 1
            output.flush()

        try:
            inst = ntcore.NetworkTableInstance.create()
            levels = ntcore.NetworkTableInstance.LogLevel

            def log_error(event):
                message = event.data
                # ntcore 2026 reports failed server binds as INFO "socket error".
                if message.level >= levels.kLogError.value or "socket error:" in message.message:
                    errors.append(f"ntcore: {message.message}")

            inst.addLogger(levels.kLogInfo, levels.kLogCritical, log_error)
            options = ntcore.PubSubOptions(
                sendAll=True, keepDuplicates=True, pollStorage=4096, periodic=0.01
            )
            for path in TOPICS:
                subscribers[path] = inst.getDoubleArrayTopic(path).subscribe([], options)
            if args.server:
                inst.setServer(args.server, args.port4)
                inst.startClient4("questx-read-only-capture")
            else:
                inst.startServer("", args.listen, args.port3, args.port4)
            print(f"READY {info['mode']} NT4 port {args.port4}; {args.output}", flush=True)
            while time.monotonic() - started < args.duration:
                drain()
                if errors:
                    raise RuntimeError("NT reported an error; see end record")
                time.sleep(min(0.01, max(0, args.duration - (time.monotonic() - started))))
        except KeyboardInterrupt:
            reason = "interrupt"
        except Exception as exc:
            reason = "error"
            errors.append(f"capture: {type(exc).__name__}: {exc}")
        finally:
            # Freeze incoming traffic, then save the remaining queued updates before close.
            if inst is not None:
                try:
                    if args.server:
                        inst.stopClient()
                    else:
                        inst.stopServer()
                except Exception as exc:
                    errors.append(f"stop: {type(exc).__name__}: {exc}")
                try:
                    drain()
                except Exception as exc:
                    errors.append(f"final drain: {type(exc).__name__}: {exc}")
                for path, sub in subscribers.items():
                    try:
                        sub.close()
                    except Exception as exc:
                        errors.append(f"close {path}: {type(exc).__name__}: {exc}")
                try:
                    if not inst.waitForListenerQueue(1.0):
                        errors.append("shutdown: NT listener queue did not drain within 1 second")
                except Exception as exc:
                    errors.append(f"listener shutdown: {type(exc).__name__}: {exc}")
                try:
                    ntcore.NetworkTableInstance.destroy(inst)
                except Exception as exc:
                    errors.append(f"destroy: {type(exc).__name__}: {exc}")
            write({
                "type": "end", "complete": not errors,
                "reason": "error" if errors else reason,
                "elapsed_seconds": time.monotonic() - started,
                "counts": dict(counts), "errors": errors,
            })
            output.flush()
        if errors:
            raise RuntimeError("; ".join(errors))


def bounded_duration(value):
    seconds = float(value)
    if not math.isfinite(seconds) or not 0 < seconds <= 3600:
        raise argparse.ArgumentTypeError("duration must be > 0 and <= 3600 seconds")
    return seconds


def port(value):
    number = int(value)
    if not 1 <= number <= 65535:
        raise argparse.ArgumentTypeError("port must be between 1 and 65535")
    return number


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path("work/napa/capture.jsonl"))
    parser.add_argument("--duration", type=bounded_duration, default=30.0)
    parser.add_argument("--listen", default="127.0.0.1", help="server bind address")
    parser.add_argument("--port4", type=port, default=5810)
    parser.add_argument("--port3", type=port, default=1735)
    parser.add_argument("--server", help="instead run a read-only NT4 client of this host")
    args = parser.parse_args(argv)
    try:
        import ntcore  # Lazy: --help and offline tools work without RobotPy.
    except ImportError as exc:
        print(f"capture failed: {exc}; install pyntcore==2026.2.2 in a venv (see README)", file=sys.stderr)
        return 1
    try:
        capture(args, ntcore)
    except Exception as exc:
        print(f"capture failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
