#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 5 ]]; then
  echo "usage: $0 <runtime.tar.xz> <arm64-v8a|x86_64> <source-url> <download-url> <output.json>" >&2
  exit 2
fi

archive="$1"
architecture="$2"
source_url="$3"
download_url="$4"
output="$5"

case "$architecture" in
  arm64-v8a|x86_64) ;;
  *) echo "unsupported Android ABI: $architecture" >&2; exit 2 ;;
esac
[[ "$source_url" == https://* ]]
[[ "$download_url" == https://* ]]
test -f "$archive"
test -f "$archive.sha256"
test -f "$archive.metadata"
command -v jq >/dev/null

archive_name="$(basename "$archive")"
archive_size="$(wc -c < "$archive" | tr -d ' ')"
checksum_line="$(cat "$archive.sha256")"
checksum="${checksum_line%% *}"
checksum_name="${checksum_line##*  }"
[[ "$checksum" =~ ^[0-9a-f]{64}$ ]]
[[ "$checksum_name" == "$archive_name" ]]

metadata_archive="$(sed -n 's/^archive-bytes=//p' "$archive.metadata")"
extracted_size="$(sed -n 's/^extracted-bytes=//p' "$archive.metadata")"
[[ "$metadata_archive" =~ ^[0-9]+$ ]]
[[ "$extracted_size" =~ ^[0-9]+$ ]]
[[ "$archive_size" == "$metadata_archive" ]]
(( extracted_size >= archive_size ))

mkdir -p "$(dirname "$output")"
temporary="$output.tmp"
trap 'rm -f "$temporary"' EXIT
jq -n -S \
  --arg id "jay-android-runtime25-v3-$architecture" \
  --arg architecture "$architecture" \
  --argjson archiveSize "$archive_size" \
  --arg archiveSha256 "$checksum" \
  --argjson maximumExtractedSize "$extracted_size" \
  --arg license "OpenJDK GPL-2.0 with Classpath Exception; MobileGlues LGPL-2.1-only; LWJGL BSD; JNA Apache-2.0 OR LGPL-2.1-or-later; bundled component licenses included" \
  --arg sourceUrl "$source_url" \
  --arg downloadUrl "$download_url" \
  '{
    Format: 1,
    Id: $id,
    Architecture: $architecture,
    ArchiveSize: $archiveSize,
    ArchiveSha256: $archiveSha256,
    MaximumExtractedSize: $maximumExtractedSize,
    License: $license,
    SourceUrl: $sourceUrl,
    DownloadUrl: $downloadUrl
  }' > "$temporary"
jq -e \
  --arg architecture "$architecture" \
  --argjson archiveSize "$archive_size" \
  --arg checksum "$checksum" \
  '.Format == 1 and .Architecture == $architecture and .ArchiveSize == $archiveSize and .ArchiveSha256 == $checksum' \
  "$temporary" >/dev/null
mv "$temporary" "$output"
trap - EXIT

echo "Created $output for $archive_name."
