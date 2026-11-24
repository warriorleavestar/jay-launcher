#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || ! -f "$1" ]]; then
  echo "usage: $0 /path/to/minecraft-client.jar" >&2
  exit 2
fi

jar_path=$1
javap_bin=${JAVAP_BIN:-javap}
work_dir=$(mktemp -d "${TMPDIR:-/tmp}/jay-input-bridge.XXXXXX")
cleanup() {
  find "$work_dir" -type f -delete
  rmdir "$work_dir"
}
trap cleanup EXIT

dump() {
  local class_name=$1
  local output_name=$2
  "$javap_bin" -classpath "$jar_path" -private "$class_name" >"$work_dir/$output_name"
}

require() {
  local output_name=$1
  local member=$2
  if ! grep -Fq "$member" "$work_dir/$output_name"; then
    echo "Android input bridge mismatch: $output_name is missing: $member" >&2
    exit 1
  fi
}

require_any() {
  local output_name=$1
  shift
  local member
  for member in "$@"; do
    if grep -Fq "$member" "$work_dir/$output_name"; then
      return 0
    fi
  done
  echo "Android input bridge mismatch: $output_name has none of: $*" >&2
  exit 1
}

if "$javap_bin" -classpath "$jar_path" -private net.minecraft.client.Minecraft \
    >/dev/null 2>&1; then
  dump net.minecraft.client.Minecraft minecraft.txt
  dump net.minecraft.client.multiplayer.MultiPlayerGameMode game-mode.txt
  require minecraft.txt 'net.minecraft.client.multiplayer.MultiPlayerGameMode gameMode;'
  require minecraft.txt 'void pauseGame(boolean);'
  require game-mode.txt 'float destroyProgress;'
  require game-mode.txt 'boolean isDestroying;'
  named_input=net.minecraft.world.inventory.ContainerInput
  if ! "$javap_bin" -classpath "$jar_path" "$named_input" >/dev/null 2>&1; then
    named_input=net.minecraft.world.inventory.ClickType
  fi
  dump "$named_input" click-type.txt
  require click-type.txt "$named_input PICKUP_ALL;"
  dump net.minecraft.client.MouseHandler mouse.txt
  dump net.minecraft.client.KeyboardHandler keyboard.txt
  dump net.minecraft.client.gui.Gui gui.txt
  dump com.mojang.blaze3d.platform.Window window.txt
  dump net.minecraft.client.gui.screens.Screen screen.txt
  dump net.minecraft.client.gui.screens.inventory.AbstractContainerScreen container.txt
  dump net.minecraft.world.inventory.Slot slot.txt
  dump net.minecraft.world.item.ItemStack stack.txt
  dump net.minecraft.world.phys.HitResult hit.txt
  dump 'net.minecraft.world.phys.HitResult$Type' hit-type.txt
  require hit.txt 'net.minecraft.world.phys.HitResult$Type getType();'
  require hit-type.txt 'net.minecraft.world.phys.HitResult$Type BLOCK;'

  require minecraft.txt "public static net.minecraft.client.Minecraft getInstance();"
  require minecraft.txt "net.minecraft.client.MouseHandler mouseHandler;"
  require minecraft.txt "net.minecraft.client.KeyboardHandler keyboardHandler;"
  require minecraft.txt "com.mojang.blaze3d.platform.Window window;"
  require minecraft.txt "net.minecraft.world.phys.HitResult hitResult;"
  require_any minecraft.txt \
    "net.minecraft.client.gui.screens.Screen screen;" \
    "net.minecraft.client.gui.Gui gui;"
  if grep -Fq "net.minecraft.client.gui.Gui gui;" "$work_dir/minecraft.txt"; then
    require gui.txt "net.minecraft.client.gui.screens.Screen screen();"
  fi

  require_any mouse.txt \
    "onMove(long, double, double);" \
    "onMove(long, double, double, double, double);"
  require mouse.txt "onScroll(long, double, double);"
  require_any mouse.txt \
    'onPress(long, com.mojang.blaze3d.platform.InputConstants$Key, int);' \
    "onButton(long, net.minecraft.client.input.MouseButtonInfo, int);"

  require_any keyboard.txt \
    "keyPress(long, int, int, int, int);" \
    "keyPress(long, int, net.minecraft.client.input.KeyEvent);"
  require_any keyboard.txt \
    "charTyped(long, int, int);" \
    "charTyped(long, net.minecraft.client.input.CharacterEvent);"

  require_any window.txt "long getWindow();" "long handle();"
  require window.txt "int getScreenWidth();"
  require window.txt "int getScreenHeight();"
  require screen.txt "int width;"
  require screen.txt "int height;"
  require container.txt "net.minecraft.world.inventory.Slot hoveredSlot;"
  require slot.txt "net.minecraft.world.item.ItemStack getItem();"
  require stack.txt "int getCount();"
