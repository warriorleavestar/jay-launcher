using System.Text.RegularExpressions;

namespace Amber.Core.Launch;

/// <summary>
/// One row of the Logs tab: a line, plus the stack trace lines folded under it ("▸ 42 lines of stack trace") or the
/// number of times the same warning repeated right after it ("×N"). Index is the first source line's position.
/// </summary>
public sealed record LogEvent(int Index, StoredLogLine Line, IReadOnlyList<string> Stack, int Repeats)
{
    public bool HasStack => Stack.Count > 0;
    public bool IsError => !Line.FromJay && Line.Level >= GameLogLevel.Error;
    public bool IsWarning => !Line.FromJay && Line.Level == GameLogLevel.Warn;
}

/// <summary>Groups log lines into events (official design §6.9).</summary>
public static partial class LogEvents
{
    /// <summary>
    /// Stack trace lines ("\tat …", "Caused by: …", "\t... 12 more", "Suppressed: …") join the line above; the same
    /// warning or error repeated on consecutive lines becomes one row with a count.
    /// </summary>
    public static List<LogEvent> Group(IReadOnlyList<StoredLogLine> lines)
    {
        var events = new List<LogEvent>();
        List<string>? stack = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (events.Count > 0 && !line.FromJay && IsStackLine(line.Message))
            {
                var last = events[^1];
                if (stack is null || !ReferenceEquals(stack, last.Stack))
                {
                    stack = [.. last.Stack];
                    events[^1] = last with { Stack = stack };
                }
                stack.Add(line.Message);
                continue;
            }
            stack = null;
            if (events.Count > 0 && line.Level >= GameLogLevel.Warn && events[^1] is { Stack.Count: 0 } prev
                && prev.Line.Level == line.Level && prev.Line.FromJay == line.FromJay && prev.Line.Message == line.Message)
            {
                events[^1] = prev with { Repeats = prev.Repeats + 1 };
                continue;
            }
            events.Add(new LogEvent(i, line, [], 1));
        }
        return events;
    }

    internal static bool IsStackLine(string message) => StackLine().IsMatch(message);

    [GeneratedRegex(@"^(\s+at\s|\s*at [\w$.<>/]+\(|\s*\.\.\. \d+ more|\s*Caused by: |\s*Suppressed: )")]
    private static partial Regex StackLine();
}
