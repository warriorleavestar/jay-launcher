#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <Android-OpenJDK-Build checkout>" >&2
  exit 2
fi

settings="$1/set_devkit_path.sh"
test -f "$settings"
old_line='export LDFLAGS="-L$NDK/platforms/android-$API/arch-$TARGET_SHORT/usr/lib"'
new_line='export LDFLAGS="-L$NDK/platforms/android-$API/arch-$TARGET_SHORT/usr/lib -Wl,-z,max-page-size=16384"'

if [[ "$(grep -Fxc "$old_line" "$settings")" -ne 1 ]]; then
  echo "Pinned OpenJDK build source no longer has the expected linker settings" >&2
  exit 1
fi

OLD_LINE="$old_line" NEW_LINE="$new_line" perl -pi -e '
  if ($_ eq "$ENV{OLD_LINE}\n") { $_ = "$ENV{NEW_LINE}\n"; }
' "$settings"

if [[ "$(grep -Fxc "$new_line" "$settings")" -ne 1 ]]; then
  echo "Failed to enable 16 KiB OpenJDK linking" >&2
  exit 1
fi
