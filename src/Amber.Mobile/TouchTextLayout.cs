namespace Amber.Mobile;

internal static class TouchTextLayout
{
    public static float FitTextSize(
        float preferredSize,
        float measuredWidth,
        float availableWidth,
        float minimumSize)
    {
        if (!float.IsFinite(preferredSize) || preferredSize <= 0 ||
            !float.IsFinite(measuredWidth) || measuredWidth <= 0 ||
            !float.IsFinite(availableWidth) || availableWidth <= 0 ||
            !float.IsFinite(minimumSize) || minimumSize <= 0)
            return Math.Max(1f, minimumSize);
        if (measuredWidth <= availableWidth) return preferredSize;
        return Math.Clamp(preferredSize * availableWidth / measuredWidth,
            Math.Min(minimumSize, preferredSize), preferredSize);
    }
}
