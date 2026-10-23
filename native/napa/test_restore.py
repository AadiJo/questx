"""Exercise dependency restoration without network access or production output changes."""

import copy
import io
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import restore


class RestoreChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.payload = b"published native library"
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, "w") as wheel:
            wheel.writestr("native/core.so", self.payload)
        self.archive = archive.getvalue()
        self.file = {"member": "native/core.so", "destination": "plugins/napa.so", "sha256": restore.digest(self.payload)}
        self.artifact = {"filename": "test.whl", "url": "https://example.invalid/test.whl", "sha256": restore.digest(self.archive), "files": [self.file]}
        self.lock = {"package": "test", "version": "1", "artifacts": [self.artifact]}
        self.path = self.root / self.file["destination"]

    def download(self):
        return patch.object(restore.urllib.request, "urlopen", return_value=io.BytesIO(self.archive))

    def test_restore_then_offline_check_and_idempotent_restore(self):
        with self.download():
            restore.restore(self.lock, self.root)
        self.assertEqual(self.path.read_bytes(), self.payload)
        with patch.object(restore.urllib.request, "urlopen", side_effect=AssertionError("unexpected network")):
            restore.restore(self.lock, self.root, check=True)
            restore.restore(self.lock, self.root)

    def test_missing_and_modified_are_rejected_offline(self):
        with self.assertRaisesRegex(ValueError, "Missing or modified"):
            restore.restore(self.lock, self.root, check=True)
        self.path.parent.mkdir()
        self.path.write_bytes(b"stale")
        with self.assertRaisesRegex(ValueError, "Missing or modified"):
            restore.restore(self.lock, self.root, check=True)
        with self.download():
            restore.restore(self.lock, self.root)
        self.assertEqual(self.path.read_bytes(), self.payload)

    def test_bad_wheel_hash_preserves_installed_file(self):
        self.path.parent.mkdir()
        self.path.write_bytes(b"old")
        self.artifact["sha256"] = "0" * 64
        with self.download(), self.assertRaisesRegex(ValueError, "Wheel checksum"):
            restore.restore(self.lock, self.root)
        self.assertEqual(self.path.read_bytes(), b"old")

    def test_bad_member_hash_writes_nothing(self):
        self.file["sha256"] = "0" * 64
        with self.download(), self.assertRaisesRegex(ValueError, "Member checksum"):
            restore.restore(self.lock, self.root)
        self.assertFalse(self.path.exists())

    def test_second_artifact_failure_does_not_install_first(self):
        second = copy.deepcopy(self.artifact)
        second["sha256"] = "0" * 64
        self.lock["artifacts"].append(second)
        with patch.object(restore.urllib.request, "urlopen", side_effect=lambda *a, **kw: io.BytesIO(self.archive)):
            with self.assertRaisesRegex(ValueError, "Wheel checksum"):
                restore.restore(self.lock, self.root)
        self.assertFalse(self.path.exists())

    def test_missing_member_writes_nothing(self):
        self.file["member"] = "absent"
        with self.download(), self.assertRaises(KeyError):
            restore.restore(self.lock, self.root)
        self.assertFalse(self.path.exists())

    def test_destination_cannot_escape_output_root(self):
        self.file["destination"] = "../outside.so"
        with self.assertRaisesRegex(ValueError, "escapes repository"):
            restore.restore(self.lock, self.root)

    @unittest.skipIf(os.name == "nt", "Windows uses ACLs rather than POSIX mode bits")
    def test_restored_and_existing_outputs_are_readable_by_build_users(self):
        with self.download():
            restore.restore(self.lock, self.root)
        self.assertEqual(self.path.stat().st_mode & 0o777, 0o644)
        self.path.chmod(0o600)
        with patch.object(restore.urllib.request, "urlopen", side_effect=AssertionError("unexpected network")):
            restore.restore(self.lock, self.root, check=True)
            self.assertEqual(self.path.stat().st_mode & 0o777, 0o600, "check must not mutate files")
            restore.restore(self.lock, self.root)
        self.assertEqual(self.path.stat().st_mode & 0o777, 0o644)


if __name__ == "__main__":
    unittest.main()
