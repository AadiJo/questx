"""Offline wire/CLI tests using generated C# fixtures and explicitly synthetic captures."""

from collections import Counter
import contextlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

import quest_nt_capture as capture
import verify_physical_capture as verifier


HERE = Path(__file__).resolve().parent
FIXTURES = HERE.parents[1] / "questx-lib/src/test/resources/measurement-v4"


def fixture(name="apriltag.txt"):
    return [float(v) for v in (FIXTURES / name).read_text().strip().split(",")]


def synthetic_vio(sequence=1, tracking=1, epoch=0):
    values = fixture()[:12] + [tracking, epoch]
    values[0:4] = [sequence, 0, 1_000_000 + sequence * 20_000, 0]
    return values


def synthetic_records(samples):
    args = SimpleNamespace(server=None, listen="127.0.0.1", port4=5810,
                           port3=1735, duration=1)
    records = [capture.metadata(args)]
    counts = Counter()
    for index, (topic, values) in enumerate(samples, 1):
        counts[topic] += 1
        records.append({
            "type": "sample", "path": topic, "value": values,
            "receive_monotonic_ns": 10**9 + index * 1000,
            "receive_unix_ns": 10**18 + index * 1000,
            "nt_local_now_us": 7_000_000 + index,
            # Deliberately unrelated to embedded publisher timestamps.
            "nt_local_us": 6_000_000 + index, "nt_server_us": 9_000_000 + index,
            "nt_server_offset_us": 3_000_000,
        })
    records.append({"type": "end", "complete": True, "reason": "duration",
                    "counts": dict(counts), "errors": [], "elapsed_seconds": 1})
    return records


class OfflineValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "synthetic.jsonl"

    def write(self, records):
        self.path.write_text("".join(json.dumps(r) + "\n" for r in records))
        return verifier.verify_capture(self.path)

    def good_records(self):
        return synthetic_records([(verifier.V4, fixture()), (verifier.VIO, synthetic_vio())])

    def test_generated_csharp_fixtures_accepted(self):
        paths = sorted(FIXTURES.glob("*.txt"))
        self.assertEqual(len(paths), 3)
        for path in paths:
            with self.subTest(fixture=path.name):
                self.assertFalse(verifier.validate_apriltag(fixture(path.name))["planar_unavailable"])

    def test_old_47_header_rejected(self):
        data = fixture()
        del data[47:53]
        with self.assertRaisesRegex(ValueError, "header|length"):
            verifier.validate_apriltag(data)

    def test_bad_covariance_and_nonfinite_rejected(self):
        for name, offset, value in [
            ("negative variance", 26, -1), ("singular 6D", 26, 0),
            ("indefinite 6D", 27, 100), ("nonfinite", 5, float("nan")),
            ("encoded nonfinite", 5, "+Infinity"), ("infinite", 26, float("inf")),
            ("partial planar zero", 47, 0), ("indefinite planar", 48, 100),
        ]:
            with self.subTest(case=name):
                data = fixture()
                data[offset] = value
                with self.assertRaises(ValueError):
                    verifier.validate_apriltag(data)
        data = fixture()
        data[47:53] = [0, 0, 0, 0, 0, 1]
        with self.assertRaisesRegex(ValueError, "planar covariance"):
            verifier.validate_apriltag(data)

    def test_quality_and_common_ranges_rejected(self):
        for offset, value in [(0, -.1), (2, 2**32), (3, 2**31), (4, -1),
                              (12, 2.5), (13, 13), (14, -1), (15, 1.1),
                              (16, -1), (17, -1), (23, 1.1), (24, -1),
                              (25, -1), (53, -1), (56, 1.1)]:
            with self.subTest(offset=offset):
                data = fixture()
                data[offset] = value
                with self.assertRaises(ValueError):
                    verifier.validate_apriltag(data)
        data = fixture()
        data[8:12] = [0] * 4
        with self.assertRaisesRegex(ValueError, "quaternion"):
            verifier.validate_apriltag(data)

    def test_sentinel_accepted_and_counted_without_accuracy_claim(self):
        records = self.good_records()
        records[1]["value"][47:53] = [0] * 6
        report = self.write(records)
        self.assertTrue(report["passed"], report)
        self.assertEqual(report["planar_unavailable_samples"], 1)
        self.assertEqual(report["check"], "capture transport checks")
        self.assertEqual(report["accuracy"], "unverified")
        self.assertEqual(report["hardware_provenance"], "unverified")
        # Intentionally mismatched clocks must never turn into a latency assertion.
        self.assertNotIn("latency", report["timing"][verifier.V4])

    def test_vio_epochs_tracking_and_frozen_pose_progression(self):
        # Identical pose, advancing timestamps; flags/epochs carry the only tracking evidence.
        samples = [(verifier.V4, fixture())]
        for i, (tracking, epoch) in enumerate([(1, 5), (0, 6), (0, 6), (1, 7)], 1):
            samples.append((verifier.VIO, synthetic_vio(i, tracking, epoch)))
        report = self.write(synthetic_records(samples))
        self.assertTrue(report["passed"], report)
        self.assertEqual(report["vio"], {
            "epochs_observed": [5, 6, 7], "epoch_changes": 2, "not_tracking_samples": 2,
            "tracking_loss_events": 1, "tracking_resume_events": 1,
        })
        self.assertEqual(report["timing"][verifier.VIO]["capture_advancing_pairs"], 3)
        self.assertNotIn("live", report["vio"])

    def test_repeated_regressing_capture_and_sequence_are_reported(self):
        data = synthetic_vio(10)
        report = self.write(synthetic_records([
            (verifier.V4, fixture()), (verifier.VIO, data),
            (verifier.VIO, data), (verifier.VIO, synthetic_vio(1)),
        ]))
        self.assertTrue(report["passed"], report)
        self.assertEqual(report["timing"][verifier.VIO]["capture_repeated_pairs"], 1)
        self.assertEqual(report["timing"][verifier.VIO]["capture_regressions"], 1)
        self.assertEqual(report["timing"][verifier.VIO]["sequence_regressions"], 1)

    def test_invalid_vio_length_flags_epoch(self):
        for values in [synthetic_vio()[:-1], synthetic_vio(tracking=2),
                       synthetic_vio(epoch=-1), synthetic_vio(epoch=2**32)]:
            with self.subTest(values=values[-2:]):
                with self.assertRaises(ValueError):
                    verifier.validate_vio(values)

    def test_no_v4_no_vio_or_incomplete_capture_fails(self):
        for records in [synthetic_records([]),
                        synthetic_records([(verifier.VIO, synthetic_vio())]),
                        synthetic_records([(verifier.V4, fixture())]), self.good_records()[:-1]]:
            with self.subTest(records=len(records)):
                self.assertFalse(self.write(records)["passed"])

    def test_bad_schema_clock_counts_runtime_and_json_fail(self):
        for change in [lambda r: r[0].update(schema="old"),
                       lambda r: r[0].update(clocks={}),
                       lambda r: r[-1].update(counts={}),
                       lambda r: r[-1].update(complete=False, errors=["stop failed"]),
                       lambda r: r[1].update(nt_local_us=0)]:
            records = self.good_records()
            change(records)
            self.assertFalse(self.write(records)["passed"])
        self.path.write_text('{"type":')
        self.assertFalse(verifier.verify_capture(self.path)["passed"])

    def test_cli_exit_status_and_help_without_site_packages(self):
        self.write(self.good_records())
        result = subprocess.run([sys.executable, "-S", str(HERE / "verify_physical_capture.py"),
                                 str(self.path)], capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(json.loads(result.stdout)["passed"])
        self.write(self.good_records()[:-1])
        result = subprocess.run([sys.executable, "-S", str(HERE / "verify_physical_capture.py"),
                                 str(self.path)], capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 1, result.stderr)
        result = subprocess.run([sys.executable, "-S", str(HERE / "quest_nt_capture.py"), "--help"],
                                capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 0, result.stderr)


class CaptureFailureTests(unittest.TestCase):
    def test_ctrl_c_drains_and_closes_cleanly(self):
        with tempfile.TemporaryDirectory() as directory:
            ntcore = MagicMock()
            inst = ntcore.NetworkTableInstance.create.return_value
            sub = inst.getDoubleArrayTopic.return_value.subscribe.return_value
            sub.readQueue.side_effect = [KeyboardInterrupt()] + [[]] * len(capture.TOPICS)
            output = Path(directory) / "interrupted.jsonl"
            with patch.dict(sys.modules, ntcore=ntcore), patch.object(capture, "version", return_value="synthetic-mock"), contextlib.redirect_stdout(io.StringIO()):
                result = capture.main(["--duration", "1", "--output", str(output)])
            self.assertEqual(result, 0)
            end = json.loads(output.read_text().splitlines()[-1])
            self.assertTrue(end["complete"])
            self.assertEqual(end["reason"], "interrupt")
            inst.stopServer.assert_called_once()
            self.assertEqual(sub.close.call_count, len(capture.TOPICS))

    def test_capture_and_each_shutdown_failure_return_nonzero(self):
        for failing in ("readQueue", "stopServer", "close", "destroy", "waitForListenerQueue"):
            with self.subTest(failing=failing), tempfile.TemporaryDirectory() as directory:
                ntcore = MagicMock()
                inst = ntcore.NetworkTableInstance.create.return_value
                sub = inst.getDoubleArrayTopic.return_value.subscribe.return_value
                sub.readQueue.return_value = []
                if failing in ("readQueue", "close"):
                    getattr(sub, failing).side_effect = RuntimeError("injected " + failing)
                elif failing == "destroy":
                    ntcore.NetworkTableInstance.destroy.side_effect = RuntimeError("injected destroy")
                elif failing == "waitForListenerQueue":
                    inst.waitForListenerQueue.return_value = False
                else:
                    inst.stopServer.side_effect = RuntimeError("injected stopServer")
                output = Path(directory) / "capture.jsonl"
                stderr = io.StringIO()
                with patch.dict(sys.modules, ntcore=ntcore), patch.object(capture, "version", return_value="synthetic-mock"), contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(stderr):
                    result = capture.main(["--duration", "0.001", "--output", str(output)])
                self.assertEqual(result, 1)
                self.assertIn("capture failed:", stderr.getvalue())
                end = json.loads(output.read_text().splitlines()[-1])
                self.assertFalse(end["complete"])
                self.assertTrue(end["errors"])
                ntcore.NetworkTableInstance.destroy.assert_called_once_with(inst)

    def test_duration_is_bounded_and_existing_output_preserved(self):
        for seconds in ("0", "-1", "3601", "nan", "inf"):
            with self.subTest(seconds=seconds), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as error:
                    capture.main(["--duration", seconds])
                self.assertEqual(error.exception.code, 2)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.jsonl"
            path.write_text("existing evidence")
            with self.assertRaises(FileExistsError):
                capture.capture(SimpleNamespace(output=path), MagicMock())
            self.assertEqual(path.read_text(), "existing evidence")


if __name__ == "__main__":
    unittest.main()
