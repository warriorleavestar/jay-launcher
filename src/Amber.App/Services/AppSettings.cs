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

    /// <summary>"For a child" (official design §9.6): main switch, six items and the time of the last change.</summary>
    public Amber.Core.Safety.ChildModeSettings ChildMode { get; set; } = new();

    /// <summary>无痕模式：游戏日志（logs/latest.log）里不写聊天行。默认关。</summary>
    public bool NoTrace { get; set; }

    /// <summary>
    /// Launch the game without the OS sandbox (privacy plan item 15). Off by default: the game only sees its own
    /// instance folder. When on, the multiplayer page says so on a line that stays.
    /// </summary>
    public bool LaunchWithoutSandbox { get; set; }

    /// <summary>
    /// Windows: strong sandbox mode (AppContainer). On by default; the first launch asks once for the loopback exemption.
    /// Off: the standard sandbox (low integrity).
    /// </summary>
    public bool StrongSandbox { get; set; } = true;

    /// <summary>
    /// Why strong mode fell back to the standard sandbox (a localization key: declined, no admin, failed), so Jay does
    /// not ask on every launch. Cleared by "Retry strong mode".
    /// </summary>
    public string? StrongSandboxFallback { get; set; }

    /// <summary>Detail for <see cref="StrongSandboxFallback"/> (the error of a failed exemption).</summary>
    public string? StrongSandboxFallbackDetail { get; set; }

    /// <summary>
    /// Parent folders Jay's admin step let app containers list (names only, that folder only), so "remove sandbox
    /// permissions" takes exactly these entries away again.
    /// </summary>
    public List<string> StrongSandboxFolders { get; set; } = [];

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

    /// <summary>用户自己申请的 CurseForge API Key；留空时读环境变量 AMBER_CF_KEY，再用官方版内置的 Key。</summary>
    public string? CurseForgeApiKey { get; set; }

    /// <summary>CurseForge 接口地址。留空用官方。可以指向用户信任的代理或镜像。</summary>
    public string? CurseForgeApiRoot { get; set; }

    /// <summary>Update feed. Blank means the official Jay releases (<see cref="Amber.Core.Launch.AutoUpdateCheck.OfficialFeed"/>).</summary>
    public string? UpdateFeedUrl { get; set; }

    /// <summary>
    /// Check the update feed once a day and tell the user about a signed newer version. Jay never downloads it.
    /// On for a new install. A settings file from before this switch existed keeps what that Jay did: it never checked
    /// by itself (even with an update address it only checked on click), so it stays off. The saved choice then carries over.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AutoUpdateCheck
    {
        get => AutoUpdateCheckSaved ?? true;
        set => AutoUpdateCheckSaved = value;
    }

    /// <summary>The saved switch; null in a fresh install and in settings files written before it existed.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("AutoUpdateCheck")]
    public bool? AutoUpdateCheckSaved { get; set; }

    /// <summary>When the automatic update check last ran (successfully or not).</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>The version the user chose to skip: the automatic check doesn't mention it again.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>system / light / dark</summary>
    public string Theme { get; set; } = "system";

    /// <summary>导入的界面主题包 id（{数据目录}/themes/{id}）；null 表示 Jay 默认主题。</summary>
    public string? ThemePack { get; set; }

    /// <summary>联机网络配置里带的主题：在联机页里使用（整个 Jay 要另外同意）。关掉就一律不用。</summary>
    public bool AllowNetworkThemes { get; set; } = true;

    /// <summary>The first-run wizard (official design §6.1) was finished or skipped; it never opens again.</summary>
    public bool FirstRunDone { get; set; }

    /// <summary>No settings file existed: a brand-new Jay (not saved).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFresh { get; private set; }

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

    /// <summary>
    /// The public network on which this Jay keeps a signed record of its relays (set when a relay share code went out as a
    /// JAYNR1 record code; Jay puts the record again each time it connects there). Null: none.
    /// </summary>
    public string? RelayRecordNetwork { get; set; }

    /// <summary>Volunteer node: let other players reach the public network through this computer (off by default).</summary>
    public bool VolunteerNode { get; set; }

    /// <summary>
    /// Sign volunteer registrations with the Jay account bound to the Minecraft account, so the network's operator knows
    /// who runs the relay. Off by default, also for installs that volunteered before this setting existed.
    /// </summary>
    public bool VolunteerSigned { get; set; }

    /// <summary>This computer's public address (IP or domain) for volunteering.</summary>
    public string? VolunteerAddress { get; set; }

    /// <summary>Public port to listen on when volunteering (forward it on the router, TCP and UDP).</summary>
    public int VolunteerPort { get; set; } = 11010;

    /// <summary>Most a volunteer receives from the public network, KB/s (0 = no limit).</summary>
    public int VolunteerLimitKBps { get; set; } = 1024;

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

    /// <summary>旧版：侧边栏手动收成图标栏。新版读 SidebarMode；这个勾过的升级成 collapsed。</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>侧栏：auto（按窗口宽度，默认）/ expanded / collapsed。</summary>
    public string? SidebarMode { get; set; }

    /// <summary>新版外壳的“东西搬到哪了”卡片已经关掉。</summary>
    public bool ShellMovedNoticeSeen { get; set; }

    /// <summary>减少动画（页面切换、抽屉滑入这些都不做过渡）。</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>减少动画：system（跟随系统，默认）/ on / off。旧的 ReduceMotion 勾过的升级成 on（见 MotionPreference.Migrate）。</summary>
    public string? ReduceMotionMode { get; set; }

    /// <summary>启动游戏后启动器怎么办：minimize（最小化，默认）/ keep（保持）/ tray（隐藏到托盘）。</summary>
    public string AfterLaunch { get; set; } = "minimize";

    /// <summary>Contextual tips already seen and kinds muted (official design §9.3 rule 7).</summary>
    public Amber.Core.Tutorial.TutorialState Tutorial { get; set; } = new();

    /// <summary>standard / compact (official design §6.20 Appearance).</summary>
    public string Density { get; set; } = Amber.Core.Layout.Density.Standard;

    /// <summary>Multiplayer lobby: copy the first invite when a room is opened (cleared from the clipboard later).</summary>
    public bool MultiplayerAutoCopyInvite { get; set; } = true;

    /// <summary>New-instance panel: "Add mods while you're at it" stays open once opened (§6.11).</summary>
    public bool NewInstanceModsOpen { get; set; }
    /// <summary>How long a room waits for the host to answer a knock: "2m", "10m" or "forever".</summary>
    public string MultiplayerKnockWait { get; set; } = "10m";
    /// <summary>
    /// "Always relays only": in every room I open or join, this computer makes no direct connections, so people in the
    /// room do not see its IP address (slower). A room opened as "relays only" does this for everyone regardless.
    /// </summary>
    public bool MultiplayerRelayOnly { get; set; }
    /// <summary>Knocks and incoming files while Jay is in the background open a small topmost window.</summary>
    public bool MultiplayerRequestPopup { get; set; } = true;
    /// <summary>The last room I opened, for the lobby's “Open like last time”.</summary>
    public LastRoom? MultiplayerLastRoom { get; set; }
    /// <summary>Room settings › mod sync: people who join may get my mod list and the mod files that are not on public sites.</summary>
    public bool MultiplayerAllowModSync { get; set; } = true;

    // Calls and screen (reserved, redesign batch 11): kept for when calls exist; nothing reads them while the feature is off.
    /// <summary>Who may call me: "friends-room" (default), "room" or "nobody". People I've only met and strangers never can.</summary>
    public string CallsWhoCanCall { get; set; } = "friends-room";
    public bool CallsNoiseSuppression { get; set; } = true;
    public bool CallsPushToTalk { get; set; }
    /// <summary>"auto" (default), "sharp" or "smooth".</summary>
    public string CallsVideoQuality { get; set; } = "auto";
    /// <summary>Sharing the screen starts with the game window only; the whole screen is a choice each time.</summary>
    public bool CallsShareGameOnly { get; set; } = true;
    /// <summary>Friends may ask to control my shared screen; each time still needs my "just this once".</summary>
    public bool CallsAllowControlRequests { get; set; } = true;

    // Files (redesign batch 12): Settings › Multiplayer › Files.
    /// <summary>Where received files go; empty means "Downloads/Jay".</summary>
    public string FilesSaveFolder { get; set; } = "";
    /// <summary>Files from friends whose emoji I compared are taken without asking. Programs still ask every time; off by default.</summary>
    public bool FilesAutoAcceptFriends { get; set; }
    /// <summary>A file for a friend who is not online waits on this computer and starts when we both are.</summary>
    public bool FilesQueueOffline { get; set; } = true;
    /// <summary>"unlimited", "give-way" (slow while a game runs) or "save-data" (always slow).</summary>
    public string FilesSpeed { get; set; } = Amber.Core.Multiplayer.Files.TransferPace.Unlimited;
    /// <summary>Say so when a file has finished arriving.</summary>
    public bool FilesNotifyDone { get; set; } = true;

    /// <summary>打开启动器时检查一次更新（只在设置导航上显示提示，不自动下载）。</summary>
    public bool CheckUpdatesOnStart { get; set; } = true;

    /// <summary>
    /// The universal loader's default: instances that have not chosen for themselves (and new instances, which start from
    /// this) launch through it. It used to be the one global "bake" switch; the name stays so old settings keep their meaning.
    /// </summary>
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
        : Path.Combine(Amber.Core.SpecialFolders.ApplicationData(), "Amber");
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
        UniversalLoader = ExperimentalBake,
    };

    public static AppSettings Load() => Load(FilePath, File.ReadAllText);

    internal static AppSettings Load(string path, Func<string, string> read)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize(read(path), AppJsonContext.Default.AppSettings) ?? new AppSettings();
                    // Upgraded from a Jay without the switch: that Jay never checked by itself, so neither does this one.
                    loaded.AutoUpdateCheckSaved ??= false;
                    return loaded;
                }
                return new AppSettings { IsFresh = true };
            }
            catch (JsonException)
            {
                // 设置文件损坏时回到默认值，原文件保留一份方便排查。
                try { File.Copy(path, path + ".broken", true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                // Only a real new install gets the daily check by default; a file that exists but can't be read keeps it off.
                return new AppSettings { AutoUpdateCheckSaved = false };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Locked by a sync client or antivirus: retry briefly, then run on defaults without
                // ever saving over the real file (ReadOnly), so nothing the user set is lost.
                if (attempt < 3) { Thread.Sleep(200); continue; }
                return new AppSettings { ReadOnly = true, AutoUpdateCheckSaved = false };
            }
        }
    }

    /// <summary>The settings file could not be read at startup: this session runs on defaults and never writes it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ReadOnly { get; private init; }

    /// <summary>测试用：这份设置存到别的文件（不序列化）。</summary>
    internal string? SavePathForTest { get; set; }

    public void Save()
    {
        if (ReadOnly) return;
        // A new install shows the daily update check on: write that down, or the next start would read it as an upgrade.
        AutoUpdateCheckSaved ??= true;
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

    /// <summary>Pinned to the top of the library.</summary>
    public bool Pinned { get; set; }

    /// <summary>When a game of this instance last started (local runs only).</summary>
    public DateTimeOffset? LastPlayed { get; set; }

    /// <summary>Total minutes played from this launcher.</summary>
    public double PlayMinutes { get; set; }

    /// <summary>
    /// Joined a room with this instance although its mods differed from the host's (“never mind, just join”): the host's
    /// name. If the game then crashes, the last-session panel names this first. Cleared when a later check matches.
    /// </summary>
    public string? ModsDifferFromHost { get; set; }
}

/// <summary>What “Open like last time” repeats: the world, the first invite's terms and who can find the room (never a password).</summary>
public sealed class LastRoom
{
    /// <summary>The world's name as the game announces it on the LAN.</summary>
    public string? World { get; set; }
    public int Uses { get; set; } = 1;
    /// <summary>Minutes the invite stays valid; null = while the room is open.</summary>
    public int? LifetimeMinutes { get; set; } = 10;
    public bool AutoApprove { get; set; }
    /// <summary>A <c>RoomVisibility</c> name.</summary>
    public string Visibility { get; set; } = "Hidden";
    public string? RoomName { get; set; }
    public bool PublishAutoApprove { get; set; }
    /// <summary>The four choices of the open-a-room card, as enum names (<c>RoomAccess</c>, <c>RoomRoute</c>, <c>RoomProtection</c>, <c>RoomScope</c>); null in settings written before them.</summary>
    public string? Access { get; set; }
    public string? Route { get; set; }
    public string? Protection { get; set; }
    public string? Scope { get; set; }
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
