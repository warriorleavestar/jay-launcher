#!/bin/bash
# 把固定版本的 EasyTier 安装包（Jay 的加固版本）放进发布目录，用户不用再联网下载联机组件。
# Version, size and SHA-256 come from build/pins.json, the same pins Jay checks (PinsTests keeps EasyTier.cs in step); a mismatch fails.
# 用法：scripts/bundle-easytier.sh <rid> <目标目录>，例如 scripts/bundle-easytier.sh win-x64 out/win-x64/easytier
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
pins="$root/build/pins.json"
rid="${1:?usage: $0 <rid> <dir>}"
dest="${2:?usage: $0 <rid> <dir>}"

case "$rid" in
    win-x64) platform=windows-x86_64 ;;
    win-arm64) platform=windows-arm64 ;;
    osx-x64) platform=macos-x86_64 ;;
    osx-arm64) platform=macos-aarch64 ;;
    linux-x64) platform=linux-x86_64 ;;
    linux-arm64) platform=linux-aarch64 ;;
    *) echo "unsupported rid: $rid" >&2; exit 2 ;;
esac

command -v jq >/dev/null || { echo "jq is needed to read $pins" >&2; exit 1; }
pin() { jq -r --arg id "easytier-$platform" ".pins[] | select(.id == \$id) | .$1 // empty" "$pins"; }
version="$(pin version)"
tag="$(pin tag)"
name="$(pin file)"
url="$(jq -r --arg id "easytier-$platform" '.pins[] | select(.id == $id) | .urls[0] // empty' "$pins")"
source_url="$(pin source)"
sha="$(pin sha256)"
size="$(pin size)"
if [[ -z "$version" || -z "$tag" || -z "$name" || -z "$url" || -z "$source_url" || -z "$sha" || -z "$size" ]]; then
    echo "could not read the easytier-$platform pin from $pins" >&2
    exit 1
fi

mkdir -p "$dest"
curl -fL --retry 5 --retry-all-errors --connect-timeout 20 -o "$dest/$name.tmp" "$url"

actual_size="$(wc -c < "$dest/$name.tmp" | tr -d ' ')"
if command -v sha256sum >/dev/null; then actual_sha="$(sha256sum "$dest/$name.tmp" | cut -d' ' -f1)"
else actual_sha="$(shasum -a 256 "$dest/$name.tmp" | cut -d' ' -f1)"; fi
if [[ "$actual_size" != "$size" || "$actual_sha" != "$sha" ]]; then
    echo "EasyTier package mismatch: size $actual_size (want $size), sha256 $actual_sha (want $sha)" >&2
    rm -f "$dest/$name.tmp"
    exit 1
fi
mv "$dest/$name.tmp" "$dest/$name"

cat > "$dest/README.txt" <<TXT
EasyTier $version, Jay build $tag ($platform)

这是开源组网工具 EasyTier 的 Jay 加固版本，Jay 的联机功能用它。改动和构建方法见源码仓库里的 JAY.md。
This is Jay's hardened build of EasyTier, used by Jay for multiplayer. JAY.md in the source repository lists the changes and how it is built.

License: LGPL-3.0
Source:  $source_url/tree/$tag
Upstream: https://github.com/EasyTier/EasyTier
Package: $url
SHA-256: $sha
TXT
echo "bundled $name"
