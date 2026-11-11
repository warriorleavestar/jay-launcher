#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <android-ndk> <arm64-v8a|x86_64> <output-directory>" >&2
  exit 2
fi

ndk="$1"
abi="$2"
output="$3"
lwjgl_commit="f0ec1437a6149dae00eaa27c47268e5e4efb129f" # LWJGL 3.3.3
lwjgl_sha="388f5e64c708486cd6693adf40bb0bb01a51b83026298ad19eb764fa6f460784"
libffi_version="3.4.4"
libffi_sha="d66c56ad259a82cf2a9dfc408b32bf5da52371500b84745f7fb8b645712df676"
freetype_commit="45903920b984540bb629bc89f4c010159c23a89a"
freetype_sha="ce4fe730699b48d643df448655a37ac13b361e7225fec843637a30f1b4ae49e3"
openal_commit="d9fed51aa6391debc31dbbca550f055c980afe70"
openal_sha="e6212437f103d15543cf4a478ef6c9a49b0dd5cff19c9f1a71183ab16a6da3fb"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

case "$abi" in
  arm64-v8a) triple="aarch64-linux-android"; host_triple="aarch64-linux-android"; arch_define="LWJGL_arm64" ;;
  x86_64) triple="x86_64-linux-android"; host_triple="x86_64-linux-android"; arch_define="LWJGL_x64" ;;
  *) echo "unsupported Android ABI: $abi" >&2; exit 2 ;;
esac
case "$(uname -s)" in
  Darwin) host="darwin-x86_64" ;;
  Linux) host="linux-x86_64" ;;
  *) echo "unsupported build host" >&2; exit 2 ;;
esac

toolchain="$ndk/toolchains/llvm/prebuilt/$host"
cc="$toolchain/bin/${triple}23-clang"
cxx="$toolchain/bin/${triple}23-clang++"
ar="$toolchain/bin/llvm-ar"
ranlib="$toolchain/bin/llvm-ranlib"
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

curl -fL --retry 3 "https://github.com/libffi/libffi/releases/download/v$libffi_version/libffi-$libffi_version.tar.gz" -o "$work/libffi.tar.gz"
verify_sha "$libffi_sha" "$work/libffi.tar.gz"
tar -xzf "$work/libffi.tar.gz" -C "$work"
mkdir "$work/libffi-build"
(
  cd "$work/libffi-build"
  CC="$cc" CXX="$cxx" AR="$ar" RANLIB="$ranlib" STRIP="$strip" \
  NM="$toolchain/bin/llvm-nm" OBJDUMP="$toolchain/bin/llvm-objdump" \
    "$work/libffi-$libffi_version/configure" \
      --host="$host_triple" --disable-shared --enable-static \
      --disable-docs --disable-dependency-tracking --prefix="$work/libffi-install"
  make -j"${JAY_BUILD_JOBS:-4}"
  make install
)

common_flags=(
  -std=gnu11 -O3 -fPIC -ffunction-sections -fdata-sections
  -fvisibility=hidden -pthread -DNDEBUG -DLWJGL_LINUX "-D$arch_define"
  -U_FORTIFY_SOURCE -D_FORTIFY_SOURCE=0 -D_GNU_SOURCE -D_FILE_OFFSET_BITS=64
  -I"$toolchain/sysroot/usr/include"
  -I"$lwjgl/modules/lwjgl/core/src/main/c"
  -I"$lwjgl/modules/lwjgl/core/src/main/c/linux"
)
link_flags=(
  -shared -Wl,--no-undefined -Wl,--gc-sections -Wl,-z,noexecstack
  -Wl,-z,max-page-size=16384 -pthread
)

build_library() {
  local name="$1" include_dir="$2"
  shift 2
  local object_dir="$work/objects/$name" index=0 source_file object_file
  mkdir -p "$object_dir" "$output"
  local objects=()
  for source_file in "$@"; do
    object_file="$object_dir/$index.o"
    "$cc" "${common_flags[@]}" -I"$include_dir" -c "$source_file" -o "$object_file"
    objects+=("$object_file")
    index=$((index + 1))
  done
  "$cc" "${link_flags[@]}" \
    -Wl,--version-script,"$lwjgl/config/linux/version.script" \
    "${objects[@]}" "$work/libffi-install/lib/libffi.a" -ldl -llog -lm \
    -o "$output/lib$name.so"
  "$strip" --strip-unneeded "$output/lib$name.so"
}

