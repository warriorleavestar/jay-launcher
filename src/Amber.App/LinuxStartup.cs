using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Amber.App;

/// <summary>
/// Linux 上启动前的检查，免得在精简的系统上一声不响地崩掉：
///   1. 没装 ICU（libicu）时 .NET 一用到区域信息就直接 FailFast。这里在任何区域代码运行前先找 ICU，
///      找不到就带上「不变区域」的环境变量重新执行自己（execv，进程号不变），界面语言再按 LANG 选。
///   2. 缺 X11 相关的库（libX11、libICE、libSM……）或 fontconfig 时，Avalonia 抛 DllNotFoundException，
///      双击启动的用户什么都看不到。这里先查一遍，缺了就写到标准错误，并尽量用系统自带的对话框工具弹出来。
/// 只在 Linux 上做事，其他系统直接返回。
/// </summary>
internal static class LinuxStartup
{
    /// <summary>重新执行时加上的标记，避免反复重启。</summary>
    private const string ReexecMarker = "JAY_ICU_FALLBACK";

    /// <summary>界面要用的原生库和对应的软件包名（Debian/Ubuntu，Fedora，Arch）。</summary>
    internal static readonly (string Library, string Apt, string Dnf, string Pacman)[] RequiredLibraries =
    [
        ("libX11.so.6", "libx11-6", "libX11", "libx11"),
        ("libICE.so.6", "libice6", "libICE", "libice"),
        ("libSM.so.6", "libsm6", "libSM", "libsm"),
        ("libfontconfig.so.1", "libfontconfig1", "fontconfig", "fontconfig"),
    ];

    /// <summary>不预先检查、但 Avalonia 用到时缺了也要能给出包名的库。</summary>
    private static readonly (string Library, string Apt, string Dnf, string Pacman)[] OtherLibraries =
    [
        ("libXi.so.6", "libxi6", "libXi", "libxi"),
        ("libXcursor.so.1", "libxcursor1", "libXcursor", "libxcursor"),
        ("libXrandr.so.2", "libxrandr2", "libXrandr", "libxrandr"),
        ("libXext.so.6", "libxext6", "libXext", "libxext"),
        ("libXfixes.so.3", "libxfixes3", "libXfixes", "libxfixes"),
    ];

    /// <summary>Main 的第一行调用。必须在任何用到 CultureInfo、字符串格式化的代码之前。</summary>
    public static void BeforeAnything(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return;
        EnsureIcuOrReexec(args);
        if (IcuFallbackActive) ApplyLanguageFromEnvironment();
        var missing = MissingLibraries(name => NativeLibrary.TryLoad(name, out var handle) && Free(handle));
        if (missing.Count > 0)
        {
            ReportFatal(MissingLibrariesMessage(missing, SystemLanguage()));
            Environment.Exit(1);
        }
    }

    /// <summary>Avalonia 启动时还是缺库（比如上面没列的 libXi），给出同样看得懂的提示。</summary>
    public static bool TryReportStartupFailure(Exception e)
    {
        if (!OperatingSystem.IsLinux() || e is not DllNotFoundException dll) return false;
        var library = RequiredLibraries.Concat(OtherLibraries).Select(r => r.Library).FirstOrDefault(l => dll.Message.Contains(l, StringComparison.Ordinal))
                      ?? ExtractLibraryName(dll.Message);
        ReportFatal(MissingLibrariesMessage([library], SystemLanguage()));
        return true;
    }

    /// <summary>这一次是不是在没有 ICU 的「不变区域」模式下运行。</summary>
    public static bool IcuFallbackActive => Environment.GetEnvironmentVariable(ReexecMarker) == "1";

    private static bool Free(IntPtr handle)
    {
        NativeLibrary.Free(handle);
        return true;
    }

    // ---- ICU ----

