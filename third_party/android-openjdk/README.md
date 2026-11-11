# Jay Android OpenJDK build

Jay needs a relocatable Android build of OpenJDK; it does not call or require another
launcher. The reproducible runtime workflow starts from these pinned sources:

- OpenJDK 21.0.12.1: `openjdk/jdk21u`, tag `jdk-21.0.12.1-ga`.
- Android cross-build patches and scripts: `FCL-Team/Android-OpenJDK-Build`, commit
  `b79a530da187dbb33d40c4ed51ddeeeb9d379360`.
- Jay's small overlay: `openjdk-android-16k.patch`, which changes only the linker
  maximum page size so the resulting native libraries work on 16 KiB Android devices.

The produced JRE includes OpenJDK's `legal/` tree, including GPL-2.0 with the Classpath
Exception and third-party notices. The workflow verifies every native library's ELF
architecture and 16 KiB `LOAD` alignment before uploading an artifact.

The build scripts are an input to the reproducible component build, not a runtime or
launcher dependency. No FCL launcher code is copied into Jay.
