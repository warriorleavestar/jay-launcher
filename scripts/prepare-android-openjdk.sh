#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <Android-OpenJDK-Build checkout>" >&2
  exit 2
fi

settings="$1/set_devkit_path.sh"
test -f "$settings"
old_jdk21='export LDFLAGS="-L$NDK/platforms/android-$API/arch-$TARGET_SHORT/usr/lib"'
new_jdk21='export LDFLAGS="-L$NDK/platforms/android-$API/arch-$TARGET_SHORT/usr/lib -Wl,-z,max-page-size=16384"'
old_jdk25='export LDFLAGS="-L$NDK/platforms/android-$API/arch-$TARGET_SHORT/usr/lib -lstdc++ -lc++abi"'
new_jdk25='export LDFLAGS="-L$NDK/platforms/android-$API/arch-$TARGET_SHORT/usr/lib -lstdc++ -lc++abi -Wl,-z,max-page-size=16384"'

matches=0
for candidate in "$old_jdk21" "$old_jdk25"; do
  count="$(grep -Fxc "$candidate" "$settings" || true)"
  matches=$((matches + count))
done
if [[ "$matches" -ne 1 ]]; then
  echo "Pinned OpenJDK build source no longer has exactly one expected linker setting" >&2
  exit 1
fi

OLD_JDK21="$old_jdk21" NEW_JDK21="$new_jdk21" \
OLD_JDK25="$old_jdk25" NEW_JDK25="$new_jdk25" perl -pi -e '
  if ($_ eq "$ENV{OLD_JDK21}\n") { $_ = "$ENV{NEW_JDK21}\n"; }
  if ($_ eq "$ENV{OLD_JDK25}\n") { $_ = "$ENV{NEW_JDK25}\n"; }
' "$settings"

if [[ "$(grep -Fc -- '-Wl,-z,max-page-size=16384' "$settings")" -ne 1 ]]; then
  echo "Failed to enable 16 KiB OpenJDK linking" >&2
  exit 1
fi
