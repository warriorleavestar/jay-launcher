namespace Amber.Core.Multiplayer;

/// <summary>Commands typed into the server console: ↑ goes back, ↓ forward, past the newest is an empty line again.</summary>
public sealed class ConsoleHistory(int capacity = 100)
{
    private readonly List<string> _lines = [];
    private int _cursor;

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Remember a sent command (blank lines and an immediate repeat are skipped) and reset the cursor.</summary>
    public void Add(string line)
    {
        line = line.Trim();
        if (line.Length > 0 && (_lines.Count == 0 || _lines[^1] != line))
        {
            _lines.Add(line);
            if (_lines.Count > capacity) _lines.RemoveAt(0);
        }
        _cursor = _lines.Count;
    }

    /// <summary>↑: the command before the current one (stays on the oldest).</summary>
    public string? Previous()
    {
        if (_lines.Count == 0) return null;
        _cursor = Math.Max(0, _cursor - 1);
        return _lines[_cursor];
    }

    /// <summary>↓: the next command, or "" after the newest.</summary>
    public string? Next()
    {
        if (_cursor >= _lines.Count) return null;
        _cursor++;
        return _cursor == _lines.Count ? "" : _lines[_cursor];
    }
}
