# Android LWJGL native bindings

Jay builds the JNI libraries required by Minecraft directly from the pinned,
official LWJGL 3.4.3 source and official libffi 3.5.2 source. The current target
is Minecraft 26.3 and newer, which use Java 25 and LWJGL 3.4.3/SDL3. FreeType
and OpenAL Soft are built from the pinned upstream sources. It does not use or
depend on another Minecraft launcher. The script verifies every source archive
and Android 16 KiB ELF alignment.

LWJGL 3.4.3 also binds libffi's newer reusable-call-plan API. libffi 3.5.2
predates those four entry points, so Jay provides a small portable compatibility
implementation that retains the prepared CIF and delegates calls to `ffi_call`.

The resulting libraries remain covered by the upstream BSD-3-Clause LWJGL
license and the upstream libffi license, both copied beside every artifact.

Minecraft 26.3's Vulkan backend additionally requires the LWJGL VMA JNI
binding. `scripts/build-android-lwjgl-vma.sh` builds it from the same pinned
LWJGL 3.4.3 source, for either supported Android ABI, and verifies its 16 KiB
ELF alignment and allocator entry point.