elif "$javap_bin" -classpath "$jar_path" -private gfj >/dev/null 2>&1; then
  dump gfj minecraft.txt
  dump hio game-mode.txt
  require minecraft.txt "hio q;"
  require minecraft.txt "void b(boolean);"
  require game-mode.txt "float f;"
  require game-mode.txt "boolean i;"
  dump gfk mouse.txt
  dump gfi keyboard.txt
  dump fyk window.txt
  dump gsb screen.txt
  dump gti container.txt
  dump dji slot.txt
  dump dlt stack.txt
  dump gzd mouse-button.txt
  dump fti block-hit.txt
  dump dhu click-type.txt

  require minecraft.txt "static gfj A;"
  require minecraft.txt "gfk n;"
  require minecraft.txt "gfi o;"
  require minecraft.txt "fyk O;"
  require minecraft.txt "ftk u;"
  require minecraft.txt "gsb x;"
  require minecraft.txt "private boolean bu();"
  require minecraft.txt "private void bv();"
  require mouse.txt "double h;"
  require mouse.txt "double i;"
  require mouse.txt "void a(long, gzd, int);"
  require mouse.txt "void b(long, double, double);"
  require mouse.txt "void c(long, double, double);"
  require keyboard.txt "void a(long, int, int, int, int);"
  require keyboard.txt "void a(long, int, int);"
  require window.txt "long h();"
  require window.txt "int m();"
  require window.txt "int n();"
  require screen.txt "int o;"
  require screen.txt "int p;"
  require container.txt "dji y;"
  require container.txt "dji b(double, double);"
  require container.txt "void a(dji, int, int, dhu);"
  require slot.txt "int a;"
  require slot.txt "dlt g();"
  require stack.txt "int r;"
  require mouse-button.txt "gzd(int, int);"
  require click-type.txt "dhu a;"
  require click-type.txt "dhu g;"
else
  # Mojang 1.20.1 (official client mappings 1.20.1-20230612.114412).
  dump enn minecraft.txt
  dump ffa game-mode.txt
  require minecraft.txt "ffa r;"
  require minecraft.txt "void c(boolean);"
  require game-mode.txt "float f;"
  require game-mode.txt "boolean i;"
  dump eno mouse.txt
  dump enm keyboard.txt
  dump ehn window.txt
  dump euq screen.txt
  dump evp container.txt
  dump ccx slot.txt
  dump cfz stack.txt
  dump eee block-hit.txt
  dump cbo click-type.txt

  require minecraft.txt "static enn F;"
  require minecraft.txt "eno n;"
  require minecraft.txt "enm o;"
  require minecraft.txt "ehn Q;"
  require minecraft.txt "eeg w;"
  require minecraft.txt "euq z;"
  require minecraft.txt "boolean bj();"
  require minecraft.txt "void bs();"
  require mouse.txt "double e;"
  require mouse.txt "double f;"
  require mouse.txt "void a(long, int, int, int);"
  require mouse.txt "void b(long, double, double);"
  require mouse.txt "void a(long, double, double);"
  require keyboard.txt "void a(long, int, int, int, int);"
  require keyboard.txt "void a(long, int, int);"
  require window.txt "long i();"
  require window.txt "int m();"
  require window.txt "int n();"
  require screen.txt "int g;"
  require screen.txt "int h;"
  require container.txt "ccx r;"
  require container.txt "void a(ccx, int, int, cbo);"
  require slot.txt "int a;"
  require slot.txt "cfz e();"
  require stack.txt "int s;"
  require click-type.txt "cbo a;"
  require click-type.txt "cbo g;"
fi

echo "Android input bridge signatures match $(basename "$jar_path")."
