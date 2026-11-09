using Amber.Core;
using System.Text.Json;
using Amber.Core.Launch;

namespace Amber.App.Services;

/// <summary>启动器设置，保存在系统应用数据目录的 Amber/settings.json。</summary>
public sealed class AppSettings
{
    public string GameDirectory { get; set; } = Amber.Core.Game.GameDirectory.DefaultRoot();

    /// <summary>auto / official / mirror</summary>
    public string DownloadSource { get; set; } = "auto";

    /// <summary>同时下载的文件数。</summary>
    public int DownloadConcurrency { get; set; } = 32;

    /// <summary>为空表示按版本要求自动选择。</summary>
    public string? JavaPath { get; set; }

    public bool AutoJavaDownload { get; set; } = true;
    public bool AutoMemory { get; set; } = true;
    public int MaxMemoryMb { get; set; } = 4096;
    public bool IsolateVersions { get; set; } = true;

    /// <summary>无痕模式：游戏日志（logs/latest.log）里不写聊天行。默认关。</summary>
    public bool NoTrace { get; set; }
    public string PlayerName { get; set; } = "Player";
    public string? LastVersion { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool Fullscreen { get; set; }
    public string? JvmArgs { get; set; }
    public string? GameArgs { get; set; }
    public string? PreLaunchCommand { get; set; }
    public string? PostExitCommand { get; set; }

    /// <summary>可选：覆盖微软应用 ID。留空时读环境变量 AMBER_MS_CLIENT_ID，再没有就用内置默认值。</summary>
    public string? MicrosoftClientId { get; set; }

    /// <summary>用户自己申请的 CurseForge API Key，留空时读环境变量 AMBER_CF_KEY。</summary>
    public string? CurseForgeApiKey { get; set; }

    /// <summary>CurseForge 接口地址。留空用官方。可以指向用户信任的代理或镜像。</summary>
    public string? CurseForgeApiRoot { get; set; }

    /// <summary>更新检查地址。留空就不检查，启动器不会为此联网。</summary>
    public string? UpdateFeedUrl { get; set; }

    /// <summary>system / light / dark</summary>
    public string Theme { get; set; } = "system";

    /// <summary>导入的界面主题包 id（{数据目录}/themes/{id}）；null 表示 Jay 默认主题。</summary>
    public string? ThemePack { get; set; }

    /// <summary>联机网络配置里带的主题：在联机页里使用（整个 Jay 要另外同意）。关掉就一律不用。</summary>
    public bool AllowNetworkThemes { get; set; } = true;

    /// <summary>界面语言：zh-Hans / zh-Hant / en / ja；null 表示跟随系统。</summary>
    public string? Language { get; set; }

    /// <summary>启动器窗口上次的位置和大小（逻辑像素，按当时那块屏幕记）。</summary>
    public WindowPlacement? LauncherWindow { get; set; }

    /// <summary>用户自己添加的 EasyTier 中继节点。</summary>
    public List<string>? MultiplayerPeers { get; set; }

    /// <summary>除了自己的节点，也连 Jay 内置的节点（默认开）。</summary>
    public bool UseBuiltInNodes { get; set; } = true;

    /// <summary>Jay 公共网络的种子节点（程序里内置的之外，自己加的；比如朋友的 VPS 上跑的 jay-server directory）。</summary>
    public List<string>? PublicNetworkNodes { get; set; }

    /// <summary>自己加的别的公共网络（连接串原文，每次读出来都重新验签）。</summary>
    public List<string>? PublicNetworks { get; set; }

    /// <summary>现在用哪个公共网络（编号）；空 = 内置的 Jay 公共网络。</summary>
    public string? PublicNetworkId { get; set; }

    /// <summary>看过并同意了公共网络的隐私说明（第一次连公共网络时显示）。</summary>
    public bool PublicNetworkNoticeAccepted { get; set; }

    /// <summary>联机设备验证：off / on / required（必须：不支持验证的人不让进我开的房间）。</summary>
    public string SecurityAttestation { get; set; } = "on";

    /// <summary>熔断：房间里确认有人冒充时，所有人下线，倒计时后重启 Jay。</summary>
    public bool SecurityBreaker { get; set; } = true;

    /// <summary>熔断后重启前的倒计时（秒），倒计时里按空格可以取消。</summary>
    public int BreakerRestartDelay { get; set; } = Amber.Core.Multiplayer.Transport.SecurityPolicy.DefaultRestartDelay;

    /// <summary>熔断后不等倒计时直接重启。</summary>
    public bool BreakerSkipDelay { get; set; }

    /// <summary>上次因为熔断自动重启的时间：10 分钟内不再自动重启，免得反复熔断变成反复重启。</summary>
    public DateTimeOffset? LastBreakerRestart { get; set; }

    /// <summary>本机设置给出的安全策略（公开网络的最低要求再用 Tighten 合进来）。</summary>
    public Amber.Core.Multiplayer.Transport.SecurityPolicy LocalSecurityPolicy() => new(
        SecurityAttestation switch { "off" => Amber.Core.Multiplayer.Transport.AttestationMode.Off, "required" => Amber.Core.Multiplayer.Transport.AttestationMode.Required, _ => Amber.Core.Multiplayer.Transport.AttestationMode.On },
        SecurityBreaker,
        Math.Clamp(BreakerRestartDelay, 0, Amber.Core.Multiplayer.Transport.SecurityPolicy.MaxRestartDelay),
        BreakerSkipDelay);

    /// <summary>最近玩过的实例 id，最近的在前（启动页的“最近游玩”）。</summary>
    public List<string>? RecentInstances { get; set; }

    /// <summary>侧边栏手动收成图标栏。</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>减少动画（页面切换、抽屉滑入这些都不做过渡）。</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>启动游戏后启动器怎么办：minimize（最小化，默认）/ keep（保持）/ tray（隐藏到托盘）。</summary>
    public string AfterLaunch { get; set; } = "minimize";

    /// <summary>打开启动器时检查一次更新（只在设置导航上显示提示，不自动下载）。</summary>
    public bool CheckUpdatesOnStart { get; set; } = true;

    /// <summary>实验功能“烘焙”：Fabric / Quilt 实例启动前先用通用加载器预先处理，启动时用处理结果。</summary>
    public bool ExperimentalBake { get; set; }

    /// <summary>通用加载器原型的目录（实验）；为空时看环境变量 AMBER_BAKE_LOADER。</summary>
    public string? BakeLoaderPath { get; set; }

    /// <summary>删除的模组、存档等在回收目录里保留几天，过期自动清掉。</summary>
    public int TrashRetentionDays { get; set; } = 7;

    /// <summary>实例的显示名和状态，按实例 id（文件夹名）记。显示名和文件夹名分开，改名只改显示名。</summary>
    public Dictionary<string, InstanceMeta>? Instances { get; set; }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = AmberJson.WithReflectionFallback(AppJsonContext.Default),
    };

