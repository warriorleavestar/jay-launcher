using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Files;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>
/// Files taken without asking from a checked friend: each sender has an allowance, and the room's cards stay bounded
/// however many small files arrive, without losing a file that is queued or can carry on.
/// </summary>
public sealed class AutoAcceptCardsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-auto-accept-" + Guid.NewGuid().ToString("N"));

    public AutoAcceptCardsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void TheBudgetCountsFilesAndBytesPerSenderOverAWindow()
    {
        var now = DateTimeOffset.UnixEpoch;
        var budget = new AutoAcceptBudget(maxFiles: 3, maxBytes: 1000, window: TimeSpan.FromMinutes(10), clock: () => now);
        Assert.True(budget.TryTake("a", 100));
        Assert.True(budget.TryTake("a", 100));
        Assert.True(budget.TryTake("a", 100));
        Assert.False(budget.TryTake("a", 1));          // fourth file
        Assert.True(budget.TryTake("b", 900));          // another sender has its own
        Assert.False(budget.TryTake("b", 101));         // bytes
        Assert.False(budget.TryTake("c", -1));
        Assert.False(budget.TryTake("c", 1001));
        Assert.False(budget.TryTake("", 1));
        now += TimeSpan.FromMinutes(10);
        Assert.True(budget.TryTake("a", 100));
        Assert.Equal(1, budget.Senders);                // b's window passed and was let go
        budget.Clear();
        Assert.Equal(0, budget.Senders);
    }

    /// <summary>Me and a checked friend, going through the real add-and-compare path.</summary>
    private static (MultiplayerViewModel Vm, X25519KeyPair Friend, ContactItem Item) WithCheckedFriend(LauncherService service, string root)
    {
        var vm = new MultiplayerViewModel(service) { ContactsDir = root };
        var me = vm.Identity.PublicKey;
        var peer = X25519KeyPair.Generate();
        var peerBook = new ContactBook(Path.Combine(root, "peer.json")) { Me = peer.PublicKey };
        var hello = vm.Contacts.StartAdd(me, peer.PublicKey, "Me")!;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(peerBook.Receive(me, hello, "Robin"));
        var reply = peerBook.Accept(me, "Robin")!;
        var nonce = vm.Contacts.Receive(peer.PublicKey, reply, "Me")!;
        Assert.Null(peerBook.Receive(me, nonce, "Robin"));
        Dispatcher.UIThread.RunJobs();
        vm.ConfirmContactCommand.Execute(Assert.Single(vm.ContactRequests));
        Dispatcher.UIThread.RunJobs();
        return (vm, peer, Assert.Single(vm.ContactList));
    }

    private static FileProgress Received(string id, string savedPath) =>
        new(id, "fp-robin", Path.GetFileName(savedPath), 10, 10, false) { Finished = true, SavedPath = savedPath };

    [Theory]
    [InlineData(60)]
    [InlineData(300)]
    public void ManySmallFilesFromAFriendKeepTheCardsBounded(int files) => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings
        {
            GameDirectory = TestDataDir.Path,
            PlayerName = "",
            FilesAutoAcceptFriends = true,
            FilesQueueOffline = true,
            SavePathForTest = Path.Combine(_root, "settings.json"),
        });
        var (vm, friend, item) = WithCheckedFriend(service, _root);
        using var _ = friend;

        // A file of mine waiting for the friend, and one of mine that broke part way: neither may be forgotten.
        var source = Path.Combine(_root, "mine.txt");
        File.WriteAllText(source, "hello");
        var queued = vm.SendFileThingAsync(new FileRecipient("Robin", item, null), new FileSource(FileKind.File, source)).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FileCardState.Queued, queued.State);
        Assert.Equal(1, vm.QueuedFiles);

        var saved = Path.Combine(_root, "got.txt");
        File.WriteAllText(saved, "x");
        var taken = 0;
        for (var i = 0; i < files; i++)
        {
            var offer = new FileOffer($"in-{i}", "fp-robin", "Robin", $"note-{i}.txt", 10);
            if (!vm.TakeOfferForTest(offer, friend.PublicKey)) continue;
            taken++;
            vm.ApplyTransferForTest(Received(offer.Id, saved));
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(vm.Transfers.Count, 1, MultiplayerViewModel.MaxCards);
            Assert.InRange(vm.CardCount, 1, MultiplayerViewModel.MaxCards);
        }
        // The friend's allowance stopped taking files without asking.
        Assert.Equal(AutoAcceptBudget.DefaultFiles, taken);

        // Progress reports for transfers the page has no card for (a mod sync, say) share the same limit.
        for (var i = 0; i < files; i++)
        {
            vm.ApplyTransferForTest(Received($"sync-{i}", saved));
            Dispatcher.UIThread.RunJobs();
        }
        Assert.InRange(vm.Transfers.Count, 1, MultiplayerViewModel.MaxCards);
        Assert.InRange(vm.CardCount, 1, MultiplayerViewModel.MaxCards);
        Assert.InRange(service.Tasks.All.Count, 0, MultiplayerViewModel.MaxCards + 1);

        // The queued file is still there and still known by id.
        Assert.Contains(queued, vm.Transfers);
        Assert.Same(queued, vm.CardOf(queued.Id));
        Assert.Equal(1, vm.QueuedFiles);

        // Leaving the room lets every finished card go and keeps the queued one.
        vm.ForgetRoomCardsForTest();
        Assert.Empty(vm.Transfers);
        Assert.Equal(1, vm.CardCount);
        Assert.Same(queued, vm.CardOf(queued.Id));
    });

    [Fact]
    public void AProgramIsNeverTakenWithoutAsking() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings
        {
            GameDirectory = TestDataDir.Path,
            PlayerName = "",
            FilesAutoAcceptFriends = true,
            SavePathForTest = Path.Combine(_root, "settings.json"),
        });
        var (vm, friend, _) = WithCheckedFriend(service, _root);
        using var __ = friend;
        Assert.False(vm.TakeOfferForTest(new FileOffer("p", "fp-robin", "Robin", "setup.exe", 10), friend.PublicKey));
        Assert.False(vm.TakeOfferForTest(new FileOffer("s", "fp-robin", "Robin", "a.txt", 10), X25519KeyPair.Generate().PublicKey));
        Assert.True(vm.TakeOfferForTest(new FileOffer("t", "fp-robin", "Robin", "a.txt", 10), friend.PublicKey));
    });
    // ---- sending and receiving: cancel, turns, a friend who left, a receive that cannot start ----

    private sealed class NoLink : IPrivateLink
    {
        public event Action<PrivateFrame>? PrivateReceived { add { } remove { } }
        public Task<bool> SendPrivateAsync(string to, byte[] sealedData, CancellationToken ct) => Task.FromResult(false);
    }

    private LauncherService NewService(bool queue = true) => new(new AppSettings
    {
        GameDirectory = TestDataDir.Path,
        PlayerName = "",
        FilesQueueOffline = queue,
        SavePathForTest = Path.Combine(_root, "settings.json"),
    });

    private static OnlinePeerItem Online(MultiplayerViewModel vm, byte[] key, string name)
    {
        var peer = new OnlinePeerItem(new RosterEntry(MemberIdentity.FingerprintOf(key), key, name));
        vm.OnlinePeers.Add(peer);
        return peer;
    }

    private string Source(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, name);
        return path;
    }

    private static void Pump(Func<bool> until)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!until())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void CancellingWhileTheFileIsStillBeingHashedStopsIt() => UiTestThread.Run(() =>
    {
        using var service = NewService();
        var vm = new MultiplayerViewModel(service) { ContactsDir = _root };
        vm.UsePrivateLinkForTest(new NoLink());
        using var robin = X25519KeyPair.Generate();
        var peer = Online(vm, robin.PublicKey, "Robin");
        var sends = 0;
        // Stands in for the channel while it hashes a big file: nothing is registered yet, only the token can stop it.
        vm.SendForTest = async (_, _, _, ct) =>
        {
            Interlocked.Increment(ref sends);
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        };
        var item = vm.SendFileThingAsync(new FileRecipient("Robin", null, peer), new FileSource(FileKind.File, Source("big.bin"))).GetAwaiter().GetResult();
        Pump(() => Volatile.Read(ref sends) == 1);
        Assert.Equal(FileCardState.Waiting, item.State);

        vm.CancelCardAsync(item).GetAwaiter().GetResult();
        Pump(() => item.State == FileCardState.Cancelled);
        Assert.False(item.Task?.IsRunning ?? false);
        Assert.Equal((0, 0), vm.SendCounts);
    });

    [Fact]
    public void ABatchGoesOutAFewAtATime() => UiTestThread.Run(() =>
    {
        using var service = NewService();
        var vm = new MultiplayerViewModel(service) { ContactsDir = _root };
        vm.UsePrivateLinkForTest(new NoLink());
        using var robin = X25519KeyPair.Generate();
        var peer = Online(vm, robin.PublicKey, "Robin");
        var waiting = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        vm.SendForTest = (_, _, id, _) => waiting.GetOrAdd(id, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        var cards = Enumerable.Range(0, 12)
            .Select(i => vm.SendFileThingAsync(new FileRecipient("Robin", null, peer), new FileSource(FileKind.File, Source($"f{i}.txt"))).GetAwaiter().GetResult())
            .ToList();
        Pump(() => waiting.Count == MultiplayerViewModel.MaxSendsAtOnce);
        Assert.Equal((MultiplayerViewModel.MaxSendsAtOnce, 12 - MultiplayerViewModel.MaxSendsAtOnce), vm.SendCounts);
        Assert.All(cards, c => Assert.Equal(FileCardState.Waiting, c.State));
        // Far fewer than the other side holds at once.
        Assert.True(MultiplayerViewModel.MaxSendsAtOnce < PrivateMessenger.MaxIncoming);

        // One waiting its turn is cancelled: it leaves the line without ever going out.
        var last = cards[^1];
        vm.CancelCardAsync(last).GetAwaiter().GetResult();
        Assert.Equal(FileCardState.Cancelled, last.State);
        Assert.False(waiting.ContainsKey(last.Id));

        // Each one that ends lets the next go.
        for (var done = 0; done < 11; done++)
        {
            var id = waiting.Keys.First(k => !waiting[k].Task.IsCompleted);
            vm.ApplyTransferForTest(new FileProgress(id, peer.Entry.Fingerprint, "f", 1, 1, true) { Finished = true, SavedPath = "", Started = true });
            waiting[id].SetResult(true);
            Pump(() => waiting.Values.Count(t => !t.Task.IsCompleted) == Math.Min(MultiplayerViewModel.MaxSendsAtOnce, 10 - done));
        }
        Assert.Equal(11, waiting.Count);
        Pump(() => vm.SendCounts == (0, 0));
        Assert.Equal(11, cards.Count(c => c.State == FileCardState.Delivered));
    });

    [Fact]
    public void ASendToAFriendWhoLeftTheRoomCarriesOnWhenTheyAreBack() => UiTestThread.Run(() =>
    {
        using var service = NewService();
        var (vm, friend, item) = WithCheckedFriend(service, _root);
        using var _ = friend;
        vm.UsePrivateLinkForTest(new NoLink());
        var peer = Online(vm, friend.PublicKey, "Robin");
        var sends = new List<string>();
        vm.SendForTest = (_, _, id, _) =>
        {
            lock (sends) sends.Add(id);
            return Task.FromResult(false);
        };
        var card = vm.SendFileThingAsync(new FileRecipient("Robin", item, null), new FileSource(FileKind.File, Source("world.zip"))).GetAwaiter().GetResult();
        Pump(() => { lock (sends) return sends.Count == 1; });

        // Only Robin left (the room stays): the transfer broke part way.
        vm.OnlinePeers.Remove(peer);
        vm.RosterChangedForTest();
        vm.ApplyTransferForTest(new FileProgress(card.Id, peer.Entry.Fingerprint, card.Name, 10, 4, true)
            { Finished = true, End = FileEnd.Stalled, Error = "stalled", Started = true });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FileCardState.Interrupted, card.State);
        Assert.Equal(1, vm.QueuedFiles);

        // Robin is back: it carries on by itself, under the same card.
        Online(vm, friend.PublicKey, "Robin");
        vm.RosterChangedForTest();
        Pump(() => { lock (sends) return sends.Count == 2; });
        Assert.Equal(card.Id, sends[1]);
        Assert.Equal(0, vm.QueuedFiles);
    });

    [Fact]
    public void AReceiveThatCannotStartEndsItsCardAndTask() => UiTestThread.Run(() =>
    {
        using var service = NewService();
        var vm = new MultiplayerViewModel(service) { ContactsDir = _root };
        vm.UsePrivateLinkForTest(new NoLink());
        vm.AcceptForTest = _ => throw new IOException("no room on the disk");
        var offer = new FileOffer("0123456789abcdef", "fp-robin", "Robin", "big.zip", 10);
        vm.FileOffers.Add(new FileOfferItem(offer));
        vm.AcceptFileCommand.Execute(vm.FileOffers[0]);
        Pump(() => vm.CardOf(offer.Id)?.State == FileCardState.Failed);
        var card = vm.CardOf(offer.Id)!;
        Assert.Equal("no room on the disk", card.Error);
        Assert.False(card.Task?.IsRunning ?? false);
    });
}
