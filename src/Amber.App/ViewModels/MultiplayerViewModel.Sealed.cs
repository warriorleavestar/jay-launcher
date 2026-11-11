using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// Sealed direct codes: a direct room with no server at all. The host's code and the friend's reply carry the
/// addresses encrypted with a 16-digit passphrase that Jay makes up and the host tells the friend by another way
/// (a call, in person). See <see cref="SealedDirect"/>.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private SealedOffer? _sealedOffer;
    private PendingInvite? _sealedInvite;
    private OpenedOffer? _openedOffer;

    /// <summary>The pasted code is a sealed offer: the join box asks for the passphrase.</summary>
    public bool JoinCodeIsSealed => JoinCodeKind == PastedCodeKind.SealedOffer;

    [ObservableProperty] public partial string JoinPassphrase { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSealedOffer))]
    public partial string SealedOfferCode { get; set; } = "";

    public bool HasSealedOffer => SealedOfferCode.Length > 0;

    [ObservableProperty] public partial string SealedPassphrase { get; set; } = "";
    [ObservableProperty] public partial string SealedReplyInput { get; set; } = "";
    [ObservableProperty] public partial bool IsSealing { get; set; }

    /// <summary>Guest: the sealed reply to send back to the host.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMySealedReply))]
    public partial string MySealedReply { get; set; } = "";

    public bool HasMySealedReply => MySealedReply.Length > 0;

    /// <summary>Host: a one-use, 10-minute invite plus this room's addresses, sealed with a new passphrase.</summary>
    [RelayCommand]
    private async Task CreateSealedOfferAsync()
    {
        if (_direct is not { IsHost: true, LocalAddresses: { } addresses } || _lobby is not { } lobby || _credentials is not { } credentials || IsSealing) return;
        IsSealing = true;
        try
        {
            ClearSealedOffer();
            var invite = lobby.Book.Create(1, SealedDirect.Lifetime, false, credentials.Tunnel);
            RefreshInvites();
            await lobby.UpdateAsync();
            // Argon2id takes a moment: off the UI thread.
            var offer = await Task.Run(() => SealedDirect.Seal(addresses, invite.Invite, DateTimeOffset.UtcNow));
            if (_lobby != lobby)
            {
                offer.Dispose();
                return;
            }
            _sealedOffer = offer;
            _sealedInvite = invite;
            SealedOfferCode = offer.Code;
            SealedPassphrase = offer.Passphrase;
            SetStatus(StatusMessage.Success("multiplayer.sealed.created"));
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus(StatusMessage.Failure("multiplayer.invite.lobby_failed", e, KnockProtocol.Port, ErrorText.Of(e)));
        }
        finally
        {
            IsSealing = false;
        }
    }

    [RelayCommand]
    private async Task CopySealedCodeAsync()
    {
        if (HasSealedOffer && await SecretClipboard.CopyAsync(SealedOfferCode)) SetStatus(StatusMessage.Success("multiplayer.sealed.code_copied"));
    }

    [RelayCommand]
    private async Task CopySealedPassphraseAsync()
    {
        if (SealedPassphrase.Length > 0 && await SecretClipboard.CopyAsync(SealedPassphrase)) SetStatus(StatusMessage.Success("multiplayer.sealed.passphrase_copied"));
    }

    [RelayCommand]
    private async Task CopyMySealedReplyAsync()
    {
        if (HasMySealedReply && await SecretClipboard.CopyAsync(MySealedReply)) SetStatus(StatusMessage.Success("multiplayer.sealed.reply_copied"));
    }

    /// <summary>Host: opens the friend's sealed reply and punches towards the friend.</summary>
    [RelayCommand]
    private async Task AddSealedReplyAsync()
    {
        if (_direct is not { IsHost: true } direct || _sealedOffer is not { } offer) return;
        DirectAddresses guest;
        try
        {
            guest = offer.OpenReply(SealedReplyInput, DateTimeOffset.UtcNow);
        }
        catch (InvalidDataException e)
        {
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Warning));
            return;
        }
        SealedReplyInput = "";
        SetStatus(StatusMessage.Info("multiplayer.direct.punching"));
        try
        {
            SetStatus(await direct.AddGuestAsync(guest)
                ? StatusMessage.Success("multiplayer.sealed.punched")
                : StatusMessage.Warning("multiplayer.sealed.punch_failed"));
        }
        catch (DirectFailedException e)
        {
            SetStatus(StatusMessage.Plain(e.Message, StatusKind.Error));
        }
    }

    /// <summary>
    /// Guest: opens the sealed offer with the passphrase, makes the sealed reply (shown to copy back to the host), punches
    /// towards the host, knocks over the bridge and joins once approved.
    /// </summary>
    private async Task JoinSealedAsync()
    {
        if (SealedDirect.NormalizePassphrase(JoinPassphrase) is null)
        {
            SetStatus(StatusMessage.Warning("multiplayer.sealed.enter_passphrase"));
            return;
        }
        if (!IsComponentReady)
        {
            SetStatus(StatusMessage.Warning("multiplayer.component.download_required"));
            return;
        }
        var code = JoinCode;
        var passphrase = JoinPassphrase;
        OpenedOffer opened;
        IsKnocking = true;
        try
        {
            SetStatus(StatusMessage.Info("multiplayer.sealed.opening"));
            opened = await Task.Run(() => SealedDirect.Open(code, passphrase, DateTimeOffset.UtcNow));
        }
        catch (InvalidDataException e)
        {
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Warning));
            return;
        }
        finally
        {
            IsKnocking = false;
        }
        if (opened.Invite.Tunnel.Mode == TransportMode.Extreme && !NoiseHandshake.HybridSupported)
        {
            opened.Dispose();
            SetStatus(StatusMessage.Warning("multiplayer.room.extreme_unsupported"));
            return;
        }
        var bridgeKey = DirectKeys.Bridge(opened.Invite.LobbyKey);
        var direct = new DirectSession(_service.EasyTier, EasyTierWorkRoot, null, false, MakeDirectOptions(), _diag) { BridgeKey = bridgeKey };
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bridgeKey);
        SubscribeGuestDirect(direct);
        try
        {
            SetStatus(StatusMessage.Info("multiplayer.direct.gathering_guest"));
            var mine = await direct.AnswerAsync(opened.Host) ?? throw new DirectFailedException(L.T("core.mp.session.nooffer"));
            Mode = "join";
            IsDirect = true;
            _direct = direct;
            _openedOffer = opened;
            RoomCodeText = "";
            JoinCode = "";
            JoinPassphrase = "";
            MySealedReply = SealedDirect.SealReply(opened, mine, DateTimeOffset.UtcNow);
            DirectHint = L.T("multiplayer.sealed.guest_hint");
            SetStatus(StatusMessage.Info("multiplayer.sealed.send_reply"));
            if (!await direct.JoinThroughBridgeAsync((peers, ct) => DirectKnockAsync(opened.Invite, peers, ct)))
            {
                // Turned down or expired: the reason is already shown.
                await LeaveFailedDirectAsync(direct);
                return;
            }
            MySealedReply = "";
            FinishDirectJoin(direct, direct.Room);
        }
        catch (TimeoutException e)
        {
            await LeaveFailedDirectAsync(direct);
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Warning));
        }
        catch (Exception e) when (e is DirectFailedException or OperationCanceledException or IOException or System.Net.Sockets.SocketException or InvalidOperationException
            or System.ComponentModel.Win32Exception or PlatformNotSupportedException or System.Security.Cryptography.CryptographicException or InvalidDataException or System.Text.Json.JsonException)
        {
            await LeaveFailedDirectAsync(direct);
            SetStatus(StatusMessage.Failure("multiplayer.direct.connect_failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>Guest of a sealed code: knocks in the host's lobby over the bridge (or the relays) and waits for approval.</summary>
    private async Task<RoomCode?> DirectKnockAsync(JayInvite invite, IReadOnlyList<string> peers, CancellationToken ct)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsKnocking = true;
            SetStatus(StatusMessage.Info("multiplayer.invite.entering_lobby"));
        });
        try
        {
            var me = Identity.KeyPair;
            var name = ChatName;
            var (device, account, jay) = await Dispatcher.UIThread.InvokeAsync(() => (Device, AccountToken, JayCertificate));
            var outcome = await Task.Run(() => LobbyJoin.KnockAsync(_service.EasyTier, Path.Combine(EasyTierWorkRoot, "lobby-join"), peers, invite,
                me, name, Amber.Core.Launch.UpdateChecker.CurrentVersion, line => _diag.Add(L.T("multiplayer.invite.diagnostics_category"), line),
                () => Dispatcher.UIThread.Post(() => SetStatus(StatusMessage.Info("multiplayer.invite.waiting_approval"))), ct,
                () => Dispatcher.UIThread.Post(() => SetStatus(StatusMessage.Info("multiplayer.invite.entering_lobby"))), device, account, jay), ct);
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MySealedReply = "";
                if (outcome.Credentials is not { } credentials)
                {
                    SetStatus(StatusMessage.Warning(KnockReasonKey(outcome.Reason)));
                    return null;
                }
                if (credentials.Tunnel.Mode == TransportMode.Extreme && !NoiseHandshake.HybridSupported)
                {
                    SetStatus(StatusMessage.Warning("multiplayer.room.extreme_unsupported"));
                    return null;
                }
                HostFingerprint = outcome.HostFingerprint;
                HostJayAccount = outcome.HostJayAccount;
                SetStatus(StatusMessage.Info("multiplayer.direct.punching"));
                return (RoomCode?)credentials.ToRoom();
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsKnocking = false);
        }
    }

    private void ClearSealedOffer()
    {
        if (_sealedInvite is { } invite && _lobby is { } lobby && invite.UsesLeft > 0)
        {
            lobby.Book.Revoke(invite);
            RefreshInvites();
        }
        _sealedInvite = null;
        _sealedOffer?.Dispose();
        _sealedOffer = null;
        SealedOfferCode = "";
        SealedPassphrase = "";
        SealedReplyInput = "";
    }

    private void ClearSealed()
    {
        ClearSealedOffer();
        _openedOffer?.Dispose();
        _openedOffer = null;
        MySealedReply = "";
    }
}
