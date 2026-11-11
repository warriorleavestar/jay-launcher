# Android LWJGL native bindings

Jay builds the JNI libraries required by Minecraft directly from the pinned,
official LWJGL 3.3.3 source and official libffi 3.4.4 source. The first playable
target is Minecraft 1.21.11, the newest release that uses Java 21 and LWJGL
3.3.3/GLFW. FreeType and OpenAL Soft are built from the same upstream commits
used by LWJGL's 3.3.3 artifacts. It does not use or depend on another Minecraft
launcher. The script verifies every source archive and Android 16 KiB ELF
alignment.

The resulting libraries remain covered by the upstream BSD-3-Clause LWJGL
license and the upstream libffi license, both copied beside every artifact.
