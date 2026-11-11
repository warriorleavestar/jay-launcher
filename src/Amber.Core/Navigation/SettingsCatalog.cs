namespace Amber.Core.Navigation;

/// <summary>A settings group (official design §6.20). Keys are localization keys.</summary>
public sealed record SettingsGroup(string Id, string TitleKey, string SummaryKey, string NoteKey);

/// <summary>
/// One setting on the settings page. <paramref name="Field"/> is the route fragment (<c>settings#Field</c>) and the name
/// of the control (<c>Field</c> or <c>FieldBox</c>); <paramref name="HintKey"/> is searched too.
/// </summary>
public sealed record SettingsField(string Field, string Group, string LabelKey, string? HintKey = null);

/// <summary>
/// The nine settings groups and every setting with a control, in page order. The settings search and the command
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
        new(Experimental, "settings.group.experimental", "settings.group.experimental.summary", "settings.group.experimental.note"),
        new(About, "settings.group.about", "settings.group.about.summary", "settings.group.about.note"),
    ];

    public static readonly IReadOnlyList<SettingsField> Fields =
    [
        new("Language", General, "settings.language.label"),
        new("AfterLaunch", General, "settings.after_launch.label", "settings.after_launch.hint"),

        new("Theme", Appearance, "settings.theme.label"),
        new("ThemePack", Appearance, "settings.themepack.label", "settings.themepack.hint"),
        new("AllowNetworkThemes", Appearance, "settings.themepack.allow_network"),
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

        new("Source", Downloads, "settings.source.label"),
        new("Concurrency", Downloads, "settings.download.concurrency"),
        new("CurseForgeKey", Downloads, "settings.curseforge.key", "settings.curseforge.key_hint"),
        new("CurseForgeRoot", Downloads, "settings.curseforge.endpoint"),
        new("UpdateFeed", Downloads, "settings.update.feed"),

        new("GameDirectory", Storage, "settings.directory.label"),
        new("Junk", Storage, "tools.junk.title", "tools.junk.initial"),
        new("RecycleKeep", Storage, "recycle.keep.label", "recycle.title"),

        new("NoTrace", Privacy, "settings.notrace.label", "settings.notrace.hint"),
        new("StrongSandbox", Privacy, "settings.sandbox.strong", "settings.sandbox.strong.hint"),
        new("LaunchWithoutSandbox", Privacy, "settings.sandbox.without", "settings.sandbox.without.hint"),
        new("Attestation", Privacy, "settings.security.attest.label", "settings.security.hint"),
        new("Breaker", Privacy, "settings.security.breaker.label", "settings.security.breaker.toggle"),
        new("BreakerDelay", Privacy, "settings.security.delay.label"),
        new("BreakerSkip", Privacy, "settings.security.skip.toggle"),
        new("NetworkList", Privacy, "settings.privacy.network_list", "about.net.heading"),
        new("ChildMode", Privacy, "settings.child.title", "settings.child.hint"),
        new("DataFolder", Privacy, "settings.privacy.data_folder"),

        new("MultiplayerSettings", Multiplayer, "settings.multiplayer.card"),

        new("Bake", Experimental, "settings.bake.title", "settings.bake.hint"),
        new("BakeLoader", Experimental, "settings.bake.loader_hint"),
        new("BakeLoaderInstall", Experimental, "settings.bake.install", "settings.bake.install_hint"),
        new("BakeCleanup", Experimental, "settings.bake.gc", "settings.bake.gc_hint"),
        new("ThemeDev", Experimental, "settings.themedev.label", "settings.themedev.hint"),

        new("Version", About, "settings.about.version"),
        new("CheckUpdate", About, "settings.about.check_update", "tools.update.hint"),
        new("AboutPage", About, "about.title", "about.promise"),
    ];

    public static SettingsGroup? Group(string? id) =>
        Groups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The setting a route fragment points at (matches <c>Field</c> and <c>FieldBox</c>, any case), or null.</summary>
    public static SettingsField? Find(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return null;
        var name = field.EndsWith("Box", StringComparison.OrdinalIgnoreCase) ? field[..^3] : field;
        return Fields.FirstOrDefault(f => string.Equals(f.Field, name, StringComparison.OrdinalIgnoreCase));
    }
}
