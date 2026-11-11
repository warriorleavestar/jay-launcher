#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <android-ndk> <arm64-v8a|x86_64> <output-directory>" >&2
  exit 2
fi

ndk="$1"
abi="$2"
output="$3"
source_commit="97558a69157b033b890958b1e49258c5b51a80c6"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

case "$abi" in
  arm64-v8a|x86_64) ;;
  *) echo "unsupported Android ABI: $abi" >&2; exit 2 ;;
esac
case "$(uname -s)" in
  Darwin) host="darwin-x86_64" ;;
  Linux) host="linux-x86_64" ;;
  *) echo "unsupported build host" >&2; exit 2 ;;
esac

git clone --filter=blob:none --no-checkout https://github.com/MobileGL-Dev/MobileGlues.git "$work/source"
git -C "$work/source" checkout --detach "$source_commit"
git -C "$work/source" submodule update --init --depth 1 \
  MobileGlues-cpp/3rdparty/xxhash \
  MobileGlues-cpp/3rdparty/glslang \
  MobileGlues-cpp/3rdparty/SPIRV-Cross \
  MobileGlues-cpp/include/ska

patch_file="$(cd "$(dirname "$0")/../third_party/android-mobileglues" && pwd)/jay-android.patch"
git -C "$work/source" apply --check "$patch_file"
git -C "$work/source" apply "$patch_file"

cmake -S "$work/source/MobileGlues-cpp" -B "$work/build" -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI="$abi" -DANDROID_PLATFORM=android-26 \
  -DANDROID_NDK="$ndk" -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384
cmake --build "$work/build" --target mobileglues --parallel 4

mkdir -p "$output"
strip="$ndk/toolchains/llvm/prebuilt/$host/bin/llvm-strip"
readelf="$ndk/toolchains/llvm/prebuilt/$host/bin/llvm-readelf"
"$strip" "$work/build/libmobileglues.so"
cp "$work/build/libmobileglues.so" "$output/libmobileglues.so"
cp "$work/source/LICENSE" "$output/MOBILEGLUES-LICENSE.txt"

headers="$("$readelf" -lW "$output/libmobileglues.so")"
grep -q 'LOAD.*0x4000' <<<"$headers"
