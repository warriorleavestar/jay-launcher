namespace Amber.Mobile;

/// <summary>
/// Where Minecraft draws its hotbar on a given game surface, so taps can select the slot under the finger.
/// The hotbar is 182 by 22 GUI pixels, centred at the bottom; each of the nine slots is 20 GUI pixels wide
/// after a one-pixel border. GUI pixels are surface pixels divided by the effective GUI scale.
/// </summary>
public static class MinecraftHotbar
{
    /// <summary>The guiScale value Jay writes into a new Android options.txt.</summary>
    public const int DefaultGuiScaleOption = 3;

    private const int HotbarWidth = 182;
    private const int HotbarHeight = 22;
    private const int SlotWidth = 20;
    private const int MinimumGuiWidth = 320;
    private const int MinimumGuiHeight = 240;

    /// <summary>
    /// The GUI scale the game uses for a surface: the chosen option (0 means automatic), lowered until the
    /// scaled screen is still at least 320 by 240 GUI pixels.
    /// </summary>
    public static int EffectiveGuiScale(int surfaceWidth, int surfaceHeight, int guiScaleOption)
    {
        var scale = 1;
        while (scale != guiScaleOption && scale < surfaceWidth && scale < surfaceHeight &&
               surfaceWidth / (scale + 1) >= MinimumGuiWidth && surfaceHeight / (scale + 1) >= MinimumGuiHeight)
            scale++;
        return scale;
    }

    /// <summary>The hotbar rectangle as fractions of the surface width and height.</summary>
    public static (double Left, double Top, double Right, double Bottom) Bounds(
        int surfaceWidth, int surfaceHeight, int guiScaleOption)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0) return (0, 1, 0, 1);
        var scale = EffectiveGuiScale(surfaceWidth, surfaceHeight, guiScaleOption);
        var guiWidth = (surfaceWidth + scale - 1) / scale;
        var guiHeight = (surfaceHeight + scale - 1) / scale;
        var left = (guiWidth / 2 - HotbarWidth / 2) * scale;
        var top = (guiHeight - HotbarHeight) * scale;
        return ((double)left / surfaceWidth, (double)top / surfaceHeight,
            (double)(left + HotbarWidth * scale) / surfaceWidth, (double)(top + HotbarHeight * scale) / surfaceHeight);
    }

    /// <summary>Maps a tap (fractions of the surface) to a hotbar slot from 0 to 8.</summary>
    public static bool TryGetSlot(double x, double y, int surfaceWidth, int surfaceHeight, int guiScaleOption,
        out int slot)
    {
        slot = -1;
        if (surfaceWidth <= 0 || surfaceHeight <= 0) return false;
        var (left, top, right, _) = Bounds(surfaceWidth, surfaceHeight, guiScaleOption);
        if (y < top || x < left || x >= right) return false;
        var scale = EffectiveGuiScale(surfaceWidth, surfaceHeight, guiScaleOption);
        var guiX = (x - left) * surfaceWidth / scale;
        slot = Math.Clamp((int)((guiX - 1) / SlotWidth), 0, 8);
        return true;
    }

    /// <summary>
    /// Reads guiScale from the text of options.txt. A file without the line uses the game's default
    /// (automatic); unreadable or missing text falls back to the value Jay writes.
    /// </summary>
    public static int ReadGuiScaleOption(string? optionsText)
    {
        if (optionsText is null) return DefaultGuiScaleOption;
        foreach (var raw in optionsText.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("guiScale:", StringComparison.Ordinal)) continue;
            return int.TryParse(line.AsSpan("guiScale:".Length), out var value) && value >= 0
                ? value
                : DefaultGuiScaleOption;
        }
        return 0;
    }
}
