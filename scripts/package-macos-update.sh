#!/bin/bash
# Package only the existing application bundle. Installer relocates a moved Jay.app
# by bundle identifier and leaves files outside the payload (including game data) alone.
set -euo pipefail
bundle=$1
output=$2
version=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$bundle/Contents/Info.plist")
test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$bundle/Contents/Info.plist")" = nz.work.minecraft.jay
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
mkdir "$stage/root"
/usr/bin/ditto "$bundle" "$stage/root/Jay.app"
/usr/bin/pkgbuild --analyze --root "$stage/root" "$stage/components.plist"
for setting in 'BundleIsRelocatable true' 'BundleIsVersionChecked true' 'BundleHasStrictIdentifier true' 'BundleOverwriteAction update'; do
    /usr/libexec/PlistBuddy -c "Set :0:${setting% *} ${setting##* }" "$stage/components.plist"
done
/usr/bin/pkgbuild --root "$stage/root" --install-location /Applications \
    --component-plist "$stage/components.plist" --identifier nz.work.minecraft.jay.update \
    --version "$version" "$output"
