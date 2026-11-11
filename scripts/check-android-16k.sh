#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <apk> <android-sdk>" >&2
  exit 2
fi

apk="$1"
android_sdk="$2"
zipalign="$(find "$android_sdk/build-tools" -type f -name zipalign -print | sort -V | tail -n 1)"
test -n "$zipalign"

# Android 15+ can use 16 KiB memory pages. Check both the ZIP layout and every
# ARM64 ELF load segment: either half on its own can still produce an APK that
# installs but crashes on a 16 KiB device.
"$zipalign" -c -P 16 -v 4 "$apk" >/dev/null

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
unzip -q "$apk" 'lib/arm64-v8a/*.so' -d "$work"

checked=0
while IFS= read -r -d '' library; do
  checked=$((checked + 1))
  while IFS= read -r alignment; do
    if (( alignment < 0x4000 )); then
      echo "16 KiB incompatible LOAD alignment in $library: $alignment" >&2
      exit 1
    fi
  done < <(readelf -lW "$library" | awk '$1 == "LOAD" { print $NF }')
done < <(find "$work/lib/arm64-v8a" -type f -name '*.so' -print0)

if (( checked == 0 )); then
  echo "No ARM64 native libraries found in $apk" >&2
  exit 1
fi

echo "16 KiB page-size checks passed for $checked ARM64 libraries."
