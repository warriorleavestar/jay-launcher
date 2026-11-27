#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <android-ndk> <arm64-v8a|x86_64> <output-directory>" >&2
  exit 2
fi

ndk="$1"
abi="$2"
output="$3"
lwjgl_commit="30fac9b95f99cda97312232be25ba55297bf9951" # signed LWJGL 3.4.3 tag
lwjgl_sha="db97020d78fb6f6086121fba9c99ac07a50c4188a7658b06d488a2eae021de40"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

case "$abi" in
  arm64-v8a) triple="aarch64-linux-android"; arch_define="LWJGL_arm64" ;;
  x86_64) triple="x86_64-linux-android"; arch_define="LWJGL_x64" ;;
  *) echo "unsupported Android ABI: $abi" >&2; exit 2 ;;
esac
case "$(uname -s)" in
  Darwin) host="darwin-x86_64" ;;
  Linux) host="linux-x86_64" ;;
  *) echo "unsupported build host" >&2; exit 2 ;;
esac

toolchain="$ndk/toolchains/llvm/prebuilt/$host"
cxx="$toolchain/bin/${triple}26-clang++"
strip="$toolchain/bin/llvm-strip"
readelf="$toolchain/bin/llvm-readelf"

verify_sha() {
  local expected="$1" file="$2" actual
  if command -v sha256sum >/dev/null 2>&1; then actual="$(sha256sum "$file" | cut -d' ' -f1)"
  else actual="$(shasum -a 256 "$file" | cut -d' ' -f1)"
  fi
  [[ "$actual" == "$expected" ]] || { echo "SHA-256 mismatch for $file" >&2; exit 1; }
}

curl -fL --retry 3 "https://codeload.github.com/LWJGL/lwjgl3/tar.gz/$lwjgl_commit" -o "$work/lwjgl.tar.gz"
verify_sha "$lwjgl_sha" "$work/lwjgl.tar.gz"
tar -xzf "$work/lwjgl.tar.gz" -C "$work"
lwjgl="$work/lwjgl3-$lwjgl_commit"
mkdir -p "$output" "$work/objects"

common=(
  -std=c++17 -O3 -fPIC -ffunction-sections -fdata-sections
  -fvisibility=hidden -pthread -DNDEBUG -DLWJGL_LINUX "-D$arch_define"
  -U_FORTIFY_SOURCE -D_FORTIFY_SOURCE=0 -D_GNU_SOURCE -D_FILE_OFFSET_BITS=64
  -I"$lwjgl/modules/lwjgl/core/src/main/c"
  -I"$lwjgl/modules/lwjgl/core/src/main/c/linux"
  -I"$lwjgl/modules/lwjgl/vma/src/main/c"
  -I"$lwjgl/modules/lwjgl/vulkan/src/main/c"
)
sources=(
  "$lwjgl/modules/lwjgl/vma/src/generated/c/org_lwjgl_util_vma_LibVma.cpp"
  "$lwjgl/modules/lwjgl/vma/src/generated/c/org_lwjgl_util_vma_Vma.cpp"
)
objects=()
for index in "${!sources[@]}"; do
  object="$work/objects/$index.o"
  "$cxx" "${common[@]}" -c "${sources[$index]}" -o "$object"
  objects+=("$object")
done

"$cxx" -shared -Wl,--no-undefined -Wl,--gc-sections -Wl,-z,noexecstack \
  -Wl,-z,max-page-size=16384 -Wl,--version-script,"$lwjgl/config/linux/version.script" \
  -pthread "${objects[@]}" -ldl -llog -lm -o "$output/liblwjgl_vma.so"
"$strip" --strip-unneeded "$output/liblwjgl_vma.so"
cp "$lwjgl/LICENSE.md" "$output/LWJGL-LICENSE.md"
cp "$lwjgl/modules/lwjgl/vma/vma_license.txt" "$output/VMA-LICENSE.txt"

headers="$("$readelf" -lW "$output/liblwjgl_vma.so")"
symbols="$("$readelf" -Ws "$output/liblwjgl_vma.so")"
grep -q 'LOAD.*0x4000' <<<"$headers"
grep -q 'Java_org_lwjgl_util_vma_Vma_nvmaCreateAllocator' <<<"$symbols"
