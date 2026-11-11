#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 6 ]]; then
  echo "usage: $0 <jre.tar.xz> <mesa-directory> <lwjgl-directory> <arm64-v8a|x86_64> <readelf> <output.tar.xz>" >&2
  exit 2
fi

jre_archive="$1"
mesa_directory="$2"
lwjgl_directory="$3"
architecture="$4"
elf_reader="$5"
output_archive="$6"

test -f "$jre_archive"
test -d "$mesa_directory"
test -d "$lwjgl_directory"
test -x "$elf_reader"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
runtime="$work/runtime"
mkdir -p "$runtime/native"
tar -xf "$jre_archive" -C "$runtime"

copy_runtime_files() {
  local source_directory="$1" file
  while IFS= read -r -d '' file; do
    cp "$file" "$runtime/native/$(basename "$file")"
  done < <(find "$source_directory" -maxdepth 1 -type f \( -name '*.so' -o -name '*.so.*' -o -iname '*license*' \) -print0)
}

copy_runtime_files "$mesa_directory"
copy_runtime_files "$lwjgl_directory"

test -f "$runtime/lib/server/libjvm.so"
test -f "$runtime/native/libEGL.so"
test -f "$runtime/native/zink_dri.so"
test -f "$runtime/native/liblwjgl.so"
test -f "$runtime/native/liblwjgl_opengl.so"
test -f "$runtime/native/liblwjgl_stb.so"
test -f "$runtime/native/libfreetype.so"
test -f "$runtime/native/libopenal.so"

mkdir -p "$(dirname "$output_archive")"
tar -C "$runtime" -cJf "$output_archive" .
"$(cd "$(dirname "$0")" && pwd)/check-android-runtime.sh" \
  "$output_archive" "$architecture" "$elf_reader"

if command -v sha256sum >/dev/null 2>&1; then
  sha256sum "$output_archive" > "$output_archive.sha256"
else
  shasum -a 256 "$output_archive" > "$output_archive.sha256"
fi

archive_size="$(wc -c < "$output_archive" | tr -d ' ')"
extracted_size=0
while IFS= read -r -d '' file; do
  file_size="$(wc -c < "$file" | tr -d ' ')"
  extracted_size=$((extracted_size + file_size))
done < <(find "$runtime" -type f -print0)
printf 'archive-bytes=%s\nextracted-bytes=%s\n' "$archive_size" "$extracted_size" > "$output_archive.metadata"
echo "Created $output_archive ($archive_size bytes compressed, $extracted_size bytes extracted)."
