using Amber.App.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Localization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public sealed partial class AccountItem(Account account) : LocalizedObservable
{
    public Account Account { get; } = account;
    public string Name => Account.Name;
    public string KindText => Account.KindText;
    public string Initial => Account.Name.Length > 0 ? Account.Name[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoAvatar))]
    public partial Bitmap? Head { get; set; }

    [ObservableProperty]
    public partial Bitmap? Body { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public bool HasNoAvatar => Head is null;

    /// <summary>"Jay account jay1qxy7…k3pf" when a Jay account is bound, else null.</summary>
    public string? JayText => Account.JayAddress is { } address ? L.F("accounts.jay.bound_line", Amber.Core.Identity.JayAddress.Short(address)) : null;
    public bool HasJay => Account.JayAddress is not null;

    /// <summary>旧版本存下的 http 外置账号：现在不能再用，提示用 https 重新添加。</summary>
    public bool InsecureServer { get; } = ExternalAuth.HasInsecureServer(account);
}

/// <summary>微软登录失败时面板里的一个按钮：打开网页、重试、改用离线。</summary>
public sealed record LoginAction(string LabelKey, LoginActionKind Kind, string? Url = null) : LocalizedRecord
{
    public string Label => L.T(LabelKey);
}

public enum LoginActionKind { OpenUrl, Retry, UseOffline }

public sealed partial class AccountsViewModel : PageViewModel, ISnapshotDemo
{
    private readonly LauncherService _service;
    private CancellationTokenSource? _loginCts;

    public override string Title => L.T("accounts.title");
    public override string Icon => "M12 12a4.5 4.5 0 1 0 0-9 4.5 4.5 0 0 0 0 9zm0 2c-4.4 0-8 2.2-8 5v2h16v-2c0-2.8-3.6-5-8-5z";

