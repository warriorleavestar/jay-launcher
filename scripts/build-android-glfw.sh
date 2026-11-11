#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <android-sdk> <output-directory>" >&2
  exit 2
fi

sdk="$1"
output="$2"
ndk="$sdk/ndk/28.2.13676358"
cmake="$sdk/cmake/3.22.1/bin/cmake"
ninja="$sdk/cmake/3.22.1/bin/ninja"
source_commit="7b6aead9fb88b3623e3b3725ebb42670cbe4c579"
patch_file="$(cd "$(dirname "$0")/../third_party/android-glfw" && pwd)/jay-android.patch"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

test -x "$cmake"
test -x "$ninja"
git clone --filter=blob:none --no-checkout https://github.com/glfw/glfw.git "$work/glfw"
git -C "$work/glfw" checkout --detach "$source_commit"
git -C "$work/glfw" apply --recount --check "$patch_file"
git -C "$work/glfw" apply --recount "$patch_file"

for abi in arm64-v8a x86_64; do
  build="$work/build-$abi"
  "$cmake" -S "$work/glfw" -B "$build" -G Ninja \
    -DCMAKE_MAKE_PROGRAM="$ninja" \
    -DCMAKE_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI="$abi" -DANDROID_PLATFORM=26 \
    -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON \
    -DGLFW_BUILD_X11=OFF -DGLFW_BUILD_WAYLAND=OFF \
    -DGLFW_BUILD_EXAMPLES=OFF -DGLFW_BUILD_TESTS=OFF \
    -DGLFW_BUILD_DOCS=OFF -DGLFW_INSTALL=OFF
  "$cmake" --build "$build" --parallel 4
  mkdir -p "$output/$abi"
  cp "$build/src/libglfw.so" "$output/$abi/libglfw.so"
done

case "$(uname -s)" in
  Darwin) host="darwin-x86_64" ;;
  Linux) host="linux-x86_64" ;;
  *) echo "unsupported build host" >&2; exit 2 ;;
esac
readelf="$ndk/toolchains/llvm/prebuilt/$host/bin/llvm-readelf"
for library in "$output"/*/libglfw.so; do
  symbols="$("$readelf" -Ws "$library")"
  headers="$("$readelf" -lW "$library")"
  grep -q 'glfwCreateWindow$' <<<"$symbols"
  grep -q 'glfwPollEvents$' <<<"$symbols"
  grep -q 'LOAD.*0x4000' <<<"$headers"
done
