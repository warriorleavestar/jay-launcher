#!/bin/bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
case "${1:-$(uname -m)}" in
    arm64|osx-arm64) rid=osx-arm64 ;;
    x86_64|osx-x64) rid=osx-x64 ;;
    *) echo "Usage: $0 [osx-arm64|osx-x64]" >&2; exit 2 ;;
esac

output="$root/publish/$rid"
app="$output/Jay.app"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
bundle="$stage/Jay.app"
mkdir -p "$bundle/Contents/MacOS"
# Same publish options as the release build in .github/workflows/build.yml.
dotnet publish "$root/src/Amber.App/Amber.App.csproj" -c Release -r "$rid" \
    --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishReadyToRun=true -p:DebugType=none -o "$bundle/Contents/MacOS"
# Info.plist versions are numbers only (0.7.0), so drop any pre-release suffix.
version="$(dotnet msbuild "$root/src/Amber.App/Amber.App.csproj" -getProperty:Version)"
version="${version%%-*}"
[[ "$version" =~ ^[0-9]+(\.[0-9]+){1,2}$ ]] || { echo "Unexpected version: $version" >&2; exit 1; }
# Scale the icon down from the full-size brand artwork (1254 px), never up from the 512 px app asset.
icon="$root/docs/brand/jay-feather-amber-v1.png"
mkdir -p "$bundle/Contents/Resources" "$stage/Jay.iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$icon" \
        --out "$stage/Jay.iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$icon" \
        --out "$stage/Jay.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$stage/Jay.iconset" -o "$bundle/Contents/Resources/Jay.icns"
# The bundle id stays io.github.amber.launcher: macOS keeps screen recording, accessibility and keychain
# grants per bundle id, so changing it would make every existing user grant them again.
cat > "$bundle/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>Jay</string>
    <key>CFBundleIdentifier</key><string>io.github.amber.launcher</string>
    <key>CFBundleName</key><string>Jay</string>
    <key>CFBundleIconFile</key><string>Jay.icns</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
plutil -lint "$bundle/Contents/Info.plist"
"$root/scripts/bundle-easytier.sh" "$rid" "$bundle/Contents/Resources/easytier"
codesign --force --sign - "$bundle"
codesign --verify --deep --strict "$bundle"
mkdir -p "$output"
if [[ -d "$app" ]]; then rm -r "$app"; fi
ditto "$bundle" "$app"
ditto -c -k --keepParent "$bundle" "$output/Jay-$rid.zip"
printf '%s\n' "$app"
