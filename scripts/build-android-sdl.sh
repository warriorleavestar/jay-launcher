#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <android-sdk> <java-sdk> <output-directory>" >&2
  exit 2
fi

sdk="$1"
java_sdk="$2"
output="$3"
ndk="$sdk/ndk/28.2.13676358"
cmake="$sdk/cmake/3.22.1/bin/cmake"
ninja="$sdk/cmake/3.22.1/bin/ninja"
source_commit="147a8ee32dbf9ac02f3794964490687b6bbda1bc" # SDL release-3.4.14
source_sha="29d9362282d347b8ae25c7f572a535e72de0749e60528abb1523daaf43d072fd"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

test -x "$cmake"
test -x "$ninja"
test -x "$java_sdk/bin/javac"
test -x "$java_sdk/bin/jar"

case "$(uname -s)" in
  Darwin) host="darwin-x86_64" ;;
  Linux) host="linux-x86_64" ;;
  *) echo "unsupported build host" >&2; exit 2 ;;
esac
strip="$ndk/toolchains/llvm/prebuilt/$host/bin/llvm-strip"
readelf="$ndk/toolchains/llvm/prebuilt/$host/bin/llvm-readelf"

verify_sha() {
  local expected="$1" file="$2" actual
  if command -v sha256sum >/dev/null 2>&1; then
    actual="$(sha256sum "$file" | cut -d' ' -f1)"
  else
    actual="$(shasum -a 256 "$file" | cut -d' ' -f1)"
  fi
  [[ "$actual" == "$expected" ]] || { echo "SHA-256 mismatch for $file" >&2; exit 1; }
}

curl -fL --retry 3 \
  "https://codeload.github.com/libsdl-org/SDL/tar.gz/$source_commit" \
  -o "$work/sdl.tar.gz"
verify_sha "$source_sha" "$work/sdl.tar.gz"
mkdir "$work/source"
tar -xzf "$work/sdl.tar.gz" -C "$work/source" --strip-components=1

patch_file="$(cd "$(dirname "$0")/../third_party/android-sdl" && pwd)/utility-windows.patch"
git -C "$work/source" apply --check "$patch_file"
git -C "$work/source" apply "$patch_file"

for abi in arm64-v8a x86_64; do
  build="$work/build-$abi"
  "$cmake" -S "$work/source" -B "$build" -G Ninja \
    -DCMAKE_MAKE_PROGRAM="$ninja" \
    -DCMAKE_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI="$abi" -DANDROID_PLATFORM=26 \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384 \
    -DSDL_SHARED=ON -DSDL_STATIC=OFF -DSDL_ANDROID_JAR=OFF \
    -DSDL_TESTS=OFF -DSDL_TEST_LIBRARY=OFF -DSDL_EXAMPLES=OFF \
    -DSDL_INSTALL_TESTS=OFF
  "$cmake" --build "$build" --target SDL3-shared --parallel "${JAY_BUILD_JOBS:-4}"
  library="$(find "$build" -type f -name 'libSDL3.so' -print -quit)"
  test -n "$library"
  mkdir -p "$output/$abi"
  cp "$library" "$output/$abi/libSDL3.so"
  "$strip" --strip-unneeded "$output/$abi/libSDL3.so"
done

android_jar="$(find "$sdk/platforms" -maxdepth 2 -type f -name android.jar -print | sort -V | tail -n 1)"
test -n "$android_jar"
classes="$work/classes"
mkdir -p "$classes"
java_sources=("$work/source/android-project/app/src/main/java/org/libsdl/app/"*.java)
(( ${#java_sources[@]} > 0 ))
"$java_sdk/bin/javac" --release 8 -encoding UTF-8 -classpath "$android_jar" -d "$classes" "${java_sources[@]}"
"$java_sdk/bin/jar" --create --file "$output/sdl-classes.jar" -C "$classes" .
cp "$work/source/LICENSE.txt" "$output/SDL-LICENSE.txt"

for library in "$output"/*/libSDL3.so; do
  headers="$("$readelf" -h -lW "$library")"
  symbols="$("$readelf" -Ws "$library")"
  grep -q 'LOAD.*0x4000' <<<"$headers"
  grep -q 'JNI_OnLoad' <<<"$symbols"
done
"$java_sdk/bin/jar" --list --file "$output/sdl-classes.jar" | grep -q '^org/libsdl/app/SDLActivity.class$'

echo "Built pinned SDL 3.4.14 for Android arm64-v8a and x86_64."
