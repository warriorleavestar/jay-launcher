using System.Net;
using System.Text;
using Amber.App.ViewModels;
using Amber.Core.Auth;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>1b.6 微软登录（改版规格 3.5）：三段式选择器、设备码自动复制和打开网页、倒计时、过期后重新获取、失败状态的按钮。用假的微软接口，不联网。</summary>
internal sealed partial class SelfTest
{
    private async Task MicrosoftLoginAsync()
    {
        var accounts = Page<AccountsViewModel>();
        main.CurrentPage = accounts;
        await Task.Delay(500);

        // 三段式：三段一样宽，正版排第一并默认选中；点“离线”切过去。
        var segments = await WaitForAsync(() => window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "LoginKind" && r.IsEffectivelyVisible).ToList(), l => l.Count == 3);
        var widths = Dispatcher.UIThread.Invoke(() => segments.Select(r => Math.Round(r.Bounds.Width)).ToList());
        Check("微软登录：三段式选择器三段等宽，正版排第一且默认选中",
            segments.Count == 3 && widths.Distinct().Count() == 1 && accounts.IsMicrosoftTab && Dispatcher.UIThread.Invoke(() => segments[0].IsChecked == true),
            string.Join("/", widths));
        Dispatcher.UIThread.Invoke(() => segments[1].IsChecked = true);
        Check("微软登录：点“离线”切到离线账号", await WaitAsync(() => accounts.IsOfflineTab, 3));
        Dispatcher.UIThread.Invoke(() => segments[0].IsChecked = true);
        await WaitAsync(() => accounts.IsMicrosoftTab, 3);

        var advanced = await FindAsync<Expander>(e => e.Name == "MicrosoftAdvanced" && e.IsEffectivelyVisible);
        Check("微软登录：Client ID 收在默认折叠的“高级”里", advanced is not null && Dispatcher.UIThread.Invoke(() => !advanced.IsExpanded));

        // 设备码：自动复制、自动打开网页、倒计时；过期后面板留着给【重新获取代码】。
        string? copied = null, opened = null;
        accounts.CopyText = text => { copied = text; return Task.FromResult(true); };
        accounts.OpenUrl = url => opened = url;
        var fake = new FakeMicrosoft();
        var login = Dispatcher.UIThread.InvokeAsync(() => accounts.LoginMicrosoftWithAsync(() => new MicrosoftAuth(new HttpClient(fake), "selftest")));
        await WaitAsync(() => accounts.HasDeviceCode, 5);
        var codeText = await FindAsync<TextBlock>(t => t.Text == accounts.CodeCopiedText && t.IsEffectivelyVisible);
        Check("微软登录：拿到代码后自动复制并打开网页，提示已复制",
            copied == "SELF-TEST" && opened == "https://microsoft.com/link" && codeText is not null && accounts.CodeCopiedText.Contains("已复制", StringComparison.Ordinal),
            $"复制 {copied}，打开 {opened}，提示 {accounts.CodeCopiedText}");
        Check("微软登录：显示有效期倒计时", accounts.CountdownText.EndsWith("内有效", StringComparison.Ordinal), accounts.CountdownText);
        await ShotAsync("微软登录-设备码");
        await LayoutHereAsync("微软登录设备码");
        fake.Expire = true;
        await login;
        var renew = await FindAsync<Button>(b => b.Name == "RenewCodeButton" && b.IsEffectivelyVisible);
        Check("微软登录：代码过期后面板保留，显示【重新获取代码】", accounts.CodeExpired && accounts.HasDeviceCode && renew is not null);
        accounts.CancelLoginCommand.Execute(null);

        // 失败状态：未成年账号给【查看家长设置步骤】【已处理，重试】【改用离线模式】，点“改用离线模式”切到离线。
        Dispatcher.UIThread.Invoke(() => accounts.ShowLoginFailure(new MicrosoftLoginException(MicrosoftLoginFailure.ChildAccount, "自检：未成年账号")));
        var buttons = await WaitForAsync(() => window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.DataContext is LoginAction && b.IsEffectivelyVisible).ToList(), l => l.Count == 3);
        Check("微软登录：未成年账号失败给出三个按钮", buttons.Count == 3, string.Join("/", buttons.Select(b => b.Content)));
        await ShotAsync("微软登录-失败");
        await LayoutHereAsync("微软登录失败");
        var offline = buttons.FirstOrDefault(b => b.DataContext is LoginAction { Kind: LoginActionKind.UseOffline });
        Check("微软登录：点“改用离线模式”切到离线", Press(offline) && await WaitAsync(() => accounts.IsOfflineTab && !accounts.HasLoginFailure, 3));
        accounts.LoginTab = AccountsViewModel.MicrosoftTab;
    }

    private static async Task<T> WaitForAsync<T>(Func<T> get, Func<T, bool> ok, int seconds = 5)
    {
        var value = Dispatcher.UIThread.Invoke(get);
        for (var i = 0; i < seconds * 5 && !ok(value); i++)
        {
            await Task.Delay(200);
            value = Dispatcher.UIThread.Invoke(get);
        }
        return value;
    }

    /// <summary>假的微软设备码接口：给出代码，然后一直“等待授权”，直到 Expire 后返回过期。</summary>
    private sealed class FakeMicrosoft : HttpMessageHandler
    {
        public volatile bool Expire;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var (code, body) = url.EndsWith("/devicecode", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """{"user_code":"SELF-TEST","device_code":"dc","verification_uri":"https://microsoft.com/link","interval":1,"expires_in":900}""")
                : (HttpStatusCode.BadRequest, Expire ? """{"error":"expired_token"}""" : """{"error":"authorization_pending"}""");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
