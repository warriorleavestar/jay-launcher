using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Amber.App.Views.Controls;

/// <summary>
/// 单字段表单按 Enter 提交（改版规格 7.3）：只认 Key.Enter，不带修饰键；输入法组字时的回车是 Key.ImeProcessed，不会触发。
/// 多行输入框（AcceptsReturn）里不触发。用法：<c>c:EnterSubmit.Command="{Binding AddOfflineCommand}"</c>。
/// </summary>
public static class EnterSubmit
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("Command", typeof(EnterSubmit));

    static EnterSubmit()
    {
        CommandProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            box.KeyDown -= OnKeyDown;
            if (e.NewValue is not null) box.KeyDown += OnKeyDown;
        });
    }

    public static ICommand? GetCommand(TextBox box) => box.GetValue(CommandProperty);
    public static void SetCommand(TextBox box, ICommand? value) => box.SetValue(CommandProperty, value);

    /// <summary>这次按键该不该提交（单元测试直接调）。</summary>
    internal static bool ShouldSubmit(Key key, KeyModifiers modifiers, bool acceptsReturn) =>
        key == Key.Enter && modifiers == KeyModifiers.None && !acceptsReturn;

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || !ShouldSubmit(e.Key, e.KeyModifiers, box.AcceptsReturn)) return;
        if (GetCommand(box) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
