#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 && $# -ne 4 ]]; then
  echo "usage: $0 <jre.tar.xz> <arm64-v8a|x86_64> <readelf> [expected Java version]" >&2
  exit 2
fi

archive="$1"
architecture="$2"
elf_reader="$3"
expected_java_version="${4:-}"
test -f "$archive"
test -x "$elf_reader"

case "$architecture" in
  arm64-v8a) expected_machine="AArch64" ;;
  x86_64) expected_machine="Advanced Micro Devices X86-64" ;;
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

# The game needs Java 21; when the caller names the exact release it pinned, require that one.
java_version="$(sed -n 's/^JAVA_VERSION="\(.*\)"$/\1/p' "$work/release")"
if [[ "$java_version" != 21 && "$java_version" != 21.* ]]; then
  echo "runtime is not Java 21: JAVA_VERSION=\"$java_version\"" >&2
  exit 1
fi
if [[ -n "$expected_java_version" && "$java_version" != "$expected_java_version" ]]; then
  echo "runtime is Java $java_version, expected $expected_java_version" >&2
  exit 1
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

echo "Android runtime checks passed for $checked $architecture libraries."
