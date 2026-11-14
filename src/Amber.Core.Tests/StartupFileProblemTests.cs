using Amber.App.Services;
using Amber.Core.Auth;

namespace Amber.Core.Tests;

/// <summary>Jay's own settings and accounts files that are damaged or locked at startup: no crash, nothing lost, and said.</summary>
public sealed class StartupFileProblemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-startup-files-" + Guid.NewGuid().ToString("N"));

    public StartupFileProblemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void ALockedAccountsFileIsNeitherReadAsEmptyNorWrittenOver()
    {
        var path = Path.Combine(_root, "accounts.json");
        var first = new AccountStore(path);
        first.AddOrUpdate(new Account { Kind = AccountKind.Offline, Name = "Alex", Uuid = Guid.NewGuid() });
        var before = File.ReadAllText(path);

        var store = new AccountStore(path) { RetryDelay = TimeSpan.Zero };
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store.Load();
            Assert.True(store.Unreadable);
            Assert.Empty(store.Accounts);
            Assert.Throws<IOException>(() => store.AddOrUpdate(new Account { Kind = AccountKind.Offline, Name = "Steve", Uuid = Guid.NewGuid() }));
            Assert.Throws<IOException>(store.Save);
        }
        Assert.Equal(before, File.ReadAllText(path));

        // Once the file can be read again, a change reads it first and keeps what was in it.
        store.AddOrUpdate(new Account { Kind = AccountKind.Offline, Name = "Steve", Uuid = Guid.NewGuid() });
        Assert.False(store.Unreadable);
        Assert.Equal(["Alex", "Steve"], store.Accounts.Select(a => a.Name));
    }

    [Fact]
    public void ADamagedAccountsFileIsKeptAndTheListStartsEmpty()
    {
        var path = Path.Combine(_root, "accounts.json");
        File.WriteAllText(path, "{ not json");
        var store = new AccountStore(path);
        store.Load();
        Assert.False(store.Unreadable);
        Assert.Empty(store.Accounts);
        Assert.True(File.Exists(path + ".broken"));
    }

    [Fact]
    public void ADamagedSettingsFileKeepsADatedCopyAndSaysSo()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{ \"GameDirectory\": ");
        var settings = AppSettings.Load(path, File.ReadAllText);
        Assert.Equal(SettingsLoadProblem.Corrupt, settings.LoadProblem);
        Assert.False(settings.ReadOnly);
        Assert.NotNull(settings.BrokenCopy);
        Assert.StartsWith(path + ".broken-", settings.BrokenCopy);
        Assert.Equal("{ \"GameDirectory\": ", File.ReadAllText(settings.BrokenCopy!));
        // Defaults standing in for a damaged file are not an old install with a player name to turn into an account.
        Assert.False(LauncherService.MigratesPlayerName(0, settings.PlayerName, settings.IsFresh || settings.LoadProblem is not null, null, null));
    }

    [Fact]
    public void ALockedSettingsFileRunsReadOnlyAndSaysSo()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{}");
        var settings = AppSettings.Load(path, _ => throw new IOException("locked"));
        Assert.Equal(SettingsLoadProblem.Locked, settings.LoadProblem);
        Assert.True(settings.ReadOnly);
        Assert.Null(settings.BrokenCopy);

        var fine = AppSettings.Load(path, File.ReadAllText);
        Assert.Null(fine.LoadProblem);
    }
}

/// <summary>A settings file that cannot be written: ordinary commands apply for this run and say so, never crash.</summary>
public sealed class SettingsSaveFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-save-failure-" + Guid.NewGuid().ToString("N"));

    public SettingsSaveFailureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void UnwritableSettingsDoNotCrashCommands() => UiTestThread.Run(() =>
    {
        // The settings path is taken by a folder, so every write of the file fails.
        var blocked = Path.Combine(_root, "settings.json");
        Directory.CreateDirectory(blocked);
        using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "", SavePathForTest = blocked });
        var failures = 0;
        service.SettingsSaveFailed += _ => failures++;
        Assert.False(service.TrySaveSettings());
        Assert.Equal(1, failures);

        var settings = new Amber.App.ViewModels.SettingsViewModel(service);
        settings.ChildMode = !settings.ChildMode;
        settings.LaunchWithoutSandbox = !settings.LaunchWithoutSandbox;
        Assert.True(failures >= 3);
        // Applied for this run all the same.
        Assert.Equal(settings.LaunchWithoutSandbox, service.Settings.LaunchWithoutSandbox);
    });
}
