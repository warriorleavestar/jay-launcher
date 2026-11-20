#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "$0")/.." && pwd)"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/jay-console-log.XXXXXX")"
cleanup() {
  find "$work_dir" -type f -delete
  find "$work_dir" -depth -type d -exec rmdir {} +
}
trap cleanup EXIT

mkdir -p "$work_dir/android/util" "$work_dir/classes"
cat >"$work_dir/android/util/Log.java" <<'JAVA'
package android.util;

import java.util.ArrayList;
import java.util.List;

public final class Log {
    public static final List<String> messages = new ArrayList<>();

    private Log() {}

    public static int println(int priority, String tag, String message) {
        messages.add(message);
        return message.length();
    }
}
JAVA

cat >"$work_dir/ConsoleLogTest.java" <<'JAVA'
import android.util.Log;
import java.io.OutputStream;
import java.io.PrintStream;
import java.lang.reflect.Constructor;
import java.nio.charset.StandardCharsets;

public final class ConsoleLogTest {
    public static void main(String[] args) throws Exception {
        Class<?> type = Class.forName(
            "nz.work.minecraft.jay.AndroidGameBootstrap$LogcatOutputStream");
        Constructor<?> constructor = type.getDeclaredConstructor(int.class);
        constructor.setAccessible(true);
        OutputStream output = (OutputStream)constructor.newInstance(4);

        byte[] split = "世界\n".getBytes(StandardCharsets.UTF_8);
        output.write(split, 0, 2);
        output.write(split, 2, split.length - 2);
        require(Log.messages.size() == 1, "split UTF-8 line count");
        require("世界".equals(Log.messages.get(0)), "split UTF-8 decoding");

        Log.messages.clear();
        PrintStream print = new PrintStream(output, true, "UTF-8");
        print.println("日本語と English");
        require(Log.messages.size() == 1, "PrintStream line count");
        require("日本語と English".equals(Log.messages.get(0)), "PrintStream UTF-8 decoding");

        Log.messages.clear();
        StringBuilder longLine = new StringBuilder();
        for (int index = 0; index < 2000; index++) longLine.append('界');
        print.println(longLine);
        StringBuilder restored = new StringBuilder();
        for (String chunk : Log.messages) {
            require(chunk.getBytes(StandardCharsets.UTF_8).length <= 3500,
                "logcat chunk exceeds byte limit");
            restored.append(chunk);
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
  "$work_dir/android/util/Log.java" \
  "$work_dir/ConsoleLogTest.java"
java -cp "$work_dir/classes" ConsoleLogTest
echo "Android console UTF-8 logging checks passed."
