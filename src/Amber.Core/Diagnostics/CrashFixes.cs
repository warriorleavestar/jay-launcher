namespace Amber.Core.Diagnostics;

/// <summary>A fix button on the "last session" panel (official design §6.3). Each one is done in place.</summary>
public enum CrashFixKind
{
    /// <summary>Turn on automatic memory, or raise the maximum.</summary>
    RaiseMemory,
    /// <summary>Open the instance's Java setting.</summary>
    ChooseJava,
    /// <summary>Disable one mod (<see cref="CrashFix.Argument"/> is its name as the crash report gives it).</summary>
    DisableMod,
    /// <summary>Switch to the mods tab.</summary>
    OpenMods,
    /// <summary>Repair game files.</summary>
    RepairFiles,
    /// <summary>Switch shaders off for the next start.</summary>
    ShadersOff,
    /// <summary>Open the log, errors only.</summary>
    ViewLog,
    /// <summary>Turn the Forge 1.12.2 start cache off for this instance.</summary>
    TransformerCacheOff,
    /// <summary>Start once without the universal loader.</summary>
    LaunchWithoutLoader,
}

public sealed record CrashFix(CrashFixKind Kind, string? Argument = null)
{
    public string LabelKey => Kind switch
    {
        CrashFixKind.RaiseMemory => "instance.lastrun.fix.memory",
        CrashFixKind.ChooseJava => "instance.lastrun.fix.java",
        CrashFixKind.DisableMod => "instance.lastrun.fix.disable_mod",
        CrashFixKind.OpenMods => "instance.lastrun.fix.open_mods",
        CrashFixKind.RepairFiles => "instance.lastrun.fix.repair",
        CrashFixKind.ShadersOff => "instance.lastrun.fix.shaders_off",
        CrashFixKind.TransformerCacheOff => "instance.lastrun.fix.cache_off",
        CrashFixKind.LaunchWithoutLoader => "instance.lastrun.fix.without_loader",
        _ => "instance.lastrun.fix.view_log",
    };
}

/// <summary>Maps a crash finding to the fixes shown next to it. Pure, keyed on the rule key, so it is unit-tested.</summary>
public static class CrashFixes
{
    /// <summary>
    /// Crash reports list suspects as "Display Name (modid)" or just a name. Returns the name to show and the id to match
    /// (the id is null when the report gives none).
    /// </summary>
    public static (string Name, string? Id) SplitSuspect(string suspect)
    {
        var text = suspect.Trim();
        var open = text.LastIndexOf('(');
        if (open > 0 && text.EndsWith(')'))
        {
            var id = text[(open + 1)..^1].Trim();
            var name = text[..open].Trim();
            if (id.Length > 0 && name.Length > 0) return (name, id);
        }
        return (text, null);
    }

    /// <summary>At most two fixes per finding, most direct first. Suspect mods only get a disable button for mod-related causes.</summary>
    public static IReadOnlyList<CrashFix> For(CrashFinding finding, IReadOnlyList<string>? suspectMods = null)
    {
        var key = finding.Key ?? "";
        var fixes = new List<CrashFix>(2);
        if (key.StartsWith("core.crash.memory.", StringComparison.Ordinal))
        {
            // The system itself is out of memory: a bigger heap makes it worse, so only point at the log.
            if (key == "core.crash.memory.system") fixes.Add(new(CrashFixKind.ViewLog));
            else fixes.Add(new(CrashFixKind.RaiseMemory));
        }
        else if (key.StartsWith("core.crash.java.", StringComparison.Ordinal))
            fixes.Add(new(CrashFixKind.ChooseJava));
        else if (key.StartsWith("core.crash.mods.", StringComparison.Ordinal))
        {
            if (suspectMods is { Count: > 0 } && key != "core.crash.mods.connector") fixes.Add(new(CrashFixKind.DisableMod, suspectMods[0]));
            fixes.Add(new(CrashFixKind.OpenMods));
        }
        else if (key is "core.crash.files.corrupt" or "core.crash.files.checksum" or "core.crash.forge.librarydownload")
            fixes.Add(new(CrashFixKind.RepairFiles));
        else if (key == "core.crash.graphics.shader")
            fixes.Add(new(CrashFixKind.ShadersOff));
        else
            fixes.Add(new(CrashFixKind.ViewLog));
        return fixes;
    }
}
