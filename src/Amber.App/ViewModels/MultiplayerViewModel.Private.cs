using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Files;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace Amber.App.ViewModels;

/// <summary>在线名单里的一个人（不含自己）。</summary>
public sealed record OnlinePeerItem(RosterEntry Entry) : LocalizedRecord
{
    public string Label => (Entry.Name.Length > 0 ? Entry.Name : L.T("multiplayer.peers.player")) + " · " + Entry.Fingerprint;
}

/// <summary>私聊记录的一行。</summary>
public sealed record PrivateLine(string Peer, string Who, string Text, DateTimeOffset Time, bool Mine)
{
    public string Display => $"{Time.ToLocalTime():HH:mm} {Who}: {Text}";
}

/// <summary>别人要发来的文件（等自己点接收或拒绝）。</summary>
public sealed record FileOfferItem(FileOffer Offer) : LocalizedRecord
{
    public string Label => L.F("multiplayer.private.offer", Offer.FromName.Length > 0 ? Offer.FromName : Offer.FromFingerprint, Offer.Name, Size(Offer.Size));

    /// <summary>"Alex wants to send you map.zip (38 MB)" on the locked card.</summary>
    public string Headline => Label;

    public string SafetyCode => Offer.FromFingerprint;

    /// <summary>The sender is a friend whose emoji I compared (the safety card says so, or that they are not).</summary>
    public bool FromFriend { get; init; }

    public FileKind Kind => FileKinds.Of(Offer.Name);

    /// <summary>"Alex is a friend you checked · World" on the request.</summary>
    public string Facts => L.T(FromFriend ? "files.offer.friend" : "files.offer.not_friend") + " · " + L.T(FileKinds.LabelKey(Kind));

    /// <summary>Programs and scripts get an extra warning (Jay never opens a received file by itself).</summary>
    public bool IsProgram => ReceivedFiles.IsProgram(Offer.Name);

    public string Warning => L.T(IsProgram ? "multiplayer.file.program_warning" : "multiplayer.file.never_runs");

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB",
    };
}

