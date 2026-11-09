using Amber.App.Controls.Safety;
using Avalonia.Controls;

namespace Amber.App.Views;

/// <summary>
/// 删除实例这类不能撤销的操作用的确认框。新界面第 1 批起就是 <see cref="SafetyDialog"/>（锁定的安全外观），
/// 这里留着原来的入口，调用方不用改。
/// </summary>
public static class ConfirmDialog
{
    public static Task<bool> AskAsync(Window owner, string title, string body, string confirm, Func<Task<string>>? bodyLater = null) =>
        SafetyDialog.AskAsync(owner, title, body, confirm, SafetyKind.Danger, bodyLater);
}
