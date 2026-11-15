# Jay Android OpenJDK build

Jay needs a relocatable Android build of OpenJDK; it does not call or require another
launcher. The reproducible runtime workflow starts from these pinned sources:

- OpenJDK 21.0.12.1: `openjdk/jdk21u`, tag `jdk-21.0.12.1-ga`.
- Android cross-build patches and scripts: `FCL-Team/Android-OpenJDK-Build`, commit
  `b79a530da187dbb33d40c4ed51ddeeeb9d379360`.
- Jay's small overlay script: `scripts/prepare-android-openjdk.sh`, which verifies and
  changes only the linker maximum page size so the resulting native libraries work on
  16 KiB Android devices. It also pins every input the upstream scripts download: the
  OpenJDK tag must resolve to commit `eac07229cf6cacc8a22eb79f4929e7fed61338b7`, and the
  Android NDK r21, the boot JDK 20, FreeType 2.10.0, CUPS 2.2.4 and termux-elf-cleaner
  v2.2.0 are checked against recorded SHA-256 values or commits before they are used.
- `scripts/check-android-runtime.sh` checks that the built runtime reports
  `JAVA_VERSION="21.0.12.1"`.

The produced JRE includes OpenJDK's `legal/` tree, including GPL-2.0 with the Classpath
Exception and third-party notices. The workflow verifies every native library's ELF
architecture and 16 KiB `LOAD` alignment before uploading an artifact.

The build scripts are an input to the reproducible component build, not a runtime or
launcher dependency. No FCL launcher code is copied into Jay.
