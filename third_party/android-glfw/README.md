# Jay Android GLFW bridge

Jay builds GLFW 3.4 from the upstream `glfw/glfw` source at commit
`7b6aead9fb88b3623e3b3725ebb42670cbe4c579` and applies `jay-android.patch`.
The patch connects GLFW's EGL-capable Null backend to Jay's owned Android
`SurfaceView` and touch-input bridge. It does not contain or call another
Minecraft launcher.

`android-egl-es.patch` keeps Minecraft's desktop OpenGL-facing GLFW contract,
while creating the native Android context through OpenGL ES 3. MobileGlues then
translates those desktop GL entry points onto that context. This is necessary
because Android EGL does not advertise native desktop OpenGL configurations.

GLFW is distributed under the zlib/libpng license in `LICENSE.md`. The Jay
patch is part of Jay and is distributed under Jay's MIT license.
