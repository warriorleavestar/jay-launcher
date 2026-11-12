using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amber.App.Services;
using Amber.Core.Activity;
using Amber.Core.Launch;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

/// <summary>The once-a-day update notice: only a signed newer Jay is shown, never downloaded; off, not due and skipped stay quiet.</summary>
public sealed class UpdateNotifierTests : IDisposable
{
    private static readonly TrustSigner[] T = [.. Enumerable.Range(0, 3).Select(_ => TrustSigner.FromSeed(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))))];
    private static readonly TrustList Own = new([T[0].PublicKey, T[1].PublicKey, T[2].PublicKey], 2);
    private const string Feed = "https://releases.example.test/latest";
    private const string Page = "https://releases.example.test/v99.0.0";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-updnotify-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    public UpdateNotifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A release feed for v99.0.0 with a package, its SHA-256 list and the list signed by <paramref name="signers"/>.</summary>
    private static FakeHttp Release(TrustSigner[] signers, string page = Page)
    {
        var http = new FakeHttp();
        var sums = $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("package")))}  Jay-win-x64.zip\n";
        var statement = new ReleaseStatement("v99.0.0", 5, new string('c', 40), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sums))), Own.Hash, ReleaseStatement.NoLog);
        var sigs = signers.Aggregate(ReleaseSignature.Create(statement), (json, s) => ReleaseSignature.Cosign(json, s));
        var assets = new JsonArray();
        foreach (var (name, body) in new[] { ("Jay-win-x64.zip", "package"), (ReleaseSignature.SumsFileName, sums), (ReleaseSignature.SignatureFileName, sigs) })
        {
            var url = $"https://releases.example.test/download/v99.0.0/{name}";
            http.On(url, body);
            assets.Add(new JsonObject { ["name"] = name, ["browser_download_url"] = url });
        }
        http.On(Feed, new JsonObject { ["tag_name"] = "v99.0.0", ["html_url"] = page, ["body"] = "notes", ["assets"] = assets }.ToJsonString());
        return http;
    }

    private readonly List<string> _opened = [];

    private (LauncherService Service, UpdateNotifier Notifier) Make(FakeHttp http, Action<AppSettings>? setup = null)
    {
        // Settings.Save() goes to AppSettings.FilePath, which TestDataDir points at a temporary folder for the whole test run.
        var settings = new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "", UpdateFeedUrl = Feed };
        setup?.Invoke(settings);
        var service = new LauncherService(settings);
        return (service, new UpdateNotifier(service, () => _now, new HttpClient(http), Path.Combine(_dir, "data"), Own, url =>
        {
            _opened.Add(url);
            return null;
        }));
    }

    private static RequestHandle? Notice(LauncherService service) => service.Requests.Pending.SingleOrDefault(r => r.Spec.Kind == UpdateNotifier.RequestKind);

    [Fact]
    public async Task ASignedNewerReleaseIsShownAndOnlyOpensThePage()
    {
        var http = Release([T[0], T[2]]);
        var (service, notifier) = Make(http);
        using var _ = service;
        Assert.True(await notifier.CheckIfDueAsync());
        var notice = Notice(service);
        Assert.NotNull(notice);
        Assert.Contains("99.0.0", notice!.Spec.Title);
        Assert.Equal(_now, service.Settings.LastUpdateCheck);
        // Only the feed and the signature files were fetched: the package itself is never downloaded.
        Assert.DoesNotContain(http.Requests, r => r.Url.EndsWith("Jay-win-x64.zip", StringComparison.Ordinal));

        service.Requests.Resolve(notice, "open", RequestSource.Bar);
        Assert.Equal([Page], _opened);
    }

    [Theory]
    [InlineData("https://phishing.example.test/jay-v99")]
    [InlineData("http://releases.example.test/v99.0.0")]
    public async Task APageTheSignaturesDontCoverIsNeverOpened(string page)
    {
        // The signatures cover the tag, not html_url: a swapped page on another host (or plain HTTP) gets no open button.
        var (service, notifier) = Make(Release([T[0], T[2]], page));
        using var _ = service;
        Assert.True(await notifier.CheckIfDueAsync());
        var notice = Notice(service)!;
        Assert.DoesNotContain(notice.Spec.Choices, c => c.Id == "open");
        Assert.Contains(notice.Spec.Choices, c => c.Id == "later" && c.IsDefault);
        service.Requests.Resolve(notice, "later", RequestSource.Bar);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task AnUnsignedOrHalfSignedReleaseStaysQuiet()
    {
        var (service, notifier) = Make(Release([T[1]]));
        using var _ = service;
        Assert.False(await notifier.CheckIfDueAsync());
        Assert.Null(Notice(service));
        Assert.Equal(_now, service.Settings.LastUpdateCheck);
    }

    [Fact]
    public async Task OffOrNotDueMakesNoRequest()
    {
        var off = new FakeHttp();
        var (service, notifier) = Make(off, s => s.AutoUpdateCheck = false);
        using (service) Assert.False(await notifier.CheckIfDueAsync());
        Assert.Empty(off.Requests);

        var recent = new FakeHttp();
        var (service2, notifier2) = Make(recent, s => s.LastUpdateCheck = _now.AddHours(-2));
        using (service2) Assert.False(await notifier2.CheckIfDueAsync());
        Assert.Empty(recent.Requests);
    }

    [Fact]
    public async Task AFailedCheckWaitsADayToo()
    {
        var down = new FakeHttp(); // every address answers 404
        var (service, notifier) = Make(down);
        using var _ = service;
        Assert.False(await notifier.CheckIfDueAsync());
        Assert.Equal(_now, service.Settings.LastUpdateCheck);
        Assert.False(await notifier.CheckIfDueAsync());
        Assert.Single(down.Requests);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("ftp://releases.example.test/latest")]
    [InlineData("https://")]
    public async Task AWrongAddressIsQuietAndWaitsADay(string feed)
    {
        var (service, notifier) = Make(new FakeHttp(), s => s.UpdateFeedUrl = feed);
        using var _ = service;
        Assert.False(await notifier.CheckIfDueAsync());
        Assert.Null(Notice(service));
        Assert.Equal(_now, service.Settings.LastUpdateCheck);
    }

    [Fact]
    public async Task ASkippedVersionIsNotShownAgain()
    {
        var (service, notifier) = Make(Release([T[0], T[1]]));
        using var _ = service;
        Assert.True(await notifier.CheckIfDueAsync());
        service.Requests.Resolve(Notice(service)!, "skip", RequestSource.Bar);
        Assert.Equal("99.0.0", service.Settings.SkippedUpdateVersion);

        service.Settings.LastUpdateCheck = null;
        Assert.False(await notifier.CheckIfDueAsync());
        Assert.Null(Notice(service));
    }
}
