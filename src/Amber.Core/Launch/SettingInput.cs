using System.Globalization;
using System.Text;

namespace Amber.Core.Launch;

/// <summary>
/// 设置里手填的数字和参数（改版规格 3.10 / 7.13）：全角数字、“4G”这类内存写法都认；认不出来时返回 false，由界面就地提示，不静默改成别的值。
/// </summary>
public static class SettingInput
{
    /// <summary>全角转半角（１９２０ → 1920，４Ｇ → 4G），再去掉首尾空白。</summary>
    public static string Normalize(string? text) => (text ?? "").Normalize(NormalizationForm.FormKC).Trim();

    /// <summary>正整数；留空表示“不设置”（value 为 null，返回 true）。</summary>
    public static bool TryParsePositiveInt(string? text, out int? value, int max = int.MaxValue)
    {
        value = null;
        var s = Normalize(text);
        if (s.Length == 0) return true;
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0 || n > max) return false;
        value = n;
        return true;
    }

    /// <summary>
    /// 内存（MB）：“4096”“4096M”“4096 MB”“4G”“4.5GB”“4g”都认；留空表示“不设置”。
    /// 太小（小于 256 MB）或大到不合理（大于 1 TB）算认不出来。
    /// </summary>
    public static bool TryParseMemoryMb(string? text, out int? megabytes)
    {
        megabytes = null;
        var s = Normalize(text).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (s.Length == 0) return true;
        var factor = 1.0;
        if (s.EndsWith("GB", StringComparison.Ordinal) || s.EndsWith('G')) factor = 1024;
        s = s.TrimEnd('B', 'G', 'M');
        if (!double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)) return false;
        var mb = n * factor;
        if (mb < 256 || mb > 1024 * 1024) return false;
        megabytes = (int)Math.Round(mb);
        return true;
    }

    /// <summary>JVM 参数和游戏参数：引号要配对（单引号、双引号各自成对，反斜杠转义的不算）。</summary>
    public static bool QuotesBalanced(string? text)
    {
        char? open = null;
        var s = text ?? "";
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length) { i++; continue; }
            if (c is not ('"' or '\'')) continue;
            if (open is null) open = c;
            else if (open == c) open = null;
        }
        return open is null;
    }
}
