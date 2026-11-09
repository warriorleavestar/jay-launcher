namespace Amber.Core.Localization;

/// <summary>
/// 界面上数字的统一写法（按当前界面语言的区域）：
///   大小：B 用整数，KB / MB / GB 保留 1 位小数（按 1024 进位，四舍五入后再选单位）；
///   时长：不到 1 小时写 m:ss，1 小时以上写“X 小时 Y 分钟”；只要分钟时写“X 分钟”（区分单复数）。
///   日志时间：保留传入时间的时区，按当前界面语言显示时、分、秒。
/// </summary>
public static class Fmt
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    public static string Time(DateTimeOffset time) => time.ToString("T", L.Culture);

    public static string Size(long bytes)
    {
        if (bytes < 1024) return string.Format(L.Culture, "{0} B", bytes);
        var value = bytes / 1024.0;
        var unit = 0;
        // 四舍五入到 1 位小数后到了 1024（比如 1023.96 KB）就进到下一级单位。
        while (unit + 1 < Units.Length && Math.Round(value, 1) >= 1024)
        {
            value /= 1024;
            unit++;
        }
        return string.Format(L.Culture, "{0:0.0} {1}", value, Units[unit]);
    }

    public static string Duration(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        var seconds = (long)Math.Round(time.TotalSeconds);
        if (seconds < 3600) return string.Format(L.Culture, "{0}:{1:00}", seconds / 60, seconds % 60);
        var minutes = seconds / 60;
        return L.F("format.duration.hours_minutes", minutes / 60, minutes % 60);
    }

    /// <summary>“X 分钟”，不到 1 分钟按 1 分钟算。</summary>
    public static string Minutes(TimeSpan time)
    {
        var minutes = Math.Max(1, (long)Math.Round(time.TotalMinutes));
        return L.P("format.duration.minutes", minutes, minutes);
    }

    /// <summary>
    /// How long ago, for "last played": just now, N minutes / hours ago, yesterday, N days ago (under a week),
    /// otherwise the short date.
    /// </summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromMinutes(1)) return L.T("format.ago.now");
        if (span < TimeSpan.FromHours(1)) return L.P("format.ago.minutes", (long)span.TotalMinutes, (long)span.TotalMinutes);
        var localWhen = when.ToLocalTime().Date;
        var localNow = now.ToLocalTime().Date;
        var days = (localNow - localWhen).Days;
        if (days <= 0) return L.P("format.ago.hours", (long)span.TotalHours, (long)span.TotalHours);
        if (days == 1) return L.T("format.ago.yesterday");
        if (days < 7) return L.P("format.ago.days", days, days);
        return when.ToLocalTime().ToString("d", L.Culture);
    }
}
