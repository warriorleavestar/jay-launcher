using System.Collections.ObjectModel;
using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One long-lived invite in multiplayer settings: whose it is, how long it has left, the (masked) code.</summary>
public sealed partial class LongInviteItem(LongLivedInvite invite, string code) : ObservableObject
{
    public LongLivedInvite Invite { get; } = invite;
    public string Code { get; } = code;

    public string Label => Invite.Label.Length > 0 ? Invite.Label : L.T("multiplayer.longinvite.unnamed");

    public string Left => L.P("multiplayer.longinvite.days_left", DaysLeft, DaysLeft);

    private int DaysLeft => Math.Max(1, (int)Math.Ceiling((Invite.Expires - DateTimeOffset.UtcNow).TotalDays));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeDisplay))]
    public partial bool Shown { get; set; }

    public string CodeDisplay => Shown ? Code : Code[..(Code.IndexOf('-') + 1)] + new string('•', 8) + Code[^4..];
}

/// <summary>
/// Long-lived invites for friends you play with regularly (official §6.15): 30 days, revoke any time, the host still
/// approves each knock and checks the safety code. Managed in multiplayer settings › invites. Every room I open through a
/// server accepts them; a room with no server cannot (a guest finds it only through that room's own sealed code).
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private LongLivedInvites? _longInvites;

    private string? _longInvitesFile;

    /// <summary>Where the thirty-day invites are kept. Tests point it at a folder of their own.</summary>
    internal string LongInvitesFile
    {
        get => _longInvitesFile ?? Path.Combine(AppSettings.DataDir, "long-invites.json");
        set
        {
            _longInvitesFile = value;
            _longInvites = null;
            RefreshLongInvites();
        }
    }

    private LongLivedInvites LongInvites => _longInvites ??= new LongLivedInvites(LongInvitesFile);

    public ObservableCollection<LongInviteItem> LongInviteItems { get; } = [];

    /// <summary>Who the new long-lived invite is for (a name to recognise it by; optional).</summary>
    [ObservableProperty] public partial string LongInviteLabel { get; set; } = "";

    public bool HasLongInvites => LongInviteItems.Count > 0;

    private void RefreshLongInvites()
    {
        LongInviteItems.Clear();
        foreach (var invite in LongInvites.Active)
            LongInviteItems.Add(new LongInviteItem(invite, LongInvites.CodeOf(invite, TunnelSettings.Default).ToString()));
        OnPropertyChanged(nameof(HasLongInvites));
    }

    /// <summary>
    /// Make one (30 days) from the room's invite panel: it goes on the invite card like any other invite, and into the
    /// list of thirty-day invites under it, where it can be revoked. When this room cannot accept it (its other
    /// invites use another lobby key), the card says it works from the next room on.
    /// </summary>
    private async Task CreateLongInviteAsync()
    {
        LongLivedInvite invite;
        try
        {
            // Thirty-day invites keep a lobby key of their own, never the open room's: this room accepts the new
            // invite at once only when it has no other invite out (or already runs on that key).
            invite = LongInvites.Create(LongInviteLabel);
        }
        catch (InvalidOperationException)
        {
            SetStatus(StatusMessage.Warning("multiplayer.longinvite.too_many", LongLivedInvites.MaxInvites));
            return;
        }
        LongInviteLabel = "";
        RefreshLongInvites();
        var accepted = true;
        if (_lobby is { } lobby)
        {
            lobby.Book.Game = await HostGameAsync();
            if (_lobby == lobby)
            {
                LongInvites.SeedInto(lobby.Book, _credentials?.Tunnel ?? TunnelSettings.Default);
                accepted = lobby.Book.Pending.Any(p => p.Standing && p.Invite.Token.AsSpan().SequenceEqual(invite.Token));
                // The knock lobby has to (re)start for it: if it can't, the invite is kept for the next room and the
                // lobby's error stays on screen instead of "created".
                if (accepted && !await UpdateLobbyQuietlyAsync(lobby))
                {
                    ShowLongInvite(invite, worksNow: false);
                    return;
                }
            }
        }
        ShowLongInvite(invite, worksNow: accepted);
        SetStatus(StatusMessage.Success(accepted ? "multiplayer.longinvite.created" : "multiplayer.longinvite.created_next_room"));
    }

    [RelayCommand]
    private async Task CopyLongInviteAsync(LongInviteItem? item)
    {
        if (item is not null && await SecretClipboard.CopyAsync(item.Code)) SetStatus(StatusMessage.Success("multiplayer.invite.copied"));
    }

    [RelayCommand]
    private static void ToggleLongInviteShown(LongInviteItem? item)
    {
        if (item is not null) item.Shown = !item.Shown;
    }

    /// <summary>Revoke: the code stops working now, also in a room that is open.</summary>
    [RelayCommand]
    private void RevokeLongInvite(LongInviteItem? item)
    {
        if (item is null) return;
        LongInvites.Revoke(item.Invite.Id);
        if (_lobby is { } lobby)
            foreach (var pending in lobby.Book.Pending.Where(p => p.Standing && p.Invite.Token.AsSpan().SequenceEqual(item.Invite.Token)))
                lobby.Book.Revoke(pending);
        var shown = _lastLongInvite?.Invite.Id == item.Invite.Id;
        RefreshLongInvites();
        if (shown) ForgetShownInvite();
        SetStatus(StatusMessage.Info("multiplayer.longinvite.revoked", item.Label));
    }

    /// <summary>A new relay room: accept the long-lived invites too (before its own first invite, so they share the lobby key).</summary>
    private void SeedLongInvites(HostLobby lobby, TunnelSettings tunnel)
    {
        if (LongInvites.SeedInto(lobby.Book, tunnel) > 0) _ = UpdateLobbyQuietlyAsync(lobby);
    }

    /// <summary>Tests: stands in for starting the knock lobby (to make it fail).</summary>
    internal Func<Task>? LobbyUpdateForTest { get; set; }

    /// <summary>False when the lobby couldn't start (the error is on screen).</summary>
    private async Task<bool> UpdateLobbyQuietlyAsync(HostLobby lobby)
    {
        try
        {
            await (LobbyUpdateForTest?.Invoke() ?? lobby.UpdateAsync());
            return true;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus(StatusMessage.Failure("multiplayer.invite.lobby_failed", e, KnockProtocol.Port, ErrorText.Of(e)));
            return false;
        }
    }
}
