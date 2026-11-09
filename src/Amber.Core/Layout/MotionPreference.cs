namespace Amber.Core.Layout;

/// <summary>
/// “减少动画”的判定（官方设计 §4.7）：设置是 跟随系统 / 开 / 关 三选一；跟随系统时看系统设置，系统读不到就当没开。
/// 打开时所有位移和缩放取消，只保留 ≤ 100ms 的淡入淡出。
/// </summary>
public static class MotionPreference
{
    public const string System = "system";
    public const string On = "on";
    public const string Off = "off";
    public static readonly string[] Modes = [System, On, Off];

    /// <summary>不认识的值按“跟随系统”处理。</summary>
    public static string Normalize(string? mode) => mode is On or Off ? mode : System;

    /// <summary>旧设置里只有一个布尔 ReduceMotion：勾过的升级成“开”，没勾过的保持“跟随系统”。</summary>
    public static string Migrate(string? mode, bool legacyReduceMotion) =>
        mode is null && legacyReduceMotion ? On : Normalize(mode);

    public static bool ShouldReduce(string? mode, bool? systemReduces) => Normalize(mode) switch
    {
        On => true,
        Off => false,
        _ => systemReduces == true,
    };

    /// <summary>动效时长（毫秒）：正常值，或减少动画时的值（位移类变 0，淡入淡出不超过 100）。</summary>
    public static IReadOnlyDictionary<string, double> Durations(bool reduce) => reduce
        ? new Dictionary<string, double> { ["MotionFast"] = 0, ["MotionBase"] = 100, ["MotionSlow"] = 100, ["MotionSlower"] = 100 }
        : new Dictionary<string, double> { ["MotionFast"] = 100, ["MotionBase"] = 160, ["MotionSlow"] = 240, ["MotionSlower"] = 320 };

    /// <summary>GNOME 的 enable-animations 输出（gsettings get 的原样文字）：false 表示系统要求减少动画。</summary>
    public static bool? ParseGnomeEnableAnimations(string? output) => output?.Trim() switch
    {
        "false" => true,
        "true" => false,
        _ => null,
    };

    /// <summary>macOS defaults read com.apple.universalaccess reduceMotion 的输出：1 表示打开。</summary>
    public static bool? ParseMacReduceMotion(string? output) => output?.Trim() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => null,
    };
}
