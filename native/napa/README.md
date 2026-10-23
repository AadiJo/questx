# NAPA native dependency

QuestX consumes the published **napa-vision==0.4.1** PyPI wheels. The detector source is not
vendored or built by QuestX. `dependency.lock.json` pins exact wheel URLs, SHA-256 hashes,
and the native files inside each wheel. The existing C# P/Invoke adapter calls that native
library directly; Python and numpy are not required on the headset or in the Unity Editor.

## Restore

From the repository root, with Python 3.11 or newer and Unity closed:

```sh
python native/napa/restore.py
python native/napa/restore.py --check
```

Restore downloads only when installed files are missing or differ from the lock. It verifies
all downloaded wheels and selected members before replacing files. `--check` is offline and
fails on missing or modified files. CI restores before Unity builds and Windows detector checks.

- Android ARM64: the Android API 24 wheel's native library becomes
  `unity/Assets/Plugins/napa/Android/arm64-v8a/libnapa.so`.
- Windows x64: the wheel's native `.pyd` (a native DLL with C exports) becomes
  `unity/Assets/Plugins/napa/Windows/x64/napa.dll`. Its bundled VC runtime DLL is restored
  alongside it and included in the standalone detector checks.

Binaries are generated dependency outputs ignored by Git; Unity importer metadata remains
tracked. No compiler, NDK, Python installation on Quest, or Python package import is involved.
The upstream license and third-party notices are preserved as `napa-*.txt` in
`unity/Assets/StreamingAssets`, so they accompany the packaged application.

To update, select a published version that provides both target wheels, update the lock's URLs,
archive/member hashes and any changed member paths, then restore and run the checks below.
If a bundled DLL filename changes, update its Unity importer metadata too. Never use a moving
`latest` URL or rebuild an upstream source snapshot as a replacement for the published package.

Release: https://pypi.org/project/napa-vision/0.4.1/

QuestX's separate managed pose covariance implementation remains based on upstream
`covariance.py` and `solvers.py` at `c5f25e2`. It applies to both detectors' solved poses and is
not part of this native detector dependency. Its checks are in `tests/measurement-checks`.

## Conventions

`napa_detect` returns corners in OpenCV order with pixel (0, 0)'s center at (0, 0). QuestX
(`NapaCore`) converts them to libapriltag's conventions, which PoseLib and
`AprilTagFieldLayout.GetTagCorners` are written against:

- libapriltag corner `i` is Napa corner `{1, 0, 3, 2}[i]`;
- add 0.5 px to x and y;
- the center is the intersection of the diagonals.

This mapping was originally measured against the bundled `apriltag.dll` on 300 randomly warped renders. Every
render produced the same permutation, the median worst-corner offset was 0.04 px, and no IDs were
missed or false.

## Checks

- `dotnet run -c Release --project tests/napa-detector` (Windows x64) runs both `ITagDetector`
  backends through the real QuestX C# on the restored Editor binaries. It covers 300 warped renders
  for IDs, corners, centers and the preview image, plus a timed 1280×1280 frame.
- `tests/native-localization/napa_android_check.c` runs the same comparison and timing on a headset
  over `adb`. See the header comment for the commands.
