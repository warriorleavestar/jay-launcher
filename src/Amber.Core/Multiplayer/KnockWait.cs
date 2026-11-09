namespace Amber.Core.Multiplayer;

/// <summary>How long the host's knock card waits for an answer (official §6.15: 2 minutes / 10 minutes / keep waiting).</summary>
public enum KnockWait
{
    TwoMinutes,
    TenMinutes,
    Forever,
}

public static class KnockWaits
{
    /// <summary>The default: 10 minutes.</summary>
    public const KnockWait Default = KnockWait.TenMinutes;

    public static TimeSpan ToTimeSpan(this KnockWait wait) => wait switch
    {
        KnockWait.TwoMinutes => TimeSpan.FromMinutes(2),
        KnockWait.Forever => Timeout.InfiniteTimeSpan,
        _ => TimeSpan.FromMinutes(10),
    };

    /// <summary>The settings value ("2m", "10m", "forever"); anything else is the default.</summary>
    public static KnockWait Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "2m" => KnockWait.TwoMinutes,
        "forever" => KnockWait.Forever,
        _ => Default,
    };

    public static string ToSetting(this KnockWait wait) => wait switch
    {
        KnockWait.TwoMinutes => "2m",
        KnockWait.Forever => "forever",
        _ => "10m",
    };
}
