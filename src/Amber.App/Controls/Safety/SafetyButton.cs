using Avalonia.Controls;

namespace Amber.App.Controls.Safety;

/// <summary>安全按钮的三种：保留 / 取消（默认焦点）、确定（安全蓝）、危险（锁定的 #B3261E 底白字）。</summary>
public enum SafetyKind { Safe, Confirm, Danger }

/// <summary>
/// 安全界面里的按钮。样式键是自己，主题包的 <c>Button</c> 选择器匹配不到；模板和颜色在 Themes/Safety.axaml，只读默认令牌。
/// </summary>
public class SafetyButton : Button
{
    protected override Type StyleKeyOverride => typeof(SafetyButton);

    public static readonly Avalonia.StyledProperty<SafetyKind> KindProperty =
        Avalonia.AvaloniaProperty.Register<SafetyButton, SafetyKind>(nameof(Kind));

    public SafetyKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public SafetyButton() => UpdateClasses();

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty) UpdateClasses();
    }

    private void UpdateClasses()
    {
        Classes.Set("safe", Kind == SafetyKind.Safe);
        Classes.Set("confirm", Kind == SafetyKind.Confirm);
        Classes.Set("danger", Kind == SafetyKind.Danger);
    }
}
