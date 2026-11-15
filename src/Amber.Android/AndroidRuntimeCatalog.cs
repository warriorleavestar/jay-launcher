using Amber.Mobile;
using System.Runtime.InteropServices;

namespace Amber.Android;

/// <summary>Immutable, release-pinned Android runtime metadata selected for this process ABI.</summary>
internal static class AndroidRuntimeCatalog
{
    public static RuntimeArchiveDescriptor? ForCurrentProcess() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => Arm64,
        Architecture.X64 => X64,
        _ => null
    };

    // Every component's licence; the full texts ship inside the archive.
    private const string License =
        "OpenJDK GPL-2.0 with Classpath Exception; MobileGlues LGPL-2.1-only; LWJGL BSD-3-Clause; " +
        "OpenAL Soft LGPL-2.0-or-later; FreeType FTL or GPL-2.0-or-later; libffi MIT";

    // Pinned source commits and archive checksums of every component.
    public const string SourceUrl =
        "https://github.com/warriorleavestar/jay-launcher/blob/main/docs/android-runtime-sources.md";

    // The installer verifies the archive size and SHA-256 below and every file inside it before use.
    // These android-runtime-v1 assets were assembled before .github/workflows/android-runtime-release.yml
    // existed, so no workflow run reproduces them (and their ids say v2). Replace them with the values a
    // run of that workflow writes to its summary: new tag, id, URL, sizes and SHA-256 together.
    private static RuntimeArchiveDescriptor Arm64 => new(
        RuntimeArchiveDescriptor.CurrentFormat,
        "jay-android-runtime21-v2-arm64-v8a",
        "arm64-v8a",
        29_411_052,
        "7c87d6de0374190988aaf89fa5e2542517f77988146330090a3aae9c6d80bb95",
        150_733_830,
        License,
        SourceUrl,
        "https://github.com/warriorleavestar/jay-launcher/releases/download/android-runtime-v1/jay-android-runtime21-arm64-v8a.tar.xz");

    private static RuntimeArchiveDescriptor X64 => new(
        RuntimeArchiveDescriptor.CurrentFormat,
        "jay-android-runtime21-v2-x86_64",
        "x86_64",
        31_308_180,
        "1cec903c2c8ba4ac177b71c0984dac9239e2dcffda243c69f11c8c50a68949fe",
        155_409_052,
        License,
        SourceUrl,
        "https://github.com/warriorleavestar/jay-launcher/releases/download/android-runtime-v1/jay-android-runtime21-x86_64.tar.xz");
}
