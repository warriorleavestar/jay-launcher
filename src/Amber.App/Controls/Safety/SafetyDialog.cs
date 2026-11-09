using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Amber.App.Controls.Safety;

/// <summary>What the user picked in a safety dialog. Closing the window or pressing Esc is <see cref="Keep"/>.</summary>
public enum SafetyChoice { Keep, Confirm, Alternate }

/// <summary>
/// 安全确认框（§5.9）：永久删除、强制结束、结束房间这类真危险、不能撤销的事。
/// 外观锁定：独立窗口 LockToDefault + SafetyPanel（2px 安全蓝框、盾牌、“Jay 安全提示”）。
/// 文案规则：第一句说后果，第二句说影响范围，按钮写动作；默认焦点在“保留 / 取消”，Esc = 取消，关窗也是取消。
/// </summary>
public sealed class SafetyDialog : Window
{
    private readonly TextBlock _body;

    /// <summary>当前开着的安全确认框（自检用）。</summary>
    internal static SafetyDialog? Current { get; private set; }

    internal SafetyPanel Panel { get; }
    internal SafetyButton ConfirmButton { get; }
    internal SafetyButton KeepButton { get; }
    /// <summary>Optional third, safe choice (for example "Minimize" in the close guard).</summary>
    internal SafetyButton? AlternateButton { get; }

    private SafetyDialog(string title, string body, string confirm, SafetyKind kind, string? keep, IReadOnlyList<string>? details,
        string? alternate = null)
    {
        Services.ThemeService.LockToDefault(this);
        Title = L.T("safety.title");
        SizeToContent = SizeToContent.Height;
        Width = 480;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _body = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap };
        KeepButton = new SafetyButton { Content = keep ?? L.T("common.cancel"), Kind = SafetyKind.Safe, IsCancel = true };
        ConfirmButton = new SafetyButton { Content = confirm, Kind = kind };
        KeepButton.Click += (_, _) => Close(SafetyChoice.Keep);
        ConfirmButton.Click += (_, _) => Close(SafetyChoice.Confirm);
        if (alternate is not null)
        {
            AlternateButton = new SafetyButton { Content = alternate, Kind = SafetyKind.Safe };
            AlternateButton.Click += (_, _) => Close(SafetyChoice.Alternate);
        }
        var content = new StackPanel { Spacing = 12, Children = { _body } };
        if (details is { Count: > 0 })
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var d in details) list.Children.Add(new TextBlock { Text = "• " + d, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(list);
        }
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        if (AlternateButton is not null) buttons.Children.Add(AlternateButton);
        buttons.Children.Add(KeepButton);
        buttons.Children.Add(ConfirmButton);
        content.Children.Add(buttons);
        Panel = new SafetyPanel { Title = title, Body = content, CornerRadius = new CornerRadius(0), Margin = new Thickness(0) };
        Content = Panel;
        Background = Brushes.Transparent;
        Opened += (_, _) =>
        {
            Current = this;
            KeepButton.Focus(NavigationMethod.Tab);
        };
        Closed += (_, _) => { if (Current == this) Current = null; };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(SafetyChoice.Keep);
        };
    }

    /// <summary>自检用：不弹模态，只建一个确认框（调用方负责 Show 和 Close）。</summary>
    internal static SafetyDialog CreateForSelfTest(string title, string body, string confirm, string[] details) =>
        new(title, body, confirm, SafetyKind.Danger, null, details);

    /// <summary>Self-check only: builds a three-choice dialog without showing it.</summary>
    internal static SafetyDialog CreateChoiceForSelfTest(string title, string body, string confirm, string keep, string alternate, IReadOnlyList<string> details) =>
        new(title, body, confirm, SafetyKind.Danger, keep, details, alternate);

    /// <summary>
    /// Shows a safety dialog with three choices: keep (default focus), an alternate safe action, and the dangerous confirm.
    /// </summary>
    public static Task<SafetyChoice> ChooseAsync(Window owner, string title, string body, string confirm, string keep, string alternate,
        IReadOnlyList<string>? details = null, SafetyKind kind = SafetyKind.Danger) =>
        new SafetyDialog(title, body, confirm, kind, keep, details, alternate).ShowDialog<SafetyChoice>(owner);

    /// <summary>
    /// 显示安全确认框，返回是否确认。bodyLater 不为 null 时在后台算好后替换正文（比如带上占用空间），算好之前也能确认。
    /// </summary>
    public static async Task<bool> AskAsync(Window owner, string title, string body, string confirm,
        SafetyKind kind = SafetyKind.Danger, Func<Task<string>>? bodyLater = null, string? keep = null, IReadOnlyList<string>? details = null)
    {
        var dialog = new SafetyDialog(title, body, confirm, kind, keep, details);
        if (bodyLater is not null)
            _ = Task.Run(async () =>
            {
                try
                {
                    var text = await bodyLater();
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => dialog._body.Text = text);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            });
        return await dialog.ShowDialog<SafetyChoice>(owner) == SafetyChoice.Confirm;
    }
}
