#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <apk>" >&2
  exit 2
fi

apk="$1"
test -f "$apk"
command -v adb >/dev/null

adb install "$apk"

adb shell wm size 1080x2400
adb shell wm density 420
adb shell monkey -p nz.work.minecraft.jay -c android.intent.category.LAUNCHER 1
sleep 8
test -n "$(adb shell pidof nz.work.minecraft.jay)"
adb shell input swipe 540 1900 540 700 500
adb exec-out screencap -p > android-phone.png

adb shell am force-stop nz.work.minecraft.jay
adb shell wm size 2560x1600
adb shell wm density 320
adb shell monkey -p nz.work.minecraft.jay -c android.intent.category.LAUNCHER 1
sleep 5
test -n "$(adb shell pidof nz.work.minecraft.jay)"
adb shell input tap 1280 800
adb shell input swipe 2100 1300 2100 500 500
sleep 2
test -n "$(adb shell pidof nz.work.minecraft.jay)"
adb exec-out screencap -p > android-tablet.png

adb shell am force-stop nz.work.minecraft.jay
adb shell wm size 600x420
adb shell wm density 160
launcher_component="$(adb shell cmd package resolve-activity --brief \
  -a android.intent.action.MAIN -c android.intent.category.LAUNCHER \
  nz.work.minecraft.jay | tr -d '\r' | tail -n 1)"
test -n "$launcher_component"
adb shell am start -W -n "$launcher_component" \
  --ez nz.work.minecraft.jay.TOUCH_SETTINGS_SELF_TEST true \
  --ez nz.work.minecraft.jay.PSEUDO_LOCALIZATION_SELF_TEST false
sleep 3
test -n "$(adb shell pidof nz.work.minecraft.jay:game)"
adb exec-out screencap -p > android-touch-settings-en.png

adb shell am force-stop nz.work.minecraft.jay
adb shell am start -W -n "$launcher_component" \
  --ez nz.work.minecraft.jay.TOUCH_SETTINGS_SELF_TEST true \
  --ez nz.work.minecraft.jay.PSEUDO_LOCALIZATION_SELF_TEST true
sleep 3
test -n "$(adb shell pidof nz.work.minecraft.jay:game)"
adb exec-out screencap -p > android-touch-settings-pseudo.png

file android-touch-settings-en.png | grep -F '600 x 420'
file android-touch-settings-pseudo.png | grep -F '600 x 420'
