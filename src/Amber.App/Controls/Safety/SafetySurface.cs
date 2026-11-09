using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace Amber.App.Controls.Safety;

/// <summary>
/// 所有安全界面的根（官方设计 §5.9、§10.7）。三道保险里的第二道：
/// 自己带一份 Jay 默认主题的令牌（跟随深浅色），查资源时先找到它，主题包的值盖不到；背景不透明，字体链固定。
/// 第一道是独立类型：样式键是自己（不是 Border），主题的 <c>Border.card</c> 这类选择器匹配不到。
/// </summary>
public class SafetySurface : Border
{
    protected override Type StyleKeyOverride => typeof(SafetySurface);

    public SafetySurface()
    {
        Resources.MergedDictionaries.Add(DefaultTokens());
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Jay/")) { Source = new Uri("avares://Jay/Themes/Safety.axaml") });
        ClipToBounds = true;
    }

    /// <summary>一份新的默认令牌字典（每个安全界面一份，主题换了也不变）。</summary>
    public static ResourceInclude DefaultTokens() =>
        new(new Uri("avares://Jay/")) { Source = new Uri("avares://Jay/Themes/Tokens.axaml") };
}
