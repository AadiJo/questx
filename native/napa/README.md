# Napa tag36h11 detector (vendored)

`src/` is the dependency-free C core of Napa, from
[Zw96042/apriltag-detector](https://github.com/Zw96042/apriltag-detector), branch
`napa/rename-and-benchmarks` at `17be29b` (`src/napa_vision/_native/`). QuestX calls it through
`unity/Assets/QuestX/Native/Napa` and uses it as the default backend behind `ITagDetector`.

## Local change

`napa_core.c` raises the per-level blob size band from `hi = 24` to `hi = 48` samples. The patch is
`napa-size-band.patch`, which applies to the upstream repo with `git apply`.

With `hi = 24`, rotated or perspective tags 40–52 px and 70–100 px across overflowed one pyramid
level. At the next, twice-coarser level the one-cell white quiet zone is only about one sample wide.
The 8-connected fill then merged the tag's border with any darker background around it, and the tag
was lost. On mid-grey and dark backgrounds, 25–40% of those tags were missed.

FRC tags have exactly a one-cell margin, and the Quest typically sees them at those sizes. On 2,000
warped renders (white, grey 120, dark 40 and textured backgrounds; blur 0 and 0.8), detection went
from 92.2% to 100%. There were still no false IDs, and a 1280×1280 frame cost about 0.3–0.5 ms
more. Napa's own test suite still passes (178 passed, 1 skipped).

## Conventions

`napa_detect` returns corners in OpenCV order with pixel (0, 0)'s center at (0, 0). QuestX
(`NapaCore`) converts them to libapriltag's conventions, which PoseLib and
`AprilTagFieldLayout.GetTagCorners` are written against:

- libapriltag corner `i` is Napa corner `{1, 0, 3, 2}[i]`;
- add 0.5 px to x and y;
- the center is the intersection of the diagonals.

This mapping was measured against the bundled `apriltag.dll` on 300 randomly warped renders. Every
render produced the same permutation, the median worst-corner offset was 0.06 px, and no IDs were
missed or false.

## Building

```bash
ANDROID_NDK_HOME=/path/to/android-ndk-r27c ./native/napa/build.sh   # needs zig for the Editor DLL
```

This writes `Assets/Plugins/napa/Android/arm64-v8a/libnapa.so` (API 32, 16 KB pages,
`-march=armv8.2-a`), `Assets/Plugins/napa/Windows/x64/napa.dll`, and a host `build/libnapa.so`.

## Checks

- `dotnet run -c Release --project tests/napa-detector` (Windows x64) runs both `ITagDetector`
  backends through the real QuestX C# on the bundled Editor binaries. It covers 300 warped renders
  for IDs, corners and centers, plus a timed 1280×1280 frame.
- `tests/native-localization/napa_android_check.c` runs the same comparison and timing on a headset
  over `adb`. See the header comment for the commands.
