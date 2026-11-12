using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// “Open like last time” (official §6.14): the same world, the same first invite (uses, how long, asks me or not) and the
/// same “who can find it”. A password is never stored: a password room asks for it again in room settings.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private LastRoom? LastRoomSettings => _service.Settings.MultiplayerLastRoom;

    public bool HasLastRoom => LastRoomSettings is not null;

    /// <summary>“Last time: New World · asks me first · invite code only”.</summary>
    public string LastRoomText => LastRoomSettings is not { } last ? "" : L.F("multiplayer.lobby.last_room",
        string.Join(" · ", new[]
        {
            last.World ?? "",
            L.T(last.AutoApprove ? "multiplayer.invite.terms_auto" : "multiplayer.invite.terms_ask"),
            // Rooms remembered before the four choices existed say how they were listed instead.
            last.Access is null ? VisibilityChoices.FirstOrDefault(v => v.Mode.ToString() == last.Visibility)?.Label ?? ""
                : WhoChoices.FirstOrDefault(c => c.Id == last.Access)?.Label ?? "",
            last.Route is null or nameof(RoomRoute.Auto) ? "" : RouteChoices.FirstOrDefault(c => c.Id == last.Route)?.Label ?? "",
            last.Scope == nameof(RoomScope.WholeLan) ? ScopeChoices[1].Label : "",
        }.Where(p => p.Length > 0)));

    internal void NotifyLastRoom()
    {
        OnPropertyChanged(nameof(HasLastRoom));
        OnPropertyChanged(nameof(LastRoomText));
    }

    /// <summary>Remember the room's world and first-invite terms (called when the room's first invite is made).</summary>
    private void RememberLastRoom(int uses, TimeSpan? lifetime, bool autoApprove)
    {
        var last = LastRoomSettings ?? new LastRoom();
        last.World = SelectedLanGame?.Game.Motd ?? last.World;
        last.Uses = uses;
        last.LifetimeMinutes = lifetime is { } l ? (int)l.TotalMinutes : null;
        last.AutoApprove = autoApprove;
        last.Visibility = nameof(RoomVisibility.Hidden);
        last.Access = SelectedWho.Id;
        last.Route = SelectedRoute.Id;
        last.Protection = SelectedProtection?.Id;
        last.Scope = SelectedScope.Id;
        _service.Settings.MultiplayerLastRoom = last;
        _service.Settings.Save();
        OnPropertyChanged(nameof(HasLastRoom));
        OnPropertyChanged(nameof(LastRoomText));
    }

    /// <summary>Remember how the room was published (after it worked).</summary>
    private void RememberLastVisibility(RoomVisibility mode)
    {
        if (LastRoomSettings is not { } last) return;
        last.Visibility = mode.ToString();
        last.RoomName = PublishRoomName;
        last.PublishAutoApprove = PublishAutoApprove;
        _service.Settings.Save();
        OnPropertyChanged(nameof(LastRoomText));
    }

    [RelayCommand]
    private async Task RepeatLastRoomAsync()
    {
        if (LastRoomSettings is not { } last || !IsIdle) return;
        // Same world when the game has it open to LAN again.
        if (last.World is { } world && LanGames.FirstOrDefault(g => g.Game.Motd == world) is { } same) SelectedLanGame = same;
        // The same four choices, as far as they can be picked today. A serverless room makes a new sealed code each
        // time, so it is opened the usual way from the button.
        PickOpenChoices(last.Access, last.Route, last.Protection, last.Scope);
        if (IsServerlessChosen)
        {
            await HostFromLobbyAsync();
            return;
        }
        await CreateRoomAsync();
        if (!IsHosting) return;
        var lifetime = last.LifetimeMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;
        // The panel shows the closest of its own choices; the invite itself is made with the remembered terms.
        PickInviteChoices(InviteOffer.LifeFromMinutes(last.LifetimeMinutes), InviteOffer.UseFromCount(last.Uses));
        InviteAutoApprove = last.AutoApprove;
        InviteDialogOpen = true;
        if (await IssueInviteAsync(Math.Clamp(last.Uses, 1, 20), lifetime, last.AutoApprove) is null) return;
        if (AutoCopyInvite && _lastInvite is { } invite && await SecretClipboard.CopyAsync(invite.ToString()))
            SetStatus(StatusMessage.Success("multiplayer.lobby.invite_copied"));
        if (last.Access == nameof(RoomAccess.Friends))
        {
            await ApplyAccessAsync(RoomAccess.Friends);
            return;
        }
        if (!Enum.TryParse<RoomVisibility>(last.Visibility, out var mode) || mode == RoomVisibility.Hidden) return;
        if (mode == RoomVisibility.Password)
        {
            // Passwords are never stored: ask for it again where it is set.
            SelectedVisibility = VisibilityChoices.First(v => v.Mode == mode);
            RoomSettingsOpen = true;
            SetStatus(StatusMessage.Info("multiplayer.lobby.last_room_password"));
            return;
        }
        if (!ShowPublicNetworks || !IsPublicConnected) return;
        SelectedVisibility = VisibilityChoices.First(v => v.Mode == mode);
        PublishRoomName = last.RoomName ?? PublishRoomName;
        PublishAutoApprove = last.PublishAutoApprove;
        await PublishRoomAsync();
    }
}
