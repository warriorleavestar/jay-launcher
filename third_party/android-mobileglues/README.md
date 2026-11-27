# Jay Android MobileGlues renderer

Jay builds MobileGlues directly from the upstream source at commit
`0c27a3a8b3f51babd59fcca8ff59f28cbee1eecd`. MobileGlues implements the
desktop OpenGL API used by Minecraft Java Edition on top of Android OpenGL ES.

- Source: https://github.com/MobileGL-Dev/MobileGlues
- License: GNU LGPL-2.1-only
- Integration: dynamically loaded shared library, with the complete license and
  pinned corresponding source location included in Jay's runtime metadata

`jay-android.patch` keeps MobileGlues' tiny bootstrap EGL context alive for the
Android process lifetime. It also legalizes dynamic indices into fragment-output
arrays after translation, including cached shaders and component-indexed writes;
OpenGL ES requires the attachment index to be a constant expression.
The graphics runtime workflow runs the focused host regression test included in
the patch before cross-compiling the Android library.

`emulator-angle.patch` lets Jay explicitly select the APK's standalone ANGLE
libraries on emulator hardware. The Android build fetches the four EGL/GLES binaries
listed in `build/pins.json` and ships their BSD license alongside the SDL license. This avoids presenting an incomplete translated
frame while preserving MobileGlues' default renderer selection on physical
devices.

This is an independently integrated graphics component. Jay does not contain,
launch, or depend on the FCL application.
