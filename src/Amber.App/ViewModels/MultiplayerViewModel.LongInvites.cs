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
/// approves each knock and checks the safety code. Managed in multiplayer settings › invites; every room I open accepts them.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private LongLivedInvites? _longInvites;

    private LongLivedInvites LongInvites => _longInvites ??= new LongLivedInvites(Path.Combine(AppSettings.DataDir, "long-invites.json"));

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

    /// <summary>Make one (30 days), show it in the list and copy it (cleared from the clipboard later).</summary>
    [RelayCommand]
    private async Task CreateLongInviteAsync()
    {
        LongLivedInvite invite;
        try
        {
            invite = LongInvites.Create(LongInviteLabel);
        }
        catch (InvalidOperationException)
        {
            SetStatus(StatusMessage.Warning("multiplayer.longinvite.too_many", LongLivedInvites.MaxInvites));
            return;
        }
        LongInviteLabel = "";
        RefreshLongInvites();
        // A room is open: it accepts the new invite at once (when it already shares the key).
        if (_lobby is { } lobby && LongInvites.SeedInto(lobby.Book, _credentials?.Tunnel ?? TunnelSettings.Default) > 0) _ = UpdateLobbyQuietlyAsync(lobby);
        if (await SecretClipboard.CopyAsync(LongInvites.CodeOf(invite, TunnelSettings.Default).ToString()))
            SetStatus(StatusMessage.Success("multiplayer.longinvite.created"));
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
        RefreshLongInvites();
        SetStatus(StatusMessage.Info("multiplayer.longinvite.revoked", item.Label));
    }

    /// <summary>A new relay room: accept the long-lived invites too (before its own first invite, so they share the lobby key).</summary>
    private void SeedLongInvites(HostLobby lobby, TunnelSettings tunnel)
    {
        if (LongInvites.SeedInto(lobby.Book, tunnel) > 0) _ = UpdateLobbyQuietlyAsync(lobby);
    }

    private async Task UpdateLobbyQuietlyAsync(HostLobby lobby)
    {
        try
        {
            await lobby.UpdateAsync();
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus(StatusMessage.Failure("multiplayer.invite.lobby_failed", e, KnockProtocol.Port, ErrorText.Of(e)));
        }
    }
}
