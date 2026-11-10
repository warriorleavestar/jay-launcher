using Amber.Core.Install;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// Invite v3 in the lobby (official §6.14): the pasted invite says which game the host plays, Jay picks the closest
/// instance, and offers to install one when nothing fits. On the host side, new invites carry the room's game.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private int _pickGeneration;

    /// <summary>The host's game from the pasted invite (null for v2 invites and other codes).</summary>
    public InviteGame? JoinGame => JoinCodeKind == PastedCodeKind.Invite && JayInvite.TryParse(JoinCode, out var invite) ? invite.Game : null;

    public bool HasJoinGame => JoinGame is not null;

    /// <summary>“1.20.1 · Fabric · 87 mods”.</summary>
    public string JoinGameText => JoinGame is { } g ? GameSummary(g.McVersion, g.Loader, g.ModCount) : "";

    internal static string GameSummary(string version, string? loader, int mods)
    {
        var parts = new List<string> { version, LoaderName(loader) };
        if (loader is not null && loader != "optifine") parts.Add(L.P("multiplayer.lobby.game.mods", mods, mods));
        return string.Join(" · ", parts);
    }

    private static string LoaderName(string? loader) => loader switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        "optifine" => "OptiFine",
        _ => L.T("multiplayer.lobby.game.vanilla"),
    };

    /// <summary>The instance Jay picked for this invite, and how close it is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JoinMatchText), nameof(JoinNeedsInstall), nameof(InstallForInviteText), nameof(ShowJoinInstancePicker))]
    public partial InstanceMatch? JoinMatch { get; set; }

    /// <summary>Picking is still running (hashing mods of matching instances).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JoinNeedsInstall), nameof(JoinMatchText))]
    public partial bool JoinPicking { get; set; }

    public bool ShowJoinInstancePicker => HasJoinGame && JoinMatch is not null;

    /// <summary>
    /// Why this instance. Never says “same as the host”: that is only known after the host's mod list has been compared.
    /// </summary>
    public string JoinMatchText => JoinMatch?.Kind switch
    {
        InstanceMatchKind.Exact => L.T("multiplayer.lobby.game.pick_exact"),
        InstanceMatchKind.SameGame => L.T("multiplayer.lobby.game.pick_close"),
        _ => JoinPicking ? L.T("multiplayer.lobby.game.picking") : "",
    };

    /// <summary>No instance has the host's version and loader.</summary>
    public bool JoinNeedsInstall => HasJoinGame && !JoinPicking && JoinMatch is null;

    public string InstallForInviteText => JoinGame is { } g ? L.F("multiplayer.lobby.game.install", g.McVersion, LoaderName(g.Loader)) : "";

    /// <summary>Opens the new-instance panel filled in with the host's version and loader; comes back here once the install has started.</summary>
    public Action<string, LoaderKind?>? InstallForInvite { get; set; }

    /// <summary>Self-check: stand-in instances instead of the game folder's.</summary>
    internal Func<IReadOnlyList<InstanceCandidate>>? TestCandidates { get; set; }

    [RelayCommand]
    private void InstallForInviteGame()
    {
        if (JoinGame is not { } g) return;
        InstallForInvite?.Invoke(g.McVersion, g.Loader switch
        {
            "fabric" => LoaderKind.Fabric,
            "forge" => LoaderKind.Forge,
            "neoforge" => LoaderKind.NeoForge,
            "quilt" => LoaderKind.Quilt,
            "optifine" => LoaderKind.OptiFine,
            _ => null,
        });
    }

    /// <summary>A new code in the box, or the instances changed: pick again.</summary>
    private void RepickJoinInstance()
    {
        OnPropertyChanged(nameof(JoinGame));
        OnPropertyChanged(nameof(HasJoinGame));
        OnPropertyChanged(nameof(JoinGameText));
        OnPropertyChanged(nameof(InstallForInviteText));
        var generation = ++_pickGeneration;
        if (JoinGame is not { } game)
        {
            JoinMatch = null;
            JoinPicking = false;
            return;
        }
        JoinPicking = true;
        var test = TestCandidates;
        var ids = Instances.ToList();
        var current = TargetInstance;
        _ = Task.Run(() =>
        {
            InstanceMatch? match = null;
            try
            {
                match = InstancePicker.Pick(game, test?.Invoke() ?? Candidates(game, ids, current));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            {
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _pickGeneration) return;
                JoinPicking = false;
                JoinMatch = match;
                if (match is not null && Instances.Contains(match.Instance.Id)) TargetInstance = match.Instance.Id;
            });
        });
    }

    /// <summary>The installed instances, the selected one first; mods are hashed only for those with the host's version and loader.</summary>
    private IEnumerable<InstanceCandidate> Candidates(InviteGame game, List<string> ids, string? current)
    {
        foreach (var id in ids.OrderBy(i => i == current ? 0 : 1))
        {
            Core.Content.GameInstance instance;
            try
            {
                instance = _service.GetInstance(id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            {
                continue;
            }
            var probe = new InstanceCandidate(id, instance.McVersion, instance.Loader, 0);
            if (!InstancePicker.SameGame(game, probe))
            {
                yield return probe;
                continue;
            }
            var manifest = ModManifest.Read(instance.ModsDir);
            yield return probe with { ModCount = manifest.Mods.Count, Fingerprint = manifest.Fingerprint };
        }
    }

    /// <summary>Host side: the room's game for new invites (version, loader, mod list fingerprint).</summary>
    private async Task<InviteGame?> HostGameAsync()
    {
        if (TargetInstance is not { } id) return null;
        try
        {
            return await Task.Run(() =>
            {
                var instance = _service.GetInstance(id);
                return ModManifest.Read(instance.ModsDir).ToInviteGame(instance.McVersion, instance.Loader);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
}
