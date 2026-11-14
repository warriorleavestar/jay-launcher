using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// 联机安全：开房时打开设备验证；房间里确认有人冒充（拿着别人的钥匙却证明不了设备、或对面冒充房主）时熔断：
/// 房主踢掉冒充者并通知所有人下线，所有人断开后倒计时（默认 7 秒，空格取消）重启 Jay。
/// 熔断只在房间里生效，只认房主经已验证通道发来的通知；没有任何外部（开发者、服务器）能触发它。
/// </summary>
public sealed partial class MultiplayerViewModel
{
    /// <summary>自动重启的限速：这么久之内只自动重启一次。</summary>
    internal static readonly TimeSpan BreakerRestartCooldown = TimeSpan.FromMinutes(10);

    /// <summary>公开网络配置里的最低安全要求（policy.security）；没进公开网络或没写时 null。</summary>
    internal SecurityPolicy? NetworkSecurityPolicy { get; set; }

    /// <summary>当前生效的安全策略：本机设置，再按公开网络的最低要求收紧。</summary>
    internal SecurityPolicy Security => _service.Settings.LocalSecurityPolicy().Tighten(NetworkSecurityPolicy);

    /// <summary>自检用：当前设置（倒计时、跳过等待、上次自动重启时间）。</summary>
    internal AppSettings SecuritySettings => _service.Settings;

    /// <summary>测试用：重启这一步（默认真的重启 Jay）。</summary>
    internal Action RestartApp { get; set; } = AppRestart.Restart;

    [ObservableProperty] public partial bool IsBreakerCounting { get; set; }
    [ObservableProperty] public partial int BreakerSecondsLeft { get; set; }
    [ObservableProperty] public partial string BreakerReason { get; set; } = "";

    public string BreakerCountdownText => L.F("multiplayer.breaker.countdown", BreakerSecondsLeft);

    partial void OnBreakerSecondsLeftChanged(int value) => OnPropertyChanged(nameof(BreakerCountdownText));

    private CancellationTokenSource? _breakerCts;

    /// <summary>开房时：按安全策略给成员表配上本机设备密钥和验证方案（方案从本机的私有索引读）。</summary>
    private void ConfigureSecurity(MemberTable members)
    {
        var policy = Security;
        if (policy.Attestation == AttestationMode.Off) return;
        members.Attestation = (Identity.KeyPair, AttestationIndex.Active(AppSettings.DataDir));
        members.RequireAttestation = policy.Attestation == AttestationMode.Required;
        members.ImpersonationDetected += entry => Dispatcher.UIThread.Post(() => _ = OnImpersonationAsync(members, entry));
    }

    /// <summary>房主：有人拿着某个成员的钥匙却证明不了设备。</summary>
    private async Task OnImpersonationAsync(MemberTable members, MemberEntry entry)
    {
        if (!ReferenceEquals(_members, members) || !IsHosting) return;
        _diag.Add(L.T("multiplayer.invite.diagnostics_category"), L.F("multiplayer.breaker.detected_member", entry.Name, entry.Fingerprint));
        if (!Security.Breaker)
        {
            // 不熔断：只把这个成员踢掉（换网络，留下的人不受影响）。
            if (Members.FirstOrDefault(m => m.PublicKey.AsSpan().SequenceEqual(entry.PublicKey)) is { } item) await KickMemberAsync(item);
            else members.Kick(entry, _lobby?.Book);
            SetStatus(StatusMessage.Warning("multiplayer.breaker.kicked_impostor", entry.Name));
            return;
        }
        members.Kick(entry, _lobby?.Book);
        // Telling the members is best effort; the breaker must trip whatever happens to the broadcast.
        if (_hub is { } hub)
        {
            try { await hub.BroadcastBreakerAsync(); }
            catch (Exception e) { System.Diagnostics.Trace.WriteLine("Jay: breaker broadcast failed: " + e.Message); }
        }
        // The host may have ended this room and opened another during the broadcast: never tear down the new one.
        if (!ReferenceEquals(_members, members)) return;
        TripBreaker(L.F("multiplayer.breaker.reason_member", entry.Name));
    }

    /// <summary>加入方：房主通知房间熔断。</summary>
    private void OnBreakerFromHost(ChatClient client)
    {
        if (_chat != client || !IsJoined) return;
        TripBreaker(L.T("multiplayer.breaker.reason_host"));
    }

    /// <summary>加入方：对面证明不了自己是房主那台电脑。</summary>
    private void OnFakeHost()
    {
        if (!IsJoined) return;
        TripBreaker(L.T("multiplayer.breaker.reason_fake_host"));
    }

    /// <summary>熔断：马上断开所有联机；策略允许时倒计时重启。</summary>
    internal void TripBreaker(string reason)
    {
        Amber.Core.Diagnostics.ExitTrail.Note("breaker", $"tripped, breaker {(Security.Breaker ? "on" : "off")}");
        StopSession();
        BreakerReason = reason;
        var policy = Security;
        if (!policy.Breaker)
        {
            SetStatus(StatusMessage.Error("multiplayer.breaker.disconnected", reason));
            return;
        }
        var last = _service.Settings.LastBreakerRestart;
        if (last is { } t && DateTimeOffset.UtcNow - t < BreakerRestartCooldown)
        {
            SetStatus(StatusMessage.Error("multiplayer.breaker.no_restart", reason));
            return;
        }
        SetStatus(StatusMessage.Error("multiplayer.breaker.disconnected", reason));
        _breakerCts?.Cancel();
        _breakerCts = new CancellationTokenSource();
        _ = CountdownAsync(policy.EffectiveRestartDelay, _breakerCts.Token);
    }

    private async Task CountdownAsync(int seconds, CancellationToken ct)
    {
        try
        {
            if (seconds > 0)
            {
                IsBreakerCounting = true;
                for (var left = seconds; left > 0; left--)
                {
                    BreakerSecondsLeft = left;
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
            IsBreakerCounting = false;
            _service.Settings.LastBreakerRestart = DateTimeOffset.UtcNow;
            _service.Settings.Save();
            RestartApp();
        }
        catch (OperationCanceledException)
        {
            IsBreakerCounting = false;
        }
    }

    /// <summary>Exit trail: the multiplayer steps by status key only (the arguments can hold codes, names and addresses).</summary>
    protected override void OnStatusChanged()
    {
        if (StatusInfo is not { } status) return;
        Amber.Core.Diagnostics.ExitTrail.Note("mp", $"{status.Kind}: {status.Key}");
        if (!_settingRoomStatus) ActionStatus?.Invoke(status);
    }

    /// <summary>
    /// What a multiplayer action says (a relay added, a backup saved, an error), not the room's running headcount. Settings
    /// and the Me page run some of these commands and show the result on their own status bar while they are on screen.
    /// </summary>
    public event Action<StatusMessage>? ActionStatus;

    private bool _settingRoomStatus;

    /// <summary>倒计时里按空格（或点按钮）：不重启，联机已经断开了。</summary>
    [RelayCommand]
    internal void CancelBreakerRestart()
    {
        if (!IsBreakerCounting) return;
        _breakerCts?.Cancel();
        _breakerCts = null;
        IsBreakerCounting = false;
        SetStatus(StatusMessage.Warning("multiplayer.breaker.cancelled"));
    }
}