    private static void EnsureIcuOrReexec(string[] args)
    {
        // 用户或打包者已经自己选了模式，就不插手。
        if (IsTrue(Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"))
            || Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU") is { Length: > 0 }
            || Environment.GetEnvironmentVariable(ReexecMarker) is { Length: > 0 })
            return;
        if (IcuAvailable()) return;
        var self = Environment.ProcessPath;
        if (self is null) return;
        setenv("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1", 1);
        // 允许按名字创建 zh-CN 这类区域（数据用不变区域的），翻译表里的格式化区域才不会抛异常。
        setenv("DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY", "0", 1);
        setenv(ReexecMarker, "1", 1);
        var argv = new string?[args.Length + 2];
        argv[0] = self;
        Array.Copy(args, 0, argv, 1, args.Length);
        argv[^1] = null;
        execv(self, argv);
        // execv 只有失败才会返回；继续往下走就会被 .NET 的 FailFast 打断，先把原因说清楚。
        // 这时还不能用翻译表（它要用区域信息），只写英文。
        Console.Error.WriteLine("Jay: ICU (libicu) was not found and Jay could not restart itself in compatibility mode. Please install libicu (Ubuntu/Debian: sudo apt install libicu78, or the libicuNN package of your release).");
    }

    /// <summary>和 .NET 一样按 libicuuc.so.主版本号 找（50 到 99），再试不带版本号的。不能用字符串插值：那会用到当前区域。</summary>
    private static bool IcuAvailable()
    {
        foreach (var name in IcuCandidates())
        {
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        return false;
    }

    internal static IEnumerable<string> IcuCandidates()
    {
        for (var major = 99; major >= 50; major--)
            yield return string.Concat("libicuuc.so.", new string([(char)('0' + major / 10), (char)('0' + major % 10)]));
        yield return "libicuuc.so";
    }

    private static bool IsTrue(string? value) => value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // ---- 不变区域模式下的界面语言 ----

    /// <summary>不变区域模式下 CurrentUICulture 永远是不变区域，界面会退成英文；按 LANG 这几个变量补回来。</summary>
    private static void ApplyLanguageFromEnvironment()
    {
        var code = LanguageFromLocale(LocaleFromEnvironment());
        var culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private static string? LocaleFromEnvironment()
    {
        foreach (var name in new[] { "LC_ALL", "LC_MESSAGES", "LANG" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) return value;
        return null;
    }

    /// <summary>POSIX 区域名（zh_CN.UTF-8、zh_TW、ja_JP@xxx）换成界面支持的语言代码；认不出时用英语。</summary>
    internal static string LanguageFromLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return "en";
        var value = locale.Trim();
        var cut = value.IndexOfAny(['.', '@']);
        if (cut >= 0) value = value[..cut];
        value = value.Replace('_', '-');
        if (value.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            var traditional = value.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) || value.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                              || value.EndsWith("-MO", StringComparison.OrdinalIgnoreCase) || value.Contains("Hant", StringComparison.OrdinalIgnoreCase);
            return traditional ? "zh-Hant" : "zh-Hans";
        }
        if (value.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
        return "en";
    }

    /// <summary>提示用的界面语言：按 LANG 等环境变量选（这时设置还没加载）。</summary>
    private static string SystemLanguage() => LanguageFromLocale(LocaleFromEnvironment());

    // ---- 缺库提示 ----

    internal static List<string> MissingLibraries(Func<string, bool> canLoad) =>
        RequiredLibraries.Select(r => r.Library).Where(l => !canLoad(l)).ToList();

    /// <summary>缺库提示（按 language 指定的界面语言）。没认出的库也照样列出，安装命令给出全部需要的包。</summary>
    internal static string MissingLibrariesMessage(IReadOnlyList<string> missing, string language)
    {
        var known = RequiredLibraries.Concat(OtherLibraries).Where(r => missing.Contains(r.Library)).ToList();
        if (known.Count == 0) known = [.. RequiredLibraries];
        // 不改全局的界面语言，直接取那种语言的翻译表。
        var key = "linux.missing_libraries";
        var template = Amber.Core.Localization.L.Load(language).TryGetValue(key, out var text) ? text : Amber.Core.Localization.L.Load(Amber.Core.Localization.L.Source)[key];
        return string.Format(CultureInfo.InvariantCulture, template, string.Join(", ", missing),
            string.Join(' ', known.Select(r => r.Apt)), string.Join(' ', known.Select(r => r.Dnf)), string.Join(' ', known.Select(r => r.Pacman)));
    }

    internal static string ExtractLibraryName(string message)
    {
        // DllNotFoundException 的消息形如 "Unable to load shared library 'libXi.so.6' or one of its dependencies…"
        var start = message.IndexOf('\'');
        var end = start >= 0 ? message.IndexOf('\'', start + 1) : -1;
        return start >= 0 && end > start ? message[(start + 1)..end] : message.Split('\n')[0];
    }

    /// <summary>写到标准错误；从文件管理器双击启动时没有终端，再试系统自带的对话框工具。</summary>
    private static void ReportFatal(string message)
    {
        try { Console.Error.WriteLine(message); } catch (IOException) { }
        if (Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 } && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not { Length: > 0 })
            return;
        (string Tool, string[] Args)[] tools =
        [
            ("zenity", ["--error", "--title=Jay", "--no-markup", "--text", message]),
            ("kdialog", ["--title", "Jay", "--error", message]),
            ("xmessage", ["-center", message]),
            ("notify-send", ["-u", "critical", "Jay", message]),
        ];
        foreach (var (tool, toolArgs) in tools)
        {
            try
            {
                // 工具自己的报错（比如它也缺同一个库）不混进上面的提示里。
                var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in toolArgs) start.ArgumentList.Add(a);
                using var process = Process.Start(start);
                if (process is null) continue;
                _ = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                if (process.ExitCode is 0 or 1) return; // zenity/kdialog 关掉对话框时返回 0 或 1。
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    [DllImport("libc", SetLastError = true)]
    private static extern int execv(string path, string?[] argv);
}
