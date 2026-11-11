using Amber.App.Services;
using Amber.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Amber.App.Views;

/// <summary>
/// 联机安全：设置页的“联机安全”卡片即改即存；熔断后的倒计时盖住窗口、空格取消、不取消就重启、
/// 10 分钟内不再自动重启、勾“跳过等待”直接重启。重启这一步换成计数，不会真的重启。
/// </summary>
internal sealed partial class SelfTest
{
    private async Task SecurityBreakerAsync()
    {
        var settings = Page<SettingsViewModel>();
        main.CurrentPage = settings;
        await Task.Delay(600);

        settings.SelectGroup("privacy");
        var attest = await FindAsync<ComboBox>(c => c.Name == "AttestationBox" && c.IsEffectivelyVisible);
        var breaker = await FindAsync<CheckBox>(c => c.Name == "BreakerBox" && c.IsEffectivelyVisible);
        var delay = await FindAsync<TextBox>(t => t.Name == "BreakerDelayBox" && t.IsEffectivelyVisible);
        var skip = await FindAsync<CheckBox>(c => c.Name == "BreakerSkipBox" && c.IsEffectivelyVisible);
        Check("联机安全：设置页有验证模式、熔断、倒计时、跳过等待四项", attest is not null && breaker is not null && delay is not null && skip is not null);
        if (attest is null || breaker is null || delay is null || skip is null) return;
        Dispatcher.UIThread.Invoke(() => delay.BringIntoView());
        await Task.Delay(300);
        await ShotAsync("security-settings");

        // 倒计时改成 3 秒：离开输入框后存进设置文件。
        Dispatcher.UIThread.Invoke(() => { delay.Focus(); delay.Text = "3"; attest.Focus(); });
        Check("联机安全：倒计时改成 3 秒后保存",
            await WaitAsync(() => File.ReadAllText(AppSettings.FilePath).Contains("\"BreakerRestartDelay\": 3", StringComparison.Ordinal), 3));
        // 0 不合法：提示，不保存。
        Dispatcher.UIThread.Invoke(() => { delay.Focus(); delay.Text = "0"; attest.Focus(); });
        Check("联机安全：倒计时填 0 时提示且不保存",
            await WaitAsync(() => settings.HasBreakerDelayError, 3) && File.ReadAllText(AppSettings.FilePath).Contains("\"BreakerRestartDelay\": 3", StringComparison.Ordinal));
        Dispatcher.UIThread.Invoke(() => { delay.Focus(); delay.Text = "3"; attest.Focus(); });
        await WaitAsync(() => !settings.HasBreakerDelayError, 3);

        var mp = Page<MultiplayerViewModel>();
        var restarts = 0;
        mp.RestartApp = () => restarts++;
        Dispatcher.UIThread.Invoke(() => { mp.SecuritySettings.LastBreakerRestart = null; settings.BreakerSkipDelay = false; settings.SecurityBreaker = true; });

        // 熔断：盖住窗口的倒计时，空格取消。
        Dispatcher.UIThread.Invoke(() => mp.TripBreaker("自检：模拟有人冒充"));
        var overlay = await FindAsync<Border>(b => b.Name == "BreakerOverlayPart" && b.IsEffectivelyVisible);
        Check("熔断：倒计时盖住窗口，写着原因和秒数",
            overlay is not null && mp.IsBreakerCounting && mp.BreakerReason.Contains("自检") && mp.BreakerCountdownText.Contains('3'), mp.BreakerCountdownText);
        await ShotAsync("security-breaker-countdown");
        Dispatcher.UIThread.Invoke(() =>
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, Source = window }));
        await Task.Delay(4000);
        Check("熔断：按空格取消，不重启", !mp.IsBreakerCounting && restarts == 0, $"重启 {restarts} 次");

        // 不取消：倒计时走完就重启，并记下时间。
        Dispatcher.UIThread.Invoke(() => mp.TripBreaker("自检：模拟有人冒充"));
        Check("熔断：不取消时 3 秒后重启", await WaitAsync(() => restarts == 1, 6) && mp.SecuritySettings.LastBreakerRestart is not null, $"重启 {restarts} 次");

        // 10 分钟内再熔断：只断开，不倒计时、不重启。
        Dispatcher.UIThread.Invoke(() => mp.TripBreaker("自检：模拟有人冒充"));
        await Task.Delay(1500);
        Check("熔断：10 分钟内第二次不再自动重启", !mp.IsBreakerCounting && restarts == 1, $"重启 {restarts} 次");

        // 跳过等待：直接重启，不出倒计时。
        Dispatcher.UIThread.Invoke(() => { mp.SecuritySettings.LastBreakerRestart = null; settings.BreakerSkipDelay = true; });
        Dispatcher.UIThread.Invoke(() => mp.TripBreaker("自检：模拟有人冒充"));
        Check("熔断：勾“跳过等待”时直接重启", await WaitAsync(() => restarts == 2, 3) && !mp.IsBreakerCounting, $"重启 {restarts} 次");

        // 恢复默认，别把自检的设置留给下一次。
        Dispatcher.UIThread.Invoke(() =>
        {
            settings.BreakerSkipDelay = false;
            settings.BreakerRestartDelay = Amber.Core.Multiplayer.Transport.SecurityPolicy.DefaultRestartDelay.ToString();
            mp.SecuritySettings.LastBreakerRestart = null;
            mp.SecuritySettings.Save();
        });
    }
}
