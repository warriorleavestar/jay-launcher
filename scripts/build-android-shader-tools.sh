#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <android-ndk> <arm64-v8a|x86_64> <output-directory>" >&2
  exit 2
fi

ndk="$1"
abi="$2"
output="$3"
spirv_cross_commit="6c09849fe88c48eaed08413aa022aaa136a3a057"
spirv_cross_sha="b81b9956289950570953738e666a031ca32ff64e4fc925eba89f227c42109518"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

case "$abi" in
  arm64-v8a) triple="aarch64-linux-android" ;;
  x86_64) triple="x86_64-linux-android" ;;
  *) echo "unsupported Android ABI: $abi" >&2; exit 2 ;;
esac
case "$(uname -s)" in
  Darwin) host="darwin-x86_64" ;;
  Linux) host="linux-x86_64" ;;
  *) echo "unsupported build host" >&2; exit 2 ;;
esac

toolchain="$ndk/toolchains/llvm/prebuilt/$host"
shaderc_source="$ndk/sources/third_party/shaderc"
ndk_build="$ndk/ndk-build"
cxx="$toolchain/bin/${triple}26-clang++"
cc="$toolchain/bin/${triple}26-clang"
strip="$toolchain/bin/llvm-strip"
readelf="$toolchain/bin/llvm-readelf"
sdk="$(cd "$ndk/../.." && pwd)"
cmake="$sdk/cmake/3.22.1/bin/cmake"
ninja="$sdk/cmake/3.22.1/bin/ninja"
test -x "$ndk_build"
test -f "$shaderc_source/Android.mk"
test -x "$cmake"
test -x "$ninja"

verify_sha() {
  local expected="$1" file="$2" actual
  if command -v sha256sum >/dev/null 2>&1; then actual="$(sha256sum "$file" | cut -d' ' -f1)"
  else actual="$(shasum -a 256 "$file" | cut -d' ' -f1)"
  fi
  [[ "$actual" == "$expected" ]] || { echo "SHA-256 mismatch for $file" >&2; exit 1; }
}

# NDK r28 ships the Android-maintained shaderc source and its complete pinned
# glslang/SPIR-V Tools dependency tree.  Build its combined static archive, then
# wrap it as the shared object expected by LWJGL.  The three no-op setters were
# added after this shaderc revision and only tune optional post-processing; the
# compatibility exports preserve the old defaults while satisfying LWJGL 3.4.3.
cat > "$work/Application.mk" <<EOF
APP_ABI := $abi
APP_PLATFORM := android-26
APP_STL := c++_shared
APP_OPTIM := release
APP_CPPFLAGS := -O2 -DNDEBUG
EOF
"$ndk_build" \
  NDK_PROJECT_PATH="$work/project" \
  APP_BUILD_SCRIPT="$shaderc_source/Android.mk" \
  NDK_APPLICATION_MK="$work/Application.mk" \
  NDK_OUT="$work/shaderc-obj" \
  NDK_LIBS_OUT="$work/shaderc-libs" \
  -j"${JAY_BUILD_JOBS:-4}" libshaderc_combined
shaderc_archive="$work/shaderc-libs/c++_shared/$abi/libshaderc.a"
test -f "$shaderc_archive"

cat > "$work/shaderc-compat.c" <<'EOF'
#include <stdbool.h>
#include <stdint.h>

void shaderc_compile_options_set_preserve_bindings(void *options, bool enabled) {
    (void)options; (void)enabled;
}
void shaderc_compile_options_set_max_id_bound(void *options, uint32_t bound) {
    (void)options; (void)bound;
}
void shaderc_compile_options_set_vulkan_rules_relaxed(void *options, bool enabled) {
    (void)options; (void)enabled;
}
EOF
cat > "$work/shaderc.map" <<'EOF'
{
  global:
    shaderc_*;
  local:
    *;
};
EOF
"$cc" -O2 -fPIC -c "$work/shaderc-compat.c" -o "$work/shaderc-compat.o"
mkdir -p "$output"
"$cxx" -shared -static-libstdc++ -Wl,--no-undefined -Wl,-z,noexecstack \
  -Wl,-z,max-page-size=16384 -Wl,--version-script,"$work/shaderc.map" \
  -Wl,--whole-archive "$shaderc_archive" -Wl,--no-whole-archive \
  "$work/shaderc-compat.o" -llog -landroid -ldl -lm \
  -Wl,-soname,libshaderc.so -o "$output/libshaderc.so"
"$strip" --strip-unneeded "$output/libshaderc.so"

# SPIRV-Cross' C shared API is what LWJGL's spvc binding loads.  Keep its
# upstream commit and archive digest immutable, and give the staged file the
# exact name selected by AndroidEmbeddedGame.
curl -fL --retry 3 \
  "https://codeload.github.com/KhronosGroup/SPIRV-Cross/tar.gz/$spirv_cross_commit" \
  -o "$work/spirv-cross.tar.gz"
verify_sha "$spirv_cross_sha" "$work/spirv-cross.tar.gz"
mkdir "$work/spirv-cross"
tar -xzf "$work/spirv-cross.tar.gz" -C "$work/spirv-cross" --strip-components=1
"$cmake" -S "$work/spirv-cross" -B "$work/spirv-cross-build" -G Ninja \
  -DCMAKE_MAKE_PROGRAM="$ninja" \
  -DCMAKE_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI="$abi" -DANDROID_PLATFORM=26 -DCMAKE_BUILD_TYPE=Release \
  -DSPIRV_CROSS_SHARED=ON -DSPIRV_CROSS_STATIC=OFF \
  -DSPIRV_CROSS_CLI=OFF -DSPIRV_CROSS_ENABLE_TESTS=OFF \
  -DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384
"$cmake" --build "$work/spirv-cross-build" \
  --target spirv-cross-c-shared --parallel "${JAY_BUILD_JOBS:-4}"
cp "$work/spirv-cross-build/libspirv-cross-c-shared.so" "$output/libspirv-cross.so"
"$strip" --strip-unneeded "$output/libspirv-cross.so"

cp "$ndk/NOTICE" "$output/SHADERC-NDK-NOTICE.txt"
cp "$work/spirv-cross/LICENSE" "$output/SPIRV-CROSS-LICENSE.txt"

for library in "$output/libshaderc.so" "$output/libspirv-cross.so"; do
  headers="$("$readelf" -h -lW "$library")"
  grep -q 'LOAD.*0x4000' <<<"$headers"
  case "$abi" in
    arm64-v8a) grep -q 'Machine:.*AArch64' <<<"$headers" ;;
    x86_64) grep -q 'Machine:.*Advanced Micro Devices X86-64' <<<"$headers" ;;
  esac
done
shaderc_symbols="$("$readelf" -Ws "$output/libshaderc.so")"
spvc_symbols="$("$readelf" -Ws "$output/libspirv-cross.so")"
grep -q 'shaderc_compile_into_spv' <<<"$shaderc_symbols"
grep -q 'shaderc_compile_options_set_preserve_bindings' <<<"$shaderc_symbols"
grep -q 'spvc_context_parse_spirv' <<<"$spvc_symbols"
