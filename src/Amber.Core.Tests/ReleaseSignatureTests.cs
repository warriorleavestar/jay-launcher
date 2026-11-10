using System.Text;
using Amber.Core.Launch;
using Amber.Core.Multiplayer.Public;
using Amber.Server;

namespace Amber.Core.Tests;

public sealed class ReleaseSignatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-relsig-" + Guid.NewGuid().ToString("N"));
    private readonly NetworkSigner _key = NetworkSigner.Generate(out _);
    private string Pub => Convert.ToBase64String(_key.PublicKey);

    public ReleaseSignatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Release(string tag = "v0.9.0")
    {
        var zip = Path.Combine(_dir, "Jay-win-x64.zip");
        File.WriteAllText(zip, "pretend package");
        File.WriteAllText(Path.Combine(_dir, "Jay-linux-x64.zip"), "other package");
        var sums = ReleaseSignature.BuildSums(Directory.GetFiles(_dir));
        File.WriteAllText(Path.Combine(_dir, ReleaseSignature.SumsFileName), sums);
        File.WriteAllText(Path.Combine(_dir, ReleaseSignature.SignatureFileName), ReleaseSignature.Sign(_key, tag, Encoding.UTF8.GetBytes(sums)));
        return zip;
    }

    [Fact]
    public async Task GenuinePackageVerifies()
    {
        var zip = Release();
        var check = await ReleaseSignature.VerifyPackageAsync(zip, "0.9.0", trustedKeys: [Pub]);
        Assert.Equal(ReleaseVerdict.Valid, check.Verdict);
        Assert.Equal("v0.9.0", check.Tag);
    }

    [Fact]
    public async Task SwappedPackageFails()
    {
        var zip = Release();
        File.WriteAllText(zip, "tampered package");
        Assert.Equal(ReleaseVerdict.HashMismatch, (await ReleaseSignature.VerifyPackageAsync(zip, "v0.9.0", trustedKeys: [Pub])).Verdict);
    }

    [Fact]
    public async Task EditedSumsFail()
    {
        var zip = Release();
        File.WriteAllText(zip, "tampered package");
        // An attacker who swaps the package and rewrites the sums still can't produce a signature.
        var sumsPath = Path.Combine(_dir, ReleaseSignature.SumsFileName);
        File.WriteAllText(sumsPath, ReleaseSignature.BuildSums(Directory.GetFiles(_dir)));
        Assert.Equal(ReleaseVerdict.BadSignature, (await ReleaseSignature.VerifyPackageAsync(zip, "v0.9.0", trustedKeys: [Pub])).Verdict);
    }

    [Fact]
    public async Task SignatureFromAnotherKeyIsRejected()
    {
        var zip = Release();
        var other = Convert.ToBase64String(NetworkSigner.Generate(out _).PublicKey);
        Assert.Equal(ReleaseVerdict.UnknownKey, (await ReleaseSignature.VerifyPackageAsync(zip, "v0.9.0", trustedKeys: [other])).Verdict);
    }

    [Fact]
    public async Task OldReleaseCannotPassAsNewOne()
    {
        var zip = Release("v0.8.0");
        var check = await ReleaseSignature.VerifyPackageAsync(zip, "v0.9.0", trustedKeys: [Pub]);
        Assert.Equal(ReleaseVerdict.WrongTag, check.Verdict);
        Assert.Equal("v0.8.0", check.Tag);
    }

    [Fact]
    public async Task MissingFilesAndNoKeysAreReported()
    {
        var zip = Path.Combine(_dir, "Jay-win-x64.zip");
        File.WriteAllText(zip, "pretend package");
        Assert.Equal(ReleaseVerdict.Unsigned, (await ReleaseSignature.VerifyPackageAsync(zip, null, trustedKeys: [Pub])).Verdict);
        Release();
        Assert.Equal(ReleaseVerdict.NoTrustedKey, (await ReleaseSignature.VerifyPackageAsync(zip, null, trustedKeys: [])).Verdict);
    }

    [Fact]
    public async Task UnlistedFileFails()
    {
        Release();
        var extra = Path.Combine(_dir, "Jay-osx-arm64.zip");
        File.WriteAllText(extra, "not in the list");
        Assert.Equal(ReleaseVerdict.NotListed, (await ReleaseSignature.VerifyPackageAsync(extra, "v0.9.0", trustedKeys: [Pub])).Verdict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("jay-release-signature-v1\ntag: v1\n")]
    [InlineData("jay-release-signature-v2\ntag: v1\nkey: AAAA\nsig: AAAA\n")]
    [InlineData("jay-release-signature-v1\ntag: v1\nkey: not base64!\nsig: AAAA\n")]
    public void MalformedSignatureFiles(string text)
    {
        Assert.Equal(ReleaseVerdict.Malformed, ReleaseSignature.Verify(text, "x"u8, null, [Pub]).Verdict);
    }

    [Fact]
    public void DuplicateKeyLinesAreMalformed()
    {
        var good = ReleaseSignature.Sign(_key, "v1.0.0", "x"u8);
        var other = NetworkSigner.Generate(out _);
        var doubled = good + "key: " + Convert.ToBase64String(other.PublicKey) + "\n";
        Assert.Equal(ReleaseVerdict.Malformed, ReleaseSignature.Verify(doubled, "x"u8, null, [Pub]).Verdict);
    }

    [Fact]
    public void SumsParserIgnoresPathsAndConflicts()
    {
        var h1 = new string('a', 64);
        var h2 = new string('b', 64);
        var text = $"{h1}  good.zip\r\n{h1} *star.zip\n{h1}  ../evil.zip\n{h1}  dir/evil.zip\n{h1}  twice.zip\n{h2}  twice.zip\nnot a line\n";
        var sums = ReleaseSignature.ParseSums(text);
        Assert.Equal(h1, sums["good.zip"]);
        Assert.Equal(h1, sums["star.zip"]);
        Assert.False(sums.ContainsKey("../evil.zip"));
        Assert.False(sums.ContainsKey("dir/evil.zip"));
        Assert.False(sums.ContainsKey("twice.zip"));
    }

    [Fact]
    public void NoReleaseKeyIsBuiltInYet()
    {
        // The maintainer adds the public key from `jay-server release key`; until then nothing verifies.
        foreach (var key in ReleaseSignature.TrustedKeys) Assert.Equal(32, Convert.FromBase64String(key).Length);
    }

    [Fact]
    public void ServerCommandsKeySumsSignVerify()
    {
        var keyPath = Path.Combine(_dir, "release.key");
        var output = new StringWriter();
        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "key", "--out", keyPath]), output));
        var pub = output.ToString().Split('：').Last().Trim();
        Assert.Equal(32, Convert.FromBase64String(pub).Length);
        Assert.Throws<IOException>(() => Program.ReleaseCommand(Options.Parse(["release", "key", "--out", keyPath]), new StringWriter()));

        var pkgDir = Path.Combine(_dir, "pkg");
        Directory.CreateDirectory(pkgDir);
        var zip = Path.Combine(pkgDir, "Jay-win-x64.zip");
        File.WriteAllText(zip, "package");
        var sums = Path.Combine(pkgDir, ReleaseSignature.SumsFileName);
        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "sums", zip, "--out", sums]), new StringWriter()));
        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "sign", "--key", keyPath, "--tag", "0.9.0", "--sums", sums]), new StringWriter()));
        Assert.True(File.Exists(Path.Combine(pkgDir, ReleaseSignature.SignatureFileName)));

        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "verify", zip, "--tag", "v0.9.0", "--pub", pub]), new StringWriter()));
        File.WriteAllText(zip, "swapped");
        Assert.Equal(2, Program.ReleaseCommand(Options.Parse(["release", "verify", zip, "--tag", "v0.9.0", "--pub", pub]), new StringWriter()));
    }
}