/// <summary>
/// 房间里成员之间的私聊和传文件（只有邀请房间有）：端到端加密，房主只按指纹转交，看不到内容。
/// 收到的文件放在“下载/Jay”里。
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private PrivateMessenger? _messenger;
    private ChatClient? _messengerChat;

    public ObservableCollection<OnlinePeerItem> OnlinePeers { get; } = [];
    public ObservableCollection<PrivateLine> PrivateLines { get; } = [];
    public ObservableCollection<FileOfferItem> FileOffers { get; } = [];
    public ObservableCollection<TransferItem> Transfers { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPrivatePeer))] public partial OnlinePeerItem? SelectedPeer { get; set; }
    [ObservableProperty] public partial string PrivateInput { get; set; } = "";
    public bool HasPrivatePeer => SelectedPeer is not null;
    public bool HasOnlinePeers => OnlinePeers.Count > 0;
    internal bool IsInviteRoom => _room is { FromInvite: true };
    internal string PrivateDebug => $"messenger={_messenger is not null} roster={_messengerChat?.Roster.Count ?? -1} chatRoster={_chat?.Roster.Count ?? -1}";

    /// <summary>收到的文件放这里（自检里换成测试目录）。</summary>
    internal string DownloadsDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Jay");

    /// <summary>界面给的“选一个文件”（文件选择框）；自检里不用它，直接给路径。</summary>
    internal Func<Task<string?>>? PickFileToSend { get; set; }

    private void StartPrivate(ChatClient chat)
    {
        StopPrivate();
        if (_room is not { FromInvite: true }) return;
        var messenger = new PrivateMessenger(chat, Identity.KeyPair, fp => chat.Roster.FirstOrDefault(r => r.Fingerprint == fp)?.PublicKey, ReceiveFolder);
        _messengerChat = chat;
        chat.RosterChanged += roster => Dispatcher.UIThread.Post(() => OnRoster(chat, roster));
        Wire(messenger, () => chat.Roster);
        if (IsHosting) ServeMods(messenger);
        OnRoster(chat, chat.Roster);
    }

    /// <summary>
    /// Self-check seam: the room's private channel over a stand-in link (no connection), with the roster the test keeps
    /// in <see cref="OnlinePeers"/>. Call <see cref="RosterChangedForTest"/> after changing it.
    /// </summary>
    internal PrivateMessenger UsePrivateLinkForTest(IPrivateLink link)
    {
        StopPrivate();
        var messenger = new PrivateMessenger(link, Identity.KeyPair, fp => OnlinePeers.FirstOrDefault(p => p.Entry.Fingerprint == fp)?.Entry.PublicKey, ReceiveFolder);
        Wire(messenger, () => [.. OnlinePeers.Select(p => p.Entry)]);
        return messenger;
    }

    /// <summary>Self-check seam: the stand-in roster changed (someone came or went).</summary>
    internal void RosterChangedForTest()
    {
        OnPropertyChanged(nameof(HasOnlinePeers));
        FlushFileQueue();
    }

    /// <summary>Self-check seam: the stand-in channel closed, as when leaving the room.</summary>
    internal void ClosePrivateForTest() => StopPrivate();

    private void Wire(PrivateMessenger messenger, Func<IReadOnlyList<RosterEntry>> roster)
    {
        // 公共网络的配置可以关掉传文件或限大小（按现在连着的网络算）。
        messenger.AllowsFile = size => NetworkPolicy.AllowsFile(size);
        messenger.SendLimit = SendLimitNow;
        _messenger = messenger;
        messenger.MessageReceived += m => Dispatcher.UIThread.Post(() =>
        {
            if (_messenger != messenger) return;
            PrivateLines.Add(new PrivateLine(m.FromFingerprint, m.FromName, m.Text, m.Time, false));
            while (PrivateLines.Count > 500) PrivateLines.RemoveAt(0);
            NoteFriendMessage(m.FromPublicKey, m.Text, m.Time, mine: false);
            if (!IsCurrent) Badge++;
        });
        messenger.FileOffered += offer => Dispatcher.UIThread.Post(() =>
        {
            if (_messenger != messenger) return;
            var key = roster().FirstOrDefault(r => r.Fingerprint == offer.FromFingerprint)?.PublicKey;
            // Child mode: files only from friends and people on the trusted list; the rest are declined at once.
            if (!Child.AcceptsFileFrom(Trusted.All.Any(m => m.Fingerprint == offer.FromFingerprint) || (key is not null && FriendByKey(key) is not null)))
            {
                _ = messenger.DeclineAsync(offer.Id);
                SetStatus(StatusMessage.Info("settings.child.file_declined", offer.FromName));
                return;
            }
            // Settings › Multiplayer › Files: a checked friend's file (never a program) may be taken without asking.
            if (TakeOfferAutomatically(offer, key))
            {
                _ = TakeOfferAsync(messenger, offer);
                return;
            }
            NoteFileOffer(offer, key);
            FileOffers.Add(new FileOfferItem(offer) { FromFriend = key is not null && FriendByKey(key) is not null });
            if (!IsCurrent) Badge++;
        });
        messenger.Progress += p => Dispatcher.UIThread.Post(() =>
        {
            if (_messenger == messenger) ApplyTransfer(p);
        });
        messenger.FileOfferExpired += id => Dispatcher.UIThread.Post(() =>
        {
            if (_messenger != messenger) return;
            if (FileOffers.FirstOrDefault(o => o.Offer.Id == id) is { } expired) FileOffers.Remove(expired);
        });
    }

    /// <param name="keepPeers">Chat moved to a new connection in the same room: the people and the one selected stay
    /// (the new connection's roster refreshes them), and so does the mod check.</param>
    private void StopPrivate(bool keepPeers = false)
    {
        var messenger = _messenger;
        _messenger = null;
        _messengerChat = null;
        if (!keepPeers)
        {
            OnlinePeers.Clear();
            ResetModCheck();
            SelectedPeer = null;
        }
        FileOffers.Clear();
        OnFilesChannelClosed();
        OnPropertyChanged(nameof(HasOnlinePeers));
        if (messenger is not null) _ = messenger.DisposeAsync().AsTask();
    }

    private void OnRoster(ChatClient chat, IReadOnlyList<RosterEntry> roster)
    {
        if (_messengerChat != chat) return;
        var me = Identity.Fingerprint;
        var selected = SelectedPeer?.Entry.Fingerprint;
        OnlinePeers.Clear();
        foreach (var entry in roster.Where(r => r.Fingerprint != me)) OnlinePeers.Add(new OnlinePeerItem(entry));
        SelectedPeer = OnlinePeers.FirstOrDefault(p => p.Entry.Fingerprint == selected);
        OnPropertyChanged(nameof(HasOnlinePeers));
        MaybeCheckMods();
        FlushFileQueue();
    }

    [RelayCommand]
    private void ChoosePeer(OnlinePeerItem? peer) => SelectedPeer = peer;

    [RelayCommand]
    private async Task SendPrivateAsync()
    {
        if (_messenger is null || SelectedPeer is not { } peer || string.IsNullOrWhiteSpace(PrivateInput)) return;
        var text = PrivateInput;
        PrivateInput = "";
        if (!await SendPrivateToAsync(peer, text)) PrivateInput = text;
    }

    /// <summary>
    /// Sends one private message to someone in the room. On success it is in the room's private chat and, when they are
    /// a friend, in the conversation with them. False (with a status line) when it could not be sent.
    /// </summary>
    internal async Task<bool> SendPrivateToAsync(OnlinePeerItem peer, string text)
    {
        if (_messenger is not { } messenger || string.IsNullOrWhiteSpace(text)) return false;
        // Checked before anything is sealed: the message goes to the fingerprint, so it must belong to the key shown.
        if (!KeyMatches(peer.Entry))
        {
            SetStatus(StatusMessage.Warning("multiplayer.private.offline"));
            return false;
        }
        bool sent;
        try { sent = await messenger.SendTextAsync(peer.Entry.Fingerprint, text); }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException) { sent = false; }
        if (!sent)
        {
            SetStatus(StatusMessage.Warning("multiplayer.private.offline"));
            return false;
        }
        var clean = RoomChat.Clean(text, PrivateMessenger.MaxTextLength);
        var now = DateTimeOffset.Now;
        PrivateLines.Add(new PrivateLine(peer.Entry.Fingerprint, L.F("multiplayer.private.to", peer.Entry.Name), clean, now, true));
        NoteFriendMessage(peer.Entry.PublicKey, clean, now, mine: true);
        return true;
    }

    /// <summary>选一个文件发给选中的人。</summary>
    [RelayCommand]
    private async Task SendFileAsync()
    {
        if (SelectedPeer is not { } peer || PickFileToSend is not { } pick) return;
        if (await pick() is { } path) await SendFileToAsync(peer, path);
    }

    /// <summary>Sends a file to someone in the room and waits until it is delivered (true) or has stopped (false).</summary>
    internal async Task<bool> SendFileToAsync(OnlinePeerItem peer, string path)
    {
        if (_messenger is null || !KeyMatches(peer.Entry)) return false;
        var friend = ContactList.FirstOrDefault(f => f.Devices.Any(d => d.PublicKey.AsSpan().SequenceEqual(peer.Entry.PublicKey)));
        var item = await SendFileThingAsync(new FileRecipient(peer.Entry.Name, friend, peer), new FileSource(FileKind.File, path));
        var end = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!item.IsActive || item.State == FileCardState.Queued) end.TrySetResult(item.State == FileCardState.Delivered);
        }
        item.PropertyChanged += Watch;
        Watch(null, new(""));
        try { return await end.Task; }
        finally { item.PropertyChanged -= Watch; }
    }

    [RelayCommand]
    private async Task AcceptFileAsync(FileOfferItem? offer)
    {
        if (offer is null) return;
        FileOffers.Remove(offer);
        if (_messenger is not { } messenger) return;
        await TakeOfferAsync(messenger, offer.Offer);
    }

    /// <summary>
    /// Accepts an offer (by hand or automatically) behind its card. When receiving cannot start (no room on the disk,
    /// the downloads folder cannot be written), the card and its task in Activity end as failed with the reason.
    /// </summary>
    private async Task TakeOfferAsync(PrivateMessenger messenger, FileOffer offer)
    {
        var item = CardForAccepted(offer);
        try { await (AcceptForTest?.Invoke(offer.Id) ?? messenger.AcceptAsync(offer.Id)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Warning));
            if (item.State is FileCardState.Waiting or FileCardState.Sending)
            {
                item.Error = e.Message;
                Finish(item, FileCardState.Failed);
            }
        }
    }

    /// <summary>Tests: stands in for accepting an offer in the channel (to make it fail).</summary>
    internal Func<string, Task>? AcceptForTest { get; set; }

    [RelayCommand]
    private async Task DeclineFileAsync(FileOfferItem? offer)
    {
        if (offer is null) return;
        FileOffers.Remove(offer);
        if (_messenger is not { } messenger) return;
        await messenger.DeclineAsync(offer.Offer.Id);
    }

    [RelayCommand]
    private async Task CancelTransferAsync(TransferItem? item)
    {
        if (item is not null) await CancelCardAsync(item);
    }

    [RelayCommand]
    private void OpenTransferFolder(TransferItem? item)
    {
        if (item?.SavedPath is not { } path || Path.GetDirectoryName(path) is not { } folder) return;
        // 只打开文件夹，不直接打开收到的文件（别人发来的可能是可执行文件）。
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
