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

    // These immutable release assets passed the repository's architecture, regular-file,
    // license and 16 KiB ELF LOAD-alignment validation before publication.
    private static RuntimeArchiveDescriptor Arm64 => new(
        RuntimeArchiveDescriptor.CurrentFormat,
        "jay-android-runtime21-v1-arm64-v8a",
        "arm64-v8a",
        35_343_912,
        "b7c6451afb752d2e48fab05c3ff4ac78fd205bc76bb06c7d65725abb0db1a807",
        193_179_476,
        "OpenJDK GPL-2.0 with Classpath Exception; Mesa MIT; LWJGL BSD; bundled component licenses included",
        "https://github.com/warriorleavestar/jay-launcher/tree/0c124f9/scripts",
        "https://github.com/warriorleavestar/jay-launcher/releases/download/android-runtime-v1/jay-android-runtime21-arm64-v8a.tar.xz");

    private static RuntimeArchiveDescriptor X64 => new(
        RuntimeArchiveDescriptor.CurrentFormat,
        "jay-android-runtime21-v1-x86_64",
        "x86_64",
        37_637_924,
        "ff250ae9890e595ed89c3c78fa9cacb952aa264566471b01325b4cc85132f58c",
        197_101_802,
        "OpenJDK GPL-2.0 with Classpath Exception; Mesa MIT; LWJGL BSD; bundled component licenses included",
        "https://github.com/warriorleavestar/jay-launcher/tree/0c124f9/scripts",
        "https://github.com/warriorleavestar/jay-launcher/releases/download/android-runtime-v1/jay-android-runtime21-x86_64.tar.xz");
}
