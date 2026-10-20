"""Bounded localhost NT4 smoke test. All published measurements are SYNTHETIC."""

import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import unittest

from test_capture_tools import FIXTURES, HERE, fixture, synthetic_vio
import verify_physical_capture as verifier


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


@unittest.skipUnless(importlib.util.find_spec("ntcore"), "requires task-local pyntcore venv")
class SyntheticNT4LoopbackTests(unittest.TestCase):
    def test_occupied_localhost_ports_fail_clearly(self):
        with socket.socket() as first, socket.socket() as second, tempfile.TemporaryDirectory() as directory:
            first.bind(("127.0.0.1", 0))
            first.listen()
            second.bind(("127.0.0.1", 0))
            second.listen()
            output = Path(directory) / "synthetic-bind-failure.jsonl"
            result = subprocess.run([
                sys.executable, str(HERE / "quest_nt_capture.py"), "--duration", "0.5",
                "--port3", str(first.getsockname()[1]), "--port4", str(second.getsockname()[1]),
                "--output", str(output),
            ], capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertIn("socket error:", result.stderr)
            self.assertFalse(json.loads(output.read_text().splitlines()[-1])["complete"])

    def test_capture_server_and_read_only_client(self):
        import ntcore

        for mode in ("server", "client"):
            with self.subTest(capture_mode=mode), tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / "synthetic-loopback.jsonl"
                port3, port4 = free_port(), free_port()
                while port3 == port4:
                    port4 = free_port()
                inst = ntcore.NetworkTableInstance.create()
                publishers = {}
                process = None
                try:
                    # Only this explicitly synthetic test publishes; capture never publishes.
                    options = ntcore.PubSubOptions(sendAll=True, keepDuplicates=True, periodic=0.01)
                    for topic in (verifier.V4, verifier.VIO):
                        publishers[topic] = inst.getDoubleArrayTopic(topic).publish(options)
                    if mode == "client":
                        inst.startServer("", "127.0.0.1", port3, port4)
                    command = [sys.executable, str(HERE / "quest_nt_capture.py"),
                               "--duration", "4", "--output", str(output),
                               "--port3", str(port3), "--port4", str(port4)]
                    if mode == "client":
                        command.extend(["--server", "127.0.0.1"])
                    process = subprocess.Popen(command, stdout=subprocess.PIPE,
                                               stderr=subprocess.PIPE, text=True)
                    if mode == "server":
                        inst.setServer("127.0.0.1", port4)
                        inst.startClient4("synthetic-fixture-test-publisher")
                    deadline = time.monotonic() + 2.5
                    while not inst.isConnected() and time.monotonic() < deadline:
                        if process.poll() is not None:
                            break
                        time.sleep(0.01)
                    self.assertTrue(inst.isConnected(), "synthetic NT4 loopback failed to connect")
                    # Allow the remote subscription announcement to reach the publisher.
                    time.sleep(0.25)
                    frames = [fixture(path.name) for path in sorted(FIXTURES.glob("*.txt"))]
                    sentinel = fixture()
                    sentinel[47:53] = [0] * 6
                    frames.extend([sentinel, sentinel])  # Includes an exact duplicate update.
                    vio = [synthetic_vio(i, tracking, epoch) for i, (tracking, epoch) in enumerate(
                        [(1, 10), (0, 11), (0, 11), (1, 12)], 1)]
                    # Burst updates exercise queued history, not just last-value reads.
                    for topic, values in [(verifier.V4, frames), (verifier.VIO, vio)]:
                        for data in values:
                            # Explicit fixture capture words remain in publisher clock domain;
                            # this test intentionally does not equate them to this NT stamp.
                            publishers[topic].set(data, ntcore._now())
                    inst.flush()
                    interrupted = mode == "client" and os.name == "posix"
                    if interrupted:
                        time.sleep(0.2)
                        process.send_signal(signal.SIGINT)
                    stdout, stderr = process.communicate(timeout=8)
                    self.assertEqual(process.returncode, 0, stdout + stderr)
                    records = [json.loads(line) for line in output.read_text().splitlines()]
                    self.assertEqual(records[-1]["reason"], "interrupt" if interrupted else "duration")
                    for topic, expected in [(verifier.V4, frames), (verifier.VIO, vio)]:
                        actual = [r["value"] for r in records
                                  if r.get("type") == "sample" and r["path"] == topic]
                        self.assertEqual(actual, expected, f"{mode}: queued synthetic updates differ")
                    result = subprocess.run([sys.executable, str(HERE / "verify_physical_capture.py"),
                                             str(output)], capture_output=True, text=True, timeout=5)
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                    report = json.loads(result.stdout)
                    self.assertEqual(report["planar_unavailable_samples"], 2)
                    self.assertEqual(report["vio"]["epoch_changes"], 2)
                    self.assertEqual(report["vio"]["tracking_loss_events"], 1)
                    self.assertEqual(report["vio"]["tracking_resume_events"], 1)
                    self.assertEqual(report["accuracy"], "unverified")
                    destination = os.environ.get("QUESTX_NT_SMOKE_OUTPUT")
                    if destination:
                        artifacts = Path(destination)
                        artifacts.mkdir(parents=True, exist_ok=True)
                        shutil.copyfile(output, artifacts / f"synthetic-{mode}.jsonl")
                        (artifacts / f"synthetic-{mode}-report.json").write_text(result.stdout)
                    print(f"SYNTHETIC localhost NT4 {mode}: 5 v4 + 4 VIO queued samples preserved; transport checks passed")
                finally:
                    if process is not None:
                        if process.poll() is None:
                            process.kill()
                        process.communicate(timeout=5)
                    for pub in publishers.values():
                        pub.close()
                    inst.stopClient()
                    inst.stopServer()
                    ntcore.NetworkTableInstance.destroy(inst)


if __name__ == "__main__":
    unittest.main()
