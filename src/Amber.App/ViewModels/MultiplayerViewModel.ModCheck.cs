using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.GameLibrary;
using Amber.Core.Localization;
using Amber.Core.Net;
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
    [NotifyPropertyChangedFor(nameof(ModSyncSummary), nameof(ModCheckTitle), nameof(ModMissingText), nameof(ModDifferentText), nameof(ModExtraText),
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

    // ---- one-click sync ----

    /// <summary>A sync is running (the card shows its progress instead of the buttons).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModCheckButtonsShown))]
    public partial bool ModSyncing { get; set; }

    [ObservableProperty] public partial string ModSyncText { get; set; } = "";

    public bool ModCheckButtonsShown => !ModSyncing;

    /// <summary>“Change my &lt;instance&gt; directly”.</summary>
    public string SyncIntoMineText => L.F("multiplayer.modcheck.into_mine", ModCheckInstance);

    partial void OnModCheckInstanceChanged(string value) => OnPropertyChanged(nameof(SyncIntoMineText));

    /// <summary>The summary under the buttons: how many from public sites, how many from the host.</summary>
    public string ModSyncSummary => ModDifference is { } diff
        ? L.P("multiplayer.modcheck.sync_summary", diff.Missing.Count + diff.Different.Count, diff.Missing.Count + diff.Different.Count)
        : "";

    /// <summary>Self-check: where an instance's mods are, and a runner without network.</summary>
    internal Func<string, string>? TestModsDirOf { get; set; }

    internal Func<PrivateMessenger?, ModSyncRunner>? TestRunner { get; set; }

    private CancellationTokenSource? _syncCts;

    /// <summary>
    /// “Sync and join”: copy my instance (default) or change it directly, then fetch what is missing or different. Public
    /// files come from Modrinth (by SHA-1), the rest from the host over the room's encrypted channel; each file is checked
    /// against the host's list. Afterwards the mods are compared again, and the synced instance is the one to play.
    /// </summary>
    [RelayCommand]
    private async Task SyncModsAsync()
    {
        if (ModSyncing || ModDifference is not { } diff || ModCheckInstance is not { Length: > 0 } source || _hostMods is not { } hostMods) return;
        var messenger = _messenger;
        if (messenger is null && TestRunner is null) return;
        ModSyncing = true;
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;
        var intoNew = SyncIntoNewInstance;
        var ctx = _service.Context;
        var task = _service.Tasks.Track("modsync", L.F("multiplayer.modcheck.task", source), () => { try { _syncCts?.Cancel(); } catch (ObjectDisposedException) { } });
        var target = source;
        try
        {
            if (intoNew)
            {
                target = NewInstanceNames.Unique(L.F("multiplayer.modcheck.new_instance", HostName), _service.InstalledVersions());
                ModSyncText = L.F("multiplayer.modcheck.copying", source, target);
                if (TestModsDirOf is { } testDirs)
                {
                    var from = testDirs(source);
                    var to = testDirs(target);
                    await Task.Run(() => CopyFolder(from, to), ct);
                }
                else
                {
                    var isolated = !_service.UsesSharedDir(source);
                    await Task.Run(() => InstanceCopier.Copy(ctx.Directory, source, target, includeSaves: false, ct), ct);
                    if (isolated) _service.ForceIsolated(target);
                    _service.NotifyInstalledChanged();
                }
            }
            var modsDir = TestModsDirOf?.Invoke(target) ?? _service.GetInstance(target).ModsDir;
            var runner = TestRunner?.Invoke(messenger) ?? new ModSyncRunner
            {
                FindPublic = async (sha1s, token) => (await _service.Modrinth.IdentifyBySha1Async(sha1s, token))
                    .Where(p => !string.IsNullOrWhiteSpace(p.Value.Url)).ToDictionary(p => p.Key, p => p.Value.Url),
                Download = (url, path, token) => _service.Downloader.DownloadAllAsync([new DownloadItem(url, path)], null, token),
                FromHost = (sha1, token) => messenger!.RequestModFileAsync(HostFingerprint, sha1, token),
                Recycle = path => TrashService.MoveToTrash(path, ctx.Directory.Root),
            };
            var progress = new Progress<ModSyncProgress>(p =>
            {
                ModSyncText = p.Current.Length > 0 ? L.F("multiplayer.modcheck.syncing", p.Done + 1, p.Total, p.Current) : "";
                task.Report(p.Total == 0 ? null : (double)p.Done / p.Total, ModSyncText);
            });
            var result = await runner.RunAsync(diff, modsDir, intoNew, progress, ct);
            if (result.Succeeded) task.Succeed(L.F("multiplayer.modcheck.synced", target));
            else task.Fail(L.P("multiplayer.modcheck.sync_failed", result.Failed.Count, result.Failed.Count, Names(result.Failed.Select(m => m.DisplayName))));
            if (Instances.Contains(target)) TargetInstance = target;
            ModCheckInstance = target;
            var mine = await Task.Run(() => ModManifest.Read(modsDir), CancellationToken.None);
            await ShowModCheckAsync(new ModListReply(ModListAnswer.Sent, hostMods), mine);
            ModCheckInstance = target;
            SetStatus(result.Succeeded
                ? StatusMessage.Success("multiplayer.modcheck.synced", target)
                : StatusMessage.Warning("multiplayer.modcheck.sync_failed", result.Failed.Count, Names(result.Failed.Select(m => m.DisplayName))));
        }
        catch (OperationCanceledException)
        {
            task.Cancelled();
            SetStatus(StatusMessage.Warning("common.cancelled"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            task.Fail(ErrorText.Describe(e));
            SetStatus(StatusMessage.Failure("multiplayer.modcheck.sync_error", e, ErrorText.Of(e)));
        }
        finally
        {
            ModSyncing = false;
            ModSyncText = "";
            _syncCts?.Dispose();
            _syncCts = null;
        }
    }

    [RelayCommand]
    private void CancelModSync()
    {
        try { _syncCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }

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
