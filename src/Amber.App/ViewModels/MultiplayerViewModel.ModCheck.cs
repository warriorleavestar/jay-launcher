using System.Collections.ObjectModel;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>Where “check the mods” is for the person who joined.</summary>
public enum ModCheckState
{
    None,
    Checking,
    /// <summary>Compared: nothing missing and nothing in another version.</summary>
    Same,
    Different,
    /// <summary>The host turned off “let people who join sync mods from me”.</summary>
    Off,
    /// <summary>The host's Jay did not answer (older version, or the connection dropped).</summary>
    NoAnswer,
    /// <summary>“Never mind, just join”, or the ✓ has been shown.</summary>
    Dismissed,
}

/// <summary>One line in “see what's different”.</summary>
public sealed record ModDiffLine(string Mark, string Text);

/// <summary>
/// “Check the mods” (official §6.14): once the host has let me in, Jay asks for the host's mod list over the room's
/// encrypted channel and compares it with the instance I join with. Until that comparison is done, nothing in the UI says
/// “same as the host”. The host can turn syncing off in room settings.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private const int NamesShown = 3;
    private ModManifest? _hostMods;
    private string? _hostModsDir;
    private (DateTime At, ModManifest Manifest)? _hostModsCache;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModCheck), nameof(ModCheckChecking), nameof(ModCheckDifferent), nameof(ModCheckSame), nameof(ModCheckNotice), nameof(HasModCheckNotice))]
    public partial ModCheckState ModCheck { get; set; }

    /// <summary>The comparison, once the host's list has arrived.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModCheckTitle), nameof(ModMissingText), nameof(ModDifferentText), nameof(ModExtraText),
        nameof(HasModMissing), nameof(HasModDifferent), nameof(HasModExtra))]
    public partial ModDiff? ModDifference { get; set; }

    /// <summary>The instance the check was made against.</summary>
    [ObservableProperty] public partial string ModCheckInstance { get; set; } = "";

    /// <summary>“See what's different”: every line, not just the first few names.</summary>
    [ObservableProperty] public partial bool ModDetailsOpen { get; set; }

    /// <summary>Sync into a new instance (default; mine stays as it is) or change mine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncIntoMine))]
    public partial bool SyncIntoNewInstance { get; set; } = true;

    public bool SyncIntoMine
    {
        get => !SyncIntoNewInstance;
        set => SyncIntoNewInstance = !value;
    }

    public ObservableCollection<ModDiffLine> ModDiffLines { get; } = [];

    public bool ShowModCheck => IsJoined && ModCheck is not (ModCheckState.None or ModCheckState.Dismissed);
    public bool ModCheckChecking => ModCheck == ModCheckState.Checking;
    public bool ModCheckDifferent => ModCheck == ModCheckState.Different;
    public bool ModCheckSame => ModCheck == ModCheckState.Same;

    public string ModCheckNotice => ModCheck switch
    {
        ModCheckState.Off => L.T("multiplayer.modcheck.off"),
        ModCheckState.NoAnswer => L.T("multiplayer.modcheck.no_answer"),
        _ => "",
    };

    public bool HasModCheckNotice => ModCheckNotice.Length > 0;

    /// <summary>A name for “Alex's” in the card: the host's reported name, else from the roster, else “the host”.</summary>
    private string HostName => _chat?.HostName is { Length: > 0 } reported ? reported
        : OnlinePeers.FirstOrDefault(p => p.Entry.Fingerprint == HostFingerprint)?.Entry.Name is { Length: > 0 } name ? name
        : TestHostName ?? L.T("multiplayer.modcheck.host");

    /// <summary>Self-check: the host's name without a chat connection.</summary>
    internal string? TestHostName { get; set; }

    public string ModCheckTitle => L.F("multiplayer.modcheck.title", ModCheckInstance, HostName);

    public bool HasModMissing => ModDifference?.Missing.Count > 0;
    public bool HasModDifferent => ModDifference?.Different.Count > 0;
    public bool HasModExtra => ModDifference?.Extra.Count > 0;

    public string ModMissingText => ModDifference is { Missing: { Count: > 0 } missing }
        ? L.P("multiplayer.modcheck.missing", missing.Count, missing.Count, Names(missing.Select(m => m.DisplayName)))
        : "";

    public string ModDifferentText => ModDifference is { Different: { Count: > 0 } different }
        ? L.P("multiplayer.modcheck.different", different.Count, different.Count, Names(different.Select(VersionPair)))
        : "";

    public string ModExtraText => ModDifference is { Extra: { Count: > 0 } extra }
        ? L.P("multiplayer.modcheck.extra", extra.Count, extra.Count, Names(extra.Select(m => m.DisplayName)))
          + (extra.All(ModSync.IsClientOnly) ? L.T("multiplayer.modcheck.extra_harmless") : "")
        : "";

    private static string VersionPair(ModVersionPair p) =>
        p.Mine.Version is { Length: > 0 } mine && p.Host.Version is { Length: > 0 } host
            ? L.F("multiplayer.modcheck.versions", p.Host.DisplayName, mine, host)
            : p.Host.DisplayName;

    /// <summary>“Create, Farmer's Delight, Supplementaries” (+ “and 4 more”).</summary>
    private static string Names(IEnumerable<string> names)
    {
        var all = names.ToList();
        var shown = string.Join(L.T("common.list_separator"), all.Take(NamesShown));
        return all.Count > NamesShown ? shown + L.P("multiplayer.modcheck.more", all.Count - NamesShown, all.Count - NamesShown) : shown;
    }

    /// <summary>Joiner: the host is online in the private channel and no check ran yet in this room.</summary>
    private void MaybeCheckMods()
    {
        if (!IsJoined || ModCheck != ModCheckState.None || _messenger is not { } messenger || HostFingerprint.Length == 0) return;
        if (OnlinePeers.All(p => p.Entry.Fingerprint != HostFingerprint)) return;
        _ = CheckModsAsync(messenger);
    }

    /// <summary>Asks the host for its list and compares it with the instance I join with.</summary>
    internal async Task CheckModsAsync(PrivateMessenger messenger)
    {
        ModCheck = ModCheckState.Checking;
        ModCheckInstance = TargetInstance ?? "";
        var reply = await messenger.RequestModListAsync(HostFingerprint);
        if (_messenger != messenger) return;
        await ShowModCheckAsync(reply);
    }

    /// <summary>Shows the outcome of asking the host (also used by the self-check with a stand-in reply).</summary>
    internal async Task ShowModCheckAsync(ModListReply reply, ModManifest? mine = null)
    {
        if (reply is not { Answer: ModListAnswer.Sent, Manifest: { } host })
        {
            ModCheck = reply.Answer == ModListAnswer.Off ? ModCheckState.Off : ModCheckState.NoAnswer;
            return;
        }
        _hostMods = host;
        ModCheckInstance = TargetInstance ?? ModCheckInstance;
        if (mine is null)
        {
            var id = ModCheckInstance;
            try
            {
                mine = await Task.Run(() => ModManifest.Read(_service.GetInstance(id).ModsDir));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            {
                mine = ModManifest.Empty;
            }
        }
        var diff = ModSync.Compare(mine, host);
        ModDifference = diff;
        ModDiffLines.Clear();
        foreach (var m in diff.Missing) ModDiffLines.Add(new("+", m.DisplayName));
        foreach (var p in diff.Different) ModDiffLines.Add(new("≠", VersionPair(p)));
        foreach (var m in diff.Extra) ModDiffLines.Add(new("−", m.DisplayName));
        ModCheck = diff.Matches ? ModCheckState.Same : ModCheckState.Different;
        if (diff.Matches && ModCheckInstance.Length > 0) _service.MarkModsDifferFromHost(ModCheckInstance, null);
        // Same: a ✓ for a moment, then out of the way.
        if (diff.Matches)
            DispatcherTimer.RunOnce(() =>
            {
                if (ModCheck == ModCheckState.Same) ModCheck = ModCheckState.Dismissed;
            }, TimeSpan.FromSeconds(4));
    }

    [RelayCommand]
    private void ToggleModDetails() => ModDetailsOpen = !ModDetailsOpen;

    /// <summary>“Never mind, just join”: the card goes; the game's last-session panel will name the mod difference first if it crashes.</summary>
    [RelayCommand]
    private void SkipModCheck()
    {
        if (ModCheck == ModCheckState.Different && ModCheckInstance.Length > 0) _service.MarkModsDifferFromHost(ModCheckInstance, HostName);
        ModCheck = ModCheckState.Dismissed;
    }

    private void ResetModCheck()
    {
        ModCheck = ModCheckState.None;
        ModDifference = null;
        ModDiffLines.Clear();
        ModDetailsOpen = false;
        _hostMods = null;
    }

    // ---- host side ----

    /// <summary>Room settings › mod sync: people who join may get my mod list and files (default on).</summary>
    public bool AllowModSync
    {
        get => _service.Settings.MultiplayerAllowModSync;
        set
        {
            if (_service.Settings.MultiplayerAllowModSync == value) return;
            _service.Settings.MultiplayerAllowModSync = value;
            _service.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Host: answer mod list and mod file requests from the room's instance (read again at most every 30 seconds).</summary>
    private void ServeMods(PrivateMessenger messenger)
    {
        _hostModsDir = null;
        _hostModsCache = null;
        if (TargetInstance is { } id)
        {
            try
            {
                _hostModsDir = _service.GetInstance(id).ModsDir;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            {
            }
        }
        messenger.ModListProvider = () => _service.Settings.MultiplayerAllowModSync ? HostModList() : null;
        messenger.ModFileProvider = sha1 => _hostModsDir is { } dir && HostModList()?.Mods.FirstOrDefault(m => m.Sha1 == sha1) is { } entry
            ? Path.Combine(dir, entry.FileName)
            : null;
    }

    private ModManifest? HostModList()
    {
        if (_hostModsDir is not { } dir) return null;
        if (_hostModsCache is { } cache && DateTime.UtcNow - cache.At < TimeSpan.FromSeconds(30)) return cache.Manifest;
        try
        {
            var manifest = ModManifest.Read(dir);
            _hostModsCache = (DateTime.UtcNow, manifest);
            return manifest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
