using Avalonia.Controls;
using Avalonia.Media;

namespace Amber.App.Controls.Safety;

/// <summary>
/// 共享提示条（§5.9）：远程控制、出口、转发进行中时贴在内容区顶部。远程控制是红底白字（.remote），出口 / 转发是安全蓝框。
/// 高 36 起，长语言换行时按内容长高，不截字。
/// </summary>
public class SafetyBanner : SafetySurface
{
    protected override Type StyleKeyOverride => typeof(SafetyBanner);

    public SafetyBanner() => MinHeight = 36;

    public SafetyBanner(string text, bool remote) : this()
    {
        Classes.Set("remote", remote);
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    }
}
