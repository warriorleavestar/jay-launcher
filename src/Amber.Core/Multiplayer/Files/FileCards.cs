namespace Amber.Core.Multiplayer.Files;

/// <summary>What a file is, for its icon, its type line and the follow-up buttons once it has arrived.</summary>
public enum FileKind
{
    File,
    /// <summary>A world packed into a zip (it has a level.dat); arrives with [Import into an instance].</summary>
    World,
    /// <summary>A mod list (names and versions only, no mod files); arrives with [Compare mods].</summary>
    ModList,
    Screenshot,
    /// <summary>A game log, with the computer's user name masked before it went out.</summary>
    Log,
    /// <summary>A folder packed into a zip.</summary>
    Folder,
    /// <summary>A program, installer or script (<see cref="ReceivedFiles.IsProgram"/>): always the extra warning.</summary>
    Program,
}

/// <summary>Where a file card is (design: "Sending and receiving files").</summary>
public enum FileCardState
{
    /// <summary>A world or folder is being zipped before it goes out.</summary>
    Packing,
    /// <summary>The offer went out; waiting for them to accept.</summary>
    Waiting,
    /// <summary>They are not reachable; it starts by itself once we both are online.</summary>
    Queued,
    /// <summary>They are not reachable and queueing is off.</summary>
    Unreachable,
    Sending,
    /// <summary>The connection broke part way; sending again carries on from there.</summary>
    Interrupted,
    /// <summary>Arrived here, checked, in the save folder.</summary>
    Received,
    /// <summary>Arrived there, and their Jay checked the whole file.</summary>
    Delivered,
    /// <summary>Declined or not answered in five minutes: one line for both, so a "no" does not feel personal.</summary>
    NotAccepted,
    Cancelled,
    Failed,
}

[Flags]
public enum FileCardActions
{
    None = 0,
    Cancel = 1,
    Resume = 2,
    OpenFolder = 4,
    ImportWorld = 8,
    CompareMods = 16,
    SendAgain = 32,
}

/// <summary>The rules behind a file card: which state a progress report means, what the card says and what it offers.</summary>
public static class FileCards
{
    /// <summary>The state a transfer report puts the card in. An outgoing offer nobody can take yet is <see cref="FileCardState.Unreachable"/>; the caller queues it when queueing is on.</summary>
    public static FileCardState StateOf(FileProgress p)
    {
        if (!p.Finished) return p.Started || !p.Outgoing ? FileCardState.Sending : FileCardState.Waiting;
        if (p.Error is null && p.End == FileEnd.None) return p.Outgoing ? FileCardState.Delivered : FileCardState.Received;
        return p.End switch
        {
            FileEnd.NotAccepted => FileCardState.NotAccepted,
            FileEnd.Cancelled => FileCardState.Cancelled,
            FileEnd.Stalled => FileCardState.Interrupted,
            FileEnd.Offline => p.Started ? FileCardState.Interrupted : FileCardState.Unreachable,
            _ => FileCardState.Failed,
        };
    }

    public static FileCardActions ActionsOf(FileCardState state, bool outgoing, FileKind kind) => state switch
    {
        FileCardState.Packing or FileCardState.Waiting or FileCardState.Queued or FileCardState.Sending => FileCardActions.Cancel,
        FileCardState.Interrupted => outgoing ? FileCardActions.Resume | FileCardActions.Cancel : FileCardActions.None,
        FileCardState.Received => FileCardActions.OpenFolder
            | (kind == FileKind.World ? FileCardActions.ImportWorld : FileCardActions.None)
            | (kind == FileKind.ModList ? FileCardActions.CompareMods : FileCardActions.None),
        FileCardState.NotAccepted or FileCardState.Cancelled or FileCardState.Unreachable or FileCardState.Failed =>
            outgoing ? FileCardActions.SendAgain : FileCardActions.None,
        _ => FileCardActions.None,
    };

