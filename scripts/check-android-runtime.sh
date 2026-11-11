#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <jre.tar.xz> <arm64-v8a|x86_64> <readelf>" >&2
  exit 2
fi

archive="$1"
architecture="$2"
elf_reader="$3"
test -f "$archive"
test -x "$elf_reader"

case "$architecture" in
  arm64-v8a) expected_machine="AArch64" ;;
  x86_64) expected_machine="Advanced Micro Devices X86-64" ;;
  *) echo "unsupported architecture: $architecture" >&2; exit 2 ;;
esac

listing="$(tar -tf "$archive")"
while IFS= read -r entry; do
  entry="${entry#./}"
  [[ -z "$entry" ]] && continue
  if [[ "$entry" == /* || "$entry" == ".." || "$entry" == ../* || "$entry" == */../* || "$entry" == */.. ]]; then
    echo "unsafe runtime archive path: $entry" >&2
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
