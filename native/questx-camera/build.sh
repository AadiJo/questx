#!/usr/bin/env bash
# Builds the QuestX passthrough camera plugin for the Quest.
#
#   ANDROID_NDK_HOME=/path/to/android-ndk-r27c ./native/questx-camera/build.sh
#
# Writes Assets/Plugins/questxcamera/Android/arm64-v8a/libquestxcamera.so (API 32, 16 KB pages).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="$repo/unity/Assets/Plugins/questxcamera/Android/arm64-v8a"

: "${ANDROID_NDK_HOME:?Set ANDROID_NDK_HOME to an Android NDK (r26 or newer)}"
host="linux-x86_64"
case "$(uname -s)" in
    Darwin) host="darwin-x86_64" ;;
    MINGW* | MSYS* | CYGWIN*) host="windows-x86_64" ;;
esac
clang="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/$host/bin/aarch64-linux-android32-clang"
[[ -x "$clang" ]] || clang="$clang.cmd"
mkdir -p "$out"

"$clang" -O2 -std=c11 -Wall -Wextra -fvisibility=hidden -fPIC -shared -march=armv8.2-a \
    -Wl,-soname,libquestxcamera.so -Wl,-z,max-page-size=16384 -Wl,--build-id=none -s \
    -o "$out/libquestxcamera.so" "$here/src/qx_camera.c" -lcamera2ndk -lmediandk -landroid -llog
echo "built $out/libquestxcamera.so"
