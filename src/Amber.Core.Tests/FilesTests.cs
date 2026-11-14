using Amber.Core.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Files;
using Amber.Core.Multiplayer.Transport;
using Amber.Core.Navigation;

namespace Amber.Core.Tests;

/// <summary>
/// Files in conversations (redesign batch 12): the card rules, telling kinds apart, packing, the offline queue, the
/// speed setting, the files group in the advanced settings, and what the private channel now reports (ids, why it ended).
/// </summary>
public sealed class FilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-files-" + Guid.NewGuid().ToString("N"));

    public FilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static FileProgress P(bool outgoing, bool finished = false, bool started = false, FileEnd end = FileEnd.None, string? error = null) =>
        new("0123456789abcdef", "fp", "a.zip", 100, 40, outgoing) { Finished = finished, Started = started, End = end, Error = error };

    // ---- card rules ----

    [Fact]
    public void ProgressReportsMapToOneCardState()
    {
        Assert.Equal(FileCardState.Waiting, FileCards.StateOf(P(outgoing: true)));
        Assert.Equal(FileCardState.Sending, FileCards.StateOf(P(outgoing: true, started: true)));
        Assert.Equal(FileCardState.Sending, FileCards.StateOf(P(outgoing: false)));
        Assert.Equal(FileCardState.Delivered, FileCards.StateOf(P(true, finished: true, started: true)));
        Assert.Equal(FileCardState.Received, FileCards.StateOf(P(false, finished: true, started: true)));
        // Declined and "no answer in five minutes" read the same.
        Assert.Equal(FileCardState.NotAccepted, FileCards.StateOf(P(true, true, end: FileEnd.NotAccepted, error: "declined")));
        Assert.Equal(FileCardState.Cancelled, FileCards.StateOf(P(true, true, end: FileEnd.Cancelled, error: "x")));
        Assert.Equal(FileCardState.Interrupted, FileCards.StateOf(P(true, true, started: true, end: FileEnd.Stalled, error: "x")));
        // Offline before it started: nobody to take it (the app queues it for a friend); after it started: the link broke.
        Assert.Equal(FileCardState.Unreachable, FileCards.StateOf(P(true, true, end: FileEnd.Offline, error: "x")));
        Assert.Equal(FileCardState.Interrupted, FileCards.StateOf(P(true, true, started: true, end: FileEnd.Offline, error: "x")));
        Assert.Equal(FileCardState.Failed, FileCards.StateOf(P(false, true, end: FileEnd.Failed, error: "corrupt")));
        // An error without a reason (older paths) is a failure, never a delivery.
        Assert.Equal(FileCardState.Failed, FileCards.StateOf(P(true, true, error: "x")));
    }

    [Fact]
    public void EachStateOffersOnlyWhatItAllows()
    {
        Assert.Equal(FileCardActions.Cancel, FileCards.ActionsOf(FileCardState.Waiting, true, FileKind.File));
        Assert.Equal(FileCardActions.Cancel, FileCards.ActionsOf(FileCardState.Queued, true, FileKind.File));
        Assert.Equal(FileCardActions.Resume | FileCardActions.Cancel, FileCards.ActionsOf(FileCardState.Interrupted, true, FileKind.File));
        Assert.Equal(FileCardActions.None, FileCards.ActionsOf(FileCardState.Interrupted, false, FileKind.File));
        Assert.Equal(FileCardActions.OpenFolder, FileCards.ActionsOf(FileCardState.Received, false, FileKind.File));
        Assert.Equal(FileCardActions.OpenFolder | FileCardActions.ImportWorld, FileCards.ActionsOf(FileCardState.Received, false, FileKind.World));
        Assert.Equal(FileCardActions.OpenFolder | FileCardActions.CompareMods, FileCards.ActionsOf(FileCardState.Received, false, FileKind.ModList));
        Assert.Equal(FileCardActions.None, FileCards.ActionsOf(FileCardState.Delivered, true, FileKind.World));
        foreach (var ended in new[] { FileCardState.NotAccepted, FileCardState.Cancelled, FileCardState.Unreachable, FileCardState.Failed })
        {
            Assert.Equal(FileCardActions.SendAgain, FileCards.ActionsOf(ended, true, FileKind.File));
            Assert.Equal(FileCardActions.None, FileCards.ActionsOf(ended, false, FileKind.File));
        }
        Assert.True(FileCards.IsActive(FileCardState.Queued));
        Assert.False(FileCards.IsActive(FileCardState.Interrupted));
    }

    [Fact]
    public void EveryCardLineIsLocalized()
    {
        var en = Amber.Core.Localization.L.Load(Amber.Core.Localization.L.Source);
        foreach (var state in Enum.GetValues<FileCardState>())
        foreach (var outgoing in new[] { true, false })
            Assert.True(en.ContainsKey(FileCards.LineKey(state, outgoing)), FileCards.LineKey(state, outgoing));
        foreach (var kind in Enum.GetValues<FileKind>())
            Assert.True(en.ContainsKey(FileKinds.LabelKey(kind)), FileKinds.LabelKey(kind));
    }

    // ---- kinds ----

    [Theory]
    [InlineData("map.zip", FileKind.File)]
    [InlineData("shot.PNG", FileKind.Screenshot)]
    [InlineData("latest.log", FileKind.Log)]
    [InlineData("old.log.gz", FileKind.Log)]
    [InlineData("pack mods.jaymods", FileKind.ModList)]
    [InlineData("setup.exe", FileKind.Program)]
    [InlineData("photo.png.bat", FileKind.Program)]
    public void KindsByName(string name, FileKind kind) => Assert.Equal(kind, FileKinds.Of(name));

    [Fact]
    public void AReceivedZipWithALevelDatIsAWorld()
    {
        var world = Path.Combine(_dir, "world.zip");
        using (var zip = ZipFile.Open(world, ZipArchiveMode.Create))
        {
            zip.CreateEntry("My World/level.dat");
            zip.CreateEntry("My World/region/r.0.0.mca");
        }
        var plain = Path.Combine(_dir, "plain.zip");
        using (var zip = ZipFile.Open(plain, ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
        Assert.Equal(FileKind.World, FileKinds.OfReceived(world));
        Assert.Equal(FileKind.File, FileKinds.OfReceived(plain));
        // Not a zip at all: just a file, no crash.
        var fake = Path.Combine(_dir, "fake.zip");
        File.WriteAllText(fake, "not a zip");
        Assert.Equal(FileKind.File, FileKinds.OfReceived(fake));
    }

    // ---- speed ----

    [Fact]
    public void SpeedFollowsTheSettingAndWhetherAGameRuns()
    {
        Assert.Equal(0, TransferPace.LimitFor(TransferPace.Unlimited, playing: true));
        Assert.Equal(0, TransferPace.LimitFor(TransferPace.GiveWay, playing: false));
        Assert.Equal(TransferPace.Slow, TransferPace.LimitFor(TransferPace.GiveWay, playing: true));
        Assert.Equal(TransferPace.Slow, TransferPace.LimitFor(TransferPace.SaveData, playing: false));
        Assert.Equal(0, TransferPace.LimitFor("nonsense", playing: true));
        Assert.Equal(TransferPace.Unlimited, TransferPace.Normalize(null));
    }

    // ---- the queue ----

    [Fact]
    public void QueuedFilesWaitForTheirPersonOldestFirst()
    {
        var queue = new FileSendQueue();
        var t = DateTimeOffset.UtcNow;
        Assert.True(queue.Add(new QueuedFile("b", "alex", "/b", "b", FileKind.File, t.AddMinutes(1))));
        Assert.True(queue.Add(new QueuedFile("a", "alex", "/a", "a", FileKind.File, t)));
        Assert.True(queue.Add(new QueuedFile("c", "mika", "/c", "c", FileKind.File, t)));
        Assert.False(queue.Add(new QueuedFile("a", "alex", "/a", "a", FileKind.File, t)));
        Assert.True(queue.IsWaitingFor("alex"));
        Assert.Equal(["a", "b"], queue.TakeFor("alex").Select(q => q.Id));
        Assert.False(queue.IsWaitingFor("alex"));
        Assert.True(queue.Remove("c"));
        Assert.Equal(0, queue.Count);
        for (var i = 0; i < FileSendQueue.MaxQueued; i++) Assert.True(queue.Add(new QueuedFile("x" + i, "p", "/x", "x", FileKind.File, t)));
        Assert.False(queue.Add(new QueuedFile("over", "p", "/x", "x", FileKind.File, t)));
    }

    // ---- packing ----

    [Fact]
    public void AWorldIsZippedWithItsFolderAndWithoutTheGameLock()
    {
        var world = Path.Combine(_dir, "saves", "Island");
        Directory.CreateDirectory(Path.Combine(world, "region"));
        File.WriteAllText(Path.Combine(world, "level.dat"), "level");
        File.WriteAllText(Path.Combine(world, "region", "r.0.0.mca"), "region");
        File.WriteAllText(Path.Combine(world, "session.lock"), "lock");
        var zip = FilePacker.PackFolder(world, Path.Combine(_dir, "out"));
        Assert.Equal("Island.zip", Path.GetFileName(zip));
        using var read = ZipFile.OpenRead(zip);
        var names = read.Entries.Select(e => e.FullName).Order().ToList();
        Assert.Equal(["Island/level.dat", "Island/region/r.0.0.mca"], names);
        // The world itself is untouched, and a second pack does not overwrite the first.
        Assert.True(File.Exists(Path.Combine(world, "session.lock")));
        Assert.Equal("Island (2).zip", Path.GetFileName(FilePacker.PackFolder(world, Path.Combine(_dir, "out"))));
        // What the receiving side imports is a world again.
        Assert.Equal(FileKind.World, FileKinds.OfReceived(zip));
    }

    [Fact]
    public void AModListHasNamesAndVersionsOnlyAndReadsBack()
    {
        var manifest = new ModManifest([new ModManifestEntry("sodium-0.5.jar", new string('a', 40), 1234, "sodium", "Sodium", "0.5")]);
        var path = FilePacker.WriteModList(manifest, "My Pack", Path.Combine(_dir, "out"));
        Assert.Equal("My Pack mods.jaymods", Path.GetFileName(path));
        Assert.Equal(FileKind.ModList, FileKinds.Of(path));
        var back = FilePacker.ReadModList(path);
        Assert.NotNull(back);
        Assert.Equal("Sodium", Assert.Single(back!.Mods).Name);
        // Not a mod list, or far too big: not read.
        var bad = Path.Combine(_dir, "bad.jaymods");
        File.WriteAllText(bad, "{ nope");
        Assert.Null(FilePacker.ReadModList(bad));
        var big = Path.Combine(_dir, "big.jaymods");
        File.WriteAllText(big, new string(' ', (int)FilePacker.MaxModListBytes + 1));
        Assert.Null(FilePacker.ReadModList(big));
    }

    [Fact]
    public void ALogGoesOutWithTheUserNameMasked()
    {
        var log = Path.Combine(_dir, "latest.log");
        File.WriteAllText(log, "[main] Loading from /home/someone/games\n[main] Saved to C:\\Users\\someone\\shots --accessToken abc123");
        var copy = FilePacker.RedactLog(log, Path.Combine(_dir, "out"), userName: "someone", homeDir: "/home/someone");
        var text = File.ReadAllText(copy);
        Assert.DoesNotContain("someone", text);
        Assert.Equal("latest.log", Path.GetFileName(copy));
        // The original log is unchanged.
        Assert.Contains("someone", File.ReadAllText(log));
    }

    [Fact]
    public void ALogGoesOutWithoutCodesSecretsTokensOrPublicAddresses()
    {
        // Documentation-only addresses (RFC 5737 / RFC 3849) and made-up codes.
        var a26 = new string('A', 13) + new string('7', 13);
        var invite = $"JAY2-{a26}-{a26}-K";
        var room = $"AMB-ABCD2345-{a26}-11010";
        var friendCode = "JAYC1-" + new string('Q', 40);
        var direct = "JAYDS1-abcDEF_123-xyz";
        var server = "JAYS2-" + new string('m', 30);
        const string roomSecret = "room-secret-value-42";
        var lines = string.Join('\n',
            $"[main] Loading from /home/someone/games",
            $"[mp] pasted {invite} into the box",
            $"[mp] joining {room} via {direct}",
            $"[mp] friend code {friendCode}, server {server}",
            $"[et] easytier-core --network-name jay-0123456789abcdef --network-secret hunter2hunter2",
            $"[et] config network_secret: \"topsecret99\"",
            $"[mp] my secret is {roomSecret}",
            $"[auth] {{\"access_token\":\"abcdefghijklmnop\",\"refreshToken\": \"qrstuvwxyz012345\"}}",
            $"[net] peer 203.0.113.7:11010 and [2001:db8::1]:11010, lan 192.168.1.5 and fe80::1",
            $"[mod] loaded examplemod v1.2.3.4 and forge-47.2.0.1");
        var log = Path.Combine(_dir, "latest.log");
        File.WriteAllText(log, lines);
        var copy = FilePacker.RedactLog(log, Path.Combine(_dir, "out"), userName: "someone", homeDir: "/home/someone", secrets: [roomSecret]);
        var text = File.ReadAllText(copy);
        foreach (var gone in new[] { "someone", invite, a26, room, "ABCD2345", friendCode, direct, server, "0123456789abcdef", "hunter2hunter2",
                     "topsecret99", roomSecret, "abcdefghijklmnop", "qrstuvwxyz012345", "203.0.113", "2001:db8" })
            Assert.DoesNotContain(gone, text);
        // What helps to read the log stays: private addresses and version numbers.
        Assert.Contains("192.168.1.5", text);
        Assert.Contains("fe80::1", text);
        Assert.Contains("v1.2.3.4", text);
        Assert.Contains("forge-47.2.0.1", text);
        Assert.Contains(Redaction.Code, text);
        Assert.Contains(Redaction.Address, text);
        // Running it again changes nothing, and the original log is untouched.
        Assert.Equal(text, Redaction.ForSharing(text, [roomSecret], "someone", "/home/someone"));
        Assert.Equal(lines, File.ReadAllText(log));
    }

    [Fact]
    public void ExportsAndErrorReportsUseTheSameMasking()
    {
        var line = "joined JAY2-" + new string('B', 26) + "-" + new string('C', 26) + " at 198.51.100.20";
        Assert.Equal(Redaction.ForSharing(line), CrashAnalyzer.RedactForExport(line));
        Assert.DoesNotContain("198.51.100.20", CrashAnalyzer.RedactForExport(line));
    }

    [Fact]
    public void LeftoverPacksAreCleanedAfterADay()
    {
        var outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(outDir);
        var old = Path.Combine(outDir, "old.zip");
        var fresh = Path.Combine(outDir, "fresh.zip");
        File.WriteAllText(old, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        FilePacker.CleanOld(outDir, TimeSpan.FromDays(1));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    // ---- settings ----

    [Fact]
    public void TheFilesGroupSitsAfterTheRoomGroupAndMarksChanges()
    {
        var groups = AdvancedSettings.GroupsFor(calls: false);
        Assert.Equal(AdvancedSettings.Files, groups[groups.ToList().IndexOf(AdvancedSettings.Room) + 1]);
        var d = AdvancedSettings.Defaults;
        Assert.False(d.FilesAutoAccept);
        Assert.True(d.FilesQueue);
        Assert.True(d.FilesNotify);
        Assert.Equal(TransferPace.Unlimited, d.FilesSpeed);
        Assert.Empty(AdvancedSettings.Customized(d, calls: false));
        Assert.Equal([AdvancedSettings.Files], AdvancedSettings.Customized(d with { FilesAutoAccept = true }, calls: false));
        Assert.Equal([AdvancedSettings.Files], AdvancedSettings.Customized(d with { FilesSpeed = TransferPace.SaveData }, calls: false));
        Assert.Equal([AdvancedSettings.Files], AdvancedSettings.Customized(d with { FilesCustomFolder = true }, calls: false));
        foreach (var field in new[] { "FilesFolder", "FilesAutoAccept", "FilesQueue", "FilesSpeed", "FilesNotify" })
            Assert.Contains(SettingsCatalog.FieldsFor(calls: false), f => f.Field == field && f.Group == SettingsCatalog.Multiplayer);
    }

    // ---- what the private channel reports ----

    private sealed class Link(string me, byte[] myKey, string name) : IPrivateLink
    {
        public Link? Peer;
        public event Action<PrivateFrame>? PrivateReceived;

        public Task<bool> SendPrivateAsync(string to, byte[] sealedData, CancellationToken ct)
        {
            if (Peer is null) return Task.FromResult(false);
            Peer.PrivateReceived?.Invoke(new PrivateFrame(me, myKey, name, sealedData));
            return Task.FromResult(true);
        }
    }

    private readonly X25519KeyPair _alice = X25519KeyPair.Generate();
    private readonly X25519KeyPair _bob = X25519KeyPair.Generate();
    private static string Fp(X25519KeyPair key) => MemberIdentity.FingerprintOf(key.PublicKey);

    private (PrivateMessenger Alice, PrivateMessenger Bob) Pair()
    {
        var a = new Link(Fp(_alice), _alice.PublicKey, "Alice");
        var b = new Link(Fp(_bob), _bob.PublicKey, "Bob");
        a.Peer = b;
        b.Peer = a;
        var keys = new Dictionary<string, byte[]> { [Fp(_alice)] = _alice.PublicKey, [Fp(_bob)] = _bob.PublicKey };
        return (new PrivateMessenger(a, _alice, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "alice")),
            new PrivateMessenger(b, _bob, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "bob")));
    }

    [Fact]
    public async Task ASendKeepsTheIdTheCardMadeAndSaysWhenItStarted()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        var source = Path.Combine(_dir, "a.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(PrivateMessenger.ChunkSize * 5));
        bob.FileOffered += offer => _ = bob.AcceptAsync(offer.Id);
        var reports = new System.Collections.Concurrent.ConcurrentQueue<FileProgress>();
        alice.Progress += reports.Enqueue;
        var id = PrivateMessenger.NewTransferId();
        Assert.True(PrivateMessenger.IsTransferId(id));
        Assert.True(await alice.SendFileAsync(Fp(_bob), source, id).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.All(reports, r => Assert.Equal(id, r.Id));
        Assert.False(reports.First().Started);
        Assert.True(reports.Last() is { Finished: true, Started: true, End: FileEnd.None });
        Assert.Throws<ArgumentException>(() => { _ = alice.SendFileAsync(Fp(_bob), source, "NOT-AN-ID"); });
    }

    [Fact]
    public async Task OnlyTheSenderReconnectingResumesUnderTheSameId()
    {
        var a = new Link(Fp(_alice), _alice.PublicKey, "Alice");
        var b = new Link(Fp(_bob), _bob.PublicKey, "Bob");
        a.Peer = b;
        b.Peer = a;
        var keys = new Dictionary<string, byte[]> { [Fp(_alice)] = _alice.PublicKey, [Fp(_bob)] = _bob.PublicKey };
        await using var alice = new PrivateMessenger(a, _alice, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "alice"));
        await using var bob = new PrivateMessenger(b, _bob, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "bob"));
        var source = Path.Combine(_dir, "big.bin");
        var data = RandomNumberGenerator.GetBytes(PrivateMessenger.ChunkSize * 40);
        await File.WriteAllBytesAsync(source, data);
        alice.SendLimit = () => PrivateMessenger.ChunkSize * 20;
        var offers = 0;
        bob.FileOffered += offer =>
        {
            Interlocked.Increment(ref offers);
            _ = bob.AcceptAsync(offer.Id);
        };
        FileProgress? saved = null;
        var cut = 0;
        bob.Progress += p =>
        {
            // Alice's side goes away part way; Bob has not noticed yet and still holds the part file.
            if (!p.Outgoing && !p.Finished && p.Done >= PrivateMessenger.ChunkSize * 3 && Interlocked.Exchange(ref cut, 1) == 0) a.Peer = null;
            if (p is { Outgoing: false, Finished: true, SavedPath.Length: > 0 }) saved = p;
        };
        var id = PrivateMessenger.NewTransferId();
        Assert.False(await alice.SendFileAsync(Fp(_bob), source, id).WaitAsync(TimeSpan.FromSeconds(20)));

        // Alice is back and sends again under the same id: Bob lets go of the stale attempt and carries on.
        a.Peer = b;
        Assert.True(await alice.SendFileAsync(Fp(_bob), source, id).WaitAsync(TimeSpan.FromSeconds(30)));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (saved is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.NotNull(saved);
        Assert.Equal(2, offers);
        Assert.Equal(data, await File.ReadAllBytesAsync(saved!.SavedPath!));
    }

    [Fact]
    public void AHugeLogIsReadOnlyFromItsEnd()
    {
        var log = Path.Combine(_dir, "huge.log");
        // A two-byte character straddles the cut: it is dropped, not turned into garbage.
        var text = new string('a', 100) + "é" + new string('b', 99);
        File.WriteAllText(log, text);
        var tail = FilePacker.ReadTail(log, 100);
        Assert.Equal(new string('b', 99), tail);
        Assert.Equal(text, FilePacker.ReadTail(log, 10_000));
        Assert.True(FilePacker.MaxLogBytes <= 8 * 1024 * 1024);
    }

    [Fact]
    public async Task ADeclineEndsAsNotAccepted()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        var source = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(source, "hello");
        bob.FileOffered += offer => _ = bob.DeclineAsync(offer.Id);
        FileProgress? last = null;
        alice.Progress += p => last = p;
        Assert.False(await alice.SendFileAsync(Fp(_bob), source).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(FileEnd.NotAccepted, last?.End);
        Assert.Equal(FileCardState.NotAccepted, FileCards.StateOf(last!));
    }

    [Fact]
    public async Task NobodyThereEndsAsOffline()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        var source = Path.Combine(_dir, "a.txt");
        await File.WriteAllTextAsync(source, "hello");
        FileProgress? last = null;
        alice.Progress += p => last = p;
        Assert.False(await alice.SendFileAsync("unknown-fingerprint", source).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(FileEnd.Offline, last?.End);
        Assert.Equal(FileCardState.Unreachable, FileCards.StateOf(last!));
    }

    [Fact]
    public async Task ClosingTheRoomMidSendIsALostConnectionNotACancel()
    {
        var (alice, bob) = Pair();
        await using var _b = bob;
        var source = Path.Combine(_dir, "big.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(PrivateMessenger.ChunkSize * 400));
        // Slow enough that the room closes part way.
        alice.SendLimit = () => PrivateMessenger.ChunkSize * 20;
        bob.FileOffered += offer => _ = bob.AcceptAsync(offer.Id);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FileProgress? last = null;
        alice.Progress += p =>
        {
            last = p;
            if (p.Started && p.Done > 0) started.TrySetResult();
        };
        var send = alice.SendFileAsync(Fp(_bob), source);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await alice.DisposeAsync();
        Assert.False(await send.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(FileEnd.Stalled, last?.End);
        Assert.Equal(FileCardState.Interrupted, FileCards.StateOf(last!));
    }

    [Fact]
    public async Task TheSpeedLimitSlowsASendDown()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        var source = Path.Combine(_dir, "paced.bin");
        var size = PrivateMessenger.ChunkSize * 30;
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(size));
        bob.FileOffered += offer => _ = bob.AcceptAsync(offer.Id);
        // 30 chunks at 20 chunks a second: at least about a second.
        alice.SendLimit = () => PrivateMessenger.ChunkSize * 20;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await alice.SendFileAsync(Fp(_bob), source).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1.2), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task ReceivedFilesGoToTheFolderSetNow()
    {
        var (alice, bob) = Pair();
        await using var _a = alice;
        await using var _b = bob;
        bob.Downloads = Path.Combine(_dir, "chosen");
        var source = Path.Combine(_dir, "b.txt");
        await File.WriteAllTextAsync(source, "hi");
        bob.FileOffered += offer => _ = bob.AcceptAsync(offer.Id);
        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.Progress += p => { if (p.Finished) saved.TrySetResult(p.SavedPath ?? ""); };
        Assert.True(await alice.SendFileAsync(Fp(_bob), source).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(Path.Combine(_dir, "chosen", "b.txt"), await saved.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
