#!/bin/bash
# Build a normal Jay.app and place it in a compressed DMG.
# Usage:
#   scripts/publish-macos.sh [osx-arm64|osx-x64]
#   scripts/publish-macos.sh <rid> <already-published-directory> <output-directory>
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
case "${1:-$(uname -m)}" in
    arm64|osx-arm64) rid=osx-arm64 ;;
    x86_64|osx-x64) rid=osx-x64 ;;
    *) echo "Usage: $0 [osx-arm64|osx-x64] [published-dir] [output-dir]" >&2; exit 2 ;;
esac

published="${2:-}"
output="${3:-$root/publish/$rid}"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
bundle="$stage/dmg/Jay.app"
contents="$bundle/Contents"
macos="$contents/MacOS"
resources="$contents/Resources"
mkdir -p "$macos" "$resources" "$stage/Jay.iconset" "$output"

if [[ -n "$published" ]]; then
    test -x "$published/Jay" || { echo "$published does not contain an executable Jay" >&2; exit 1; }
    ditto "$published" "$macos"
else
    dotnet publish "$root/src/Amber.App/Amber.App.csproj" -c Release -r "$rid" \
        --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:PublishReadyToRun=true -p:DebugType=none -o "$macos"
    "$root/scripts/bundle-easytier.sh" "$rid" "$macos/easytier"
fi
find "$macos" -name '*.pdb' -delete
chmod +x "$macos/Jay"

# Scale the icon down from the full-size brand artwork (1254 px), never up from the 512 px app asset.
icon="$root/docs/brand/jay-feather-amber-v1.png"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$icon" \
        --out "$stage/Jay.iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$icon" \
        --out "$stage/Jay.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$stage/Jay.iconset" -o "$resources/Jay.icns"

version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$root/src/Directory.Build.props" | head -1)"
cat > "$contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>Jay</string>
    <key>CFBundleIdentifier</key><string>nz.work.minecraft.jay</string>
    <key>CFBundleName</key><string>Jay</string>
    <key>CFBundleDisplayName</key><string>Jay</string>
    <key>CFBundleIconFile</key><string>Jay.icns</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
plutil -lint "$contents/Info.plist"

# Sign after every file is in place, otherwise Gatekeeper reports that the app was modified or damaged.
# A Developer ID identity can be supplied by release CI later; '-' is a consistent ad-hoc signature.
identity="${MACOS_CODESIGN_IDENTITY:--}"
if [[ "$identity" == "-" ]]; then
    codesign --force --deep --sign - --timestamp=none "$bundle"
else
    # Developer ID builds use the hardened runtime and Apple's trusted timestamp, ready for notarization.
    codesign --force --deep --options runtime --sign "$identity" --timestamp "$bundle"
fi
codesign --verify --deep --strict --verbose=2 "$bundle"

bash "$root/scripts/package-macos-update.sh" "$bundle" "$output/Jay-$rid.pkg"

ln -s /Applications "$stage/dmg/Applications"
dmg="$output/Jay-$rid.dmg"
rm -f "$dmg"
hdiutil create -quiet -volname "Jay" -srcfolder "$stage/dmg" -ov -format UDZO "$dmg"
hdiutil verify "$dmg" >/dev/null
printf '%s\n' "$dmg"
