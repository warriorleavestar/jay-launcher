using System.Security.Cryptography;
using System.Text;
using Amber.Core.Launch;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

/// <summary>EasyTier builds from Jay's fork are signed by 2 of the 3 Jay trustees, like Jay releases, but can't be swapped with them.</summary>
public sealed class EasyTierSignatureTests
{
    private static readonly TrustSigner[] T = [.. Enumerable.Range(0, 4).Select(_ => TrustSigner.FromSeed(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))))];
    private static readonly TrustList Own = new([T[0].PublicKey, T[1].PublicKey, T[2].PublicKey], 2);

    private static byte[] EmbeddedSums()
    {
        using var stream = typeof(EasyTierRelease).Assembly.GetManifestResourceStream("easytier-signed/" + ReleaseSignature.SumsFileName)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static ReleaseStatement Statement(byte[] sums, string? component = EasyTierRelease.Component, string tag = EasyTierRelease.ReleaseTag,
        long seq = EasyTierRelease.Seq, string commit = EasyTierRelease.FullCommit) =>
        new(tag, seq, commit, Convert.ToHexStringLower(SHA256.HashData(sums)), Own.Hash, ReleaseStatement.NoLog, component);

    private static string Signed(ReleaseStatement statement, params TrustSigner[] signers) =>
        signers.Aggregate(ReleaseSignature.Create(statement), (json, s) => ReleaseSignature.Cosign(json, s));

    private static ReleaseVerdict Check(string? manifest, byte[]? sums = null, IEnumerable<EasyTierRelease.Asset>? assets = null) =>
        EasyTierRelease.CheckSignature(manifest, sums ?? EmbeddedSums(), Own, assets ?? EasyTierRelease.All).Verdict;

    [Fact]
    public void EmbeddedListIsTheReleaseListAndCoversEveryPinnedPackage()
    {
        var listed = ReleaseSignature.ParseSums(Encoding.UTF8.GetString(EmbeddedSums()));
        Assert.Equal(EasyTierRelease.All.Count, listed.Count);
        foreach (var asset in EasyTierRelease.All) Assert.Equal(asset.Sha256, listed[asset.Name]);
        Assert.StartsWith(EasyTierRelease.Commit, EasyTierRelease.FullCommit, StringComparison.Ordinal);
        Assert.Equal(40, EasyTierRelease.FullCommit.Length);
    }

    /// <summary>The release gate: a Jay build with release keys must carry a valid EasyTier signature, or EasyTier won't install.</summary>
    [Fact]
    public void ABuildWithTrusteeKeysCarriesAValidSignature()
    {
        if (TrustList.Jay.Keys.Count == 0)
        {
            Assert.Equal(ReleaseVerdict.NoTrustedKey, EasyTierRelease.Signature.Verdict);
            Assert.True(EasyTierRelease.SignatureAllowsInstall);
            return;
        }
        Assert.Equal(ReleaseVerdict.Valid, EasyTierRelease.Signature.Verdict);
        Assert.True(EasyTierRelease.SignatureAllowsInstall);
    }

    [Fact]
    public void TwoOfThreeTrusteesPass()
    {
        Assert.Equal(ReleaseVerdict.Valid, Check(Signed(Statement(EmbeddedSums()), T[0], T[2])));
        Assert.Equal(ReleaseVerdict.Valid, Check(Signed(Statement(EmbeddedSums()), T[1], T[0], T[2])));
    }

    [Fact]
    public void OneTrusteeOrOutsidersAreNotEnough()
    {
        Assert.Equal(ReleaseVerdict.NotEnoughSignatures, Check(Signed(Statement(EmbeddedSums()), T[1])));
        Assert.Equal(ReleaseVerdict.NotEnoughSignatures, Check(Signed(Statement(EmbeddedSums()), T[1], T[3])));
        Assert.Equal(ReleaseVerdict.Unsigned, Check(null));
    }

    [Fact]
    public void JayAndEasyTierSignaturesCannotBeSwapped()
    {
        var sums = EmbeddedSums();
        // A Jay release signature over the same file list is not an EasyTier signature...
        var jay = Signed(new ReleaseStatement("v0.9.0", 1, EasyTierRelease.FullCommit, Convert.ToHexStringLower(SHA256.HashData(sums)), Own.Hash, ReleaseStatement.NoLog), T[0], T[1]);
        Assert.Equal(ReleaseVerdict.WrongComponent, Check(jay));
        // ...and an EasyTier signature never passes as a Jay release.
        var easytier = Signed(Statement(sums), T[0], T[1]);
        Assert.Equal(ReleaseVerdict.WrongComponent, ReleaseSignature.Verify(easytier, sums, null, null, Own).Verdict);
        // The component name is part of the signed bytes, so editing it out of the JSON breaks every signature.
        var stripped = Signed(Statement(sums, tag: "v2.6.4"), T[0], T[1]).Replace("\"component\": \"easytier\",", "", StringComparison.Ordinal);
        Assert.DoesNotContain("component", stripped, StringComparison.Ordinal);
        Assert.Equal(ReleaseVerdict.NotEnoughSignatures, ReleaseSignature.Verify(stripped, sums, null, null, Own).Verdict);
        Assert.NotEqual(Convert.ToHexString(Statement(sums).Bytes()), Convert.ToHexString((Statement(sums) with { Component = null }).Bytes()));
    }

    [Fact]
    public void OnlyThisExactBuildPasses()
    {
        var sums = EmbeddedSums();
        Assert.Equal(ReleaseVerdict.WrongTag, Check(Signed(Statement(sums, tag: EasyTierRelease.ReleaseTag + "0"), T[0], T[1])));
        Assert.Equal(ReleaseVerdict.WrongTag, Check(Signed(Statement(sums, seq: EasyTierRelease.Seq + 1), T[0], T[1])));
        Assert.Equal(ReleaseVerdict.WrongTag, Check(Signed(Statement(sums, commit: new string('c', 40)), T[0], T[1])));
        // The signature is for a different SHA256SUMS.txt.
        var other = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(sums).Replace("98dbf4c9", "08dbf4c9", StringComparison.Ordinal));
        Assert.Equal(ReleaseVerdict.SumsMismatch, Check(Signed(Statement(other), T[0], T[1])));
    }

    [Fact]
    public void APinnedHashOutsideTheSignedListIsRefused()
    {
        var signed = Signed(Statement(EmbeddedSums()), T[0], T[1]);
        var pinned = EasyTierRelease.All;
        Assert.Equal(ReleaseVerdict.HashMismatch, Check(signed, assets: [pinned[0] with { Sha256 = new string('0', 64) }, .. pinned.Skip(1)]));
        Assert.Equal(ReleaseVerdict.NotListed, Check(signed, assets: [.. pinned, pinned[0] with { Name = "easytier-other.zip" }]));
    }

    [Fact]
    public void UnknownComponentsAreRejected()
    {
        Assert.False(Statement(EmbeddedSums(), component: "other").IsValid);
        Assert.True(Statement(EmbeddedSums()).IsValid);
        Assert.Null(ReleaseManifest.Parse(Signed(Statement(EmbeddedSums())).Replace("\"easytier\"", "\"other\"", StringComparison.Ordinal)));
    }
}
