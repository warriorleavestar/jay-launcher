namespace Amber.Core.Themes;

/// <summary>
/// 安全界面的保留词（官方设计 §10.7）：只有 Jay 自己的安全确认框能写“Jay 安全提示”这类标题。
/// 主题包的名字、作者、布局里的文字一旦含有这些词就整包拒绝，免得主题冒充安全提示骗人点按钮。
/// 比对前去掉空白、统一大小写和全角半角，“Ｊａｙ 安全 提示”也算。
/// Punctuation, symbols, invisible formatting characters and combining marks are dropped too, so "Safety-notice" or
/// "Safety·notice" count as well.
/// </summary>
public static class SafetyWords
{
    /// <summary>各语言安全标题（和 safety.title 的翻译一致）加上通用说法。</summary>
    public static readonly string[] Reserved =
    [
        "Jay安全提示", "安全提示", "Jay安危之示", "安危之示",
        "Jayセキュリティ確認", "セキュリティ確認",
        "Jaysafetynotice", "Safetynotice", "Jaysecuritynotice", "Securitynotice",
    ];

    private static readonly string[] Normalized = Reserved.Select(Normalize).ToArray();

    public static bool Contains(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var n = Normalize(text);
        return Normalized.Any(r => n.Contains(r, StringComparison.Ordinal));
    }

    private static string Normalize(string s)
    {
        var folded = s.Normalize(System.Text.NormalizationForm.FormKD); // decomposed, so accents come apart and drop out below
        return string.Concat(folded.Where(Kept)).ToLowerInvariant();
    }

    /// <summary>Only letters and digits count: spacing, punctuation, symbols, invisible and combining characters don't.</summary>
    private static bool Kept(char c) =>
        !char.IsWhiteSpace(c) && !char.IsControl(c) && !char.IsPunctuation(c) && !char.IsSymbol(c)
        && char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.EnclosingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark);
}
