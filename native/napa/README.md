# Napa tag36h11 detector (vendored)

`src/` is the dependency-free C core of Napa, unmodified from
[zwatx/NAPA](https://github.com/zwatx/NAPA) `master` at `c5f25e2`
(`src/napa_vision/_native/`). QuestX calls it through `unity/Assets/QuestX/Native/Napa` and uses it
as the default backend behind `ITagDetector`. To update, copy the three files from upstream, rebuild,
and rerun the checks below.

The core picks SIMD at compile time: NEON on the Quest, and SSE2 on x86-64 with SSSE3 chosen at
runtime. Upstream tests its scalar and SIMD paths for identical output.

## Conventions

`napa_detect` returns corners in OpenCV order with pixel (0, 0)'s center at (0, 0). QuestX
(`NapaCore`) converts them to libapriltag's conventions, which PoseLib and
`AprilTagFieldLayout.GetTagCorners` are written against:

- libapriltag corner `i` is Napa corner `{1, 0, 3, 2}[i]`;
- add 0.5 px to x and y;
- the center is the intersection of the diagonals.

This mapping was measured against the bundled `apriltag.dll` on 300 randomly warped renders. Every
render produced the same permutation, the median worst-corner offset was 0.04 px, and no IDs were
missed or false.

## Building

```bash
export ANDROID_NDK_HOME=/path/to/android-ndk-r27c
PATH=/path/to/llvm-mingw/bin:$PATH ./native/napa/build.sh
```

This writes `Assets/Plugins/napa/Android/arm64-v8a/libnapa.so` (API 32, 16 KB pages,
`-march=armv8.2-a`), `Assets/Plugins/napa/Windows/x64/napa.dll`, and a host `build/libnapa.so`.

The Editor DLL needs a MinGW-w64 compiler (`WIN_CC`, default `x86_64-w64-mingw32-clang`). Use
[llvm-mingw](https://github.com/mstorsjo/llvm-mingw) or mingw-w64 gcc. zig cc can't link the DLL,
because its runtime lacks the CPU-feature check behind the SSSE3 dispatch.

## Checks

- `dotnet run -c Release --project tests/napa-detector` (Windows x64) runs both `ITagDetector`
  backends through the real QuestX C# on the bundled Editor binaries. It covers 300 warped renders
  for IDs, corners, centers and the preview image, plus a timed 1280×1280 frame.
- `tests/native-localization/napa_android_check.c` runs the same comparison and timing on a headset
  over `adb`. See the header comment for the commands.
