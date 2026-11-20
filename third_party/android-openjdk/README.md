# Jay Android OpenJDK build

Jay needs a relocatable Android build of OpenJDK; it does not call or require another
launcher. The reproducible runtime workflow starts from these pinned sources:

- OpenJDK 25: `openjdk/jdk25u`, fetched by the pinned Android build scripts.
- Android cross-build patches and scripts: `FCL-Team/Android-OpenJDK-Build`, commit
  `7a0266e745d9b4acf400afa189b58e672900f710` from its JRE 25 line.
- Jay's small overlay script: `scripts/prepare-android-openjdk.sh`, which verifies and
  changes only the linker maximum page size so the resulting native libraries work on
  16 KiB Android devices.

The produced JRE includes OpenJDK's `legal/` tree, including GPL-2.0 with the Classpath
Exception and third-party notices. Before adding the renderer, the workflow verifies that
the JRE is Java 25 for the requested ABI and checks every JRE native library's 16 KiB
`LOAD` alignment. It repeats those checks after assembling the complete game runtime and
also requires every renderer and LWJGL library before uploading an artifact.

The build scripts are an input to the reproducible component build, not a runtime or
launcher dependency. No FCL launcher code is copied into Jay.
