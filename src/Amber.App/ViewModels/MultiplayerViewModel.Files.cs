using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Game;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Files;
using Avalonia.Threading;

namespace Amber.App.ViewModels;

/// <summary>Who a file goes to: a friend (wherever they are) or someone in the room.</summary>
public sealed record FileRecipient(string Name, ContactItem? Friend, OnlinePeerItem? Peer)
{
    public string DisplayName => Name.Length > 0 ? Name : L.T("multiplayer.peers.player");
}

/// <summary>What to send: a file, or something Jay prepares first (a world or folder to zip, a mod list, a masked log).</summary>
public sealed record FileSource(FileKind Kind, string Path, string? Title = null, ModManifest? Manifest = null);

/// <summary>
/// Files as part of conversations (redesign batch 12, design "Sending and receiving files"). The transfer itself is the
/// room's private channel (<see cref="PrivateMessenger"/>): end-to-end encrypted, accepted before it starts, resumable and
/// checked as a whole. This part adds the cards, the queue for friends who are not reachable yet, the packing, the
/// Activity tasks, auto-accepting checked friends (off by default) and the follow-ups on a received world or mod list.
/// A friend is reachable while we are in the same room; the file waits on this computer until then.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private readonly Dictionary<string, TransferItem> _cards = new(StringComparer.Ordinal);
    private readonly FileSendQueue _fileQueue = new();

    /// <summary>Instances for a received world or mod list (refreshed when one arrives and when the send panel opens).</summary>
    public ObservableCollection<InstanceChoice> InstanceChoices { get; } = [];

    /// <summary>Packed worlds, folders, mod lists and masked logs wait here until they are delivered.</summary>
    internal string OutgoingDir { get; set; } = Path.Combine(AppSettings.DataDir, "outgoing");

    /// <summary>Received files go here: Settings › Multiplayer › Files, else "Downloads/Jay".</summary>
    internal string ReceiveFolder => _service.Settings.FilesSaveFolder is { Length: > 0 } folder ? folder : DownloadsDir;

    /// <summary>How the cards name that folder: "Downloads/Jay", or the folder chosen in settings.</summary>
    public string ReceiveFolderLabel => _service.Settings.FilesSaveFolder is { Length: > 0 } folder ? folder : L.T("files.downloads_jay");

    private SendFileViewModel? _sendPanel;

    /// <summary>The "Send a file" panel, over the whole window.</summary>
    public SendFileViewModel SendPanel => _sendPanel ??= new SendFileViewModel(this, _service);

    /// <summary>Opens the panel with the recipient and / or the thing filled in.</summary>
    internal void OpenSendPanel(FileRecipient? to = null, FileKind kind = FileKind.File, string? path = null, string? instanceId = null)
    {
        RefreshInstanceChoices();
        SendPanel.Open(to, kind, path, instanceId);
    }

    /// <summary>[Send a file] on a room member's card.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SendFileToPerson(PersonItem? person)
    {
        if (person is not { IsMe: false } || person.Online is not { } peer) return;
        var friend = ContactList.FirstOrDefault(f => f.Devices.Any(d => d.PublicKey.AsSpan().SequenceEqual(peer.Entry.PublicKey)));
        OpenSendPanel(new FileRecipient(peer.Entry.Name, friend, friend is null ? peer : null));
    }

    /// <summary>Files waiting for someone (self-check).</summary>
    internal int QueuedFiles => _fileQueue.Count;

    internal TransferItem? CardOf(string id) => _cards.GetValueOrDefault(id);

    private void InitFiles()
    {
        // Leftovers from a run that closed mid-send.
        try { FilePacker.CleanOld(OutgoingDir, TimeSpan.FromDays(1)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Settings › Multiplayer › Files changed: the live channel saves to the new folder from the next file on.</summary>
    internal void ApplyFileSettings()
    {
        if (_messenger is { } messenger) messenger.Downloads = ReceiveFolder;
        OnPropertyChanged(nameof(ReceiveFolderLabel));
    }

    /// <summary>The send limit right now, from Settings › Multiplayer › Files › speed.</summary>
    internal long SendLimitNow() => TransferPace.LimitFor(_service.Settings.FilesSpeed, _service.AnyRunning);

    private static string PersonOf(ContactItem friend) => PersonKey(friend.Contact);
    private static string PersonOf(OnlinePeerItem peer) => "fp:" + peer.Entry.Fingerprint;

    /// <summary>Who I can hand the file to right now: the friend's device in this room, or the room member.</summary>
    private OnlinePeerItem? ReachableNow(FileRecipient to) =>
        to.Friend is { } friend ? PeerOf(friend)
        : to.Peer is { } peer ? OnlinePeers.FirstOrDefault(p => p.Entry.Fingerprint == peer.Entry.Fingerprint && KeyMatches(p.Entry)) : null;

    /// <summary>"Direct" or "Relayed": how my computer reaches the one that forwards private frames (the host, or them when I host).</summary>
    private string RouteTo(string fingerprint)
    {
        var person = IsHosting ? People.FirstOrDefault(p => p.Fingerprint == fingerprint) : People.FirstOrDefault(p => p.IsHost);
        return person?.Link.Kind switch
        {
            LinkKind.Direct => L.T("multiplayer.room.direct"),
            LinkKind.Relay => L.T("multiplayer.room.relayed"),
            _ => "",
        };
    }

    /// <summary>The name a file will go out under (worlds and folders as a zip, a mod list as .jaymods).</summary>
    internal static string OutgoingName(FileSource source) => source.Kind switch
    {
        FileKind.World or FileKind.Folder => PrivateMessenger.SafeName(new DirectoryInfo(source.Path).Name) + ".zip",
        FileKind.ModList => PrivateMessenger.SafeName((source.Title ?? "mods") + " mods") + FileKinds.ModListExtension,
        _ => PrivateMessenger.SafeName(Path.GetFileName(source.Path)),
    };

    /// <summary>
    /// Sends a file, a world, a folder, a mod list or a log. The card goes into the conversation (friends) and the room's
    /// list, and a task into Activity. Someone not reachable gets it later (a friend, with queueing on) or the card says why not.
    /// </summary>
    internal async Task<TransferItem> SendFileThingAsync(FileRecipient to, FileSource source)
    {
        var name = OutgoingName(source);
        var kind = source.Kind == FileKind.File ? FileKinds.Of(name) : source.Kind;
        var item = new TransferItem(PrivateMessenger.NewTransferId(), true, name, 0, this, kind, to.DisplayName)
        {
            Person = to.Friend is { } f ? PersonOf(f) : to.Peer is { } p ? PersonOf(p) : null,
        };
        _cards[item.Id] = item;
        Transfers.Insert(0, item);
        while (Transfers.Count > 50) Transfers.RemoveAt(Transfers.Count - 1);
        if (to.Friend is { } friend) AddFriendLine(friend.Contact, new FriendLine(FriendLineKind.File, () => item.Name, DateTimeOffset.Now) { File = item });
        item.Task = _service.Tasks.Track("file", L.F("files.task.send", name, to.DisplayName), cancel: () => _ = CancelCardAsync(item));

        var needsPacking = source.Kind is FileKind.World or FileKind.Folder or FileKind.ModList or FileKind.Log;
        if (needsPacking)
        {
            Set(item, FileCardState.Packing);
            item.Packing = new CancellationTokenSource();
            var token = item.Packing.Token;
            try
            {
                item.SourcePath = await Task.Run(() => source.Kind switch
                {
                    FileKind.World or FileKind.Folder => FilePacker.PackFolder(source.Path, OutgoingDir, token),
                    FileKind.ModList => FilePacker.WriteModList(source.Manifest ?? ModManifest.Read(source.Path), source.Title ?? "mods", OutgoingDir),
                    _ => FilePacker.RedactLog(source.Path, OutgoingDir),
                }, token);
                item.Packed = true;
            }
            catch (OperationCanceledException)
            {
                Finish(item, FileCardState.Cancelled);
                return item;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                item.Error = e.Message;
                Finish(item, FileCardState.Failed);
                return item;
            }
            finally
            {
                item.Packing?.Dispose();
                item.Packing = null;
            }
        }
        else item.SourcePath = source.Path;

        try { item.Size = new FileInfo(item.SourcePath!).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (!NetworkPolicy.AllowsFile(item.Size))
        {
            item.Error = L.T(NetworkPolicy.AllowFiles ? "files.too_big" : "files.network_no_files");
            Finish(item, FileCardState.Failed);
            return item;
        }
        StartOrQueue(item, to);
        return item;
    }

    /// <summary>Hands the file over now, or queues it for a friend who is not reachable (when queueing is on).</summary>
    private void StartOrQueue(TransferItem item, FileRecipient to)
    {
        if (ReachableNow(to) is { } peer && _messenger is not null)
        {
            Start(item, peer);
            return;
        }
        if (to.Friend is not null && _service.Settings.FilesQueueOffline && item.Person is { } person && item.SourcePath is { } path
            && _fileQueue.Add(new QueuedFile(item.Id, person, path, item.Name, item.Kind, DateTimeOffset.Now)))
        {
            Set(item, FileCardState.Queued);
            return;
        }
        Finish(item, FileCardState.Unreachable);
    }

    private void Start(TransferItem item, OnlinePeerItem peer)
    {
        if (_messenger is not { } messenger || item.SourcePath is not { } path) return;
        item.Fingerprint = peer.Entry.Fingerprint;
        item.Route = RouteTo(peer.Entry.Fingerprint);
        item.Error = null;
        item.Finished = false;
        item.ResetSpeed();
        if (item.Task is not { IsRunning: true }) item.Task = _service.Tasks.Track("file", L.F("files.task.send", item.Name, item.PeerName), cancel: () => _ = CancelCardAsync(item));
        Set(item, FileCardState.Waiting);
        _ = Task.Run(async () =>
        {
            try { await messenger.SendFileAsync(peer.Entry.Fingerprint, path, item.Id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or ObjectDisposedException)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    item.Error = e.Message;
                    Finish(item, FileCardState.Failed);
                });
            }
        });
    }

    /// <summary>Someone may have come online: their queued files start, oldest first.</summary>
    private void FlushFileQueue()
    {
        if (_messenger is null || _fileQueue.Count == 0) return;
        foreach (var friend in ContactList)
        {
            if (PeerOf(friend) is not { } peer || !_fileQueue.IsWaitingFor(PersonOf(friend))) continue;
            foreach (var queued in _fileQueue.TakeFor(PersonOf(friend)))
                if (_cards.TryGetValue(queued.Id, out var item) && item.State is FileCardState.Queued or FileCardState.Interrupted) Start(item, peer);
        }
    }

    /// <summary>A progress report from the room's channel: the card follows it.</summary>
    private void ApplyTransfer(FileProgress p)
    {
        if (!_cards.TryGetValue(p.Id, out var item))
        {
            // A mod file during a sync, or a file accepted before its card existed.
            item = new TransferItem(p.Id, p.Outgoing, p.Name, p.Size, this, peerName: NameOfFingerprint(p.Peer));
            _cards[p.Id] = item;
            Transfers.Insert(0, item);
            while (Transfers.Count > 50) Transfers.RemoveAt(Transfers.Count - 1);
        }
        if (item.Size == 0) item.Size = p.Size;
        item.Sample(p.Done, DateTimeOffset.Now);
        var state = FileCards.StateOf(p);
        if (!p.Finished)
        {
            Set(item, state);
            item.Task?.Report(item.Size > 0 ? Math.Clamp((double)p.Done / item.Size, 0, 1) : null, item.StatusLine, p.Done);
            return;
        }
        item.Error = p.Error;
        item.SavedPath = p.SavedPath is { Length: > 0 } saved ? saved : null;
        if (!p.Outgoing && item.SavedPath is { } path)
        {
            item.Kind = FileKinds.OfReceived(path);
            RefreshInstanceChoices();
            if (_service.Settings.FilesNotifyDone) SetStatus(StatusMessage.Success("multiplayer.private.saved", path));
        }
        // An offer that found nobody: a friend's file waits again when queueing is on.
        if (state == FileCardState.Unreachable && p.Outgoing && FriendForCard(item) is { } friend)
        {
            item.Finished = false;
            StartOrQueue(item, new FileRecipient(item.PeerName, friend, null));
            return;
        }
        Finish(item, state);
    }

    /// <summary>The friend a card belongs to (by its person key), if it is a friend's.</summary>
    private ContactItem? FriendForCard(TransferItem item) => item.Person is { } person ? ContactList.FirstOrDefault(f => PersonOf(f) == person) : null;

    private string NameOfFingerprint(string fingerprint) =>
        OnlinePeers.FirstOrDefault(p => p.Entry.Fingerprint == fingerprint)?.Entry.Name is { Length: > 0 } name ? name : L.T("multiplayer.peers.player");

    private static void Set(TransferItem item, FileCardState state)
    {
        item.State = state;
        item.Changed();
    }

    /// <summary>The card reached an end (or a pause that needs me): the task in Activity ends with it.</summary>
    private void Finish(TransferItem item, FileCardState state)
    {
        item.Finished = state is not (FileCardState.Queued or FileCardState.Packing or FileCardState.Waiting or FileCardState.Sending);
        Set(item, state);
        if (item.Task is { IsRunning: true } task)
        {
            if (state is FileCardState.Delivered or FileCardState.Received) task.Succeed(item.StatusLine);
            else if (state == FileCardState.Cancelled) task.Cancelled();
            else task.Fail(item.StatusLine);
        }
        if (state == FileCardState.Delivered && item.Packed && item.SourcePath is { } packed)
        {
            FilePacker.TryDelete(packed);
            item.Packed = false;
        }
    }

    /// <summary>[Cancel] on a card: a queued or packing file simply stops; one on the wire tells the other side.</summary>
    internal async Task CancelCardAsync(TransferItem item)
    {
        if (item.State == FileCardState.Packing)
        {
            item.Packing?.Cancel();
            return;
        }
        if (_fileQueue.Remove(item.Id) || item.State == FileCardState.Queued || item.State == FileCardState.Interrupted)
        {
            Finish(item, FileCardState.Cancelled);
            return;
        }
        if (_messenger is { } messenger) await messenger.CancelAsync(item.Id);
        else Finish(item, FileCardState.Cancelled);
    }

    /// <summary>[Carry on] and [Send again]: the same file to the same person. A broken transfer resumes where it stopped.</summary>
    internal void ResumeCard(TransferItem item)
    {
        if (!item.Outgoing || item.SourcePath is not { } path || !File.Exists(path))
        {
            item.Error = L.T("files.source_gone");
            Finish(item, FileCardState.Failed);
            return;
        }
        var friend = FriendForCard(item);
        var peer = friend is null && item.Fingerprint is { } fp ? OnlinePeers.FirstOrDefault(p => p.Entry.Fingerprint == fp) : null;
        item.Task = null;
        _fileQueue.Remove(item.Id);
        StartOrQueue(item, new FileRecipient(item.PeerName, friend, peer));
    }

    /// <summary>
    /// The room's channel closed (I left, or the room ended). Files on the wire stop where they are: a friend's file
    /// waits again (one that had started says it broke and carries on from there by itself), anyone else's card says
    /// the connection broke.
    /// </summary>
    private void OnFilesChannelClosed()
    {
        foreach (var item in _cards.Values.ToList())
        {
            if (!item.IsActive || item.State is FileCardState.Queued or FileCardState.Packing) continue;
            if (item.Outgoing && FriendForCard(item) is { } friend && _service.Settings.FilesQueueOffline && item.State == FileCardState.Waiting)
            {
                item.Finished = false;
                StartOrQueue(item, new FileRecipient(item.PeerName, friend, null));
                continue;
            }
            Finish(item, FileCardState.Interrupted);
            if (item.Outgoing && item.State == FileCardState.Interrupted && FriendForCard(item) is { } again && _service.Settings.FilesQueueOffline
                && item.Person is { } person && item.SourcePath is { } path)
                _fileQueue.Add(new QueuedFile(item.Id, person, path, item.Name, item.Kind, DateTimeOffset.Now));
        }
    }

    /// <summary>Someone offered a file: decline it for "for a child" rules, take it from a checked friend when asked to, or ask.</summary>
    private bool TakeOfferAutomatically(FileOffer offer, byte[]? key)
    {
        if (!_service.Settings.FilesAutoAcceptFriends || ReceivedFiles.IsProgram(offer.Name) || Child.FriendsFilesOnly) return false;
        return key is not null && FriendOf(key, null) is { } friend && StateOf(friend) != FriendState.NewDevice
            && friend.Devices.Any(d => d.PublicKey.AsSpan().SequenceEqual(key) && d.Verified && !d.NewDevice);
    }

    /// <summary>An offer came in: the friend's conversation keeps one line pointing at the request bar.</summary>
    private void NoteFileOffer(FileOffer offer, byte[]? key)
    {
        if (key is null || FriendByKey(key) is not { } friend) return;
        AddFriendLine(friend, new FriendLine(FriendLineKind.Note, () => L.F("files.note.offer", offer.Name, FileOfferItem.Size(offer.Size)), DateTimeOffset.Now));
    }

    /// <summary>I accepted an offer: its card goes into the conversation with the friend who sent it.</summary>
    private void CardForAccepted(FileOffer offer)
    {
        var key = OnlinePeers.FirstOrDefault(p => p.Entry.Fingerprint == offer.FromFingerprint)?.Entry.PublicKey;
        var friend = key is null ? null : FriendOf(key, null);
        var item = new TransferItem(offer.Id, false, offer.Name, offer.Size, this, peerName: offer.FromName.Length > 0 ? offer.FromName : L.T("multiplayer.peers.player"))
        {
            Person = friend is not null ? PersonOf(friend) : "fp:" + offer.FromFingerprint,
            Fingerprint = offer.FromFingerprint,
        };
        item.Route = RouteTo(offer.FromFingerprint);
        _cards[item.Id] = item;
        Transfers.Insert(0, item);
        if (friend is not null) AddFriendLine(friend.Contact, new FriendLine(FriendLineKind.File, () => item.Name, DateTimeOffset.Now) { File = item });
        item.Task = _service.Tasks.Track("file", L.F("files.task.receive", offer.Name, item.PeerName), cancel: () => _ = CancelCardAsync(item));
    }

    internal void RefreshInstanceChoices()
    {
        var now = new List<InstanceChoice>();
        try
        {
            foreach (var id in _service.InstalledVersions())
                now.Add(new InstanceChoice(id, _service.MetaOf(id)?.DisplayName ?? id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (now.SequenceEqual(InstanceChoices)) return;
        InstanceChoices.Clear();
        foreach (var choice in now) InstanceChoices.Add(choice);
    }

    /// <summary>[Import into an instance] on a received world: unpacked into that instance's saves under a new name.</summary>
    internal async Task ImportReceivedWorldAsync(TransferItem item, InstanceChoice instance)
    {
        if (item.SavedPath is not { } zip) return;
        try
        {
            var saves = _service.GetInstance(instance.Id).SavesDir;
            var folder = await Task.Run(() => WorldManager.Import(zip, saves));
            SetStatus(StatusMessage.Success("files.imported", folder, instance.Name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            SetStatus(StatusMessage.Warning("files.import_failed", e.Message));
        }
    }

    /// <summary>[Compare mods] on a received mod list: what that instance is missing, has in another version, or has extra.</summary>
    internal async Task CompareReceivedModsAsync(TransferItem item, InstanceChoice instance)
    {
        if (item.SavedPath is not { } path) return;
        try
        {
            var result = await Task.Run(() =>
            {
                var theirs = FilePacker.ReadModList(path);
                if (theirs is null) return null;
                return ModSync.Compare(ModManifest.Read(_service.GetInstance(instance.Id).ModsDir), theirs);
            });
            item.CompareText = result is null ? L.T("files.compare.bad")
                : result.Missing.Count + result.Different.Count + result.Extra.Count == 0 ? L.F("files.compare.same", instance.Name)
                : L.F("files.compare.diff", instance.Name, result.Missing.Count, result.Different.Count, result.Extra.Count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            item.CompareText = e.Message;
        }
        item.Changed();
    }
}
