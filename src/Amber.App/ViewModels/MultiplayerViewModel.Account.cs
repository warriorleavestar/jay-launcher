using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace Amber.App.ViewModels;

/// <summary>两步验证 / 第三方验证的一个输入框：节点要什么就显示什么。</summary>
public sealed partial class AccountFactorInput(LoginFactorPrompt prompt) : ObservableObject
{
    public string Name => prompt.Name;
    public string Prompt => prompt.Prompt;
    [ObservableProperty] public partial string Value { get; set; } = "";
}

/// <summary>
/// 公共网络的账号登录：网络配置里有 login 段时显示。账号密码封给节点的登录公钥再发，拿回的凭证绑定这台 Jay 的联机身份，
/// 只放在内存里（关掉 Jay 就要重新登录）。敲门时带上凭证；网络要求登录时，自己开的房间也只让登录过的人进。
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private (string Network, string Token, DateTimeOffset Expires)? _account;

    [ObservableProperty] public partial string AccountUser { get; set; } = "";
    [ObservableProperty] public partial string AccountPassword { get; set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAccountLoggedIn), nameof(ShowAccountLogin))] public partial string AccountLoggedInAs { get; set; } = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoginAccountCommand))] public partial bool IsAccountLoggingIn { get; set; }

    /// <summary>节点要求的第二步验证（密码对了以后才会出现；网络配置声明了的会先显示）。</summary>
    public ObservableCollection<AccountFactorInput> AccountFactors { get; } = [];

    public bool HasNetworkLogin => _networkConfig?.Login is not null;
    public bool IsNetworkLoginRequired => _networkConfig?.Login?.Required == true;
    public bool IsAccountLoggedIn => AccountLoggedInAs.Length > 0;
    public bool ShowAccountLogin => HasNetworkLogin && !IsAccountLoggedIn;
    public bool HasAccountFactors => AccountFactors.Count > 0;

    /// <summary>敲门时带的凭证：只在还连着发凭证的那个网络、没过期时给。</summary>
    internal string? AccountToken =>
        _account is { } a && _networkConfig?.Network == a.Network && a.Expires > DateTimeOffset.UtcNow ? a.Token : null;

    /// <summary>房主的账号要求：网络配置要求登录时，敲门的人要带这个网络节点签的、绑定自己的凭证。</summary>
    internal AccountPolicy? HostAccountPolicy =>
        _networkConfig is { Login: { Required: true } login } config ? new AccountPolicy(config.Network, login.SignKey) : null;

    /// <summary>换网络、断开时调：登录框按新配置显示，旧网络的凭证作废。</summary>
    private void ResetAccount()
    {
        if (AccountToken is null)
        {
            _account = null;
            AccountLoggedInAs = "";
        }
        AccountFactors.Clear();
        foreach (var name in _networkConfig?.Login?.Factors ?? [])
            AccountFactors.Add(new AccountFactorInput(new LoginFactorPrompt(name, name == "totp" ? L.T("core.mp.factor.totp") : name)));
        OnPropertyChanged(nameof(HasAccountFactors));
        OnPropertyChanged(nameof(HasNetworkLogin));
        OnPropertyChanged(nameof(IsNetworkLoginRequired));
        OnPropertyChanged(nameof(ShowAccountLogin));
    }

    private bool CanLoginAccount() => !IsAccountLoggingIn;

    [RelayCommand(CanExecute = nameof(CanLoginAccount))]
    private async Task LoginAccountAsync()
    {
        if (_public is not { } network || _networkConfig is not { Login: { } login } config)
        {
            SetStatus(StatusMessage.Warning("multiplayer.account.connect_first"));
            return;
        }
        if (AccountUser.Trim().Length == 0 || AccountPassword.Length == 0)
        {
            SetStatus(StatusMessage.Warning("multiplayer.account.fill"));
            return;
        }
        IsAccountLoggingIn = true;
        try
        {
            var factors = AccountFactors.Where(f => f.Value.Trim().Length > 0).ToDictionary(f => f.Name, f => f.Value.Trim());
            var (token, parsed) = await DirectoryClient(network).LoginAsync(Identity.KeyPair, login, config.Network, AccountUser.Trim(), AccountPassword,
                CancellationToken.None, factors);
            if (_networkConfig?.Network != config.Network) return;
            _account = (config.Network, token, parsed.Expires);
            AccountPassword = "";
            foreach (var f in AccountFactors) f.Value = "";
            AccountLoggedInAs = parsed.User;
            SetStatus(StatusMessage.Success("multiplayer.account.logged_in", parsed.User));
        }
        catch (SecondFactorRequiredException need)
        {
            // 节点要哪几步就显示哪几步（保留已经填的）。
            var old = AccountFactors.ToDictionary(f => f.Name, f => f.Value);
            AccountFactors.Clear();
            foreach (var p in need.Need) AccountFactors.Add(new AccountFactorInput(p) { Value = need.Wrong ? "" : old.GetValueOrDefault(p.Name) ?? "" });
            OnPropertyChanged(nameof(HasAccountFactors));
            SetStatus(new StatusMessage("common.raw", [need.Message], need.Wrong ? StatusKind.Error : StatusKind.Info));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Net.Sockets.SocketException or TimeoutException)
        {
            SetStatus(StatusMessage.Error("multiplayer.account.failed", e.Message));
        }
        finally
        {
            IsAccountLoggingIn = false;
        }
    }

    [RelayCommand]
    private void LogoutAccount()
    {
        _account = null;
        AccountLoggedInAs = "";
        ResetAccount();
    }
}
