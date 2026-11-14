using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Amber.App.Services;

/// <summary>
/// 把主题包套到界面上：颜色、字号、间距、圆角、字体、背景图都写成应用级资源，盖住 Themes/Tokens.axaml 里的默认值。
/// 页面里的 token 都是 DynamicResource，所以换主题不用重启。主题里没写的 token 继续用默认值。
/// </summary>
public static class ThemeService
{
    /// <summary>Jay 自带主题的 id（设置里存这个表示不用主题包）。</summary>
    public const string DefaultId = "jay-default";

    public static ThemeStore Store { get; } = new(Path.Combine(AppSettings.DataDir, "themes"));
    private static readonly ThemeStartupGuard Guard = new(Path.Combine(AppSettings.DataDir, "theme-guard.txt"));

    /// <summary>当前套用的主题包；null 表示 Jay 默认主题。</summary>
    public static ThemePack? Current { get; private set; }

    /// <summary>这次启动因为上次主题让 Jay 崩了、或用户要求，改用了默认主题（设置页显示提示）。</summary>
    public static string? SafeModeReason { get; private set; }

    public static event Action? Changed;

    private static readonly List<string> AddedKeys = [];
    private static string _variantSetting = "system";
    private static Uri? _fontCollectionKey;
    private static Dictionary<string, string> _fontFamilies = [];
    private static Window? _mainWindow;
    private static Styles? _componentStyles;

    /// <summary>组件样式（styles.json）只套在主窗口上。主窗口创建时登记一次。</summary>
    public static void AttachMainWindow(Window window)
    {
        _mainWindow = window;
        ApplyComponentStyles();
    }

    private static void ApplyComponentStyles()
    {
        if (_mainWindow is null) return;
        if (_componentStyles is not null) _mainWindow.Styles.Remove(_componentStyles);
        _componentStyles = Current is { Styles.Count: > 0 } pack ? ThemeStyleBuilder.Build(pack) : null;
        if (_componentStyles is not null) _mainWindow.Styles.Add(_componentStyles);
    }

