using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Amber.App;

/// <summary>
/// Linux 上窗口的标题栏是窗口管理器（GNOME、KDE、WSLg……）用系统字体画的，Jay 自带的中文字体管不到。
/// 系统里没有中文/日文字体时，标题里的中日文字会变成方框，这时把含中日文的窗口标题换成「Jay」。
/// 窗口里面的文字照常用自带字体显示。
/// </summary>
internal static class LinuxFonts
{
    public const string FallbackTitle = "Jay";

    /// <summary>App 创建窗口前调用。只在 Linux 且系统没有中日文字体时挂上。</summary>
    public static void GuardWindowTitles()
    {
        if (!OperatingSystem.IsLinux() || SystemHasCjkFont()) return;
        Window.TitleProperty.Changed.AddClassHandler<Window>((window, e) =>
        {
            if (e.NewValue is string title && NeedsFallback(title))
                window.SetCurrentValue(Window.TitleProperty, FallbackTitle);
        });
    }

    /// <summary>标题里有没有中日韩文字（CJK 部首起到全角符号止，外加扩展区）。</summary>
    internal static bool NeedsFallback(string title)
    {
        foreach (var rune in title.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= 0x2E80 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFFEF or >= 0x20000 and <= 0x3FFFF)
                return true;
        }
        return false;
    }

    /// <summary>用 fontconfig 查系统里有没有支持中文或日文的字体。查不了时当作没有。</summary>
    internal static bool SystemHasCjkFont()
    {
        try
        {
            return CountFonts(":lang=zh") > 0 || CountFonts(":lang=ja") > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static int CountFonts(string pattern)
    {
        var p = FcNameParse(pattern);
        if (p == IntPtr.Zero) return 0;
        var os = FcObjectSetCreate();
        try
        {
            FcObjectSetAdd(os, "family");
            var set = FcFontList(IntPtr.Zero, p, os);
            if (set == IntPtr.Zero) return 0;
            try { return Marshal.ReadInt32(set); } // FcFontSet 的第一个字段 nfont。
            finally { FcFontSetDestroy(set); }
        }
        finally
        {
            FcObjectSetDestroy(os);
            FcPatternDestroy(p);
        }
    }

    private const string Lib = "libfontconfig.so.1";

    [DllImport(Lib)] private static extern IntPtr FcNameParse(string name);
    [DllImport(Lib)] private static extern IntPtr FcObjectSetCreate();
    [DllImport(Lib)] private static extern int FcObjectSetAdd(IntPtr os, string obj);
    [DllImport(Lib)] private static extern IntPtr FcFontList(IntPtr config, IntPtr pattern, IntPtr os);
    [DllImport(Lib)] private static extern void FcFontSetDestroy(IntPtr set);
    [DllImport(Lib)] private static extern void FcObjectSetDestroy(IntPtr os);
    [DllImport(Lib)] private static extern void FcPatternDestroy(IntPtr p);
}
