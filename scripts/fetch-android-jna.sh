#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <android-ndk> <arm64-v8a|x86_64> <output-directory>" >&2
  exit 2
fi

ndk="$1"
abi="$2"
output="$3"
version="5.17.0"
aar_sha="4dbeffffa665d97ad5aa7eee297531d3c841a86716ab7f774fd6956422b3cf38"
jar_sha="b3a9408e7c51e08ef0e3bfcc08f443f6ec0f6191ba8cd7c18d53d2b22e5bdbc0"

case "$abi" in
  arm64-v8a) expected_machine="AArch64" ;;
  x86_64) expected_machine="Advanced Micro Devices X86-64" ;;
  *) echo "unsupported Android ABI: $abi" >&2; exit 2 ;;
esac
case "$(uname -s)" in
  Darwin) host="darwin-x86_64" ;;
  Linux) host="linux-x86_64" ;;
  *) echo "unsupported build host" >&2; exit 2 ;;
esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
base="https://repo.maven.apache.org/maven2/net/java/dev/jna/jna/$version"
aar="$work/jna-$version.aar"
jar="$work/jna-$version.jar"

verify_sha() {
  local expected="$1" file="$2" actual
  if command -v sha256sum >/dev/null 2>&1; then actual="$(sha256sum "$file" | cut -d' ' -f1)"
  else actual="$(shasum -a 256 "$file" | cut -d' ' -f1)"
  fi
  [[ "$actual" == "$expected" ]] || { echo "SHA-256 mismatch for $file" >&2; exit 1; }
}

curl -fL --retry 3 "$base/jna-$version.aar" -o "$aar"
curl -fL --retry 3 "$base/jna-$version.jar" -o "$jar"
verify_sha "$aar_sha" "$aar"
verify_sha "$jar_sha" "$jar"

mkdir -p "$output"
unzip -p "$aar" "jni/$abi/libjnidispatch.so" > "$output/libjnidispatch.so"
unzip -p "$jar" META-INF/LICENSE > "$output/JNA-LICENSE.txt"
test -s "$output/libjnidispatch.so"
test -s "$output/JNA-LICENSE.txt"

readelf="$ndk/toolchains/llvm/prebuilt/$host/bin/llvm-readelf"
test -x "$readelf"
headers="$("$readelf" -h -lW "$output/libjnidispatch.so")"
machine="$(awk -F: '$1 ~ /Machine/ { sub(/^[[:space:]]+/, "", $2); print $2; exit }' <<<"$headers")"
[[ "$machine" == "$expected_machine" ]] || { echo "wrong JNA ELF architecture: $machine" >&2; exit 1; }
while IFS= read -r alignment; do
  (( alignment >= 0x4000 )) || { echo "JNA is not 16 KiB page compatible: $alignment" >&2; exit 1; }
done < <(awk '$1 == "LOAD" { print $NF }' <<<"$headers")
dependencies="$("$readelf" -dW "$output/libjnidispatch.so")"
grep -q 'Shared library: \[libc.so\]' <<<"$dependencies"
! grep -q 'Shared library: \[libc.so.6\]' <<<"$dependencies"

echo "Staged JNA $version Android native library for $abi."
