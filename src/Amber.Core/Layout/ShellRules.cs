namespace Amber.Core.Layout;

/// <summary>侧栏：自动、总是展开、总是收起（设置 › 外观 › 侧栏；主题包 shell.json 的 navStyle）。</summary>
public enum SidebarMode { Auto, Expanded, Collapsed }

/// <summary>
/// 外壳尺寸规则（官方设计 §3.1、§3.3）。App 和单元测试共用一份。
/// </summary>
public static class ShellRules
{
    public const double SidebarWidth = 240;
    public const double RailWidth = 76;
    public const double TopBarHeight = 48;
    public const double TopBarHeightShort = 40;

    public static SidebarMode ParseMode(string? text) => text switch
    {
        "expanded" or "full" => SidebarMode.Expanded,
        "collapsed" or "rail" or "icons" => SidebarMode.Collapsed,
        _ => SidebarMode.Auto,
    };

    public static string ModeName(SidebarMode mode) => mode switch
    {
        SidebarMode.Expanded => "expanded",
        SidebarMode.Collapsed => "collapsed",
        _ => "auto",
    };

    /// <summary>
    /// 侧栏展不展开。自动：展开后内容区还有宽档那么宽（窗口 ≥ 1340）才展开；
    /// 总是展开：展开后内容区还 ≥ 紧凑档阈值才展开，不会把内容挤到不能用。
    /// expanded 传当前状态，自动模式下收起要比展开再窄 32（回差），拖窗口边缘时不会来回抖。
    /// </summary>
    public static bool IsExpanded(double windowWidth, SidebarMode mode, double sidebarWidth = SidebarWidth, bool expanded = false) => mode switch
    {
        SidebarMode.Collapsed => false,
        SidebarMode.Expanded => windowWidth - sidebarWidth >= LayoutTiers.Compact,
        _ => windowWidth >= sidebarWidth + LayoutTiers.Wide - (expanded ? LayoutTiers.Hysteresis : 0),
    };

    /// <summary>顶栏高度：矮窗口 40，否则 48。</summary>
    public static double TopBarFor(HeightTier tier) => tier == HeightTier.Normal ? TopBarHeight : TopBarHeightShort;

    /// <summary>内容区外边距：紧凑 16、中 24、宽 32。</summary>
    public static double PagePaddingFor(WidthTier tier) => tier switch
    {
        WidthTier.Compact => 16,
        WidthTier.Wide => 32,
        _ => 24,
    };
}
