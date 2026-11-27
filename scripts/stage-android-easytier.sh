#!/usr/bin/env bash
# Android executes these static binaries from the read-only native library directory.
set -euo pipefail
scripts="$(cd "$(dirname "$0")" && pwd)"
out="${1:?output directory required}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
for abi in arm64-v8a x86_64; do
    case "$abi" in
        arm64-v8a) rid=linux-arm64 ;;
        x86_64) rid=linux-x64 ;;
    esac
    bash "$scripts/bundle-easytier.sh" "$rid" "$work/$abi"
    packages=("$work/$abi/"*.zip)
    mkdir -p "$out/$abi"
    for tool in core cli; do
        unzip -p "${packages[0]}" "*/easytier-$tool" > "$out/$abi/libeasytier-$tool.so"
        test -s "$out/$abi/libeasytier-$tool.so"
    done
    unzip -p "${packages[0]}" '*/LICENSE' > "$out/LICENSE"
    cp "$work/$abi/README.txt" "$out/NOTICE-$abi.txt"
done
