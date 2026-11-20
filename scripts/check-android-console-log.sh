#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "$0")/.." && pwd)"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/jay-console-log.XXXXXX")"
cleanup() {
  find "$work_dir" -type f -delete
  find "$work_dir" -depth -type d -exec rmdir {} +
}
trap cleanup EXIT

mkdir -p "$work_dir/classes"

cat >"$work_dir/ConsoleLogTest.java" <<'JAVA'
package nz.work.minecraft.jay;

import java.io.OutputStream;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

public final class ConsoleLogTest {
    public static void main(String[] args) throws Exception {
        require(AndroidGameBootstrap.gameplayControlsState() == -1,
            "unknown Minecraft mappings must preserve native GLFW gameplay state");
        require(AndroidGameBootstrap.textInputFocusState() == -1,
            "unknown Minecraft mappings must preserve manual soft-keyboard fallback");

        List<byte[]> messages = new ArrayList<>();
        OutputStream output = new AndroidGameBootstrap.LogcatOutputStream(
            4, (priority, message) -> messages.add(message));

        byte[] split = "世界\n".getBytes(StandardCharsets.UTF_8);
        output.write(split, 0, 2);
        output.write(split, 2, split.length - 2);
        require(messages.size() == 1, "split UTF-8 line count");
        require("世界".equals(new String(messages.get(0), StandardCharsets.UTF_8)),
            "split UTF-8 decoding");

        messages.clear();
        PrintStream print = new PrintStream(output, true, "UTF-8");
        print.println("日本語と English");
        require(messages.size() == 1, "PrintStream line count");
        require("日本語と English".equals(
            new String(messages.get(0), StandardCharsets.UTF_8)), "PrintStream UTF-8 decoding");

        messages.clear();
        StringBuilder longLine = new StringBuilder();
        for (int index = 0; index < 2000; index++) longLine.append('界');
        print.println(longLine);
        StringBuilder restored = new StringBuilder();
        for (byte[] chunk : messages) {
            require(chunk.length <= 3500, "logcat chunk exceeds byte limit");
            restored.append(new String(chunk, StandardCharsets.UTF_8));
        }
        require(longLine.toString().equals(restored.toString()), "chunked line changed");
    }

    private static void require(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}
JAVA

javac --release 8 -encoding UTF-8 -d "$work_dir/classes" \
  "$root_dir/src/Amber.Android/Java/nz/work/minecraft/jay/AndroidGameBootstrap.java" \
  "$root_dir/src/Amber.Android/Java/nz/work/minecraft/jay/SdlMenuBootstrap.java" \
  "$work_dir/ConsoleLogTest.java"
java -cp "$work_dir/classes" nz.work.minecraft.jay.ConsoleLogTest
echo "Android console UTF-8 logging checks passed."
