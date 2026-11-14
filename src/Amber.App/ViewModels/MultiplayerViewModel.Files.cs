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
        AddCard(item);
        if (to.Friend is { } friend) AddFriendLine(friend.Contact, new FriendLine(FriendLineKind.File, () => item.Name, DateTimeOffset.Now) { File = item });
        item.Task = _service.Tasks.Track("file", L.F("files.task.send", name, to.DisplayName), cancel: () => _ = CancelCardAsync(item));

        var needsPacking = source.Kind is FileKind.World or FileKind.Folder or FileKind.ModList or FileKind.Log;
        if (needsPacking)
        {
            Set(item, FileCardState.Packing);
            item.Packing = new CancellationTokenSource();
            var token = item.Packing.Token;
            string[] secrets = _room is { } room ? [.. room.Secrets] : [];
            try
            {
                item.SourcePath = await Task.Run(() => source.Kind switch
                {
                    FileKind.World or FileKind.Folder => FilePacker.PackFolder(source.Path, OutgoingDir, token),
                    FileKind.ModList => FilePacker.WriteModList(source.Manifest ?? ModManifest.Read(source.Path), source.Title ?? "mods", OutgoingDir),
                    _ => FilePacker.RedactLog(source.Path, OutgoingDir, secrets: secrets),
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

    /// <summary>
    /// How many files go out at once. The others wait their turn on their cards: the other side holds only a few offers
    /// at a time (<see cref="PrivateMessenger.MaxIncoming"/>), and a batch should not crowd out what others send.
    /// </summary>
    internal const int MaxSendsAtOnce = 3;

    private readonly HashSet<TransferItem> _sending = [];
    private readonly List<TransferItem> _sendLine = [];

    /// <summary>Files going out now and files waiting their turn (tests).</summary>
    internal (int Sending, int Waiting) SendCounts => (_sending.Count, _sendLine.Count);

    /// <summary>Tests: stands in for handing a file to the channel.</summary>
    internal Func<string, string, string, CancellationToken, Task<bool>>? SendForTest { get; set; }

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
        if (_sending.Count >= MaxSendsAtOnce)
        {
            if (!_sendLine.Contains(item)) _sendLine.Add(item);
            return;
        }
        _sendLine.Remove(item);
        _sending.Add(item);
        item.Sending?.Dispose();
        var cts = item.Sending = new CancellationTokenSource();
        var send = SendForTest ?? ((to, file, id, ct) => messenger.SendFileAsync(to, file, id, ct));
        var to = peer.Entry.Fingerprint;
        _ = Task.Run(async () =>
        {
            string? error = null;
            var cancelled = false;
            try { await send(to, path, item.Id, cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { cancelled = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or ObjectDisposedException) { error = e.Message; }
            Dispatcher.UIThread.Post(() =>
            {
                // Cancelled before the channel took it (while hashing): nothing went out, the card ends here.
                if (cancelled && item.IsActive) Finish(item, FileCardState.Cancelled);
                else if (error is not null)
                {
                    item.Error = error;
                    Finish(item, FileCardState.Failed);
                }
                if (ReferenceEquals(item.Sending, cts)) item.Sending = null;
                cts.Dispose();
                _sending.Remove(item);
                StartNextInLine();
            });
        });
    }

    /// <summary>A send ended: the next file waiting its turn goes, if its person is still here.</summary>
    private void StartNextInLine()
    {
        while (_sending.Count < MaxSendsAtOnce && _sendLine.Count > 0)
        {
            var next = _sendLine[0];
            _sendLine.RemoveAt(0);
            if (next.State != FileCardState.Waiting) continue;
            var peer = OnlinePeers.FirstOrDefault(p => p.Entry.Fingerprint == next.Fingerprint && KeyMatches(p.Entry));
            if (peer is not null) Start(next, peer);
            else if (FriendForCard(next) is { } friend) StartOrQueue(next, new FileRecipient(next.PeerName, friend, null));
            else Finish(next, FileCardState.Unreachable);
        }
    }

    /// <summary>
    /// A send to a friend broke because they are gone: with queueing on it waits for them and carries on by itself when
    /// they are back (the card says so). True when it was queued.
    /// </summary>
    private bool QueueInterrupted(TransferItem item)
    {
        if (!item.Outgoing || item.State != FileCardState.Interrupted || FriendForCard(item) is not { } friend || !_service.Settings.FilesQueueOffline
            || item.Person is not { } person || item.SourcePath is not { } path || PeerOf(friend) is not null) return false;
        return _fileQueue.Add(new QueuedFile(item.Id, person, path, item.Name, item.Kind, DateTimeOffset.Now));
    }

    /// <summary>Someone may have come online: their queued files start, oldest first.</summary>
    private void FlushFileQueue()
    {
        // Someone may have left too: a send to them that broke waits for them now.
        foreach (var item in _cards.Values.ToList())
            if (item.Outgoing && item.State == FileCardState.Interrupted && !_fileQueue.All.Any(q => q.Id == item.Id)) QueueInterrupted(item);
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
            AddCard(item);
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
            // By name now; whether a zip holds a world is read off the UI thread (it can be a big or odd zip).
            item.Kind = FileKinds.Of(path);
            if (FileKinds.MayBeWorld(path)) _ = CheckReceivedWorldAsync(item, path);
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
        // Only they left (the room is still open): carry on when they are back, as after a lost room.
        QueueInterrupted(item);
    }

    /// <summary>Stops world checks on received zips when the room's channel closes.</summary>
    private CancellationTokenSource _receivedChecks = new();

    /// <summary>Received a zip: looks for a world in it in the background; the card becomes a world card when there is one.</summary>
    private async Task CheckReceivedWorldAsync(TransferItem item, string path)
    {
        var token = _receivedChecks.Token;
        bool world;
        try { world = await Task.Run(() => FileKinds.LooksLikeWorld(path, token), token); }
        catch (OperationCanceledException) { return; }
        if (!world || token.IsCancellationRequested || item.SavedPath != path) return;
        item.Kind = FileKind.World;
        item.Changed();
        WorldChecked?.Invoke(item);
    }

    /// <summary>Self-checks and tests: a received zip was found to hold a world.</summary>
    internal event Action<TransferItem>? WorldChecked;

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
        TrimCards();
    }

    /// <summary>The most cards the room's list keeps; finished ones beyond it are forgotten, oldest first.</summary>
    internal const int MaxCards = 50;

    /// <summary>Cards in the room's list and the ones still known by id (tests).</summary>
    internal int CardCount => _cards.Count;

    /// <summary>Every new card goes in here, so every path shares the same limit.</summary>
    private void AddCard(TransferItem item)
    {
        _cards[item.Id] = item;
        Transfers.Insert(0, item);
        TrimCards();
    }

    /// <summary>
    /// Forgets finished cards past <see cref="MaxCards"/>, oldest first, and any finished card no longer in the list.
    /// A card still moving, packing, waiting in the queue or able to carry on stays.
    /// </summary>
    private void TrimCards()
    {
        for (var i = Transfers.Count - 1; i >= 0 && Transfers.Count > MaxCards; i--)
        {
            var old = Transfers[i];
            if (!CanForget(old)) continue;
            Transfers.RemoveAt(i);
            _cards.Remove(old.Id);
        }
        if (_cards.Count <= Transfers.Count) return;
        var shown = Transfers.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, card) in _cards.ToList())
            if (!shown.Contains(id) && CanForget(card)) _cards.Remove(id);
    }

    private bool CanForget(TransferItem item) =>
        item.Finished && item.State is not (FileCardState.Queued or FileCardState.Packing)
        && !(item.Outgoing && item.State == FileCardState.Interrupted) && !_fileQueue.All.Any(q => q.Id == item.Id);

    /// <summary>Leaving the room: the list empties and only cards that will carry on later (queued for a friend) are kept.</summary>
    private void ForgetRoomCards()
    {
        Transfers.Clear();
        foreach (var (id, card) in _cards.ToList())
            if (!_fileQueue.All.Any(q => q.Id == id) && card.State is not (FileCardState.Queued or FileCardState.Packing)) _cards.Remove(id);
        _autoAccepted.Clear();
    }

    /// <summary>What each sender may still hand over without being asked.</summary>
    private readonly AutoAcceptBudget _autoAccepted = new();

    /// <summary>Tests: an offer from the device with <paramref name="key"/> on the automatic path (a card when it was taken).</summary>
    internal bool TakeOfferForTest(FileOffer offer, byte[]? key)
    {
        if (!TakeOfferAutomatically(offer, key)) return false;
        CardForAccepted(offer);
        return true;
    }

    /// <summary>Tests: a progress report as the room's channel hands it over.</summary>
    internal void ApplyTransferForTest(FileProgress p) => ApplyTransfer(p);

    /// <summary>Tests: leaving the room, as far as the file cards go.</summary>
    internal void ForgetRoomCardsForTest() => ForgetRoomCards();

    /// <summary>[Cancel] on a card: a queued or packing file simply stops; one on the wire tells the other side.</summary>
    internal async Task CancelCardAsync(TransferItem item)
    {
        if (item.State == FileCardState.Packing)
        {
            item.Packing?.Cancel();
            return;
        }
        if (_fileQueue.Remove(item.Id) | _sendLine.Remove(item) || item.State == FileCardState.Queued || item.State == FileCardState.Interrupted)
        {
            Finish(item, FileCardState.Cancelled);
            return;
        }
        // Still being hashed (the channel does not know it yet) or already going out: both stop.
        item.Sending?.Cancel();
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
        _receivedChecks.Cancel();
        _receivedChecks.Dispose();
        _receivedChecks = new CancellationTokenSource();
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
            if (item.Outgoing && item.State == FileCardState.Interrupted && FriendForCard(item) is not null && _service.Settings.FilesQueueOffline
                && item.Person is { } person && item.SourcePath is { } path)
                _fileQueue.Add(new QueuedFile(item.Id, person, path, item.Name, item.Kind, DateTimeOffset.Now));
        }
        _sendLine.Clear();
        _sending.Clear();
        TrimCards();
    }

    /// <summary>Someone offered a file: decline it for "for a child" rules, take it from a checked friend when asked to, or ask.</summary>
    private bool TakeOfferAutomatically(FileOffer offer, byte[]? key)
    {
        if (!_service.Settings.FilesAutoAcceptFriends || ReceivedFiles.IsProgram(offer.Name) || Child.FriendsFilesOnly) return false;
        var checkedFriend = key is not null && FriendOf(key, null) is { } friend && StateOf(friend) != FriendState.NewDevice
            && friend.Devices.Any(d => d.PublicKey.AsSpan().SequenceEqual(key) && d.Verified && !d.NewDevice);
        // Past the sender's allowance the offer is asked about like anyone else's.
        return checkedFriend && _autoAccepted.TryTake(offer.FromFingerprint, offer.Size);
    }

    /// <summary>An offer came in: the friend's conversation keeps one line pointing at the request bar.</summary>
    private void NoteFileOffer(FileOffer offer, byte[]? key)
    {
        if (key is null || FriendByKey(key) is not { } friend) return;
        AddFriendLine(friend, new FriendLine(FriendLineKind.Note, () => L.F("files.note.offer", offer.Name, FileOfferItem.Size(offer.Size)), DateTimeOffset.Now));
    }

    /// <summary>I accepted an offer: its card goes into the conversation with the friend who sent it.</summary>
    private TransferItem CardForAccepted(FileOffer offer)
    {
        // The sender lost the connection and offers the same transfer again: its card carries on.
        if (_cards.TryGetValue(offer.Id, out var earlier) && !earlier.Outgoing && earlier.Fingerprint == offer.FromFingerprint)
        {
            earlier.Error = null;
            earlier.Finished = false;
            earlier.ResetSpeed();
            if (earlier.Task is not { IsRunning: true })
                earlier.Task = _service.Tasks.Track("file", L.F("files.task.receive", offer.Name, earlier.PeerName), cancel: () => _ = CancelCardAsync(earlier));
            Set(earlier, FileCardState.Waiting);
            return earlier;
        }
        var key = OnlinePeers.FirstOrDefault(p => p.Entry.Fingerprint == offer.FromFingerprint)?.Entry.PublicKey;
        var friend = key is null ? null : FriendOf(key, null);
        var item = new TransferItem(offer.Id, false, offer.Name, offer.Size, this, peerName: offer.FromName.Length > 0 ? offer.FromName : L.T("multiplayer.peers.player"))
        {
            Person = friend is not null ? PersonOf(friend) : "fp:" + offer.FromFingerprint,
            Fingerprint = offer.FromFingerprint,
        };
        item.Route = RouteTo(offer.FromFingerprint);
        AddCard(item);
        if (friend is not null) AddFriendLine(friend.Contact, new FriendLine(FriendLineKind.File, () => item.Name, DateTimeOffset.Now) { File = item });
        item.Task = _service.Tasks.Track("file", L.F("files.task.receive", offer.Name, item.PeerName), cancel: () => _ = CancelCardAsync(item));
        Set(item, FileCardState.Waiting);
        return item;
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
