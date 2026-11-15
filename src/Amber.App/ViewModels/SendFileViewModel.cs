using System.Collections.ObjectModel;
using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Files;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>Something the panel can send: a world, an instance's mod list, a screenshot or a log, or a picked file or folder.</summary>
public sealed record SendCandidate(string Label, string Detail, FileSource Source);

/// <summary>Someone the panel can send to, and what happens if I do (now, or it waits).</summary>
public sealed record SendRecipientItem(FileRecipient Recipient, string Status, bool Reachable) : LocalizedRecord
{
    public string Name => Recipient.DisplayName;
}

/// <summary>
/// The "Send a file" panel (design: "Where to send from"). It asks two things only: what, and to whom. It opens from a
/// room member's card (the recipient filled in), from a world on the instance's Worlds tab (the world filled in) and from
/// the paperclip menu in a conversation. Worlds and folders are zipped first, a mod list has names and versions only, and
/// a log has the computer's user name masked. The network's file size limit is said here, before anything is sent.
/// </summary>
public sealed partial class SendFileViewModel(MultiplayerViewModel multiplayer, LauncherService service) : ObservableObject
{
    /// <summary>The kinds in the "what" choice, in order.</summary>
    internal static readonly FileKind[] Kinds = [FileKind.File, FileKind.World, FileKind.ModList, FileKind.Screenshot, FileKind.Log, FileKind.Folder];