    /// <summary>
    /// 安全窗口（确认框、远程控制同意框和共享提示条）用 Jay 自带的配色、字号和字体：
    /// 窗口自己带一份默认 token，查资源时先找到它，主题包的值盖不到这里；组件样式本来就只加在主窗口上。
    /// </summary>
    public static void LockToDefault(Window window)
    {
        window.Resources.MergedDictionaries.Add(Controls.Safety.SafetySurface.DefaultTokens());
        window.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Jay/"))
        {
            Source = new Uri("avares://Jay/Themes/Safety.axaml"),
        });
        window.FontFamily = Avalonia.Media.FontFamily.Parse(DefaultUiFont);
        LockedWindows.Add(new WeakReference<Window>(window));
    }

    /// <summary>默认界面字体链（和 Tokens.axaml 的 UiFont 一致），锁定的窗口直接用它，主题换字体也不受影响。</summary>
    public const string DefaultUiFont = "avares://Jay/Assets/Fonts#Manrope, Segoe UI Variable Text, Segoe UI, $Default, Inter, Microsoft YaHei UI, PingFang SC, Noto Sans SC, Noto Sans CJK SC, Source Han Sans SC";

    private static readonly List<WeakReference<Window>> LockedWindows = [];

    /// <summary>这个窗口有没有锁到默认外观（自检断言用：每个安全窗口都要调用过 LockToDefault）。</summary>
    public static bool IsLocked(Window window) => LockedWindows.Any(w => w.TryGetTarget(out var x) && ReferenceEquals(x, window));

    /// <summary>启动时调用（主窗口创建之前）。</summary>
    public static void ApplyAtStartup(AppSettings settings, string[] args)
    {
        _variantSetting = settings.Theme;
        AllowNetworkThemes = settings.AllowNetworkThemes;
        // 做主题用：--theme-dev <目录>（或 AMBER_THEME_DEV=<目录>）直接用没打包的主题目录，文件一改就重新套用。不安装、不改设置。
        var devIndex = Array.IndexOf(args, "--theme-dev");
        var devDir = devIndex >= 0 && devIndex + 1 < args.Length ? args[devIndex + 1] : Environment.GetEnvironmentVariable("AMBER_THEME_DEV");
        if (devDir is { Length: > 0 } && Directory.Exists(devDir))
        {
            StartDevMode(Path.GetFullPath(devDir));
            return;
        }
        var id = settings.ThemePack;
        if (string.IsNullOrEmpty(id) || id == DefaultId) { ApplyVariant(); return; }
        if (args.Contains("--default-theme") || Environment.GetEnvironmentVariable("AMBER_DEFAULT_THEME") == "1")
        {
            SafeModeReason = "requested";
            ApplyVariant();
            return;
        }
        if (!Guard.Begin(id))
        {
            SafeModeReason = "crashed";
            ApplyVariant();
            return;
        }
        if (Store.TryLoad(id) is { } pack) Apply(pack);
        else
        {
            SafeModeReason = "missing";
            Guard.Succeeded();
            ApplyVariant();
        }
    }

    /// <summary>主题开发模式正在看的目录；不是开发模式时为空。</summary>
    public static string? DevDirectory { get; private set; }

    /// <summary>开发模式下最近一次读主题失败的原因（读成功就清空）。</summary>
    public static string? DevError { get; private set; }

    private static FileSystemWatcher? _devWatcher;
    private static Avalonia.Threading.DispatcherTimer? _devReload;

    private static void StartDevMode(string dir)
    {
        DevDirectory = dir;
        ReloadDev();
        _devReload = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _devReload.Tick += (_, _) => { _devReload.Stop(); ReloadDev(); };
        _devWatcher = new FileSystemWatcher(dir) { IncludeSubdirectories = true, EnableRaisingEvents = true };
        // 编辑器保存时会连着触发好几次：停下来 0.4 秒再读。
        void changed(object? _, FileSystemEventArgs __) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { _devReload.Stop(); _devReload.Start(); });
        _devWatcher.Changed += changed;
        _devWatcher.Created += changed;
        _devWatcher.Deleted += changed;
        _devWatcher.Renamed += (o, e) => changed(o, e);
    }

    /// <summary>重新读开发目录并套用。读失败时保留上一次能用的样子，把原因写到标准错误和设置页。</summary>
    public static void ReloadDev()
    {
        if (DevDirectory is null) return;
        try
        {
            var pack = ThemePackReader.ReadDirectory(DevDirectory);
            DevError = null;
            Apply(pack);
            foreach (var problem in pack.Problems) Console.Error.WriteLine($"[theme-dev] {problem}");
            Console.Error.WriteLine($"[theme-dev] applied {pack.Id} ({pack.Problems.Count} problems)");
        }
        catch (Exception e) when (e is ThemePackException or IOException or UnauthorizedAccessException)
        {
            DevError = e is ThemePackException t ? $"{t.Code}: {t.Detail}" : e.Message;
            Console.Error.WriteLine($"[theme-dev] read failed: {DevError}");
            Changed?.Invoke();
        }
    }

    /// <summary>主窗口已经正常显示：清掉启动保护的记录。</summary>
    public static void StartupSucceeded() => Guard.Succeeded();

    /// <summary>用户在设置里选了主题：给它一次新的启动机会。</summary>
    public static void Select(AppSettings settings, string id)
    {
        Guard.Reset();
        SafeModeReason = null;
        settings.ThemePack = id == DefaultId ? null : id;
        Apply(id == DefaultId ? null : Store.TryLoad(id));
    }

    /// <summary>深色 / 浅色 / 跟随系统的设置变了。</summary>
    public static void SetVariant(string variant)
    {
        _variantSetting = variant;
        ApplyVariant();
    }

    /// <summary>用户自己选的主题（设置里的、开发模式的）。联机网络的主题不改它，离开联机页就换回来。</summary>
    private static ThemePack? _userPack;

    /// <summary>当前联机网络配置带来的主题；不在网络里或网络没带主题时为空。</summary>
    public static ThemePack? NetworkPack { get; private set; }

    /// <summary>网络主题想要的范围：network（只在联机页）或 global（整个 Jay，要用户同意）。</summary>
    public static string NetworkScope { get; private set; } = "network";

    /// <summary>用户同意了这个网络的主题用在整个 Jay 上（只对当前这个网络主题有效）。</summary>
    public static bool NetworkGlobalAccepted { get; private set; }

    /// <summary>设置里“使用联机网络带来的主题”。关掉后网络主题一律不用，也不下载。</summary>
    public static bool AllowNetworkThemes { get; set; } = true;

    /// <summary>Whether a network's theme may be fetched at all: the setting is on and Jay wasn't started with --default-theme.</summary>
    public static bool NetworkThemesWanted => AllowNetworkThemes && SafeModeReason != "requested";

    private static bool _networkPageActive;

    public static void Apply(ThemePack? pack)
    {
        _userPack = pack;
        Refresh(force: true);
    }

    /// <summary>
    /// 联机那边拿到（或离开）一个带主题的网络时调用。网络主题从不写进设置、不进主题列表；
    /// 用 --default-theme 启动时不用。global 要等用户点头（AcceptNetworkThemeGlobally）才铺满整个 Jay，在那之前和 network 一样。
    /// </summary>
    public static void SetNetworkTheme(ThemePack? pack, string scope = "network")
    {
        if (ReferenceEquals(pack, NetworkPack) && scope == NetworkScope) return;
        NetworkPack = pack;
        NetworkScope = scope;
        NetworkGlobalAccepted = false;
        Refresh();
    }

    public static void AcceptNetworkThemeGlobally()
    {
        if (NetworkPack is null || NetworkScope != "global") return;
        NetworkGlobalAccepted = true;
        Refresh();
    }

    /// <summary>主窗口切页时调用：进入联机页时换上网络主题，离开时换回用户自己的。</summary>
    public static void SetNetworkPageActive(bool active)
    {
        if (_networkPageActive == active) return;
        _networkPageActive = active;
        Refresh();
    }

    /// <summary>窗口外壳（导航位置、顺序）用谁的：只在联机页生效的网络主题不动导航，免得一进联机页导航就跳。</summary>
    public static ShellLayout? Shell => (ReferenceEquals(Current, NetworkPack) && !NetworkGlobalAccepted ? _userPack : Current)?.Shell;

    /// <summary>现在该用哪个主题。</summary>
    public static ThemePack? Effective =>
        NetworkPack is not null && AllowNetworkThemes && SafeModeReason != "requested" && (_networkPageActive || NetworkGlobalAccepted)
            ? NetworkPack : _userPack;

    public static void Refresh(bool force = false)
    {
        var effective = Effective;
        if (!force && ReferenceEquals(effective, Current)) return;
        ApplyNow(effective);
    }

    private static void ApplyNow(ThemePack? pack)
    {
        if (Application.Current is not { } app) return;
        var resources = app.Resources;
        foreach (var key in AddedKeys) resources.Remove(key);
        AddedKeys.Clear();
        resources.ThemeDictionaries.Remove(ThemeVariant.Dark);
        resources.ThemeDictionaries.Remove(ThemeVariant.Light);
        Current = pack;
        if (pack is not null)
        {
            resources.ThemeDictionaries[ThemeVariant.Dark] = Brushes(pack.DarkColors, dark: true);
            resources.ThemeDictionaries[ThemeVariant.Light] = Brushes(pack.LightColors, dark: false);
            foreach (var (key, value) in pack.Numbers) Set(resources, key, value);
            foreach (var (key, value) in pack.Radii) Set(resources, key, new CornerRadius(value));
            if (pack.DarkColors.TryGetValue("Accent", out var accent) || pack.LightColors.TryGetValue("Accent", out accent))
                SetAccent(resources, accent);
            LoadFonts(pack);
            ApplyFonts(resources);
        }
        else UnloadFonts();
        var oldPictures = _slotPictures;
        _slotPictures = pack is null ? [] : LoadSlotPictures(pack);
        ApplyComponentStyles();
        ApplyVariant();
        SystemMotion.Reapply();
        Changed?.Invoke();
        // Everything showing the old pictures has moved to the new ones by now.
        foreach (var (bitmap, _) in oldPictures.Values) bitmap.Dispose();
    }

    private static Dictionary<string, (Bitmap Bitmap, ThemeSlot Settings)> _slotPictures = [];

    /// <summary>The picture in one of the theme's image slots, or null when the theme has none there.</summary>
    public static (Bitmap Bitmap, ThemeSlot Settings)? SlotPicture(string slot) =>
        _slotPictures.TryGetValue(slot, out var picture) ? picture : null;

    /// <summary>
    /// Decodes the slot pictures of a theme. A format 1 background image counts as the background slot (its opacity
    /// becomes how much of the page colour covers it). A broken picture leaves its slot empty and is noted on the pack.
    /// </summary>
    private static Dictionary<string, (Bitmap, ThemeSlot)> LoadSlotPictures(ThemePack pack)
    {
        var result = new Dictionary<string, (Bitmap, ThemeSlot)>();
        if (pack.Directory is null) return result;
        var slots = new Dictionary<string, ThemeSlot>(pack.Slots);
        if (!slots.ContainsKey(ThemeSlots.Background) && pack.BackgroundImage is { } legacy)
            slots[ThemeSlots.Background] = new ThemeSlot { Image = legacy, Dim = 1 - pack.BackgroundOpacity };
        foreach (var (name, settings) in slots)
        {
            if (ThemeImages.Load(Path.Combine(pack.Directory, settings.Image), name, settings) is { } bitmap) result[name] = (bitmap, settings);
            else pack.Problems.Add(new("bad_image", settings.Image));
        }
        return result;
    }

    private static void ApplyVariant()
    {
        if (Application.Current is null) return;
        // 只有一种明暗的主题：强制用那一种（深色背景的主题在浅色模式下没法看）。
        var forced = Current?.Variants is "dark" or "light" ? Current.Variants : null;
        App.ApplyTheme(forced ?? _variantSetting);
    }

    private static ResourceDictionary Brushes(Dictionary<string, uint> colors, bool dark)
    {
        var dict = new ResourceDictionary();
        foreach (var (key, argb) in colors) dict[key] = new SolidColorBrush(Color.FromUInt32(argb));
        // The wing bars follow the pack's accent. A pack that touches neither keeps the brushes from Tokens.axaml.
        if (colors.ContainsKey("Accent") || colors.ContainsKey("WingLight") || colors.ContainsKey("WingDark"))
        {
            var (accent, light, deep) = WingBars.Colors(colors, dark);
            dict["WingBrush"] = WingBrush(accent, light, deep, vertical: false);
            dict["WingBrushVertical"] = WingBrush(accent, light, deep, vertical: true);
        }
        return dict;
    }

    /// <summary>One period of the wing bars as a repeating hard-edged gradient (same shape as WingBrush in Tokens.axaml).</summary>
    internal static LinearGradientBrush WingBrush(uint accent, uint light, uint dark, bool vertical)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = vertical ? new RelativePoint(0, WingBars.Period, RelativeUnit.Absolute) : new RelativePoint(WingBars.Period, 0, RelativeUnit.Absolute),
            SpreadMethod = GradientSpreadMethod.Repeat,
        };
        foreach (var (offset, color) in WingBars.Stops(accent, light, dark)) brush.GradientStops.Add(new GradientStop(Color.FromUInt32(color), offset));
        return brush;
    }

    private static void Set(IResourceDictionary resources, string key, object value)
    {
        resources[key] = value;
        AddedKeys.Add(key);
    }

    /// <summary>Fluent 控件自己的强调色（开关、焦点框、选中项）也跟着换。</summary>
    private static void SetAccent(IResourceDictionary resources, uint accent)
    {
        var c = accent | 0xFF000000;
        Set(resources, "SystemAccentColor", Color.FromUInt32(c));
        for (var i = 1; i <= 3; i++)
        {
            Set(resources, $"SystemAccentColorLight{i}", Color.FromUInt32(ThemeColor.Mix(c, 0xFFFFFFFF, 0.12 * i)));
            Set(resources, $"SystemAccentColorDark{i}", Color.FromUInt32(ThemeColor.Mix(c, 0xFF000000, 0.14 * i)));
        }
    }

    /// <summary>
    /// 主题里的字体文件注册成一个字体集合（fonts:jaytheme-…），用的时候写成 “fonts:jaytheme-…#字体名”。
    /// 每次换主题都用新的集合名，旧主题的字体不会混进来。
    /// </summary>
    private static void LoadFonts(ThemePack pack)
    {
        UnloadFonts();
        _fontFamilies = [];
        if (pack.Directory is null) return;
        var files = pack.UiFonts.Values.Append(pack.MonoFont).Where(f => f is not null && f.StartsWith("fonts/", StringComparison.Ordinal)).Distinct().ToList();
        if (files.Count == 0) return;
        var key = new Uri($"fonts:jaytheme-{pack.Id}-{Guid.NewGuid():N}");
        var collection = new FileFontCollection(key);
        try { FontManager.Current.AddFontCollection(collection); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { pack.Problems.Add(new("font_failed", e.Message)); return; }
        _fontCollectionKey = key;
        foreach (var file in files)
        {
            var full = Path.Combine(pack.Directory, file!);
            if (collection.TryAdd(full) is { } family) _fontFamilies[file!] = $"{key}#{family}";
            else pack.Problems.Add(new("bad_font_file", file!));
        }
    }

    private static void UnloadFonts()
    {
        if (_fontCollectionKey is null) return;
        try { FontManager.Current.RemoveFontCollection(_fontCollectionKey); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { }
        _fontCollectionKey = null;
    }

    /// <summary>界面字体：当前语言指定的字体在最前，然后是主题的通用字体，最后接 Jay 默认的字体列表（缺字时回退）。</summary>
    private static void ApplyFonts(IResourceDictionary resources)
    {
        if (Current is not { } pack) return;
        string? resolve(string? value) => value is null ? null : value.StartsWith("fonts/", StringComparison.Ordinal) ? _fontFamilies.GetValueOrDefault(value) : value;
        var language = Amber.Core.Localization.L.Language;
        var names = new[] { pack.UiFonts.GetValueOrDefault(language), pack.UiFonts.GetValueOrDefault("default") }
            .Select(resolve).OfType<string>().Distinct().ToList();
        if (names.Count > 0)
        {
            var fallback = DefaultUiFont;
            Set(resources, "UiFont", new FontFamily(string.Join(", ", names) + (fallback.Length > 0 ? ", " + fallback : "")));
        }
        if (resolve(pack.MonoFont) is { } mono) Set(resources, "MonoFont", new FontFamily(mono + ", Cascadia Mono, Consolas, monospace"));
    }


    /// <summary>语言换了：按语言指定的字体也要跟着换。</summary>
    public static void OnLanguageChanged()
    {
        if (Current is null || Application.Current is not { } app) return;
        foreach (var key in new[] { "UiFont", "MonoFont" })
            if (AddedKeys.Remove(key)) app.Resources.Remove(key);
        ApplyFonts(app.Resources);
    }

    /// <summary>把当前生效的配色（默认值 + 主题覆盖）导出成 .jaytheme。</summary>
    public static void ExportCurrent(string path)
    {
        var dark = new Dictionary<string, uint>(ThemeDefaults.Dark);
        var light = new Dictionary<string, uint>(ThemeDefaults.Light);
        var numbers = new Dictionary<string, double>(ThemeDefaults.Numbers);
        var radii = new Dictionary<string, double>(ThemeDefaults.Radii);
        if (Current is { } pack)
        {
            foreach (var (k, v) in pack.DarkColors) dark[k] = v;
            foreach (var (k, v) in pack.LightColors) light[k] = v;
            foreach (var (k, v) in pack.Numbers) numbers[k] = v;
            foreach (var (k, v) in pack.Radii) radii[k] = v;
        }
        var baseId = Current?.Id ?? "my-theme";
        ThemePackWriter.Write(path, baseId.EndsWith("-copy", StringComparison.Ordinal) ? baseId : baseId + "-copy", (Current?.Name ?? "Jay") + " copy",
            dark, light, numbers, radii);
    }

    /// <summary>从文件加字体的集合。字体先过了主题读取器的文件头检查，解析交给 Avalonia（Skia）。</summary>
    private sealed class FileFontCollection(Uri key) : FontCollectionBase
    {
        public override Uri Key => key;

        public string? TryAdd(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return TryAddGlyphTypeface(stream, out var typeface) ? typeface.FamilyName : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return null; }
        }
    }
}
