# Android shader tools

Minecraft 26.3's renderer loads the Shaderc and SPIRV-Cross C APIs through
LWJGL. `scripts/build-android-shader-tools.sh` builds those two shared objects
without using code from another launcher:

- Shaderc is built from the Android-maintained source bundled with the pinned
  NDK 28.2.13676358. Its Apache-2.0 and dependency notices are copied from the
  NDK's `NOTICE` file beside the artifact.
- SPIRV-Cross is built from KhronosGroup commit
  `6c09849fe88c48eaed08413aa022aaa136a3a057`; the downloaded source archive is
  SHA-256 checked and the upstream license is copied beside the artifact.

Both libraries are built for API 26, checked for the requested ABI and linked
with 16 KiB ELF `LOAD` alignment before they can enter a runtime bundle.
