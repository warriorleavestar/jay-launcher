using Amber.Core.Game;
using Amber.Core.Auth;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

public sealed class LegacyAndroidWorldsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task EmbeddedGameUsesSelectedMicrosoftSessionInstanceAndHeap()
    {
        var dir = new GameDirectory(_root);
        const string id = "test";
        Directory.CreateDirectory(dir.VersionDir(id));
        File.WriteAllText(dir.VersionJsonPath(id), """
            {"id":"test","mainClass":"example.Main","libraries":[],"arguments":{"game":["${auth_uuid}","${auth_access_token}","${user_type}","${auth_xuid}","${clientid}","${game_directory}"],"jvm":["-cp","${classpath}"]}}
            """);
        using var http = new HttpClient(new FakeHttp());
        var accounts = new AccountService(http, new AccountStore(Path.Combine(_root, "accounts.json")), _root, () => "test-client");
        var account = new Account { Name = "Player", Kind = AccountKind.Microsoft, Uuid = Guid.NewGuid(),
            AccessToken = "test-session", AccessTokenExpires = DateTimeOffset.UtcNow.AddHours(1), Xuid = "123" };
        var resolved = await accounts.ResolveAsync(account);
        var settings = new InstanceSettings { Isolate = true, AutoMemory = false, MaxMemoryMb = 1536 };
        var options = settings.ToLaunchOptions(dir, id, "java", resolved.Session, resolved.ExtraJvmArgs);
        var invocation = new GameLauncher(dir).BuildEmbeddedInvocation(options);
        Assert.Equal([account.Uuid.ToString("N"), "test-session", "msa", "123", "test-client", dir.VersionDir(id)],
            invocation.Arguments);
        Assert.Contains("-Xmx1536m", invocation.VmOptions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpgradePreservesOriginalsAndKeepsIsolatedCopiesIndependent(bool hasHistory)
    {
        var game = new GameDirectory(Path.Combine(_root, "game"));
        foreach (var id in new[] { "played", "other", "shared" })
        {
            Directory.CreateDirectory(game.VersionDir(id));
            File.WriteAllText(game.VersionJsonPath(id), "{}");
        }
        var shared = Directory.CreateDirectory(Path.Combine(game.Root, "saves", "world"));
        File.WriteAllText(Path.Combine(shared.FullName, "level.dat_old"), "legacy");
        var existing = Directory.CreateDirectory(Path.Combine(game.VersionDir("played"), "saves", "world"));
        File.WriteAllText(Path.Combine(existing.FullName, "level.dat"), "existing");
        void Upgrade() => LegacyAndroidWorlds.Preserve(game, _root, hasHistory ? ["played", "shared"] : [],
            id => id != "shared", CancellationToken.None);
        Upgrade();
        var imported = Path.Combine(existing.Parent!.FullName, "world (legacy 1)", "level.dat_old");
        Assert.Equal("legacy", File.ReadAllText(imported));
        File.WriteAllText(imported, "next session");
        Upgrade();
        Assert.Equal("next session", File.ReadAllText(imported));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(shared.FullName, "level.dat_old")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(existing.FullName, "level.dat")));
        Assert.Equal(!hasHistory, Directory.Exists(Path.Combine(game.VersionDir("other"), "saves", "world")));
        Assert.False(Directory.Exists(Path.Combine(game.VersionDir("shared"), "saves")));
    }
}
