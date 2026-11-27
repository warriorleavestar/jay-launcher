#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

"${CC:-cc}" \
  -std=c17 -O2 -Wall -Wextra -Werror \
  -I"$repo_root/src/Amber.Android.Native" \
  "$repo_root/src/Amber.Android.Native/jay_menu_queue.c" \
  "$repo_root/tests/android-native/jay_menu_queue_test.c" \
  -o "$work_dir/jay-menu-queue-test"

"$work_dir/jay-menu-queue-test"