    /// <summary>The localization key of the line under the file name (arguments: see each key in en.json).</summary>
    public static string LineKey(FileCardState state, bool outgoing) => state switch
    {
        FileCardState.Packing => "files.card.packing",
        FileCardState.Waiting => "files.card.waiting",
        FileCardState.Queued => "files.card.queued",
        FileCardState.Unreachable => "files.card.unreachable",
        FileCardState.Sending => outgoing ? "files.card.sending" : "files.card.receiving",
        FileCardState.Interrupted => outgoing ? "files.card.interrupted" : "files.card.interrupted_in",
        FileCardState.Received => "files.card.received",
        FileCardState.Delivered => "files.card.delivered",
        FileCardState.NotAccepted => outgoing ? "files.card.not_accepted" : "files.card.not_accepted_in",
        FileCardState.Cancelled => "files.card.cancelled",
        _ => "files.card.failed",
    };

    /// <summary>Still moving or about to: counts in the Activity bell.</summary>
    public static bool IsActive(FileCardState state) =>
        state is FileCardState.Packing or FileCardState.Waiting or FileCardState.Queued or FileCardState.Sending;
}

/// <summary>Telling file kinds apart by name (before a file arrives) and by content (a received zip with a world in it).</summary>
public static class FileKinds
{
    /// <summary>A mod list Jay wrote (<see cref="FilePacker.WriteModList"/>).</summary>
    public const string ModListExtension = ".jaymods";

    public static FileKind Of(string fileName)
    {
        if (ReceivedFiles.IsProgram(fileName)) return FileKind.Program;
        var ext = Path.GetExtension(fileName.TrimEnd(' ', '.')).ToLowerInvariant();
        if (ext == ModListExtension) return FileKind.ModList;
        if (ext is ".png" or ".jpg" or ".jpeg" or ".webp") return FileKind.Screenshot;
        if (ext == ".log" || fileName.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase)) return FileKind.Log;
        return FileKind.File;
    }

    /// <summary>A received zip holds a world when it has a level.dat somewhere (what importing a world looks for).</summary>
    public static bool LooksLikeWorld(string zipPath)
    {
        if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            return FilePacker.HasWorld(zip);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The kind of a file that arrived (by name, and a world inside a zip).</summary>
    public static FileKind OfReceived(string path) => Of(path) is var kind && kind == FileKind.File && LooksLikeWorld(path) ? FileKind.World : kind;

    /// <summary>The localization key of the type line ("World", "Mod list", …).</summary>
    public static string LabelKey(FileKind kind) => kind switch
    {
        FileKind.World => "files.kind.world",
        FileKind.ModList => "files.kind.mod_list",
        FileKind.Screenshot => "files.kind.screenshot",
        FileKind.Log => "files.kind.log",
        FileKind.Folder => "files.kind.folder",
        FileKind.Program => "files.kind.program",
        _ => "files.kind.file",
    };
}

/// <summary>How fast files may go out (Settings › Multiplayer › Files › speed).</summary>
public static class TransferPace
{
    public const string Unlimited = "unlimited";
    /// <summary>Full speed, except while a game runs.</summary>
    public const string GiveWay = "give-way";
    /// <summary>Always slow, to save data.</summary>
    public const string SaveData = "save-data";

    public static readonly string[] All = [Unlimited, GiveWay, SaveData];

    /// <summary>The slow speed, in bytes per second.</summary>
    public const long Slow = 48 * 1024;

    public static string Normalize(string? setting) => All.FirstOrDefault(s => string.Equals(s, setting, StringComparison.OrdinalIgnoreCase)) ?? Unlimited;

    /// <summary>The send limit right now (0 = no limit).</summary>
    public static long LimitFor(string? setting, bool playing) => Normalize(setting) switch
    {
        GiveWay => playing ? Slow : 0,
        SaveData => Slow,
        _ => 0,
    };
}
