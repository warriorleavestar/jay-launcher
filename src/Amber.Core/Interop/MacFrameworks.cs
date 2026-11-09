using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Amber.Core.Interop;

/// <summary>
/// macOS 系统框架的 P/Invoke 库名。DllImport 里不能直接写 /System/Library/Frameworks/… 路径：
/// iOS 版也引用 Amber.Core，iOS 的链接器会把这种路径当成要链接的框架，而 ApplicationServices、Carbon 在 iOS 上没有，整个 iOS 构建失败。
/// 所以用这里的假名字，运行时（只在 macOS 上）换成真路径；iOS 上这些函数从来不会被调用。
/// </summary>
internal static class MacFrameworks
{
    public const string CoreGraphics = "JayMac.CoreGraphics";
    public const string CoreFoundation = "JayMac.CoreFoundation";
    public const string ApplicationServices = "JayMac.ApplicationServices";
    public const string Carbon = "JayMac.Carbon";

    private const string Prefix = "JayMac.";

    /// <summary>假名字 → 框架里的二进制路径；不是假名字时 null。</summary>
    internal static string? PathOf(string libraryName) =>
        libraryName.StartsWith(Prefix, StringComparison.Ordinal) && libraryName.Length > Prefix.Length
            ? $"/System/Library/Frameworks/{libraryName[Prefix.Length..]}.framework/{libraryName[Prefix.Length..]}"
            : null;

    // 一个程序集只能设一次解析器；以后 Amber.Core 还要别的解析规则时加在 Resolve 里。
    [ModuleInitializer]
    internal static void Register()
    {
        if (OperatingSystem.IsMacOS()) NativeLibrary.SetDllImportResolver(typeof(MacFrameworks).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        PathOf(libraryName) is { } path && NativeLibrary.TryLoad(path, out var handle) ? handle : 0;
}
