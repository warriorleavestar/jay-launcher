using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amber.Core.Content;

/// <summary>
/// "Help me fix" when no known cause was found (official design §6.3, J6): turn half of the suspect mods off, play, say
/// whether it still crashes, and repeat on the half that holds the problem until one mod is left. The session is saved in
/// the instance (<c>.jay-bisect.json</c>) so it carries on after Jay restarts, and [Restore everything] turns every mod
/// it switched off back on. Only mods that were on when it started are touched.
/// </summary>
public sealed class CrashBisect
{
    public const string FileName = ".jay-bisect.json";
    private const string Off = ".disabled";

    private readonly string _workDir;
    private State _state;

    private CrashBisect(string workDir, State state)
    {
        _workDir = workDir;
        _state = state;
    }

    /// <summary>Mods that may still be the problem.</summary>
    public IReadOnlyList<string> Candidates => _state.Candidates;

    /// <summary>Mods this session has switched off right now.</summary>
    public IReadOnlyList<string> DisabledNow => _state.Disabled;

    /// <summary>Round number (1-based once a round has started).</summary>
    public int Round => _state.Round;

    /// <summary>The mod that was found, once only one is left.</summary>
    public string? Found => _state.Candidates.Count == 1 ? _state.Candidates[0] : null;

    /// <summary>Waiting for "still crashes" / "works now" after a round turned mods off.</summary>
    public bool AwaitingAnswer => _state.Disabled.Count > 0 && Found is null;

    /// <summary>About how many more rounds until one mod is left.</summary>
    public int RoundsLeft => _state.Candidates.Count <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(_state.Candidates.Count));

    public DateTimeOffset StartedAt => _state.StartedAt;

    public static string PathFor(string workDir) => Path.Combine(workDir, FileName);

    private static string ModsDir(string workDir) => Path.Combine(workDir, "mods");

    /// <summary>A session saved in this instance, or null.</summary>
    public static CrashBisect? Load(string workDir)
    {
        var path = PathFor(workDir);
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize(File.ReadAllText(path), CrashBisectJson.Default.State);
            // The file sits in the instance folder, so a modpack or another program could have written it. Every name must be a
            // plain jar file name in mods; otherwise the session is not ours and nothing in it is renamed.
            return state is null || !IsSane(state) ? null : new CrashBisect(workDir, state);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSane(State state) =>
        state.Candidates.All(IsModFileName) && state.Disabled.All(IsModFileName)
        && state.History.All(step => step.TurnedOff is { } off && off.All(IsModFileName));

    /// <summary>A jar file name with no folder part, as <see cref="Start"/> records it.</summary>
    internal static bool IsModFileName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 255 && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(['/', '\\', ':', '\0']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.Trim('.', ' ').Length > 0 && Path.GetFileName(name) == name;

    /// <summary>Starts a session over the mods that are on now (jar files in <c>mods</c>). Nothing is switched off yet.</summary>
    public static CrashBisect Start(string workDir, DateTimeOffset? now = null)
    {
        var mods = ModsDir(workDir);
        var enabled = Directory.Exists(mods)
            ? Directory.EnumerateFiles(mods, "*.jar").Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        var bisect = new CrashBisect(workDir, new State { StartedAt = now ?? DateTimeOffset.Now, Candidates = enabled });
        bisect.Save();
        return bisect;
    }

    /// <summary>Turns the first half of the candidates off for the next try. Returns the mods switched off.</summary>
    public IReadOnlyList<string> NextRound()
    {
        if (AwaitingAnswer || Found is not null || _state.Candidates.Count == 0) return _state.Disabled;
        var half = _state.Candidates.Take(_state.Candidates.Count / 2).ToList();
        var done = new List<string>();
        try
        {
            foreach (var name in half)
            {
                SetEnabled(name, false);
                done.Add(name);
            }
        }
        finally
        {
            _state = _state with { Disabled = done, Round = _state.Round + 1 };
            Save();
        }
        return done;
    }

    /// <summary>
    /// The answer after playing with the current half off. Still crashing → the problem is in the mods that stayed on,
    /// so the ones switched off go back on; works now → the problem is among the ones switched off (they go back on too,
    /// the next round splits them again).
    /// </summary>
    public void Answer(bool stillCrashes)
    {
        if (!AwaitingAnswer) return;
        var off = _state.Disabled;
        var next = stillCrashes ? _state.Candidates.Except(off, StringComparer.Ordinal).ToList() : [.. off];
        foreach (var name in off) TryEnable(name);
        _state = _state with { Candidates = next, Disabled = [], History = [.. _state.History, new Step(off, stillCrashes)] };
        Save();
    }

    /// <summary>[Restore everything]: every mod this session switched off goes back on and the session ends.</summary>
    public void RestoreAll()
    {
        foreach (var name in _state.Disabled) TryEnable(name);
        _state = _state with { Disabled = [] };
        End();
    }

    /// <summary>Ends the session, leaving mods as they are (after "Turn off X and restore the others").</summary>
    public void End()
    {
        try { File.Delete(PathFor(_workDir)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void SetEnabled(string name, bool enabled)
    {
        if (!IsModFileName(name)) return;
        var on = Path.Combine(ModsDir(_workDir), name);
        var off = on + Off;
        if (enabled && File.Exists(off) && !File.Exists(on)) File.Move(off, on);
        else if (!enabled && File.Exists(on)) File.Move(on, off, overwrite: false);
    }

    private void TryEnable(string name)
    {
        try { SetEnabled(name, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void Save()
    {
        try { File.WriteAllText(PathFor(_workDir), JsonSerializer.Serialize(_state, CrashBisectJson.Default.State)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal sealed record Step(List<string> TurnedOff, bool StillCrashed);

    internal sealed record State
    {
        public DateTimeOffset StartedAt { get; init; }
        public List<string> Candidates { get; init; } = [];
        public List<string> Disabled { get; init; } = [];
        public int Round { get; init; }
        public List<Step> History { get; init; } = [];
    }
}

[JsonSerializable(typeof(CrashBisect.State))]
internal sealed partial class CrashBisectJson : JsonSerializerContext;
