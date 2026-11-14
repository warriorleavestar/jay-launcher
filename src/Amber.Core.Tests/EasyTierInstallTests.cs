using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>联机组件安装：自带安装包、多个下载来源依次尝试、哈希不对就拒绝、手动导入。</summary>
public sealed class EasyTierInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-et-install-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _good;
    private readonly EasyTierRelease.Asset _asset;

    public EasyTierInstallTests()
    {
        Directory.CreateDirectory(_root);
        _good = MakeZip("good");
        _asset = new EasyTierRelease.Asset("test-x64", "easytier-test-x64-v2.6.4.zip",
            Convert.ToHexStringLower(SHA256.HashData(_good)), _good.Length, "easytier-core.exe", "easytier-cli.exe");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static byte[] MakeZip(string marker)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var name in new[] { "easytier-core.exe", "easytier-cli.exe", "WinDivert64.sys" })
                using (var w = new StreamWriter(zip.CreateEntry("easytier-test/" + name).Open())) w.Write(marker + name);
        return ms.ToArray();
    }

    /// <summary>同样大小但内容不同的数据（模拟被篡改的镜像）。</summary>
    private byte[] Tampered()
    {
        var bad = (byte[])_good.Clone();
        bad[bad.Length / 2] ^= 0xFF;
        return bad;
    }

    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            return Task.FromResult(respond(url));
        }
    }

    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private (EasyTierInstaller Installer, Handler Handler) Create(Func<string, HttpResponseMessage> respond, string? bundledDir = null, string? dataDir = null,
        EasyTierRelease.Asset? asset = null)
    {
        var handler = new Handler(respond);
        var downloader = new Downloader(new HttpClient(handler), new SourceChain([new OfficialSource()])) { RetriesPerSource = 1 };
        IReadOnlyList<(string, string)> sources = [("A", "https://a.test/et.zip"), ("B", "https://b.test/et.zip"), ("C", "https://c.test/et.zip")];
        var installer = new EasyTierInstaller(downloader, dataDir ?? Path.Combine(_root, "data"), bundledDir ?? Path.Combine(_root, "no-bundle"), asset ?? _asset, sources);
        return (installer, handler);
    }

    private static HttpResponseMessage NoNetwork(string _) => throw new InvalidOperationException("must not download");

    /// <summary>Bound on every await in the lock tests, so a regression fails instead of hanging the run.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private string WriteFile(string name, byte[] data)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private string Bundle(string name)
    {
        var bundle = Path.Combine(_root, name);
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, _asset.Name), _good);
        return bundle;
    }

    private static void AssertInstalledGood(EasyTierInstaller installer)
    {
        Assert.True(installer.IsInstalled);
        Assert.Equal("goodeasytier-core.exe", File.ReadAllText(installer.CorePath));
        Assert.Equal("goodeasytier-cli.exe", File.ReadAllText(installer.CliPath));
        Assert.False(Directory.Exists(installer.InstallDir + ".tmp"));
    }

    /// <summary>Keeps a real install paused right after it took the folder's lock, until <see cref="Go"/>.</summary>
    private sealed class Hold
    {
        private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Go() => _go.TrySetResult();

        public async Task Run(CancellationToken _)
        {
            Entered.TrySetResult();
            // Only a safety net (tests always call Go in a finally); longer than Bound so a test's own bound fails first.
            await _go.Task.WaitAsync(3 * Bound);
        }
    }

    /// <summary>What a waiting install saw of the folder once it got the lock.</summary>
    private static TaskCompletionSource<(bool Installed, bool TempLeft)> Observe(EasyTierInstaller installer)
    {
        var saw = new TaskCompletionSource<(bool, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        installer.WhileLockedForTests = _ =>
        {
            saw.TrySetResult((installer.IsInstalled, Directory.Exists(installer.InstallDir + ".tmp")));
            return Task.CompletedTask;
        };
        return saw;
    }

    [Fact]
    public async Task UsesBundledPackageWithoutNetwork()
    {
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, _asset.Name), _good);
        var (installer, handler) = Create(_ => throw new InvalidOperationException("不该联网"), bundle);

        await installer.EnsureAsync().WaitAsync(Bound);

        Assert.True(installer.IsInstalled);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FallsBackFromBrokenBundleAndFailedSources()
    {
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, _asset.Name), Tampered());
        var (installer, handler) = Create(url => url.Contains("a.test") ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : url.Contains("b.test") ? Bytes(Tampered()) : Bytes(_good), bundle);

        await installer.EnsureAsync().WaitAsync(Bound);

        Assert.True(installer.IsInstalled);
        Assert.Contains(handler.Requests, u => u.Contains("c.test"));
        // The elevated guard gets the checked download, not the broken bundle.
        Assert.Equal(installer.KeptPackagePath, installer.PackagePath);
        Assert.Equal(_good, File.ReadAllBytes(installer.PackagePath));
        // 装好后下载用的临时文件不留。
        Assert.False(File.Exists(Path.Combine(_root, "data", "easytier", "download", _asset.Name)));
    }

    [Fact]
    public async Task ReportsEverySourceWhenAllFail()
    {
        var (installer, _) = Create(url => url.Contains("a.test") ? throw new TaskCanceledException("timeout")
            : url.Contains("b.test") ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Bytes(Tampered()));

        var e = await Assert.ThrowsAsync<EasyTierInstallException>(() => installer.EnsureAsync().WaitAsync(Bound));

        Assert.Equal(["A", "B", "C"], e.Failures.Select(f => f.Source));
        Assert.Equal(L.T("common.request_timeout"), e.Failures[0].Reason);
        Assert.Contains("403", e.Failures[1].Reason);
        Assert.Contains(_asset.Name, e.Message);
        Assert.False(installer.IsInstalled);
        // 内容被改过的包丢掉，不能留给下一次续传。
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "data", "easytier", "download")));
    }

    [Fact]
    public async Task ImportRequiresPinnedHash()
    {
        var (installer, _) = Create(_ => throw new InvalidOperationException("不该联网"));
        var bad = Path.Combine(_root, "bad.zip");
        File.WriteAllBytes(bad, Tampered());
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(bad).WaitAsync(Bound));
        var other = Path.Combine(_root, "other.zip");
        File.WriteAllBytes(other, MakeZip("other-size-" + new string('x', 50)));
        var wrong = await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(other).WaitAsync(Bound));
        Assert.Contains(_asset.Name, wrong.Message);
        Assert.False(installer.IsInstalled);

        var good = Path.Combine(_root, "good.zip");
        File.WriteAllBytes(good, _good);
        await installer.ImportAsync(good).WaitAsync(Bound);
        Assert.True(installer.IsInstalled);
        Assert.Equal(_good, File.ReadAllBytes(installer.PackagePath));
    }

    [Fact]
    public async Task APackageIsKeptForTheElevatedGuard()
    {
        var (installer, _) = Create(_ => Bytes(_good));
        await installer.EnsureAsync();
        Assert.True(installer.IsInstalled);
        Assert.Equal(_good, File.ReadAllBytes(installer.PackagePath));
        // Without the package (an install from before it was kept) the component counts as missing and is fetched again.
        File.Delete(installer.KeptPackagePath);
        Assert.False(installer.IsInstalled);
        await installer.EnsureAsync();
        Assert.True(installer.IsInstalled);
    }

    [Fact]
    public async Task ConcurrentFirstInstallsDoNotCollide()
    {
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, _asset.Name), _good);
        // Several rounds over a fresh data folder each: every round is a first-time install raced by several installers.
        for (var round = 0; round < 10; round++)
        {
            var data = Path.Combine(_root, "data-" + round);
            var installers = Enumerable.Range(0, 6)
                .Select(_ => Create(_ => throw new InvalidOperationException("must not download"), bundle, data).Installer)
                .ToList();

            await Task.WhenAll(installers.Select(i => Task.Run(() => i.EnsureAsync()))).WaitAsync(Bound);

            Assert.All(installers, i => Assert.True(i.IsInstalled));
            Assert.False(Directory.Exists(installers[0].InstallDir + ".tmp"));
        }
    }

    /// <summary>An install in progress holds the folder: a second caller waits for it even when the files already show up.</summary>
    [Fact]
    public async Task EnsureWaitsForAnInstallInProgressEvenWhenTheFilesAreThere()
    {
        var bundle = Path.Combine(_root, "bundle-wait");
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, _asset.Name), _good);
        var installer = Create(_ => throw new InvalidOperationException("must not download"), bundle, Path.Combine(_root, "data-wait")).Installer;
        await installer.EnsureAsync().WaitAsync(Bound);
        Assert.True(installer.IsInstalled);
        var gate = installer.InstallLock;
        Assert.True(await gate.WaitAsync(Bound));
        Task ensure;
        try
        {
            ensure = installer.EnsureAsync();
            await Task.Delay(200);
            Assert.False(ensure.IsCompleted);
        }
        finally { gate.Release(); }
        await ensure.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Two imports into one folder: the second starts only once the first has finished. ImportAsync asks for the lock
    /// before it first yields, so without the lock the second one's hook would already have run when the call returns.
    /// </summary>
    [Fact]
    public async Task ConcurrentImportsIntoTheSameFolderRunOneAfterTheOther()
    {
        var data = Path.Combine(_root, "data-imports");
        var zip = WriteFile("import.zip", _good);
        var first = Create(NoNetwork, dataDir: data).Installer;
        var second = Create(NoNetwork, dataDir: data).Installer;
        Assert.Same(first.InstallLock, second.InstallLock);
        var hold = new Hold();
        first.WhileLockedForTests = hold.Run;
        var secondSaw = Observe(second);

        var a = first.ImportAsync(zip);
        try
        {
            await hold.Entered.Task.WaitAsync(Bound);
            var b = second.ImportAsync(zip);
            Assert.False(secondSaw.Task.IsCompleted, "the second import went ahead while the first held the folder");
            Assert.False(b.IsCompleted);
            hold.Go();
            await Task.WhenAll(a, b).WaitAsync(Bound);
        }
        finally { hold.Go(); }

        var (installed, tempLeft) = await secondSaw.Task.WaitAsync(Bound);
        Assert.True(installed, "the second import started before the first had finished");
        Assert.False(tempLeft);
        AssertInstalledGood(first);
        Assert.Equal(1, first.InstallLock.CurrentCount);
    }

    /// <summary>A manual import and an automatic install (here from the bundled package) into one folder take turns, whichever comes first.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportAndAutomaticInstallIntoTheSameFolderDoNotInterleave(bool automaticFirst)
    {
        var data = Path.Combine(_root, "data-mixed");
        // Both belong to the same Jay, so they share its bundled package: the elevated guard's copy counts toward "installed".
        var bundle = Bundle("bundle-mixed");
        var (auto, autoHandler) = Create(NoNetwork, bundle, data);
        var import = Create(NoNetwork, bundle, data).Installer;
        var zip = WriteFile("import.zip", _good);
        Task Start(EasyTierInstaller i) => i == auto ? i.EnsureAsync() : i.ImportAsync(zip);
        var (holder, waiter) = automaticFirst ? (auto, import) : (import, auto);
        var hold = new Hold();
        holder.WhileLockedForTests = hold.Run;
        var waiterSaw = Observe(waiter);

        var a = Start(holder);
        try
        {
            await hold.Entered.Task.WaitAsync(Bound);
            var b = Start(waiter);
            Assert.False(waiterSaw.Task.IsCompleted, "the second install went ahead while the first held the folder");
            Assert.False(b.IsCompleted);
            hold.Go();
            await Task.WhenAll(a, b).WaitAsync(Bound);
        }
        finally { hold.Go(); }

        var (installed, tempLeft) = await waiterSaw.Task.WaitAsync(Bound);
        Assert.True(installed, "the second install started before the first had finished");
        Assert.False(tempLeft);
        AssertInstalledGood(auto);
        Assert.Empty(autoHandler.Requests);
        Assert.Equal(1, auto.InstallLock.CurrentCount);
    }

    /// <summary>A call waiting for the folder gives up as soon as it is cancelled; the install holding the folder carries on.</summary>
    [Fact]
    public async Task CancellingACallThatWaitsForTheFolderLeavesTheInstallInProgressAlone()
    {
        var data = Path.Combine(_root, "data-cancel");
        var zip = WriteFile("import.zip", _good);
        var holder = Create(NoNetwork, dataDir: data).Installer;
        var importWaiter = Create(NoNetwork, dataDir: data).Installer;
        var ensureWaiter = Create(NoNetwork, Bundle("bundle-cancel"), data).Installer;
        var hold = new Hold();
        holder.WhileLockedForTests = hold.Run;
        var importSaw = Observe(importWaiter);
        var ensureSaw = Observe(ensureWaiter);

        var a = holder.ImportAsync(zip);
        try
        {
            await hold.Entered.Task.WaitAsync(Bound);
            using var cts = new CancellationTokenSource();
            var waitingImport = importWaiter.ImportAsync(zip, cts.Token);
            var waitingEnsure = ensureWaiter.EnsureAsync(null, cts.Token);
            Assert.False(waitingImport.IsCompleted);
            Assert.False(waitingEnsure.IsCompleted);

            cts.Cancel();
            // The holder is still paused inside, so these can only end by giving up the wait.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingImport.WaitAsync(Bound));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingEnsure.WaitAsync(Bound));
            Assert.False(a.IsCompleted);
            Assert.False(importSaw.Task.IsCompleted);
            Assert.False(ensureSaw.Task.IsCompleted);

            hold.Go();
            await a.WaitAsync(Bound);
        }
        finally { hold.Go(); }

        AssertInstalledGood(holder);
        Assert.False(importSaw.Task.IsCompleted);
        Assert.False(ensureSaw.Task.IsCompleted);
        Assert.Equal(1, holder.InstallLock.CurrentCount);
    }

    /// <summary>A call that fails while it holds the folder lets it go: the next call goes ahead and succeeds.</summary>
    [Fact]
    public async Task AFailedInstallReleasesTheFolder()
    {
        var data = Path.Combine(_root, "data-fail");
        var (installer, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound), dataDir: data);
        var gate = installer.InstallLock;

        // Same size, wrong hash.
        var tampered = WriteFile("tampered.zip", Tampered());
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.ImportAsync(tampered).WaitAsync(Bound));
        Assert.Equal(1, gate.CurrentCount);

        // Passes the pinned size and hash but isn't a zip: fails while extracting.
        var junk = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("not a zip ", 40)));
        var corrupt = Create(NoNetwork, dataDir: data,
            asset: _asset with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(junk)), Size = junk.Length }).Installer;
        Assert.Same(gate, corrupt.InstallLock);
        var junkPath = WriteFile("junk.zip", junk);
        await Assert.ThrowsAsync<InvalidDataException>(() => corrupt.ImportAsync(junkPath).WaitAsync(Bound));
        Assert.Equal(1, gate.CurrentCount);

        // An automatic install where every source fails.
        await Assert.ThrowsAsync<EasyTierInstallException>(() => installer.EnsureAsync().WaitAsync(Bound));
        Assert.Equal(1, gate.CurrentCount);

        var good = WriteFile("good.zip", _good);
        await installer.ImportAsync(good).WaitAsync(Bound);
        AssertInstalledGood(installer);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void InstallFoldersThatDifferOnlyInCaseShareALockWhereTheFileSystemIgnoresCase()
    {
        var comparer = EasyTierInstaller.PathComparer;
        Assert.Equal(!OperatingSystem.IsLinux(), comparer.Equals(@"C:\Data\EasyTier", @"c:\data\easytier"));
        Assert.True(comparer.Equals("/data/easytier", "/data/easytier"));
    }

    [Fact]
    public void OrdersMirrorsAndKeepsOfficial()
    {
        var asset = EasyTierRelease.For("windows", System.Runtime.InteropServices.Architecture.X64);
        var official = EasyTierRelease.Sources(asset, false);
        var mirrored = EasyTierRelease.Sources(asset, true);
        Assert.Equal("GitHub", official[0].Name);
        Assert.Equal("GitHub", mirrored[^1].Name);
        Assert.All(mirrored, s => Assert.EndsWith(asset.Url, s.Url));
        Assert.Equal(EasyTierRelease.Mirrors.Count + 1, mirrored.Count);
    }

    [Fact]
    public void TimeoutIsNotShownAsCancel()
    {
        Assert.Equal(L.T("common.request_timeout"), EasyTierInstaller.Describe(new TaskCanceledException()));
        Assert.Equal(L.T("common.request_timeout"), EasyTierInstaller.Describe(new TimeoutException("x", new TaskCanceledException())));
    }

    [Fact]
    public async Task InstallRemovesOldBuildsButKeepsWorkFoldersAndFolderInUse()
    {
        var data = Path.Combine(_root, "data");
        var easytier = Path.Combine(data, "easytier");
        var upstream = Path.Combine(easytier, "2.4.5", "test-x64");
        var oldBuild = Path.Combine(easytier, "2.6.4-jay-0123456789ab", "test-x64");
        var inUse = Path.Combine(easytier, "2.6.4-jay-ba9876543210", "test-x64");
        var lobby = Path.Combine(easytier, "lobby");
        foreach (var dir in new[] { upstream, oldBuild, inUse, lobby }) Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(lobby, "keep.txt"), "work");
        var downloads = Path.Combine(easytier, "download");
        Directory.CreateDirectory(downloads);
        var oldPart = Path.Combine(downloads, "easytier-test-x64-v2.6.3.zip.part");
        var freshPart = Path.Combine(downloads, "easytier-test-x64-v2.6.5.zip.part");
        File.WriteAllText(oldPart, "x");
        File.SetLastWriteTimeUtc(oldPart, DateTime.UtcNow - EasyTierInstaller.StaleDownloadAge - TimeSpan.FromHours(1));
        File.WriteAllText(freshPart, "x");
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, _asset.Name), _good);
        var downloader = new Downloader(new HttpClient(new Handler(_ => throw new InvalidOperationException())), new SourceChain([new OfficialSource()]));
        var installer = new EasyTierInstaller(downloader, data, bundle, _asset, [])
        {
            RunningForTest = () => [Path.Combine(inUse, "easytier-core.exe")],
        };

        await installer.EnsureAsync();

        Assert.True(installer.IsInstalled);
        Assert.False(Directory.Exists(Path.GetDirectoryName(upstream)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldBuild)));
        Assert.True(Directory.Exists(inUse));
        Assert.True(File.Exists(Path.Combine(lobby, "keep.txt")));
        Assert.False(File.Exists(oldPart));
        Assert.True(File.Exists(freshPart));
    }

    [Fact]
    public async Task OldBuildsStayWhenARunningEasyTierCannotBeChecked()
    {
        var data = Path.Combine(_root, "data");
        var oldBuild = Path.Combine(data, "easytier", "2.6.4-jay-0123456789ab", "test-x64");
        Directory.CreateDirectory(oldBuild);
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, _asset.Name), _good);
        var downloader = new Downloader(new HttpClient(new Handler(_ => throw new InvalidOperationException())), new SourceChain([new OfficialSource()]));
        var installer = new EasyTierInstaller(downloader, data, bundle, _asset, []) { RunningForTest = () => null };

        await installer.EnsureAsync();
        await installer.EnsureAsync();

        Assert.True(installer.IsInstalled);
        Assert.True(Directory.Exists(oldBuild));
    }
}