    public OptionItem[] KindOptions { get; } = OptionItem.Keys("files.menu.file", "files.menu.world", "files.menu.mods", "files.menu.screenshot", "files.menu.log", "files.menu.folder");

    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Kind), nameof(UsesPicker), nameof(UsesList), nameof(Note), nameof(HasNote), nameof(CanSend), nameof(PickText))]
    public partial int KindIndex { get; set; }

    public FileKind Kind => Kinds[Math.Clamp(KindIndex, 0, Kinds.Length - 1)];

    /// <summary>A file or a folder comes from the system picker; the rest from a list of what is on this computer.</summary>
    public bool UsesPicker => Kind is FileKind.File or FileKind.Folder;
    public bool UsesList => !UsesPicker;

    public ObservableCollection<SendCandidate> Candidates { get; } = [];
    public bool HasNoCandidates => UsesList && Candidates.Count == 0;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSend))] public partial SendCandidate? Candidate { get; set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSend), nameof(PickText), nameof(HasPicked))] public partial string? PickedPath { get; set; }
    public bool HasPicked => PickedPath is not null;
    public string PickText => PickedPath is { } path ? Path.GetFileName(path.TrimEnd('/', '\\')) : L.T(Kind == FileKind.Folder ? "files.panel.pick_folder" : "files.panel.pick_file");

    public ObservableCollection<SendRecipientItem> Recipients { get; } = [];
    public bool HasNoRecipients => Recipients.Count == 0;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSend))] public partial SendRecipientItem? Recipient { get; set; }

    /// <summary>The system picker (the view sets it; true asks for a folder). Self-checks set <see cref="PickedPath"/> directly.</summary>
    internal Func<bool, Task<string?>>? Pick { get; set; }

    /// <summary>What happens to this kind before it goes ("A world is zipped first").</summary>
    public string Note => Kind switch
    {
        FileKind.World => L.T("files.panel.world_note"),
        FileKind.Folder => L.T("files.panel.folder_note"),
        FileKind.ModList => L.T("files.panel.mods_note"),
        FileKind.Log => L.T("files.panel.log_note"),
        _ => "",
    };
    public bool HasNote => Note.Length > 0;

    /// <summary>The network's rule, said before choosing: no files at all, or up to a size.</summary>
    public string LimitNote => !multiplayer.NetworkPolicy.AllowFiles ? L.T("files.network_no_files")
        : multiplayer.NetworkPolicy.MaxFileBytes is { } max ? L.F("files.panel.limit", FileOfferItem.Size(max)) : "";
    public bool HasLimitNote => LimitNote.Length > 0;

    public bool CanSend => Recipient is not null && (UsesPicker ? PickedPath is not null : Candidate is not null) && multiplayer.NetworkPolicy.AllowFiles;

    /// <summary>Instance to list first (opened from that instance's Worlds tab).</summary>
    private string? _instanceFirst;

    /// <summary>Opens the panel; whatever is given is filled in.</summary>
    internal void Open(FileRecipient? to = null, FileKind kind = FileKind.File, string? path = null, string? instanceId = null)
    {
        _instanceFirst = instanceId;
        LoadRecipients(to);
        PickedPath = null;
        KindIndex = Math.Max(0, Array.IndexOf(Kinds, kind));
        LoadCandidates(path);
        if (UsesPicker && path is not null) PickedPath = path;
        OnPropertyChanged(nameof(LimitNote));
        OnPropertyChanged(nameof(HasLimitNote));
        IsOpen = true;
    }

    partial void OnKindIndexChanged(int value)
    {
        PickedPath = null;
        if (IsOpen) LoadCandidates(null);
    }

    private void LoadRecipients(FileRecipient? to)
    {
        Recipients.Clear();
        foreach (var friend in multiplayer.ContactList)
        {
            var here = multiplayer.PeerOf(friend) is not null;
            Recipients.Add(new SendRecipientItem(new FileRecipient(friend.Name, friend, null),
                L.T(here ? "files.panel.in_room" : service.Settings.FilesQueueOffline ? "files.panel.offline_queue" : "files.panel.offline"), here));
        }
        foreach (var peer in multiplayer.OnlinePeers)
        {
            if (multiplayer.ContactList.Any(f => f.Devices.Any(d => d.PublicKey.AsSpan().SequenceEqual(peer.Entry.PublicKey)))) continue;
            Recipients.Add(new SendRecipientItem(new FileRecipient(peer.Entry.Name, null, peer), L.T("files.panel.in_room"), true));
        }
        Recipient = to is null ? Recipients.FirstOrDefault(r => r.Reachable) ?? Recipients.FirstOrDefault()
            : Recipients.FirstOrDefault(r => (to.Friend is not null && ReferenceEquals(r.Recipient.Friend?.Contact, to.Friend.Contact))
                || (to.Peer is not null && r.Recipient.Peer?.Entry.Fingerprint == to.Peer.Entry.Fingerprint));
        OnPropertyChanged(nameof(HasNoRecipients));
    }

    /// <summary>What this computer has of the chosen kind: worlds, mod lists, screenshots, logs (newest first; the opening instance first).</summary>
    private void LoadCandidates(string? select)
    {
        Candidates.Clear();
        Candidate = null;
        if (UsesList)
        {
            foreach (var (id, name, instance) in Instances())
            {
                switch (Kind)
                {
                    case FileKind.World:
                        foreach (var world in SafeList(() => WorldManager.List(instance.SavesDir)))
                            Candidates.Add(new SendCandidate(world.Name, name, new FileSource(FileKind.World, world.Path)));
                        break;
                    case FileKind.ModList when instance.Loader is not null && Directory.Exists(instance.ModsDir):
                        var mods = SafeList(() => Directory.EnumerateFiles(instance.ModsDir, "*.jar").ToList()).Count;
                        Candidates.Add(new SendCandidate(name, L.F("files.panel.mod_count", mods), new FileSource(FileKind.ModList, instance.ModsDir, name)));
                        break;
                    case FileKind.Screenshot:
                        foreach (var shot in SafeList(() => Directory.Exists(instance.ScreenshotsDir)
                                     ? new DirectoryInfo(instance.ScreenshotsDir).EnumerateFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Take(20).ToList() : []))
                            Candidates.Add(new SendCandidate(shot.Name, name, new FileSource(FileKind.Screenshot, shot.FullName)));
                        break;
                    case FileKind.Log:
                        var logs = Path.Combine(instance.WorkDir, "logs");
                        foreach (var log in SafeList(() => Directory.Exists(logs)
                                     ? new DirectoryInfo(logs).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(5).ToList() : []))
                            Candidates.Add(new SendCandidate(log.Name, name, new FileSource(FileKind.Log, log.FullName)));
                        break;
                }
            }
            Candidate = Candidates.FirstOrDefault(c => select is not null && string.Equals(Path.GetFullPath(c.Source.Path), Path.GetFullPath(select), StringComparison.OrdinalIgnoreCase))
                ?? Candidates.FirstOrDefault();
        }
        OnPropertyChanged(nameof(HasNoCandidates));
    }

    private IEnumerable<(string Id, string Name, GameInstance Instance)> Instances()
    {
        var ids = SafeList(() => service.InstalledVersions());
        if (_instanceFirst is { } first && ids.Remove(first)) ids.Insert(0, first);
        foreach (var id in ids)
        {
            GameInstance? instance = null;
            try { instance = service.GetInstance(id); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
            if (instance is not null) yield return (id, service.MetaOf(id)?.DisplayName ?? id, instance);
        }
    }

    private static List<T> SafeList<T>(Func<List<T>> read)
    {
        try { return read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    [RelayCommand]
    private async Task PickAsync()
    {
        if (Pick is { } pick && await pick(Kind == FileKind.Folder) is { } path) PickedPath = path;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!CanSend || Recipient is not { } to) return;
        var source = UsesPicker ? new FileSource(Kind, PickedPath!) : Candidate!.Source;
        IsOpen = false;
        await multiplayer.SendFileThingAsync(to.Recipient, source);
    }

    [RelayCommand]
    private void Close() => IsOpen = false;
}
