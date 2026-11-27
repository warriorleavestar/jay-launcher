#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 6 || $# -gt 7 ]]; then
  echo "usage: $0 <jre.tar.xz> <renderer-directory> <lwjgl-directory> <arm64-v8a|x86_64> <readelf> <output.tar.xz> [expected-java-major]" >&2
  exit 2
fi

jre_archive="$1"
renderer_directory="$2"
lwjgl_directory="$3"
architecture="$4"
elf_reader="$5"
output_archive="$6"
expected_java_major="${7:-}"

test -f "$jre_archive"
test -d "$renderer_directory"
test -d "$lwjgl_directory"
test -x "$elf_reader"

case "$architecture" in
  arm64-v8a) android_triple="aarch64-linux-android" ;;
  x86_64) android_triple="x86_64-linux-android" ;;
  *) echo "unsupported architecture: $architecture" >&2; exit 2 ;;
esac
toolchain="$(cd "$(dirname "$elf_reader")/.." && pwd)"
cxx_runtime="$toolchain/sysroot/usr/lib/$android_triple/libc++_shared.so"
if [[ ! -f "$cxx_runtime" ]]; then
  echo "Android C++ runtime not found beside the NDK ELF reader: $cxx_runtime" >&2
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
runtime="$work/runtime"
mkdir -p "$runtime/native"
tar -xf "$jre_archive" -C "$runtime"
# Android OpenJDK's libjvm links the NDK C++ runtime dynamically. Keep it next
# to the JRE libraries so the host can preload it before opening libjvm.so.
cp "$cxx_runtime" "$runtime/lib/libc++_shared.so"

copy_runtime_files() {
  local source_directory="$1" file
  while IFS= read -r -d '' file; do
    cp "$file" "$runtime/native/$(basename "$file")"
  done < <(find "$source_directory" -maxdepth 1 -type f \( -name '*.so' -o -name '*.so.*' -o -iname '*license*' \) -print0)
}

copy_runtime_files "$renderer_directory"
copy_runtime_files "$lwjgl_directory"

test -f "$runtime/lib/server/libjvm.so"
test -f "$runtime/lib/libc++_shared.so"
test -f "$runtime/native/libmobileglues.so"
test -f "$runtime/native/liblwjgl.so"
test -f "$runtime/native/liblwjgl_opengl.so"
test -f "$runtime/native/liblwjgl_stb.so"
test -f "$runtime/native/liblwjgl_vma.so"
test -f "$runtime/native/libshaderc.so"
test -f "$runtime/native/libspirv-cross.so"
test -f "$runtime/native/libfreetype.so"
test -f "$runtime/native/libopenal.so"

mkdir -p "$(dirname "$output_archive")"
# BSD tar otherwise serializes macOS extended attributes as `._*` AppleDouble
# files, which Android would treat as untrusted extra runtime entries.
COPYFILE_DISABLE=1 tar -C "$runtime" -cJf "$output_archive" .
check_args=("$output_archive" "$architecture" "$elf_reader")
if [[ -n "$expected_java_major" ]]; then check_args+=("$expected_java_major"); fi
"$(cd "$(dirname "$0")" && pwd)/check-android-runtime.sh" "${check_args[@]}"

checksum_directory="$(cd "$(dirname "$output_archive")" && pwd)"
checksum_name="$(basename "$output_archive")"
if command -v sha256sum >/dev/null 2>&1; then
  (cd "$checksum_directory" && sha256sum "$checksum_name" > "$checksum_name.sha256")
else
  (cd "$checksum_directory" && shasum -a 256 "$checksum_name" > "$checksum_name.sha256")
fi

archive_size="$(wc -c < "$output_archive" | tr -d ' ')"
extracted_size=0
while IFS= read -r -d '' file; do
  file_size="$(wc -c < "$file" | tr -d ' ')"
  extracted_size=$((extracted_size + file_size))
done < <(find "$runtime" -type f -print0)
printf 'archive-bytes=%s\nextracted-bytes=%s\n' "$archive_size" "$extracted_size" > "$output_archive.metadata"
echo "Created $output_archive ($archive_size bytes compressed, $extracted_size bytes extracted)."