    public ObservableCollection<AccountItem> Accounts { get; } = [];
    /// <summary>三段式选择器的序号（改版规格 3.5）：正版排第一。</summary>
    public const int MicrosoftTab = 0, OfflineTab = 1, ExternalTab = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOfflineTab), nameof(IsMicrosoftTab), nameof(IsExternalTab))]
    public partial int LoginTab { get; set; }

    public bool IsMicrosoftTab
    {
        get => LoginTab == MicrosoftTab;
        set { if (value) LoginTab = MicrosoftTab; }
    }

    public bool IsOfflineTab
    {
        get => LoginTab == OfflineTab;
        set { if (value) LoginTab = OfflineTab; }
    }

    public bool IsExternalTab
    {
        get => LoginTab == ExternalTab;
        set { if (value) LoginTab = ExternalTab; }
    }

    [ObservableProperty]
    public partial AccountItem? Current { get; set; }

    [ObservableProperty]
    public partial string OfflineName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceCode), nameof(CodeCopiedText))]
    public partial string? DeviceCode { get; set; }

    [ObservableProperty]
    public partial string? DeviceUri { get; set; }

    public bool HasDeviceCode => DeviceCode is not null;

    /// <summary>“代码 XXXX 已复制，在打开的网页里粘贴即可”（没复制成功时只说去网页输入）。</summary>
    public string CodeCopiedText => L.F(CodeCopied ? "accounts.microsoft.code_copied" : "accounts.microsoft.code_enter", DeviceCode);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeCopiedText))]
    public partial bool CodeCopied { get; set; }

    /// <summary>点【复制代码】后按钮短暂写“已复制 ✓”。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyCodeLabel))]
    public partial bool JustCopied { get; set; }

    public string CopyCodeLabel => L.T(JustCopied ? "accounts.microsoft.copied" : "accounts.microsoft.copy_code");

    /// <summary>设备码还剩多久有效（“14:32 内有效”）。</summary>
    [ObservableProperty]
    public partial string CountdownText { get; set; } = "";

    /// <summary>设备码过期了：面板留着，给【重新获取代码】。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeActive))]
    public partial bool CodeExpired { get; set; }

    /// <summary>代码还能用（没过期）：显示复制、倒计时、打开网页。</summary>
    public bool CodeActive => !CodeExpired;

    /// <summary>失败原因（null 表示没失败），显示在登录面板里，配下面的按钮。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoginFailure))]
    public partial string? LoginFailure { get; set; }

    public bool HasLoginFailure => LoginFailure is not null;
    public ObservableCollection<LoginAction> LoginActions { get; } = [];

    /// <summary>登录成功后的一行：“已登录 {name}，已设为当前账号”。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoginSuccess))]
    public partial string? LoginSuccess { get; set; }

    public bool HasLoginSuccess => LoginSuccess is not null;

    /// <summary>复制文字、打开网页（测试里换掉，不碰真剪贴板和浏览器）。</summary>
    internal Func<string, Task<bool>> CopyText { get; set; } = AvaloniaClipboard.TrySetTextAsync;

    internal Action<string> OpenUrl { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public const string XboxProfileUrl = "https://account.xbox.com/";
    public const string FamilyUrl = "https://account.microsoft.com/family/";
    public const string XboxSupportUrl = "https://support.xbox.com/";
    public const string BuyUrl = "https://www.minecraft.net/";
    public const string ProfileNameUrl = "https://www.minecraft.net/msaprofile/mygames/editprofile";

    /// <summary>失败种类对应的按钮（改版规格 3.5 失败状态表）。</summary>
    public static LoginAction[] ActionsFor(MicrosoftLoginFailure failure) => failure switch
    {
        MicrosoftLoginFailure.NoXboxProfile => [new("accounts.ms_fail.create_xbox", LoginActionKind.OpenUrl, XboxProfileUrl), new("accounts.ms_fail.retry_done", LoginActionKind.Retry)],
        MicrosoftLoginFailure.ChildAccount => [new("accounts.ms_fail.family_steps", LoginActionKind.OpenUrl, FamilyUrl), new("accounts.ms_fail.retry_handled", LoginActionKind.Retry), new("accounts.ms_fail.use_offline", LoginActionKind.UseOffline)],
        MicrosoftLoginFailure.RegionOrAge => [new("accounts.ms_fail.read_more", LoginActionKind.OpenUrl, XboxSupportUrl), new("accounts.ms_fail.use_offline", LoginActionKind.UseOffline)],
        MicrosoftLoginFailure.NoOwnership => [new("accounts.ms_fail.visit_store", LoginActionKind.OpenUrl, BuyUrl), new("accounts.ms_fail.other_account", LoginActionKind.Retry), new("accounts.ms_fail.use_offline", LoginActionKind.UseOffline)],
        MicrosoftLoginFailure.NoProfileName => [new("accounts.ms_fail.set_name", LoginActionKind.OpenUrl, ProfileNameUrl), new("accounts.ms_fail.retry_done", LoginActionKind.Retry)],
        MicrosoftLoginFailure.Banned => [new("accounts.ms_fail.read_more", LoginActionKind.OpenUrl, XboxSupportUrl), new("accounts.ms_fail.use_offline", LoginActionKind.UseOffline)],
        _ => [new("common.retry", LoginActionKind.Retry)],
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicrosoftMissingClientId))]
    public partial string ClientIdInput { get; set; }

    public bool MicrosoftMissingClientId => string.IsNullOrWhiteSpace(ClientIdInput) && _service.Accounts.MicrosoftClientId is null;

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "";

    [ObservableProperty]
    public partial string ExternalUser { get; set; } = "";

    [ObservableProperty]
    public partial string ExternalPassword { get; set; } = "";

    public ObservableCollection<ExternalProfile> ExternalProfiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsProfileChoice))]
    public partial Account? PendingExternal { get; set; }

    public bool NeedsProfileChoice => PendingExternal is not null;

    [ObservableProperty]
    public partial ExternalProfile? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public AccountsViewModel(LauncherService service)
    {
        _service = service;
        ClientIdInput = service.Settings.MicrosoftClientId ?? "";
    }

    protected override void OnFirstActivated()
    {
        _active = true;
        Rebuild();
    }

    private bool _active;

    private void Rebuild()
    {
        if (!_active) return;
        var selected = _service.Accounts.Store.Selected;
        Accounts.Clear();
        foreach (var account in _service.Accounts.Store.Accounts)
        {
            var item = new AccountItem(account) { IsCurrent = account.Id == selected?.Id };
            Accounts.Add(item);
            _ = LoadSkinAsync(item);
        }
        Current = Accounts.FirstOrDefault(a => a.IsCurrent);
        RebuildJay();
    }

    private async Task LoadSkinAsync(AccountItem item)
    {
        var path = await _service.Accounts.GetSkinFileAsync(item.Account);
        if (path is null) return;
        if (await Task.Run(() => SkinRenderer.Render(path, item.Account.SlimSkin)) is { } rendered)
        {
            item.Head = rendered.Head;
            item.Body = rendered.Body;
        }
    }

    private void Changed()
    {
        Rebuild();
        _service.NotifyAccountsChanged();
    }

    [RelayCommand]
    private void Use(AccountItem? item)
    {
        if (item is null) return;
        _service.Accounts.Store.SelectedId = item.Account.Id;
        _service.Accounts.Store.Save();
        Changed();
    }

    [RelayCommand]
    private void Remove(AccountItem? item)
    {
        if (item is null) return;
        _service.Accounts.Store.Remove(item.Account);
        SetStatus(StatusMessage.Success("accounts.status.removed", item.Name));
        Changed();
    }

    [RelayCommand]
    private void AddOffline()
    {
        var name = OfflineName.Trim();
        if (!IsValidOfflineName(name))
        {
            SetStatus(StatusMessage.Error("accounts.status.invalid_offline_name"));
            return;
        }
        _service.Accounts.AddOffline(name);
        OfflineName = "";
        // 中文之类的名字能用，只提醒一句（规则和新手卡一样）。
        SetStatus(OfflineNameRules.Check(name).NotAscii
            ? StatusMessage.Warning("accounts.status.offline_added_not_ascii", name)
            : StatusMessage.Success("accounts.status.offline_added", name));
        Changed();
        OfferJayFor(_service.Accounts.Store.Selected);
    }

    /// <summary>硬性规则见 OfflineNameRules：3–16 个字符、没有空格；非英文字母数字只提醒。</summary>
    public static bool IsValidOfflineName(string name) => OfflineNameRules.Check(name).Ok;

    [RelayCommand]
    private void SaveClientId()
    {
        _service.Settings.MicrosoftClientId = string.IsNullOrWhiteSpace(ClientIdInput) ? null : ClientIdInput.Trim();
        _service.Settings.Save();
        OnPropertyChanged(nameof(MicrosoftMissingClientId));
        SetStatus(StatusMessage.Success("accounts.status.client_id_saved"));
    }

    [RelayCommand]
    private Task LoginMicrosoftAsync() => LoginMicrosoftWithAsync(() => _service.Accounts.CreateMicrosoftAuth());

    /// <summary>
    /// 设备码登录（改版规格 3.5）：拿到代码后自动复制并打开网页（接口给了带代码的网址就开那个），显示倒计时；
    /// 过期后面板留着给【重新获取代码】；失败按种类给出能点的下一步。
    /// </summary>
    internal async Task LoginMicrosoftWithAsync(Func<MicrosoftAuth> createAuth)
    {
        SaveClientId();
        if (_service.Accounts.MicrosoftClientId is null)
        {
            SetStatus(StatusMessage.Warning("accounts.status.client_id_missing"));
            return;
        }

        _loginCts?.Cancel();
        var cts = _loginCts = new CancellationTokenSource();
        IsBusy = true;
        CodeExpired = false;
        ClearLoginResult();
        Avalonia.Threading.DispatcherTimer? timer = null;
        try
        {
            var auth = createAuth();
            SetStatus(StatusMessage.Info("accounts.status.requesting_device_code"));
            var code = await auth.BeginDeviceCodeAsync(cts.Token);
            DeviceCode = code.UserCode;
            DeviceUri = code.VerificationUri;
            CodeCopied = await TryCopyAsync(code.UserCode);
            TryOpen(code.VerificationUriComplete ?? code.VerificationUri);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresInSeconds);
            UpdateCountdown(deadline);
            timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => UpdateCountdown(deadline);
            timer.Start();
            SetStatus(StatusMessage.Info("accounts.status.awaiting_device_login"));
            var account = await auth.CompleteDeviceCodeAsync(code, new Progress<string>(s => Status = s), cts.Token);
            _service.Accounts.Store.AddOrUpdate(account);
            DeviceCode = null;
            LoginSuccess = L.F("accounts.microsoft.logged_in_current", account.Name);
            SetStatus(StatusMessage.Success("accounts.status.logged_in", account.Name));
            Changed();
            OfferJayFor(_service.Accounts.Store.Selected);
        }
        catch (OperationCanceledException) when (_loginCts?.IsCancellationRequested == true)
        {
            DeviceCode = null;
            SetStatus(StatusMessage.Warning("accounts.status.login_cancelled"));
        }
        catch (MicrosoftLoginException e) when (e.Failure == MicrosoftLoginFailure.CodeExpired)
        {
            // 代码留在面板上，配【重新获取代码】。
            CodeExpired = true;
            CountdownText = "";
            SetStatus(StatusMessage.Warning("accounts.microsoft.expired"));
        }
        catch (Exception e)
        {
            DeviceCode = null;
            ShowLoginFailure(e);
            SetStatus(StatusMessage.Failure("accounts.status.login_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            timer?.Stop();
            if (!CodeExpired) DeviceUri = null;
            IsBusy = false;
        }
    }

    private void UpdateCountdown(DateTimeOffset deadline)
    {
        var left = deadline - DateTimeOffset.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        CountdownText = L.F("accounts.microsoft.countdown", $"{(int)left.TotalMinutes}:{left.Seconds:D2}");
    }

    private void ClearLoginResult()
    {
        LoginFailure = null;
        LoginActions.Clear();
        LoginSuccess = null;
    }

    /// <summary>失败写进面板：原因 + 按种类给的按钮（不是分好类的微软登录错误时只给【重试】）。</summary>
    internal void ShowLoginFailure(Exception e)
    {
        LoginActions.Clear();
        LoginFailure = ErrorText.Describe(e);
        var failure = e is MicrosoftLoginException m ? m.Failure : MicrosoftLoginFailure.Other;
        foreach (var action in ActionsFor(failure)) LoginActions.Add(action);
    }

    [RelayCommand]
    private async Task RunLoginActionAsync(LoginAction? action)
    {
        switch (action?.Kind)
        {
            case LoginActionKind.OpenUrl when action.Url is { } url:
                TryOpen(url);
                break;
            case LoginActionKind.Retry:
                await LoginMicrosoftAsync();
                break;
            case LoginActionKind.UseOffline:
                ClearLoginResult();
                LoginTab = OfflineTab;
                break;
        }
    }

    private async Task<bool> TryCopyAsync(string text)
    {
        try
        {
            return await CopyText(text);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            return false;
        }
    }

    private void TryOpen(string url)
    {
        try
        {
            OpenUrl(url);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 打不开浏览器：网址还在面板上，用户可以自己去。
        }
    }

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        if (DeviceCode is not { } code || !await TryCopyAsync(code)) return;
        CodeCopied = true;
        JustCopied = true;
        await Task.Delay(TimeSpan.FromSeconds(2));
        JustCopied = false;
    }

    [RelayCommand]
    private void OpenDeviceUri()
    {
        if (DeviceUri is { } uri) TryOpen(uri);
    }

    [RelayCommand]
    private void CancelLogin()
    {
        _loginCts?.Cancel();
        if (!CodeExpired) return;
        CodeExpired = false;
        DeviceCode = null;
        DeviceUri = null;
    }

    [RelayCommand]
    private async Task LoginExternalAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl) || string.IsNullOrWhiteSpace(ExternalUser) || string.IsNullOrEmpty(ExternalPassword))
        {
            SetStatus(StatusMessage.Warning("accounts.status.external_credentials_required"));
            return;
        }
        _loginCts = new CancellationTokenSource();
        IsBusy = true;
        var password = ExternalPassword;
        ExternalPassword = "";
        try
        {
            SetStatus(StatusMessage.Info("accounts.status.connecting_external"));
            var auth = _service.Accounts.External;
            var api = await auth.ResolveApiRootAsync(ServerUrl, _loginCts.Token);
            var result = await auth.LoginAsync(api, ExternalUser.Trim(), password, _loginCts.Token);
            if (result.Account.Uuid == Guid.Empty)
            {
                ExternalProfiles.Clear();
                foreach (var p in result.Profiles) ExternalProfiles.Add(p);
                SelectedProfile = ExternalProfiles[0];
                PendingExternal = result.Account;
                SetStatus(StatusMessage.Info("accounts.status.choose_profile"));
                return;
            }
            FinishExternal(result.Account);
        }
        catch (OperationCanceledException) when (_loginCts?.IsCancellationRequested == true)
        {
            SetStatus(StatusMessage.Warning("accounts.status.login_cancelled"));
        }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("accounts.status.login_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmProfileAsync()
    {
        if (PendingExternal is not { } account || SelectedProfile is not { } profile) return;
        IsBusy = true;
        try
        {
            await _service.Accounts.External.SelectProfileAsync(account, profile);
            PendingExternal = null;
            FinishExternal(account);
        }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("accounts.status.profile_selection_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void FinishExternal(Account account)
    {
        _service.Accounts.Store.AddOrUpdate(account);
        SetStatus(StatusMessage.Success("accounts.status.external_logged_in", account.Name, account.ServerName));
        Changed();
    }

    public Task RunSnapshotDemoAsync()
    {
        LoginTab = MicrosoftTab;
        return Task.CompletedTask;
    }
}
