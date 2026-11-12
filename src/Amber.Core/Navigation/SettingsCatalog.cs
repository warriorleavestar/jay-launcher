namespace Amber.Core.Navigation;

/// <summary>A settings group (official design §6.20). Keys are localization keys.</summary>
public sealed record SettingsGroup(string Id, string TitleKey, string SummaryKey, string NoteKey);

/// <summary>
/// One setting on the settings page. <paramref name="Field"/> is the route fragment (<c>settings#Field</c>) and the name
/// of the control (<c>Field</c> or <c>FieldBox</c>); <paramref name="HintKey"/> is searched too. A <paramref name="WindowsOnly"/>
/// setting has no control on other systems, so it is left out of <see cref="SettingsCatalog.Fields"/> there; a
/// <paramref name="CallsOnly"/> setting belongs to the reserved calls group and is listed only while calls are switched on.
/// </summary>
public sealed record SettingsField(string Field, string Group, string LabelKey, string? HintKey = null, bool WindowsOnly = false, bool CallsOnly = false);

/// <summary>
/// The ten settings groups and every setting with a control, in page order. The settings search and the command
/// palette both read this list, so every setting is findable from either.
/// </summary>
public static class SettingsCatalog
{
    public const string General = "general";
    public const string Appearance = "appearance";
    public const string Game = "game";
    public const string Downloads = "downloads";
    public const string Storage = "storage";
    public const string Privacy = "privacy";
    public const string Multiplayer = "multiplayer";
    public const string Network = "network";
    public const string Experimental = "experimental";
    public const string About = "about";

    public static readonly IReadOnlyList<SettingsGroup> Groups =
    [
        new(General, "settings.group.general", "settings.group.general.summary", "settings.group.general.note"),
        new(Appearance, "settings.group.appearance", "settings.group.appearance.summary", "settings.group.appearance.note"),
        new(Game, "settings.group.game", "settings.group.game.summary", "settings.group.game.note"),
        new(Downloads, "settings.group.downloads", "settings.group.downloads.summary", "settings.group.downloads.note"),
        new(Storage, "settings.group.storage", "settings.group.storage.summary", "settings.group.storage.note"),
        new(Privacy, "settings.group.privacy", "settings.group.privacy.summary", "settings.group.privacy.note"),
        new(Multiplayer, "settings.group.multiplayer", "settings.group.multiplayer.summary", "settings.group.multiplayer.note"),
        new(Network, "settings.group.network", "settings.group.network.summary", "settings.group.network.note"),
        new(Experimental, "settings.group.experimental", "settings.group.experimental.summary", "settings.group.experimental.note"),
        new(About, "settings.group.about", "settings.group.about.summary", "settings.group.about.note"),
    ];

