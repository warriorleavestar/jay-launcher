using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Content;

namespace Amber.Core.Tests;

/// <summary>The update check asks CurseForge about many mods with a bounded number of requests at a time.</summary>
public sealed class CurseForgeUpdateCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-cf-update-" + Guid.NewGuid().ToString("N")[..8]);

    public CurseForgeUpdateCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(81920)]
    [InlineData(81920 * 2 + 7)]
    public async Task StreamedFingerprintMatchesTheInMemoryOne(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        for (var i = 0; i < size; i += 5) bytes[i] = (byte)" \t\r\n"[i % 4];
        var path = Path.Combine(_dir, $"f{size}.jar");
        await File.WriteAllBytesAsync(path, bytes);
        Assert.Equal(CurseForgeProvider.Fingerprint(bytes), await CurseForgeProvider.FingerprintAsync(path));
    }

    [Fact]
    public async Task ManyModsTakeOneDistributionLookupAndBoundedParallelRequests()
    {
        const int count = 10;
        var mods = new List<LocalMod>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(_dir, $"mod{i}.jar");
            await File.WriteAllTextAsync(path, $"mod number {i}");
            mods.Add(new LocalMod(path, $"mod{i}.jar", true, null, $"Mod {i}", "1", null, null, null));
        }
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var updates = await ModManager.CheckUpdatesAsync(mods, new ModrinthProvider(http), new CurseForgeProvider(http, () => "test-key"), "1.21.1", "fabric");

        Assert.Equal(count, updates.Count);
        Assert.All(updates, u => Assert.StartsWith("2", u.Latest.FileId));
        Assert.Equal(count, handler.FileListRequests);
        // One read of the distribution settings while identifying, one for all the file lists (not one per mod).
        Assert.Equal(2, handler.ProjectRequests);
        Assert.InRange(handler.MostAtOnce, 1, CurseForgeProvider.MaxParallelProjects);
    }

    private sealed class Handler : HttpMessageHandler
    {
        private int _running;
        public int ProjectRequests;
        public int FileListRequests;
        public int MostAtOnce;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.modrinth.com") return Json("{}");
            var path = uri.AbsolutePath;
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            if (path == "/v1/fingerprints")
            {
                var matches = new JsonArray();
                var i = 0;
                foreach (var fp in body!["fingerprints"]!.AsArray())
                {
                    matches.Add(new JsonObject { ["file"] = File(1000 + i, 100 + i, "2020-01-01T00:00:00Z", fp!.GetValue<uint>()) });
                    i++;
                }
                return Data(new JsonObject { ["exactMatches"] = matches });
            }
            if (path == "/v1/mods")
            {
                Interlocked.Increment(ref ProjectRequests);
                var projects = new JsonArray();
                foreach (var id in body!["modIds"]!.AsArray())
                    projects.Add(new JsonObject
                    {
                        ["id"] = id!.GetValue<long>(), ["name"] = "M", ["allowModDistribution"] = true,
                        ["links"] = new JsonObject { ["websiteUrl"] = "https://www.curseforge.com/minecraft/mc-mods/m" },
                    });
                return Data(projects);
            }
            if (path.StartsWith("/v1/mods/", StringComparison.Ordinal) && path.EndsWith("/files", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref FileListRequests);
                var now = Interlocked.Increment(ref _running);
                lock (this) MostAtOnce = Math.Max(MostAtOnce, now);
                try { await Task.Delay(30, cancellationToken); }
                finally { Interlocked.Decrement(ref _running); }
                var project = long.Parse(path.Split('/')[3]);
                return Data(new JsonArray(File(2000 + (int)(project - 100), (int)project, "2024-01-01T00:00:00Z", 1)));
            }
            throw new InvalidOperationException("unexpected request " + uri);
        }

        private static JsonObject File(int id, int project, string date, uint fingerprint) => new()
        {
            ["id"] = id, ["modId"] = project, ["fileName"] = $"f{id}.jar", ["downloadUrl"] = $"https://edge.forgecdn.net/files/{id}.jar",
            ["fileDate"] = date, ["fileFingerprint"] = fingerprint, ["gameVersions"] = new JsonArray("1.21.1", "Fabric"), ["dependencies"] = new JsonArray(),
        };

        private static HttpResponseMessage Data(JsonNode data) => Json(new JsonObject { ["data"] = data }.ToJsonString());

        private static HttpResponseMessage Json(string text) =>
            new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
