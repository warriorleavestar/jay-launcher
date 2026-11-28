using System.Net;
using System.Security.Cryptography;
using Amber.Core.Auth;

namespace Amber.Core.Tests;

/// <summary>authlib-injector is a Java agent: only the pinned build is used, whichever source serves it.</summary>
public sealed class AuthlibInjectorPinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-authlib-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Good = "pinned agent bytes"u8.ToArray();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Pins.Pin Pin(params string[] urls) => new("authlib-injector", "test", "1.0", null, null, null, "authlib-injector-1.0.jar",
        urls, null, null, Convert.ToHexStringLower(SHA256.HashData(Good)), null, Good.Length, ["x"]);

    private sealed class Handler(Dictionary<string, byte[]> bodies) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            return Task.FromResult(bodies.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public void ThePinIsInPinsJson()
    {
        var pin = Pins.Get(AuthlibInjector.PinId);
        Assert.NotNull(pin.Sha256);
        Assert.NotNull(pin.Size);
        Assert.All(pin.Urls, u => Assert.StartsWith("https://", u));
    }

    [Fact]
    public async Task MirrorServingOtherBytesIsSkippedAndThePinnedOneIsKept()
    {
        using var handler = new Handler(new() { ["https://mirror.test/a.jar"] = "tampered"u8.ToArray(), ["https://official.test/a.jar"] = Good });
        var injector = new AuthlibInjector(new HttpClient(handler), _dir, Pin("https://mirror.test/a.jar", "https://official.test/a.jar"));
        var jar = await injector.EnsureJarAsync();
        Assert.Equal(Good, File.ReadAllBytes(jar));
        Assert.Equal(2, handler.Requests.Count);

        // The cached copy still matches: no network on the next launch.
        Assert.Equal(jar, await injector.EnsureJarAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task OnlyWrongBytesFailsTheChecksumAndLeavesNothing()
    {
        using var handler = new Handler(new() { ["https://mirror.test/a.jar"] = "tampered"u8.ToArray() });
        var injector = new AuthlibInjector(new HttpClient(handler), _dir, Pin("https://mirror.test/a.jar"));
        await Assert.ThrowsAsync<InvalidDataException>(() => injector.EnsureJarAsync());
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ChangedCacheIsReplacedByThePinnedBuild()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "authlib-injector-1.0.jar"), "changed on disk");
        using var handler = new Handler(new() { ["https://official.test/a.jar"] = Good });
        var jar = await new AuthlibInjector(new HttpClient(handler), _dir, Pin("https://official.test/a.jar")).EnsureJarAsync();
        Assert.Equal(Good, File.ReadAllBytes(jar));
    }

    [Fact]
    public async Task UnreachableSourcesSayTheDownloadFailed()
    {
        using var handler = new Handler([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AuthlibInjector(new HttpClient(handler), _dir, Pin("https://official.test/a.jar")).EnsureJarAsync());
    }

    /// <summary>Sends headers, then never sends the body.</summary>
    private sealed class StalledHandler(Dictionary<string, byte[]> bodies, string stalled, bool breaks = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == stalled)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(breaks)) });
            return Task.FromResult(bodies.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>Never sends a byte; with <c>breaks</c>, the connection drops instead.</summary>
    private sealed class StalledStream(bool breaks = false) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (breaks) throw new HttpIOException(HttpRequestError.ResponseEnded, "the connection dropped");
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ASourceThatStopsSendingTheBodyGivesWayToTheNext()
    {
        using var handler = new StalledHandler(new() { ["https://official.test/a.jar"] = Good }, "https://mirror.test/a.jar");
        var injector = new AuthlibInjector(new HttpClient(handler), _dir, Pin("https://mirror.test/a.jar", "https://official.test/a.jar"))
        {
            SourceTimeout = TimeSpan.FromMilliseconds(300),
        };
        var download = injector.EnsureJarAsync();
        Assert.Same(download, await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(30))));
        Assert.Equal(Good, File.ReadAllBytes(await download));
    }

    [Fact]
    public async Task ASourceWhoseBodyBreaksOffGivesWayToTheNext()
    {
        using var handler = new StalledHandler(new() { ["https://official.test/a.jar"] = Good }, "https://mirror.test/a.jar", breaks: true);
        var jar = await new AuthlibInjector(new HttpClient(handler), _dir, Pin("https://mirror.test/a.jar", "https://official.test/a.jar")).EnsureJarAsync();
        Assert.Equal(Good, File.ReadAllBytes(jar));
    }
}
