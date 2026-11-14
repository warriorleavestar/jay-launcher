#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <Android-OpenJDK-Build checkout>" >&2
  exit 2
fi

source_root="$1"
settings="$source_root/set_devkit_path.sh"
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

# The upstream build scripts download their inputs by mutable tags and URLs without checking them.
# Pin each one: after every download or clone, add a check against the value recorded here, so a
# changed input stops the build instead of ending up in the runtime.
add_check_after() {
  local file="$source_root/$1" line="$2" check="$3"
  test -f "$file"
  if [[ "$(grep -Fxc "$line" "$file")" -ne 1 ]]; then
    echo "Pinned OpenJDK build source no longer has the expected download in $1: $line" >&2
    exit 1
  fi
  LINE="$line" CHECK="$check" perl -pi -e '
    if ($_ eq "$ENV{LINE}\n") { $_ .= "$ENV{CHECK}\n"; }
  ' "$file"
  if [[ "$(grep -Fxc "$check" "$file")" -ne 1 ]]; then
    echo "Failed to pin the download in $1" >&2
    exit 1
  fi
}

# OpenJDK 21.0.12.1: the tag must point at the reviewed commit.
add_check_after clone_jdk.sh \
  'git clone --branch jdk-21.0.12.1-ga --depth 1 https://github.com/openjdk/jdk21u openjdk' \
  'test "$(git -C openjdk rev-parse HEAD)" = eac07229cf6cacc8a22eb79f4929e7fed61338b7'

# Android NDK r21 (the toolchain the upstream scripts require).
add_check_after ci_build_global.sh \
  'wget -nc -nv -O android-ndk-$NDK_VERSION-linux-x86_64.zip "https://dl.google.com/android/repository/android-ndk-$NDK_VERSION-linux-x86_64.zip"' \
  'echo "b65ea2d5c5b68fb603626adcbcea6e4d12c68eb8a73e373bbb9d23c252fc647b  android-ndk-$NDK_VERSION-linux-x86_64.zip" | sha256sum -c -'

# Boot JDK 20 used only to compile OpenJDK.
add_check_after get_boot_jdk.sh \
  'wget https://download.java.net/java/GA/jdk20/bdc68b4b9cbc4ebcb30745c85038d91d/36/GPL/openjdk-20_linux-x64_bin.tar.gz' \
  'echo "bb863b2d542976d1ae4b7b81af3e78b1e4247a64644350b552d298d8dc5980dc  openjdk-20_linux-x64_bin.tar.gz" | sha256sum -c -'

# FreeType 2.10.0 and CUPS 2.2.4 sources used by the OpenJDK desktop modules.
add_check_after get_libs.sh \
  'wget https://downloads.sourceforge.net/project/freetype/freetype2/$BUILD_FREETYPE_VERSION/freetype-$BUILD_FREETYPE_VERSION.tar.gz' \
  'echo "955e17244e9b38adb0c98df66abb50467312e6bb70eac07e49ce6bd1a20e809a  freetype-$BUILD_FREETYPE_VERSION.tar.gz" | sha256sum -c -'
add_check_after get_libs.sh \
  'wget https://github.com/apple/cups/releases/download/v2.2.4/cups-2.2.4-source.tar.gz' \
  'echo "596d4db72651c335469ae5f37b0da72ac9f97d73e30838d787065f559dea98cc  cups-2.2.4-source.tar.gz" | sha256sum -c -'

# termux-elf-cleaner v2.2.0, built and run on the produced libraries.
add_check_after tar_jdk.sh \
  "git clone --depth 1 --branch 'v2.2.0' https://github.com/termux/termux-elf-cleaner || true" \
  'test "$(git -C termux-elf-cleaner rev-parse HEAD)" = d08dfdc73ff5b6061e134b90bec16489655015f5'
