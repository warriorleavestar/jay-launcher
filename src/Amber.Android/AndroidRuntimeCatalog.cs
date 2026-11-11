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

    // Filled only from artifacts that passed the repository's architecture, license and
    // 16 KiB alignment workflow. Zero-size placeholders are intentionally not accepted.
    private static RuntimeArchiveDescriptor? Arm64 => null;
    private static RuntimeArchiveDescriptor? X64 => null;
}
