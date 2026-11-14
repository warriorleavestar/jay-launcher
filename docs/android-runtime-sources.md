# Android game runtime: sources and checksums

The Android app downloads one runtime archive per CPU architecture the first time you open the game
screen, and only when you tap the install button. The archive is pinned in
`src/Amber.Android/AndroidRuntimeCatalog.cs` by size and SHA-256, and every file inside it is checked
before the runtime is used. This page lists what is inside the archive, where each part's source comes
from and under which licence it is distributed. Each component's full licence text ships inside the
archive next to it.

## Archives

| Architecture | Size | SHA-256 |
|---|---|---|
| arm64-v8a | 29,411,052 bytes | `7c87d6de0374190988aaf89fa5e2542517f77988146330090a3aae9c6d80bb95` |
| x86_64 | 31,308,180 bytes | `1cec903c2c8ba4ac177b71c0984dac9239e2dcffda243c69f11c8c50a68949fe` |

The archive is assembled by `scripts/assemble-android-runtime.sh` from the three component builds below.
The `android-runtime-release` workflow (`.github/workflows/android-runtime-release.yml`) builds all
three from the pinned sources, assembles both architectures and publishes the archives with their
`.sha256` and size files under a new tag that is never rewritten. The archives listed above were
assembled before that workflow existed; the next runtime update replaces them with a workflow-built
release and updates this table and the catalog together.

## Components

| Component | Licence | Pinned source | Build |
|---|---|---|---|
| OpenJDK 21.0.12.1 (Java runtime) | GPL-2.0 with the Classpath Exception; third-party notices in `legal/` | `openjdk/jdk21u` tag `jdk-21.0.12.1-ga`, commit `eac07229cf6cacc8a22eb79f4929e7fed61338b7`; Android build scripts and patches from `FCL-Team/Android-OpenJDK-Build` commit `b79a530da187dbb33d40c4ed51ddeeeb9d379360` | `.github/workflows/android-runtime.yml`, `scripts/prepare-android-openjdk.sh` |
| MobileGlues (OpenGL on OpenGL ES) | LGPL-2.1-only | `MobileGL-Dev/MobileGlues` commit `97558a69157b033b890958b1e49258c5b51a80c6`, plus `third_party/android-mobileglues/jay-android.patch` | `.github/workflows/android-graphics-runtime.yml`, `scripts/build-android-mobileglues.sh` |
| LWJGL 3.3.3 native bindings | BSD-3-Clause | `LWJGL/lwjgl3` commit `f0ec1437a6149dae00eaa27c47268e5e4efb129f`, archive SHA-256 `388f5e64c708486cd6693adf40bb0bb01a51b83026298ad19eb764fa6f460784` | `.github/workflows/android-lwjgl-runtime.yml`, `scripts/build-android-lwjgl.sh` |
| libffi 3.4.4 | MIT | release archive SHA-256 `d66c56ad259a82cf2a9dfc408b32bf5da52371500b84745f7fb8b645712df676` | same as LWJGL |
| FreeType | FreeType License or GPL-2.0-or-later | `freetype/freetype` commit `45903920b984540bb629bc89f4c010159c23a89a`, archive SHA-256 `ce4fe730699b48d643df448655a37ac13b361e7225fec843637a30f1b4ae49e3` | same as LWJGL |
| OpenAL Soft | LGPL-2.0-or-later | `kcat/openal-soft` commit `d9fed51aa6391debc31dbbca550f055c980afe70`, archive SHA-256 `e6212437f103d15543cf4a478ef6c9a49b0dd5cff19c9f1a71183ab16a6da3fb` | same as LWJGL |

The GLFW bridge is not part of this archive: it is built from `glfw/glfw` commit
`7b6aead9fb88b3623e3b3725ebb42670cbe4c579` with `third_party/android-glfw/jay-android.patch` and ships
inside the APK (zlib licence).

To get the corresponding source of any component, fetch the commit or archive listed above and apply the
patch named next to it; the build scripts show every step.