    /// <summary>Every setting on every system.</summary>
    public static readonly IReadOnlyList<SettingsField> All =
    [
        new("Language", General, "settings.language.label"),
        new("AfterLaunch", General, "settings.after_launch.label", "settings.after_launch.hint"),

        new("Theme", Appearance, "settings.theme.label"),
        new("ThemePack", Appearance, "settings.themepack.label", "settings.themepack.hint"),
        new("AllowNetworkThemes", Appearance, "settings.themepack.allow_network"),
        new("ImageThemeCard", Appearance, "theme.image.title", "theme.image.summary"),
        new("Motion", Appearance, "settings.motion.label", "settings.motion.hint"),
        new("Sidebar", Appearance, "settings.sidebar.label"),
        new("Density", Appearance, "settings.density.label", "settings.density.hint"),

        new("Java", Game, "settings.java.label"),
        new("AutoJava", Game, "settings.java.auto_download"),
        new("Memory", Game, "settings.memory.label", "settings.memory.by_mod_count"),
        new("Isolation", Game, "instance.settings.isolation", "settings.isolation.hint"),
        new("WindowWidth", Game, "instance.settings.window_size", "instance.settings.fullscreen"),
        new("JvmArgs", Game, "instance.settings.jvm_args"),
        new("GameArgs", Game, "instance.settings.game_args"),
        new("PreLaunch", Game, "instance.settings.pre_launch"),
        new("PostExit", Game, "instance.settings.post_exit"),
        new("JavaDownload", Game, "tools.java.title", "tools.java.hint"),
        // The universal loader (redesign batch 10): moved from Experimental. Each instance turns it on in its own settings.
        new("Bake", Game, "settings.bake.title", "settings.bake.hint"),
        new("BakeLoaderInstall", Game, "settings.bake.install", "settings.bake.install_hint"),
        new("BakeCleanup", Game, "settings.bake.gc", "settings.bake.gc_hint"),
        new("BakeLoader", Game, "settings.bake.loader_hint"),

        new("Source", Downloads, "settings.source.label"),
        new("Concurrency", Downloads, "settings.download.concurrency"),
        new("CurseForgeKey", Downloads, "settings.curseforge.key", "settings.curseforge.key_hint"),
        new("CurseForgeRoot", Downloads, "settings.curseforge.endpoint"),
        new("UpdateFeed", Downloads, "settings.update.feed"),

        new("GameDirectory", Storage, "settings.directory.label"),
        new("Junk", Storage, "tools.junk.title", "tools.junk.initial"),
        new("RecycleKeep", Storage, "recycle.keep.label", "recycle.title"),

        new("NetworkList", Privacy, "settings.privacy.network_list", "about.net.heading"),
        // The game sandbox card, right after the network list (the multiplayer page links here).
        new("GameSandbox", Privacy, "settings.sandbox.label", "settings.sandbox.card_hint"),
        new("StrongSandbox", Privacy, "settings.sandbox.strong", "settings.sandbox.strong.hint", WindowsOnly: true),
        new("LaunchWithoutSandbox", Privacy, "settings.sandbox.without", "settings.sandbox.without.hint"),
        new("ChildMode", Privacy, "settings.child.title", "settings.child.hint"),
        new("NoTrace", Privacy, "settings.notrace.label", "settings.notrace.hint"),
        new("DataFolder", Privacy, "settings.privacy.data_folder"),

        // The multiplayer advanced settings panel (redesign batch 10), group by group.
        new("ProtectionLevel", Multiplayer, "settings.mp.protection.level", "settings.mp.protection.level_hint"),
        new("Transport", Multiplayer, "multiplayer.transport.label", "multiplayer.transport.hint"),
        new("Disguise", Multiplayer, "multiplayer.transport.disguise"),
        new("Attestation", Multiplayer, "settings.security.attest.label", "settings.security.hint"),
        new("Breaker", Multiplayer, "settings.security.breaker.label", "settings.security.breaker.toggle"),
        new("BreakerDelay", Multiplayer, "settings.security.delay.label"),
        new("BreakerSkip", Multiplayer, "settings.security.skip.toggle"),
        new("DefaultRelayOnly", Multiplayer, "multiplayer.settings.relay_only", "multiplayer.settings.relay_only_hint"),
        new("FullLanMode", Multiplayer, "multiplayer.lan.full_mode", "multiplayer.lan.full_mode_hint"),
        new("MultiplayerComponentCard", Multiplayer, "multiplayer.settings.component", "multiplayer.component.import"),
        new("InviteAutoCopy", Multiplayer, "multiplayer.lobby.auto_copy", "multiplayer.clipboard.autoclear_hint"),
        new("DefaultKnockWait", Multiplayer, "multiplayer.settings.knock_wait"),
        new("RequestPopup", Multiplayer, "multiplayer.settings.request_popup"),
        new("ModSyncDefault", Multiplayer, "multiplayer.room.mod_sync_allow", "multiplayer.room.mod_sync"),
        new("AllowGameProxy", Multiplayer, "multiplayer.proxy.allow"),
        new("AllowExitNode", Multiplayer, "multiplayer.exit.allow", "multiplayer.exit.warning"),
        // Files (redesign batch 12).
        new("FilesFolder", Multiplayer, "settings.files.folder", "settings.files.folder_hint"),
        new("FilesAutoAccept", Multiplayer, "settings.files.auto_accept", "settings.files.auto_accept_hint"),
        new("FilesQueue", Multiplayer, "settings.files.queue", "settings.files.queue_hint"),
        new("FilesSpeed", Multiplayer, "settings.files.speed"),
        new("FilesNotify", Multiplayer, "settings.files.notify"),
        new("Troubleshoot", Multiplayer, "multiplayer.room.problems", "settings.mp.troubleshoot.hint"),
        // Calls and screen (redesign batch 11, reserved).
        new("CallsWho", Multiplayer, "settings.calls.who", "settings.calls.who_hint", CallsOnly: true),
        new("CallsMic", Multiplayer, "settings.calls.mic_device", CallsOnly: true),
        new("CallsCamera", Multiplayer, "settings.calls.camera_device", CallsOnly: true),
        new("CallsNoise", Multiplayer, "settings.calls.noise", CallsOnly: true),
        new("CallsPushToTalk", Multiplayer, "settings.calls.ptt", CallsOnly: true),
        new("CallsQuality", Multiplayer, "settings.calls.quality", CallsOnly: true),
        new("CallsShareGame", Multiplayer, "settings.calls.share_game", CallsOnly: true),
        new("CallsAllowControl", Multiplayer, "settings.calls.allow_control", "settings.calls.allow_control_hint", CallsOnly: true),
        new("MultiplayerReset", Multiplayer, "settings.mp.reset_all", "settings.mp.reset_confirm"),

        new("NetworksCard", Network, "multiplayer.settings.public", "multiplayer.network.hint"),
        new("PublicNodes", Network, "multiplayer.public.nodes_title", "multiplayer.public.nodes_hint"),
        new("RelayGroup", Network, "settings.network.relays", "multiplayer.nodes.hint"),
        new("Stun", Network, "multiplayer.direct.stun_heading"),
        new("Volunteer", Network, "multiplayer.volunteer.title", "multiplayer.volunteer.enable"),

        new("ThemeDev", Experimental, "settings.themedev.label", "settings.themedev.hint"),

        new("Version", About, "settings.about.version"),
        new("CheckUpdate", About, "settings.about.check_update", "tools.update.hint"),
        new("AboutPage", About, "about.title", "about.promise"),
    ];

