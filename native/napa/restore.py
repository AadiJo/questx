#!/usr/bin/env python3
"""Restore the checksum-pinned NAPA native binaries from published PyPI wheels."""

import argparse
import hashlib
import io
import json
from pathlib import Path
import tempfile
import urllib.request
import zipfile


HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent


def digest(data):
    return hashlib.sha256(data).hexdigest()


def destination(root, name):
    path = (root / name).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError(f"Dependency destination escapes repository: {name}")
    return path


def matches(path, expected):
    return path.is_file() and digest(path.read_bytes()) == expected


def restore(lock, root, check=False):
    pending = []
    # Verify every downloaded wheel and selected member before replacing any installed file.
    for artifact in lock["artifacts"]:
        files = artifact["files"]
        missing = [f for f in files if not matches(destination(root, f["destination"]), f["sha256"])]
        if not missing:
            continue
        if check:
            raise ValueError("Missing or modified dependency: " + ", ".join(f["destination"] for f in missing))
        print(f"Downloading {artifact['filename']}")
        with urllib.request.urlopen(artifact["url"], timeout=60) as response:
            data = response.read()
        if digest(data) != artifact["sha256"]:
            raise ValueError(f"Wheel checksum mismatch: {artifact['filename']}")
        with zipfile.ZipFile(io.BytesIO(data)) as wheel:
            for entry in files:
                payload = wheel.read(entry["member"])
                if digest(payload) != entry["sha256"]:
                    raise ValueError(f"Member checksum mismatch: {entry['member']}")
                pending.append((destination(root, entry["destination"]), payload))
    for path, payload in pending:
        path.parent.mkdir(parents=True, exist_ok=True)
        # Close before replace so this also works on Windows. Keep the original on write failure.
        with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as output:
            temporary = Path(output.name)
            try:
                output.write(payload)
            except BaseException:
                output.close()
                temporary.unlink()
                raise
        try:
            # These are public package artifacts; make them readable by build/container users.
            temporary.chmod(0o644)
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)
    if not check:
        # Repair owner-only outputs from earlier restores even when their hashes already match.
        for artifact in lock["artifacts"]:
            for entry in artifact["files"]:
                destination(root, entry["destination"]).chmod(0o644)
    print(f"Verified {lock['package']}=={lock['version']} native dependencies")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Verify installed files without network access")
    args = parser.parse_args()
    lock = json.loads((HERE / "dependency.lock.json").read_text())
    try:
        restore(lock, ROOT, check=args.check)
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        parser.exit(1, f"NAPA dependency restore failed: {error}\nRun: python native/napa/restore.py\n")


if __name__ == "__main__":
    main()
