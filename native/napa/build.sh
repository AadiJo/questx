#!/usr/bin/env bash
# Builds the Napa tag36h11 detector for the Unity plugin folders.
#
#   ANDROID_NDK_HOME=/path/to/android-ndk-r27c ./native/napa/build.sh
#
# Quest:  Assets/Plugins/napa/Android/arm64-v8a/libnapa.so (NDK clang, API 32, 16 KB pages)
# Editor: Assets/Plugins/napa/Windows/x64/napa.dll         (zig cc, skipped when zig is absent)
# Linux:  native/napa/build/libnapa.so                     (host cc, for the source checks)
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
src="$here/src/napa_core.c"
plugins="$repo/unity/Assets/Plugins/napa"
flags=(-O3 -std=c99 -fvisibility=hidden -fPIC -shared -I"$here/src")

: "${ANDROID_NDK_HOME:?Set ANDROID_NDK_HOME to an Android NDK (r26 or newer)}"
ndk_bin="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin"
mkdir -p "$plugins/Android/arm64-v8a" "$plugins/Windows/x64" "$here/build"

# armv8.2-a covers the Quest 2 / 3 / 3S XR2 cores and enables the NEON popcount path.
"$ndk_bin/aarch64-linux-android32-clang" "${flags[@]}" -march=armv8.2-a \
    -Wl,-soname,libnapa.so -Wl,-z,max-page-size=16384 -Wl,--build-id=none -s \
    -o "$plugins/Android/arm64-v8a/libnapa.so" "$src" -lm
echo "built $plugins/Android/arm64-v8a/libnapa.so"

"${CC:-cc}" "${flags[@]}" -o "$here/build/libnapa.so" "$src" -lm
echo "built $here/build/libnapa.so"

zig_cmd="${ZIG:-zig}"
if command -v "${zig_cmd%% *}" >/dev/null 2>&1; then
    $zig_cmd cc -target x86_64-windows-gnu "${flags[@]}" -s \
        -o "$plugins/Windows/x64/napa.dll" "$src"
    rm -f "$plugins/Windows/x64/"*.lib "$plugins/Windows/x64/"*.pdb
    echo "built $plugins/Windows/x64/napa.dll"
else
    echo "zig not found; skipped the Windows Editor DLL" >&2
fi
