using System.Security.Cryptography;
using Amber.Server;

namespace Amber.Core.Tests;

public sealed class EasyTierBinaryCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-etbin-" + Guid.NewGuid().ToString("N"));
    private readonly string _core;
    private readonly string _sha;

    public EasyTierBinaryCheckTests()
    {
        Directory.CreateDirectory(_dir);
        _core = Path.Combine(_dir, "easytier-core");
        File.WriteAllText(_core, "pretend binary");
        _sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_core)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void WithoutAPinItRunsButLogsTheHash()
    {
        var log = new List<string>();
        Program.CheckEasyTierBinary(_core, null, log.Add);
        Assert.Contains(log, l => l.Contains(_sha));
    }

    [Fact]
    public void MatchingPinPasses()
    {
        var log = new List<string>();
        Program.CheckEasyTierBinary(_core, _sha.ToUpperInvariant(), log.Add);
        Assert.Single(log);
    }

    [Fact]
    public void WrongPinRefusesToStart()
    {
        Assert.Throws<InvalidDataException>(() => Program.CheckEasyTierBinary(_core, new string('0', 64), _ => { }));
    }
}
