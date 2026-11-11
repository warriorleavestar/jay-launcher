#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <android-ndk> <arm64-v8a|x86_64> <output-directory>" >&2
  exit 2
fi

ndk="$1"
abi="$2"
output="$3"
version="26.2.6"
expected_sha="b9436b3e1a8be76fbd4823dc884f90596ab7c6ac5e1867a305e66ffccfb31a92"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

case "$abi" in
  arm64-v8a) triple="aarch64-linux-android"; cpu_family="aarch64"; cpu="armv8" ;;
  x86_64) triple="x86_64-linux-android"; cpu_family="x86_64"; cpu="x86_64" ;;
  *) echo "unsupported Android ABI: $abi" >&2; exit 2 ;;
esac
case "$(uname -s)" in
  Darwin) host="darwin-x86_64" ;;
  Linux) host="linux-x86_64" ;;
  *) echo "unsupported build host" >&2; exit 2 ;;
esac

toolchain="$ndk/toolchains/llvm/prebuilt/$host"
archive="$work/mesa.tar.xz"
curl -fL --retry 3 "https://archive.mesa3d.org/mesa-$version.tar.xz" -o "$archive"
printf '%s  %s\n' "$expected_sha" "$archive" | sha256sum --check --status
tar -xf "$archive" -C "$work"
source="$work/mesa-$version"
patch_file="$(cd "$(dirname "$0")/../third_party/android-mesa" && pwd)/jay-android.patch"
patch -d "$source" -p1 --forward --batch < "$patch_file"
cross="$work/android-$abi.ini"
cat >"$cross" <<EOF
[binaries]
c = ['$toolchain/bin/${triple}26-clang', '-femulated-tls']
cpp = ['$toolchain/bin/${triple}26-clang++', '-femulated-tls', '-fno-exceptions', '-fno-rtti', '-static-libstdc++']
ar = '$toolchain/bin/llvm-ar'
strip = '$toolchain/bin/llvm-strip'
pkg-config = 'pkg-config'

[host_machine]
system = 'android'
cpu_family = '$cpu_family'
cpu = '$cpu'
endian = 'little'

[properties]
needs_exe_wrapper = true
EOF

meson setup "$work/build" "$source" --cross-file "$cross" \
  --prefix=/opt/jay-mesa --libdir=lib --buildtype=release \
  -Dc_link_args=-Wl,-z,max-page-size=16384 \
  -Dcpp_link_args=-Wl,-z,max-page-size=16384 \
  -Dplatforms=android -Dplatform-sdk-version=35 -Dandroid-stub=true \
  -Dandroid-libbacktrace=disabled -Dandroid-libperfetto=disabled \
  -Dgallium-drivers=zink -Dvulkan-drivers=[] -Dvulkan-layers=[] \
  -Dallow-fallback-for=libdrm \
  -Dlibdrm:tests=false -Dlibdrm:cairo-tests=disabled \
  -Dlibdrm:man-pages=disabled -Dlibdrm:valgrind=disabled \
  -Degl=enabled -Degl-native-platform=android -Dopengl=true \
  -Dgles1=disabled -Dgles2=enabled -Dglx=disabled -Dgbm=disabled \
  -Dllvm=disabled -Dlibunwind=disabled -Dvalgrind=disabled \
  -Dexpat=disabled -Dxmlconfig=disabled -Dzstd=disabled -Dshader-cache=enabled \
  -Dgallium-va=disabled -Dgallium-mediafoundation=disabled \
  -Dvideo-codecs=[] -Dbuild-tests=false -Dtools=[]
meson compile -C "$work/build"
DESTDIR="$work/install" meson install -C "$work/build"

mkdir -p "$output"
while IFS= read -r -d '' library; do
  # Flatten the runtime while retaining every loader-visible SONAME and DRI alias.
  # Symlinks are deliberately dereferenced because Jay's installer rejects links.
  cp -L "$library" "$output/$(basename "$library")"
done < <(find "$work/install/opt/jay-mesa" \( -type f -o -type l \) \( -name '*.so' -o -name '*.so.*' \) -print0)
cp "$source/docs/license.rst" "$output/MESA-LICENSE.rst"
cp "$source/licenses/MIT" "$output/MESA-MIT.txt"

test -f "$output/libEGL.so"
test -f "$output/zink_dri.so"

readelf="$toolchain/bin/llvm-readelf"
found=0
for library in "$output"/*.so*; do
  [[ -f "$library" ]] || continue
  found=1
  headers="$("$readelf" -lW "$library")"
  grep -q 'LOAD.*0x4000' <<<"$headers"
done
test "$found" -eq 1
