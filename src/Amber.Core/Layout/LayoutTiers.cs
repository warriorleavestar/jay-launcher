namespace Amber.Core.Layout;

/// <summary>内容区宽度档（官方设计 §3.2）。页面布局只认档位，不认具体像素。</summary>
public enum WidthTier { Compact, Medium, Wide }

/// <summary>客户区高度档（§3.3）。</summary>
public enum HeightTier { Normal, Short, Tight }

/// <summary>
/// 宽度和高度分档的阈值和回差，App 和单元测试共用一份。
/// 量的是“内容区尺寸 ÷ 文字大小”，所以文字放大到 125% 时更早进入窄档、矮档（§4.4）。
/// 每次切档有 32px 回差：拖窗口边缘时不会在阈值附近来回抖。
/// </summary>
public static class LayoutTiers
{
    public const double Compact = 640;
    public const double Wide = 1100;
    public const double Short = 560;
    public const double Tight = 480;
    public const double Hysteresis = 32;

    /// <summary>设置 › 外观 › 文字大小允许的三档。</summary>
    public static readonly double[] TextScales = [1.0, 1.12, 1.25];

    /// <summary>不带回差的分档：第一次布局时用。</summary>
    public static WidthTier ForWidth(double contentWidth, double textScale = 1) =>
        Normalize(contentWidth, textScale) switch
        {
            < Compact => WidthTier.Compact,
            < Wide => WidthTier.Medium,
            _ => WidthTier.Wide,
        };

    public static HeightTier ForHeight(double clientHeight, double textScale = 1) =>
        Normalize(clientHeight, textScale) switch
        {
            < Tight => HeightTier.Tight,
            < Short => HeightTier.Short,
            _ => HeightTier.Normal,
        };

    /// <summary>带回差：只有越过阈值 32px 以上才离开当前档；跨过不止一档时直接跳到目标档。</summary>
    public static WidthTier ForWidth(double contentWidth, WidthTier current, double textScale = 1)
    {
        var target = ForWidth(contentWidth, textScale);
        if (target == current) return current;
        var w = Normalize(contentWidth, textScale);
        return (current, target) switch
        {
            (WidthTier.Compact, WidthTier.Medium) when w < Compact + Hysteresis => current,
            (WidthTier.Medium, WidthTier.Compact) when w >= Compact - Hysteresis => current,
            (WidthTier.Medium, WidthTier.Wide) when w < Wide + Hysteresis => current,
            (WidthTier.Wide, WidthTier.Medium) when w >= Wide - Hysteresis => current,
            _ => target,
        };
    }

    public static HeightTier ForHeight(double clientHeight, HeightTier current, double textScale = 1)
    {
        var target = ForHeight(clientHeight, textScale);
        if (target == current) return current;
        var h = Normalize(clientHeight, textScale);
        return (current, target) switch
        {
            (HeightTier.Tight, HeightTier.Short) when h < Tight + Hysteresis => current,
            (HeightTier.Short, HeightTier.Tight) when h >= Tight - Hysteresis => current,
            (HeightTier.Short, HeightTier.Normal) when h < Short + Hysteresis => current,
            (HeightTier.Normal, HeightTier.Short) when h >= Short - Hysteresis => current,
            _ => target,
        };
    }

    /// <summary>最小窗口跟着文字大小放大（112%：672×470；125%：750×525），保证紧凑档仍有足够有效宽度。</summary>
    public static (double Width, double Height) MinimumWindow(double textScale = 1)
    {
        var s = ClampScale(textScale);
        return (Math.Round(600 * s), Math.Round(420 * s));
    }

    private static double Normalize(double size, double textScale) =>
        double.IsFinite(size) ? Math.Max(0, size) / ClampScale(textScale) : 0;

    private static double ClampScale(double s) => double.IsFinite(s) ? Math.Clamp(s, 1, 1.25) : 1;
}