    private static readonly SettingsField[] OnThisSystem = [.. All.Where(f => !f.WindowsOnly || OperatingSystem.IsWindows())];
    private static readonly SettingsField[] WithoutCalls = [.. OnThisSystem.Where(f => !f.CallsOnly)];

    /// <summary>The settings this system shows (Windows-only ones are dropped elsewhere; the calls group only while calls are on).</summary>
    public static IReadOnlyList<SettingsField> Fields => FieldsFor(Amber.Core.Calls.CallFeature.Enabled);

    public static IReadOnlyList<SettingsField> FieldsFor(bool calls) => calls ? OnThisSystem : WithoutCalls;

    public static SettingsGroup? Group(string? id) =>
        Groups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The setting a route fragment points at (matches <c>Field</c> and <c>FieldBox</c>, any case), or null.</summary>
    public static SettingsField? Find(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return null;
        // Exact name first: a field may itself end in "Box" (StrongSandbox).
        return Fields.FirstOrDefault(f => string.Equals(f.Field, field, StringComparison.OrdinalIgnoreCase))
            ?? (field.EndsWith("Box", StringComparison.OrdinalIgnoreCase)
                ? Fields.FirstOrDefault(f => string.Equals(f.Field, field[..^3], StringComparison.OrdinalIgnoreCase))
                : null);
    }
}
