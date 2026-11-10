using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;
using Amber.NetworkServer;
using Amber.NetworkServer.Hardware;
using Amber.Server;

namespace Amber.Core.Tests;

/// <summary>
/// Node hardware binding (docs/security/node-hardware-binding.md). Everything runs on fake hardware in a temporary
/// node folder; nothing outside it is ever touched.
/// </summary>
public sealed class NodeHardwareBindingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("jay-hw-test-").FullName;
    private readonly string _dir;
    private readonly NetworkSigner _rootKey = NetworkSigner.Generate(out _);

    public NodeHardwareBindingTests()
    {
        _dir = Path.Combine(_root, "node");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class FakeHardware(params (string Kind, string Value)[] factors) : IHardwareSource
    {
        public List<HardwareFactor> Factors { get; } = [.. factors.Select(f => new HardwareFactor(f.Kind, f.Value))];
        public IReadOnlyList<HardwareFactor> Read() => Factors;
        public void Change(string kind, string value) { Factors.RemoveAll(f => f.Kind == kind); Factors.Add(new HardwareFactor(kind, value)); }
    }

    /// <summary>A pretend TPM: seals with a per-instance secret, so a "different TPM" can't unseal; <see cref="BootChanged"/> refuses like a PCR change.</summary>
    private sealed class FakeTpm : ITpmSealer
    {
        private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
        public bool BootChanged { get; set; }
        public bool Available => true;
        public bool BindsBootState => true;
        public byte[] Seal(byte[] secret) => [.. secret.Zip(_secret, (a, b) => (byte)(a ^ b))];
        public byte[] Unseal(byte[] blob) => BootChanged ? throw new TpmUnsealException("PCR 7 changed") : [.. blob.Zip(_secret, (a, b) => (byte)(a ^ b))];
    }

    private sealed class FakeIntegrity : IRuntimeIntegrity
    {
        public List<string> Issues { get; } = [];
        public IReadOnlyList<string> Problems(string dataDir, bool requireSecureBoot) => Issues;
    }

    private static FakeHardware Machine() => new(("tpm", "EK-1234"), ("board", "UUID-AAAA"), ("disk", "SN-DISK-1"), ("machine", "mid-42"));

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);
    private bool Has(string name) => File.Exists(Path.Combine(_dir, name)) || Directory.Exists(Path.Combine(_dir, name));

    /// <summary>A node with a directory key, a login key and some state, bound to <paramref name="hw"/>.</summary>
    private (byte[] DirectoryKey, string LoginKey) Bind(FakeHardware hw, ITpmSealer tpm, int? need = null, bool? requireTpm = null)
    {
        using var dir = DirectoryChannel.LoadOrCreateIdentity(Path.Combine(_dir, "directory.key"));
        LoginAuthority.LoadOrCreateKeys(Path.Combine(_dir, "login.key"));
        var login = File.ReadAllText(Path.Combine(_dir, "login.key"));
        Write("accounts.json", "{}");
        Write("bans.json", "[]");
        Write("admin.token", "secret-token");
        Directory.CreateDirectory(Path.Combine(_dir, "public-node", "state"));
        Write(Path.Combine("public-node", "state", "et.toml"), "x");
        var request = HardwareBinding.CreateRequest(_dir, hw, tpm);
        var cert = HardwareCertificate.Issue(_rootKey, request, "jay-net-test", need, requireTpm);
        HardwareBinding.Install(_dir, cert, hw, tpm, [_rootKey.PublicKey]);
        return (dir.PublicKey, login);
    }

    private NodeHardwareGuard Guard(FakeHardware hw, ITpmSealer tpm, FakeIntegrity? integrity = null, bool dryRun = false) =>
        new(_dir) { Hardware = hw, Tpm = tpm, Integrity = integrity ?? new FakeIntegrity(), TrustedRoots = [_rootKey.PublicKey], DryRun = dryRun };

    [Fact]
    public void RequestCarriesOnlySaltedHashes()
    {
        var hw = Machine();
        var request = HardwareBinding.CreateRequest(_dir, hw, new FakeTpm());
        var text = request.ToJsonString();
        foreach (var f in hw.Factors) Assert.DoesNotContain(f.Value, text);
        Assert.Equal(4, ((JsonObject)request["f"]!).Count);
        Assert.True(Has(NodeWiper.Marker));
    }

    [Fact]
    public void BoundNodeOpensItsKeysAndPlaintextIsGone()
    {
        var hw = Machine();
        var tpm = new FakeTpm();
        var (dirKey, login) = Bind(hw, tpm);
        Assert.False(Has("directory.key"));
        Assert.False(Has("login.key"));
        Assert.True(Has("directory.key.hw"));

        var keys = Guard(hw, tpm).Enforce()!;
        using var pair = X25519KeyPair.FromPrivateKey(Convert.FromBase64String(Encoding.ASCII.GetString(keys["directory.key"]).Trim()));
        Assert.Equal(dirKey, pair.PublicKey);
        Assert.Equal(login, Encoding.ASCII.GetString(keys["login.key"]));
    }

    [Fact]
    public void UnboundNodeIsLeftAlone() => Assert.Null(Guard(Machine(), new FakeTpm()).Enforce());

    [Fact]
    public void OneChangedPartIsTolerated()
    {
        var hw = Machine();
        var tpm = new FakeTpm();
        Bind(hw, tpm);
        hw.Change("disk", "SN-NEW-DISK");
        Assert.NotNull(Guard(hw, tpm).Enforce());
        Assert.True(Has("directory.key.hw"));
    }

    [Fact]
    public void TwoChangedPartsWipeTheNode()
    {
        var hw = Machine();
        var tpm = new FakeTpm();
        Bind(hw, tpm);
        Write("unrelated.txt", "keep me");
        hw.Change("disk", "SN-NEW-DISK");
        hw.Change("board", "UUID-BBBB");

        var e = Assert.Throws<HardwareMismatchException>(() => Guard(hw, tpm).Enforce());
        Assert.NotEmpty(e.Wiped);
        foreach (var name in NodeWiper.Files) Assert.False(Has(name), name);
        Assert.False(Has("public-node"));
        Assert.True(Has("unrelated.txt"));
        Assert.True(Has(NodeWiper.Marker));
        Assert.Contains("identifiers match", File.ReadAllText(Path.Combine(_dir, NodeWiper.LogFile)));

        // Back on the right hardware, the wiped node still refuses to come up as a fresh, unbound node.
        Assert.Throws<HardwareMismatchException>(() => Guard(Machine(), tpm).Enforce());
        Assert.False(Has("directory.key"));
    }

    [Fact]
    public void ADifferentTpmWipesEvenIfEverythingElseMatches()
    {
        var hw = Machine();
        var tpm = new FakeTpm();
        Bind(hw, tpm);
        hw.Change("tpm", "EK-OTHER");
        Assert.Throws<HardwareMismatchException>(() => Guard(hw, new FakeTpm()).Enforce());
        Assert.False(Has("directory.key.hw"));
    }

    [Fact]
    public void CopiedFilesOnAnotherMachineAreWiped()
    {
        Bind(Machine(), new FakeTpm());
        var other = new FakeHardware(("tpm", "EK-9999"), ("board", "UUID-ZZZZ"), ("disk", "SN-X"), ("machine", "mid-42"));
        Assert.Throws<HardwareMismatchException>(() => Guard(other, new FakeTpm()).Enforce());
        Assert.False(Has("login.key.hw"));
    }

    [Fact]
    public void DryRunListsButDeletesNothing()
    {
        var hw = Machine();
        var tpm = new FakeTpm();
        Bind(hw, tpm);
        hw.Change("tpm", "EK-OTHER");
        var e = Assert.Throws<HardwareMismatchException>(() => Guard(hw, tpm, dryRun: true).Enforce());
        Assert.True(e.DryRun);
        Assert.Contains(e.Wiped, p => p.EndsWith("directory.key.hw", StringComparison.Ordinal));
        Assert.True(Has("directory.key.hw"));
        Assert.True(Has("public-node"));
        Assert.False(Has(NodeWiper.LogFile));
    }

    [Fact]
    public void TamperedRuntimeRefusesWithoutWiping()
    {
        var hw = Machine();
        var tpm = new FakeTpm();
        Bind(hw, tpm);
        var integrity = new FakeIntegrity();
        integrity.Issues.Add("a debugger or tracer is attached to this process");
        Assert.Throws<NodeIntegrityException>(() => Guard(hw, tpm, integrity).Enforce());
        Assert.True(Has("directory.key.hw"));
    }

    [Fact]
    public void ChangedBootStateRefusesWithoutWiping()
    {
        var hw = Machine();
        var tpm = new FakeTpm();
        Bind(hw, tpm);
        tpm.BootChanged = true;
        Assert.Throws<NodeIntegrityException>(() => Guard(hw, tpm).Enforce());
        Assert.True(Has("directory.key.hw"));
    }

    [Fact]
    public void DeletedOrForgedCertificateWipes()
    {
        var hw = Machine();
        var tpm = new FakeTpm();
        Bind(hw, tpm);
        File.Delete(Path.Combine(_dir, HardwareBinding.CertificateFile));
        Assert.Throws<HardwareMismatchException>(() => Guard(hw, tpm).Enforce());
        Assert.False(Has("directory.key.hw"));
    }

    [Fact]
    public void CertificateFromAnotherRootWipes()
    {
        var hw = Machine();
        var tpm = new FakeTpm();
        Bind(hw, tpm);
        var guard = new NodeHardwareGuard(_dir) { Hardware = hw, Tpm = tpm, Integrity = new FakeIntegrity(), TrustedRoots = [NetworkSigner.Generate(out _).PublicKey] };
        Assert.Throws<HardwareMismatchException>(guard.Enforce);
    }

    [Fact]
    public void WithoutTpmTheIdentifiersAloneOpenTheKeys()
    {
        var hw = new FakeHardware(("board", "UUID-AAAA"), ("disk", "SN-1"), ("machine", "mid-42"));
        Bind(hw, NoTpm.Instance);
        Assert.NotNull(Guard(hw, NoTpm.Instance).Enforce());
        hw.Change("machine", "mid-new");
        Assert.NotNull(Guard(hw, NoTpm.Instance).Enforce());
        hw.Change("disk", "SN-2");
        Assert.Throws<HardwareMismatchException>(() => Guard(hw, NoTpm.Instance).Enforce());
    }

    [Fact]
    public void InstallRefusesACertificateForOtherHardware()
    {
        var hw = Machine();
        var request = HardwareBinding.CreateRequest(_dir, hw, new FakeTpm());
        var cert = HardwareCertificate.Issue(_rootKey, request, null);
        hw.Change("board", "UUID-OTHER");
        Assert.Throws<InvalidDataException>(() => HardwareBinding.Install(_dir, cert, hw, new FakeTpm(), []));
        Assert.True(Has("directory.key"));
    }

    [Fact]
    public void CertificateSignatureCoversTheRules()
    {
        var request = HardwareBinding.CreateRequest(_dir, Machine(), new FakeTpm());
        var cert = HardwareCertificate.Issue(_rootKey, request, null, need: 3);
        var parsed = HardwareCertificate.Parse(cert)!;
        Assert.Equal(3, parsed.Need);
        Assert.Equal(["tpm"], parsed.Must);
        var wire = (JsonObject)JsonNode.Parse(cert)!;
        var content = (JsonObject)JsonNode.Parse(Convert.FromBase64String(wire["c"]!.GetValue<string>()))!;
        content["need"] = 1;
        wire["c"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content.ToJsonString()));
        Assert.Null(HardwareCertificate.Parse(Encoding.UTF8.GetBytes(wire.ToJsonString())));
    }

    [Fact]
    public void WipeRefusesUnmarkedAndUnsafeFolders()
    {
        Write("directory.key", "x");
        Assert.Throws<InvalidOperationException>(() => NodeWiper.Wipe(_dir, "test", false));
        Assert.True(Has("directory.key"));
        Assert.Throws<InvalidOperationException>(() => NodeWiper.Wipe(Path.GetPathRoot(_dir)!, "test", true));
        Assert.Throws<InvalidOperationException>(() => NodeWiper.Wipe(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "test", true));
    }

    [Fact]
    public void WipeRemovesLinksWithoutFollowingThem()
    {
        if (OperatingSystem.IsWindows()) return; // symlinks need privileges there
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");
        NodeWiper.Mark(_dir);
        File.CreateSymbolicLink(Path.Combine(_dir, "accounts.json"), Path.Combine(outside, "precious.txt"));
        Directory.CreateSymbolicLink(Path.Combine(_dir, "public-node"), outside);
        NodeWiper.Wipe(_dir, "test", false);
        Assert.False(Has("accounts.json"));
        Assert.False(Has("public-node"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public async Task NetworkServerRunsOnTheSealedKeysAndWipesOnOtherHardware()
    {
        var hw = Machine();
        var tpm = new FakeTpm();
        var (dirKey, _) = Bind(hw, tpm);
        JayNetworkServerBuilder Builder(FakeHardware h) => new JayNetworkServerBuilder().UseDataDir(_dir)
            .WithoutEasyTier(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)).WithoutAdmin().WithoutBanList().UseAccounts()
            .UseHardwareGuard(Guard(h, tpm));

        await using (var server = Builder(hw).Build())
        {
            await server.StartAsync();
            Assert.Equal(Convert.ToBase64String(dirKey), server.DirectoryKey);
            Assert.False(Has("directory.key"));
        }

        var other = Machine();
        other.Change("tpm", "EK-OTHER");
        Assert.Throws<HardwareMismatchException>(() => Builder(other).Build());
        Assert.False(Has("directory.key.hw"));
    }

    [Fact]
    public void SubsetsAreAllCombinations()
    {
        var all = HardwareBinding.Subsets(["a", "b", "c", "d"], 3).Select(s => string.Join("", s)).ToArray();
        Assert.Equal(["abc", "abd", "acd", "bcd"], all);
    }

    [Fact]
    public void CliBindsOfflineInThreeSteps()
    {
        var hw = Machine();
        var tpm = new FakeTpm();
        var output = new StringWriter();
        var rootFile = Path.Combine(_root, "root.key");
        Assert.Equal(0, Program.NetworkCommand(Options.Parse(["network", "root-key", "--out", rootFile]), output));

        Assert.Equal(0, Program.NodeBind(_dir, Options.Parse(["node", "bind"]), output, hw, tpm));
        Assert.True(Has(HardwareBinding.RequestFile));
        var certFile = Path.Combine(_root, "hardware.cert");
        Assert.Equal(0, Program.NetworkCommand(Options.Parse(["network", "hw-cert", "--root", rootFile, "--request", Path.Combine(_dir, HardwareBinding.RequestFile), "--out", certFile]), output));
        Assert.Equal(0, Program.NodeBind(_dir, Options.Parse(["node", "bind", "--install", certFile]), output, hw, tpm));
        Assert.False(Has("directory.key"));
        Assert.False(Has(HardwareBinding.RequestFile));

        // dir-key reads the public key from the certificate and never makes a new plaintext key.
        Assert.Equal(0, Program.NetworkCommand(Options.Parse(["network", "dir-key", "--dir", _dir]), output, _dir));
        Assert.False(Has("directory.key"));
        Program.NodeBind(_dir, Options.Parse(["node", "bind", "--check"]), output, hw, tpm); // exit code also reflects this test host's runtime
        Assert.Contains("硬件对得上", output.ToString());
        hw.Change("tpm", "EK-OTHER");
        Assert.Equal(2, Program.NodeBind(_dir, Options.Parse(["node", "bind", "--check"]), output, hw, tpm));
        Assert.True(Has("directory.key.hw")); // --check never wipes
    }
}
