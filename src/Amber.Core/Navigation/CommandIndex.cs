using System.Text;

namespace Amber.Core.Navigation;

/// <summary>What a command palette entry is. The order is the order groups are shown in.</summary>
public enum CommandKind { Invite, Instance, Action, Setting, Page, Help }

/// <summary>
/// One palette entry. <paramref name="Keywords"/> are other names it answers to: the name in the other UI languages,
/// old page names ("Downloads", "Toolbox"…), mod names inside an instance.
/// </summary>
public sealed record CommandItem(string Id, CommandKind Kind, string Title, string? Subtitle = null, IReadOnlyList<string>? Keywords = null)
{
    internal string TitleInitials { get; } = Pinyin.Initials(Title);
}

public sealed record CommandMatch(CommandItem Item, int Score);

/// <summary>
/// Command palette search (official design §6.23): title, pinyin initials ("sczhb" finds 生存整合包), names in other
/// languages and old page names. Prefixes narrow the kind: <c>&gt;</c> actions, <c>@</c> instances, <c>#</c> settings,
/// <c>?</c> help.
/// </summary>
public static class CommandIndex
{
    public const int MaxResults = 40;

    /// <summary>Splits a leading prefix off the query.</summary>
    public static (CommandKind? Only, string Text) ParsePrefix(string query)
    {
        var q = query.TrimStart();
        if (q.Length == 0) return (null, "");
        CommandKind? only = q[0] switch
        {
            '>' => CommandKind.Action,
            '@' => CommandKind.Instance,
            '#' => CommandKind.Setting,
            '?' or '\uFF1F' => CommandKind.Help, // ASCII or full-width question mark
            _ => null,
        };
        return only is null ? (null, q.Trim()) : (only, q[1..].Trim());
    }

    public static List<CommandMatch> Search(string query, IEnumerable<CommandItem> items, int max = MaxResults)
    {
        var (only, text) = ParsePrefix(query);
        var result = new List<CommandMatch>();
        foreach (var item in items)
        {
            if (only is { } kind && item.Kind != kind) continue;
            var score = text.Length == 0 ? 1 : Score(text, item);
            if (score > 0) result.Add(new CommandMatch(item, score));
        }
        // Groups stay together; a group with a better best match comes first (a recognised invite always leads).
        var best = result.GroupBy(m => m.Item.Kind).ToDictionary(g => g.Key, g => g.Max(m => m.Score));
        return [.. result.OrderBy(m => m.Item.Kind == CommandKind.Invite ? 0 : 1).ThenByDescending(m => best[m.Item.Kind]).ThenBy(m => m.Item.Kind)
            .ThenByDescending(m => m.Score).ThenBy(m => m.Item.Title, StringComparer.CurrentCultureIgnoreCase).Take(max)];
    }

    /// <summary>0 = no match; higher is better.</summary>
    internal static int Score(string query, CommandItem item)
    {
        var q = query.Trim();
        if (q.Length == 0) return 1;
        var best = Match(q, item.Title, 100);
        var lower = q.ToLowerInvariant();
        if (IsAsciiLetters(lower))
        {
            if (item.TitleInitials.StartsWith(lower, StringComparison.Ordinal)) best = Math.Max(best, 75);
            else if (item.TitleInitials.Contains(lower, StringComparison.Ordinal)) best = Math.Max(best, 55);
            else if (lower.Length >= 2 && IsSubsequence(lower, item.TitleInitials)) best = Math.Max(best, 25);
        }
        if (item.Subtitle is { } sub) best = Math.Max(best, Match(q, sub, 35));
        foreach (var keyword in item.Keywords ?? [])
        {
            best = Math.Max(best, Match(q, keyword, 50));
            if (IsAsciiLetters(lower) && Pinyin.Initials(keyword).StartsWith(lower, StringComparison.Ordinal)) best = Math.Max(best, 30);
        }
        return best;
    }

    /// <summary>Starts with → top, a word starts with → a bit less, contains anywhere → less.</summary>
    private static int Match(string query, string text, int top)
    {
        if (text.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return top;
        var at = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0) return 0;
        return at > 0 && !char.IsLetterOrDigit(text[at - 1]) ? top * 8 / 10 : top * 6 / 10;
    }

    private static bool IsAsciiLetters(string s) => s.Length > 0 && s.All(c => c is >= 'a' and <= 'z');

    private static bool IsSubsequence(string needle, string hay)
    {
        var i = 0;
        foreach (var c in hay)
            if (i < needle.Length && c == needle[i]) i++;
        return i == needle.Length;
    }
}

/// <summary>
/// First letters of the pinyin of common Chinese characters, for "type the first letters" search. Uses the GB2312
/// order of the 3755 level-1 characters, which is sorted by pinyin; other characters are kept as they are (lower case).
/// </summary>
public static class Pinyin
{
    private static readonly int[] Starts =
        [45217, 45253, 45761, 46318, 46826, 47010, 47297, 47614, 48119, 48119, 49062, 49324, 49896, 50371, 50614, 50622, 50906, 51387, 51446, 52218, 52698, 52698, 52698, 52980, 53689, 54481, 55290];

    private static readonly Encoding? Gb2312 = Load();

    private static Encoding? Load()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string Initials(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c < 128)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                continue;
            }
            if (Initial(c) is { } letter) sb.Append(letter);
        }
        return sb.ToString();
    }

    public static char? Initial(char c)
    {
        if (Gb2312 is null || c < 0x4E00 || c > 0x9FFF) return null;
        var bytes = Gb2312.GetBytes([c]);
        if (bytes.Length != 2) return null;
        var code = bytes[0] * 256 + bytes[1];
        if (code < Starts[0] || code >= Starts[^1]) return null;
        for (var i = Starts.Length - 2; i >= 0; i--)
            if (code >= Starts[i]) return (char)('a' + i);
        return null;
    }
}
