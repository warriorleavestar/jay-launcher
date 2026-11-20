#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 || $# -gt 5 ]]; then
  echo "usage: $0 <runtime.tar.xz> <arm64-v8a|x86_64> <readelf> [expected-java-major] [complete|jre-only]" >&2
  exit 2
fi

archive="$1"
architecture="$2"
elf_reader="$3"
expected_java_major="${4:-}"
contents="${5:-complete}"
test -f "$archive"
test -x "$elf_reader"

if [[ -n "$expected_java_major" && ! "$expected_java_major" =~ ^[0-9]+$ ]]; then
  echo "expected Java major must be a number: $expected_java_major" >&2
  exit 2
fi
case "$contents" in
  complete|jre-only) ;;
  *) echo "unsupported runtime contents mode: $contents" >&2; exit 2 ;;
esac

case "$architecture" in
  arm64-v8a)
    expected_machine="AArch64"
    expected_release_arch="aarch64"
    ;;
  x86_64)
    expected_machine="Advanced Micro Devices X86-64"
    expected_release_arch="x86_64"
    ;;
  *) echo "unsupported architecture: $architecture" >&2; exit 2 ;;
esac

listing="$(tar -tf "$archive")"
verbose_listing="$(tar -tvf "$archive")"
if awk 'substr($1, 1, 1) == "l" || substr($1, 1, 1) == "h" { found=1 } END { exit !found }' <<<"$verbose_listing"; then
  echo "runtime archive must not contain symbolic or hard links" >&2
  exit 1
fi
while IFS= read -r entry; do
  entry="${entry#./}"
  [[ -z "$entry" ]] && continue
  if [[ "$entry" == /* || "$entry" == ".." || "$entry" == ../* || "$entry" == */../* || "$entry" == */.. ]]; then
    echo "unsafe runtime archive path: $entry" >&2
    exit 1
  fi
  if [[ "$entry" == ._* || "$entry" == */._* ]]; then
    echo "AppleDouble metadata is not allowed in the runtime archive: $entry" >&2
    exit 1
  fi
done <<<"$listing"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
tar -xf "$archive" -C "$work"

test -f "$work/lib/server/libjvm.so"
test -f "$work/lib/modules"
test -f "$work/release"
test -f "$work/legal/java.base/LICENSE"
test -f "$work/legal/java.base/ASSEMBLY_EXCEPTION"

release_value() {
  local key="$1"
  sed -nE "s/^${key}=\"([^\"]*)\"$/\\1/p" "$work/release" | head -n 1
}

java_version="$(release_value JAVA_VERSION)"
runtime_architecture="$(release_value OS_ARCH)"
runtime_os="$(release_value OS_NAME)"
if [[ -z "$java_version" ]]; then
  echo "runtime release file has no JAVA_VERSION" >&2
  exit 1
fi
java_major="${java_version%%.*}"
if [[ ! "$java_major" =~ ^[0-9]+$ ]]; then
  echo "runtime has an invalid JAVA_VERSION: $java_version" >&2
  exit 1
fi
if [[ -n "$expected_java_major" && "$java_major" != "$expected_java_major" ]]; then
  echo "wrong Java major in runtime: expected $expected_java_major, found $java_version" >&2
  exit 1
fi
if [[ "$runtime_architecture" != "$expected_release_arch" ]]; then
  echo "wrong OS_ARCH in runtime release file: $runtime_architecture" >&2
  exit 1
fi
if [[ "$runtime_os" != Linux ]]; then
  echo "wrong OS_NAME in runtime release file: $runtime_os" >&2
  exit 1
fi

if [[ "$contents" == complete ]]; then
  for required_library in \
    libmobileglues.so \
    liblwjgl.so \
    liblwjgl_opengl.so \
    liblwjgl_stb.so \
    liblwjgl_vma.so \
    libshaderc.so \
    libspirv-cross.so \
    libfreetype.so \
    libopenal.so
  do
    if [[ ! -f "$work/native/$required_library" ]]; then
      echo "complete runtime is missing native/$required_library" >&2
      exit 1
    fi
  done
fi

checked=0
while IFS= read -r -d '' library; do
  checked=$((checked + 1))
  headers="$("$elf_reader" -h -lW "$library")"
  machine="$(awk -F: '$1 ~ /Machine/ { sub(/^[[:space:]]+/, "", $2); print $2; exit }' <<<"$headers")"
  if [[ "$machine" != "$expected_machine" ]]; then
    echo "wrong ELF architecture in $library: $machine" >&2
    exit 1
  fi
  while IFS= read -r alignment; do
    if (( alignment < 0x4000 )); then
      echo "16 KiB incompatible LOAD alignment in $library: $alignment" >&2
      exit 1
    fi
  done < <(awk '$1 == "LOAD" { print $NF }' <<<"$headers")
done < <(find "$work" -type f -name '*.so' -print0)

if (( checked == 0 )); then
  echo "No native libraries found in $archive" >&2
  exit 1
fi

echo "Android $contents runtime checks passed for Java $java_version and $checked $architecture libraries."