core_sources=(
  "$lwjgl"/modules/lwjgl/core/src/main/c/*.c
  "$lwjgl"/modules/lwjgl/core/src/generated/c/*.c
  "$lwjgl"/modules/lwjgl/core/src/generated/c/linux/*.c
)
# io_uring is neither needed by Minecraft nor part of Android's public native API.
filtered_core=()
for source_file in "${core_sources[@]}"; do
  [[ "$source_file" == *LibIOURing.c || "$source_file" == *LibURing.c ]] || filtered_core+=("$source_file")
done
build_library lwjgl "$work/libffi-install/include" "${filtered_core[@]}"

opengl_sources=()
for source_file in "$lwjgl"/modules/lwjgl/opengl/src/generated/c/*.c; do
  [[ "$source_file" == *WGL.c ]] || opengl_sources+=("$source_file")
done
build_library lwjgl_opengl "$lwjgl/modules/lwjgl/opengl/src/main/c" "${opengl_sources[@]}"

stb_sources=("$lwjgl"/modules/lwjgl/stb/src/generated/c/*.c)
build_library lwjgl_stb "$lwjgl/modules/lwjgl/stb/src/main/c" "${stb_sources[@]}"

tinyfd_sources=(
  "$lwjgl"/modules/lwjgl/tinyfd/src/main/c/*.c
  "$lwjgl"/modules/lwjgl/tinyfd/src/generated/c/*.c
)
build_library lwjgl_tinyfd "$lwjgl/modules/lwjgl/tinyfd/src/main/c" "${tinyfd_sources[@]}"

android_cmake=(
  -G Ninja
  -DCMAKE_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake"
  -DANDROID_ABI="$abi" -DANDROID_PLATFORM=23
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON
  -DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384
)

curl -fL --retry 3 "https://codeload.github.com/freetype/freetype/tar.gz/$freetype_commit" -o "$work/freetype.tar.gz"
verify_sha "$freetype_sha" "$work/freetype.tar.gz"
mkdir "$work/freetype"
tar -xzf "$work/freetype.tar.gz" -C "$work/freetype" --strip-components=1
cmake -S "$work/freetype" -B "$work/freetype-build" "${android_cmake[@]}" \
  -DFT_DISABLE_ZLIB=TRUE -DFT_DISABLE_BZIP2=TRUE -DFT_DISABLE_PNG=TRUE \
  -DFT_DISABLE_HARFBUZZ=TRUE -DFT_DISABLE_BROTLI=TRUE
cmake --build "$work/freetype-build" --parallel "${JAY_BUILD_JOBS:-4}"
cp "$(find "$work/freetype-build" -type f -name 'libfreetype.so' -print -quit)" "$output/libfreetype.so"

curl -fL --retry 3 "https://codeload.github.com/kcat/openal-soft/tar.gz/$openal_commit" -o "$work/openal.tar.gz"
verify_sha "$openal_sha" "$work/openal.tar.gz"
mkdir "$work/openal"
tar -xzf "$work/openal.tar.gz" -C "$work/openal" --strip-components=1
cmake -S "$work/openal" -B "$work/openal-build" "${android_cmake[@]}" \
  -DALSOFT_UTILS=OFF -DALSOFT_EXAMPLES=OFF -DALSOFT_BACKEND_OBOE=OFF \
  -DALSOFT_BACKEND_OPENSL=ON -DALSOFT_REQUIRE_OPENSL=ON
cmake --build "$work/openal-build" --parallel "${JAY_BUILD_JOBS:-4}"
cp "$(find "$work/openal-build" -type f -name 'libopenal.so' -print -quit)" "$output/libopenal.so"

cp "$lwjgl/LICENSE.md" "$output/LWJGL-LICENSE.md"
cp "$work/libffi-$libffi_version/LICENSE" "$output/LIBFFI-LICENSE.md"
cp "$lwjgl/modules/lwjgl/freetype/freetype_license.txt" "$output/FREETYPE-LICENSE.md"
cp "$lwjgl/modules/lwjgl/openal/openal_soft_license.txt" "$output/OPENAL-SOFT-LICENSE.md"

for library in "$output"/*.so; do
  "$readelf" -lW "$library" | grep -q 'LOAD.*0x4000'
done
"$readelf" -Ws "$output/liblwjgl.so" | grep -q 'JNI_OnLoad'
"$readelf" -Ws "$output/liblwjgl_opengl.so" | grep -q 'org_lwjgl_opengl'
"$readelf" -Ws "$output/liblwjgl_stb.so" | grep -q 'org_lwjgl_stb'
