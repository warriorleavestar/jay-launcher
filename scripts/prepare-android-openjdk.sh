#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <Android-OpenJDK-Build checkout>" >&2
  exit 2
fi

settings="$1/set_devkit_path.sh"
test -f "$settings"
source_commit='5ee4eed9f7a7f3bca99a662cfe7f2642ec4e22be'
clone_script="$1/clone_jdk.sh"
build_script="$1/build_jdk.sh"
strip_script="$1/remove_jdk_debug_info.sh"
overlay_source="$(cd "$(dirname "$0")/.." && pwd)/third_party/android-openjdk/jdk25u-jay.patch"
overlay_target="$1/patches/jdk25u-jay.patch"
test -f "$clone_script"
test -f "$build_script"
test -f "$strip_script"
test -f "$overlay_source"
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

# The upstream FCL script used a shallow clone of the moving jdk25u head. That
# made an otherwise pinned runtime stop building when OpenJDK changed around its
# Android patch. Pin the official OpenJDK tree too, then apply Jay's small API-21
# overlay after the upstream Android patch set.
old_clone='git clone --depth 1 https://github.com/openjdk/jdk25u openjdk'
if [[ "$(grep -Fxc "$old_clone" "$clone_script" || true)" -ne 1 ]]; then
  echo "Pinned OpenJDK build source no longer has the expected clone command" >&2
  exit 1
fi
OPENJDK_COMMIT="$source_commit" perl -pi -e '
  if ($_ eq "git clone --depth 1 https://github.com/openjdk/jdk25u openjdk\n") {
    $_ = "git init openjdk\n" .
         "git -C openjdk remote add origin https://github.com/openjdk/jdk25u.git\n" .
         "git -C openjdk fetch --depth 1 origin $ENV{OPENJDK_COMMIT}\n" .
         "git -C openjdk checkout --detach FETCH_HEAD\n";
  }
' "$clone_script"

cp "$overlay_source" "$overlay_target"
old_apply='git apply --reject --whitespace=fix ../patches/jdk25u_android.diff || echo "git apply failed (Android patch set)"'
if [[ "$(grep -Fxc "$old_apply" "$build_script" || true)" -ne 1 ]]; then
  echo "Pinned OpenJDK build source no longer has the expected patch command" >&2
  exit 1
fi
perl -pi -e '
    if ($_ eq "git apply --reject --whitespace=fix ../patches/jdk25u_android.diff || echo \"git apply failed (Android patch set)\"\n") {
      $_ .= "git apply --check ../patches/jdk25u-jay.patch\n";
      $_ .= "git apply --whitespace=fix ../patches/jdk25u-jay.patch\n";
    }
  ' "$build_script"

grep -Fqx "git -C openjdk fetch --depth 1 origin $source_commit" "$clone_script"
grep -Fqx 'git apply --check ../patches/jdk25u-jay.patch' "$build_script"

# FCL ships one prebuilt Xawt bridge, and it is AArch64. Copying it into the x86_64
# runtime silently creates a mixed-architecture archive. Minecraft renders through
# GLFW/MobileGlues, so the x86_64 headless runtime must simply omit this optional bridge.
old_xawt='cp awt_xawt/libawt_xawt.so jreout/lib/'
if [[ "$(grep -Fxc "$old_xawt" "$strip_script" || true)" -ne 1 ]]; then
  echo "Pinned OpenJDK build source no longer has the expected Xawt copy" >&2
  exit 1
fi
OLD_XAWT="$old_xawt" perl -pi -e '
  if ($_ eq "$ENV{OLD_XAWT}\n") {
    $_ = "if [[ \"\$TARGET_JDK\" == \"aarch64\" ]]; then\n" .
         "  cp awt_xawt/libawt_xawt.so jreout/lib/\n" .
         "fi\n";
  }
' "$strip_script"
grep -Fqx 'if [[ "$TARGET_JDK" == "aarch64" ]]; then' "$strip_script"
