#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <android-sdk> <output-directory>" >&2
  exit 2
fi

sdk="$1"
output="$2"
ndk_version="28.2.13676358"
ndk="$sdk/ndk/$ndk_version"
source_dir="$(cd "$(dirname "$0")/../src/Amber.Android.Native" && pwd)"

case "$(uname -s)" in
  Darwin) host="darwin-x86_64" ;;
  Linux) host="linux-x86_64" ;;
  *) echo "unsupported build host" >&2; exit 2 ;;
esac

toolchain="$ndk/toolchains/llvm/prebuilt/$host"
test -x "$toolchain/bin/aarch64-linux-android26-clang"
test -x "$toolchain/bin/x86_64-linux-android26-clang"

build_one() {
  local abi="$1"
  local compiler="$2"
  mkdir -p "$output/$abi"
  "$toolchain/bin/$compiler" \
    -std=c17 -O2 -fPIC -fvisibility=hidden -Wall -Wextra -Werror \
    -shared "$source_dir/jay_jvm_host.c" \
    -Wl,-z,max-page-size=16384 \
    -landroid -ldl \
    -o "$output/$abi/libjayjvm.so"
}

build_one arm64-v8a aarch64-linux-android26-clang
build_one x86_64 x86_64-linux-android26-clang

for library in "$output"/*/libjayjvm.so; do
  program_headers="$("$toolchain/bin/llvm-readelf" -lW "$library")"
  if ! grep -q 'LOAD.*0x4000' <<<"$program_headers"; then
    echo "$library does not use 16 KiB LOAD segment alignment" >&2
    exit 1
  fi
done
