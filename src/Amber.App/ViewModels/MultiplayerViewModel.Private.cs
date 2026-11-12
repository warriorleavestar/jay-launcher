using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
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

/// <summary>一次传文件（发或收）的进度。</summary>
public sealed partial class TransferItem(string id, bool outgoing, string name, long size) : ObservableObject
{
    public string Id { get; } = id;
    public bool Outgoing { get; } = outgoing;
    public string Name { get; } = name;
    public long Size { get; } = size;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Text))] public partial long Done { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Text), nameof(IsActive), nameof(CanOpen))] public partial string? SavedPath { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Text), nameof(IsActive))] public partial string? Error { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Text), nameof(IsActive))] public partial bool Finished { get; set; }
    public bool IsActive => !Finished;
    public bool CanOpen => !string.IsNullOrEmpty(SavedPath);
    public string Text => L.F(Outgoing ? "multiplayer.private.sending" : "multiplayer.private.receiving", Name)
        + " · " + (Error is { } error ? error
            : Finished ? L.T("multiplayer.private.done")
            : $"{FileOfferItem.Size(Done)} / {FileOfferItem.Size(Size)}");
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
        var messenger = new PrivateMessenger(chat, Identity.KeyPair, fp => chat.Roster.FirstOrDefault(r => r.Fingerprint == fp)?.PublicKey, DownloadsDir);
        // 公共网络的配置可以关掉传文件或限大小（按现在连着的网络算）。
        messenger.AllowsFile = size => NetworkPolicy.AllowsFile(size);
        _messenger = messenger;
        _messengerChat = chat;
        chat.RosterChanged += roster => Dispatcher.UIThread.Post(() => OnRoster(chat, roster));
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
            // Child mode: files only from people on the trusted list; the rest are declined at once.
            if (!Child.AcceptsFileFrom(Trusted.All.Any(m => m.Fingerprint == offer.FromFingerprint)))
            {
                _ = messenger.DeclineAsync(offer.Id);
                SetStatus(StatusMessage.Info("settings.child.file_declined", offer.FromName));
                return;
            }
            FileOffers.Add(new FileOfferItem(offer));
            if (!IsCurrent) Badge++;
        });
        messenger.Progress += p => Dispatcher.UIThread.Post(() => OnTransfer(messenger, p));
        messenger.FileOfferExpired += id => Dispatcher.UIThread.Post(() =>
        {
            if (_messenger != messenger) return;
            if (FileOffers.FirstOrDefault(o => o.Offer.Id == id) is { } expired) FileOffers.Remove(expired);
        });
        if (IsHosting) ServeMods(messenger);
        OnRoster(chat, chat.Roster);
    }

    private void StopPrivate()
    {
        var messenger = _messenger;
        _messenger = null;
        _messengerChat = null;
        OnlinePeers.Clear();
        FileOffers.Clear();
        ResetModCheck();
        SelectedPeer = null;
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
    }

    private void OnTransfer(PrivateMessenger messenger, FileProgress p)
    {
        if (_messenger != messenger) return;
        var item = Transfers.FirstOrDefault(t => t.Id == p.Id);
        if (item is null)
        {
            item = new TransferItem(p.Id, p.Outgoing, p.Name, p.Size);
            Transfers.Insert(0, item);
            while (Transfers.Count > 50) Transfers.RemoveAt(Transfers.Count - 1);
        }
        item.Done = p.Done;
        if (!p.Finished) return;
        item.Error = p.Error;
        item.SavedPath = p.SavedPath is { Length: > 0 } saved ? saved : null;
        item.Finished = true;
        if (!p.Outgoing && p.SavedPath is { Length: > 0 } path) SetStatus(StatusMessage.Success("multiplayer.private.saved", path));
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

    internal async Task<bool> SendFileToAsync(OnlinePeerItem peer, string path)
    {
        if (_messenger is not { } messenger || !KeyMatches(peer.Entry)) return false;
        try
        {
            return await Task.Run(() => messenger.SendFileAsync(peer.Entry.Fingerprint, path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Warning));
            return false;
        }
    }

    [RelayCommand]
    private async Task AcceptFileAsync(FileOfferItem? offer)
    {
        if (offer is null) return;
        FileOffers.Remove(offer);
        if (_messenger is not { } messenger) return;
        try { await messenger.AcceptAsync(offer.Offer.Id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Warning));
        }
    }

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
        if (item is null || _messenger is not { } messenger) return;
        await messenger.CancelAsync(item.Id);
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
