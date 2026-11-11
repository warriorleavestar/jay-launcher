# Jay Android Mesa/Zink runtime

Jay's Android graphics runtime is built directly from the official Mesa source
archive. The build enables the Zink Gallium driver, which translates the
desktop OpenGL API used by Minecraft Java Edition to Android's Vulkan driver.

- Mesa version: 26.2.6
- Source: https://archive.mesa3d.org/mesa-26.2.6.tar.xz
- SHA-256: `b9436b3e1a8be76fbd4823dc884f90596ab7c6ac5e1867a305e66ffccfb31a92`
- License: Mesa's MIT, SGI and Khronos notices from the source archive

This is a graphics library component, not a dependency on FCL, PojavLauncher,
Amethyst, or any other Minecraft launcher.
