using Amber.App.Controls.Safety;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Amber.App.Views.Controls;

/// <summary>
/// 删除按钮（删世界、移除账号）。新界面第 1 批起不再是“点两下”：点一下弹出锁定的安全确认框（官方设计 §8.2），
/// 写明删的是什么，默认焦点在“保留”，确认后才执行 Command。按钮本身还是普通的描边红字按钮。
/// 对象名放在 ToolTip 和读屏名字里，按钮上只写动作。
/// </summary>
public class ConfirmButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public static readonly StyledProperty<string?> ConfirmTextProperty =
        AvaloniaProperty.Register<ConfirmButton, string?>(nameof(ConfirmText));

    public static readonly StyledProperty<string?> ConfirmMessageProperty =
        AvaloniaProperty.Register<ConfirmButton, string?>(nameof(ConfirmMessage));

    public static readonly StyledProperty<string?> ObjectNameProperty =
        AvaloniaProperty.Register<ConfirmButton, string?>(nameof(ObjectName));

    /// <summary>以前两步确认用的全局取消；现在确认框自己处理 Esc，留着给旧调用方。</summary>
    public static void CancelAll() { }

    /// <summary>确认框里确认按钮的文字，默认“确认删除”。</summary>
    public string? ConfirmText
    {
        get => GetValue(ConfirmTextProperty);
        set => SetValue(ConfirmTextProperty, value);
    }

    /// <summary>What happens, shown in the dialog body (default "This can't be undone.").</summary>
    public string? ConfirmMessage
    {
        get => GetValue(ConfirmMessageProperty);
        set => SetValue(ConfirmMessageProperty, value);
    }

    /// <summary>要删的东西叫什么（确认框标题、ToolTip、读屏用）。</summary>
    public string? ObjectName
    {
        get => GetValue(ObjectNameProperty);
        set => SetValue(ObjectNameProperty, value);
    }

    private bool _asking;

    public ConfirmButton() => Classes.Add("danger");

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        L.Changed += OnLanguageChanged;
        UpdateNames();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        L.Changed -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(UpdateNames);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ObjectNameProperty || change.Property == ContentProperty) UpdateNames();
    }

    protected override async void OnClick()
    {
        if (_asking || TopLevel.GetTopLevel(this) is not Window owner) return;
        _asking = true;
        try
        {
            var action = Content as string ?? L.T("common.delete");
            var title = string.IsNullOrEmpty(ObjectName) ? action : L.F("safety.action_object", action, ObjectName);
            if (await SafetyDialog.AskAsync(owner, title, ConfirmMessage ?? L.T("safety.cannot_undo"), ConfirmText ?? L.T("common.confirm_delete"),
                    SafetyKind.Danger, keep: L.T("safety.keep")))
                base.OnClick();
        }
        finally
        {
            _asking = false;
        }
    }

    private void UpdateNames()
    {
        var action = Content as string ?? "";
        var name = string.IsNullOrEmpty(ObjectName) ? action : action + " " + ObjectName;
        AutomationProperties.SetName(this, name);
        ToolTip.SetTip(this, string.IsNullOrEmpty(ObjectName) ? null : name);
    }
}
