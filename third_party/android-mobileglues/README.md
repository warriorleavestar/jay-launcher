# Jay Android MobileGlues renderer

Jay builds MobileGlues directly from the upstream source at commit
`97558a69157b033b890958b1e49258c5b51a80c6`. MobileGlues implements the
desktop OpenGL API used by Minecraft Java Edition on top of Android OpenGL ES.

- Source: https://github.com/MobileGL-Dev/MobileGlues
- License: GNU LGPL-2.1-only
- Integration: dynamically loaded shared library, with the complete license and
  pinned corresponding source location included in Jay's runtime metadata

`jay-android.patch` keeps MobileGlues' tiny bootstrap EGL context alive for the
Android process lifetime. Android HWUI and the game share the process-wide EGL
driver on some devices; destroying the bootstrap context can invalidate driver
bookkeeping still used by HWUI.

This is an independently integrated graphics component. Jay does not contain,
launch, or depend on the FCL application.
