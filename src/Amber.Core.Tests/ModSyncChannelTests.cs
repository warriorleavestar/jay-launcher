using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Mod sync over the private channel: the host's list arrives in pieces, “off” is honoured, and only listed files are sent.</summary>
public sealed class ModSyncChannelTests : IDisposable
{
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly X25519KeyPair _guest = X25519KeyPair.Generate();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-modsync-" + Guid.NewGuid().ToString("N"));

    private static string Fp(X25519KeyPair key) => MemberIdentity.FingerprintOf(key.PublicKey);

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

    private (PrivateMessenger Host, PrivateMessenger Guest) Pair()
    {
        var h = new Link(Fp(_host), _host.PublicKey, "Host");
        var g = new Link(Fp(_guest), _guest.PublicKey, "Guest");
        h.Peer = g;
        g.Peer = h;
        var keys = new Dictionary<string, byte[]> { [Fp(_host)] = _host.PublicKey, [Fp(_guest)] = _guest.PublicKey };
        return (new PrivateMessenger(h, _host, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "host")),
            new PrivateMessenger(g, _guest, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "guest")));
    }

    private static ModManifest Big(int count) => new([.. Enumerable.Range(0, count).Select(i =>
        new ModManifestEntry($"mod-{i}.jar", Convert.ToHexStringLower(SHA1.HashData(BitConverter.GetBytes(i))), 1000 + i, "mod" + i, "Mod " + i, "1." + i))]);

    [Fact]
    public async Task ListArrivesInPieces()
    {
        var (host, guest) = Pair();
        await using var _h = host;
        await using var _g = guest;
        var manifest = Big(400);
        Assert.True(PrivateMessenger.Pack(manifest).Count > 1);
        host.ModListProvider = () => manifest;
        var reply = await guest.RequestModListAsync(Fp(_host));
        Assert.Equal(ModListAnswer.Sent, reply.Answer);
        Assert.Equal(manifest.Mods, reply.Manifest!.Mods);
    }

    [Fact]
    public async Task HostCanTurnSyncOff()
    {
        var (host, guest) = Pair();
        await using var _h = host;
        await using var _g = guest;
        host.ModListProvider = () => null;
        Assert.Equal(ModListAnswer.Off, (await guest.RequestModListAsync(Fp(_host))).Answer);
    }

    [Fact]
    public async Task OnlyListedFilesAreSentAndChecked()
    {
        var (host, guest) = Pair();
        await using var _h = host;
        await using var _g = guest;
        Directory.CreateDirectory(_dir);
        var listed = Path.Combine(_dir, "private-mod.jar");
        File.WriteAllBytes(listed, RandomNumberGenerator.GetBytes(10_000));
        var secret = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(secret, "not a mod");
        var listedSha = Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(listed)));
        var secretSha = Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(secret)));
        host.ModListProvider = () => new ModManifest([new ModManifestEntry("private-mod.jar", listedSha, 10_000)]);
        host.ModFileProvider = sha => sha == listedSha ? listed : sha == secretSha ? secret : null;
        var offered = false;
        guest.FileOffered += _ => offered = true;

        var saved = await guest.RequestModFileAsync(Fp(_host), listedSha).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(saved);
        Assert.Equal(File.ReadAllBytes(listed), File.ReadAllBytes(saved));
        Assert.False(offered);

        // Not in the published list: refused even though the host has the file.
        Assert.Null(await guest.RequestModFileAsync(Fp(_host), secretSha).WaitAsync(TimeSpan.FromSeconds(10)));
        // Sync turned off afterwards: refused.
        host.ModListProvider = () => null;
        Assert.Null(await guest.RequestModFileAsync(Fp(_host), listedSha).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// A member who floods the host with mod file requests gets at most two transfers at once; repeats of a file already on
    /// its way are ignored and the rest are answered "no".
    /// </summary>
    [Fact]
    public async Task ModFileRequestsAreLimitedPerMember()
    {
        var h = new Link(Fp(_host), _host.PublicKey, "Host");
        var g = new Link(Fp(_guest), _guest.PublicKey, "Guest");
        h.Peer = g;
        g.Peer = h;
        var keys = new Dictionary<string, byte[]> { [Fp(_host)] = _host.PublicKey, [Fp(_guest)] = _guest.PublicKey };
        await using var host = new PrivateMessenger(h, _host, f => keys.GetValueOrDefault(f), Path.Combine(_dir, "host"));
        Directory.CreateDirectory(_dir);
        var files = Enumerable.Range(0, 4).Select(i =>
        {
            var path = Path.Combine(_dir, $"mod-{i}.jar");
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(5_000));
            return (Path: path, Sha1: Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(path))));
        }).ToList();
        host.ModListProvider = () => new ModManifest([.. files.Select(f => new ModManifestEntry(Path.GetFileName(f.Path), f.Sha1, 5_000))]);
        host.ModFileProvider = sha => files.FirstOrDefault(f => f.Sha1 == sha).Path;

        var received = new List<(string Type, string? Mod)>();
        g.PrivateReceived += frame =>
        {
            var plain = PrivateSeal.Open(_guest, frame.FromPublicKey, frame.Sealed);
            if (System.Text.Json.Nodes.JsonNode.Parse(plain!) is not System.Text.Json.Nodes.JsonObject body) return;
            lock (received) received.Add((body["t"]!.GetValue<string>(), body["mod"]?.GetValue<string>() ?? body["sha1"]?.GetValue<string>()));
        };
        async Task Ask(string sha1) => await g.SendPrivateAsync(Fp(_host),
            PrivateSeal.Seal(_guest, _host.PublicKey, System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject { ["t"] = "modfile?", ["sha1"] = sha1 }.ToJsonString())), default);

        for (var round = 0; round < 10; round++)
            foreach (var f in files) await Ask(f.Sha1);
        await Task.Delay(1000);
        lock (received)
        {
            var offers = received.Where(r => r.Type == "offer").ToList();
            Assert.Equal(PrivateMessenger.MaxModSendsPerPeer, offers.Count);
            Assert.Equal(offers.Count, offers.Select(o => o.Mod).Distinct().Count());
            Assert.Contains(received, r => r.Type == "modfileno");
        }
    }

    [Fact]
    public void DamagedListIsRejected()
    {
        var pieces = PrivateMessenger.Pack(Big(5));
        Assert.Null(PrivateMessenger.Unpack(["AAAA" + pieces[0][4..]]));
        Assert.Null(PrivateMessenger.Unpack(["not base64!"]));
        // A gzip bomb (lots of zeros) stops at the size limit.
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Optimal, true)) gzip.Write(new byte[8 * 1024 * 1024]);
        Assert.Null(PrivateMessenger.Unpack([Convert.ToBase64String(buffer.ToArray())]));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