    /// <summary>Amber 自己的数据目录。单元测试用 AMBER_DATA_DIR 指到临时目录，免得读写真实的设置和账号。</summary>
    public static string DataDir => Environment.GetEnvironmentVariable("AMBER_DATA_DIR") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Amber");
    public static string FilePath => Path.Combine(DataDir, "settings.json");

    public InstanceSettings ToGlobalInstanceSettings() => new()
    {
        Isolate = IsolateVersions,
        JavaPath = JavaPath,
        AutoMemory = AutoMemory,
        MaxMemoryMb = MaxMemoryMb,
        JvmArgs = JvmArgs,
        GameArgs = GameArgs,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        Fullscreen = Fullscreen,
        PreLaunchCommand = PreLaunchCommand,
        PostExitCommand = PostExitCommand,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // 设置文件损坏时回到默认值，原文件保留一份方便排查。
            File.Copy(FilePath, FilePath + ".broken", true);
        }
        return new AppSettings();
    }

    /// <summary>测试用：这份设置存到别的文件（不序列化）。</summary>
    internal string? SavePathForTest { get; set; }

    public void Save()
    {
        var path = SavePathForTest ?? FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions.Info<AppSettings>()));
        File.Move(temp, path, true);
    }
}

/// <summary>实例在启动器里的附加信息（不写进游戏的版本文件）。</summary>
public sealed class InstanceMeta
{
    /// <summary>界面上显示的名字；null 时按游戏版本和加载器生成。</summary>
    public string? DisplayName { get; set; }

    /// <summary>null 表示正常；incomplete（安装未完成）/ broken（已损坏）。</summary>
    public string? State { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>测试用：比较新旧两种读写方式。</summary>
internal static class AppSettingsJson
{
    internal static JsonSerializerOptions Options => AppSettings.JsonOptions;
    internal static System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver Generated => AppJsonContext.Default;
}

/// <summary>窗口位置（物理像素）和大小（逻辑像素），以及是否最大化。</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
