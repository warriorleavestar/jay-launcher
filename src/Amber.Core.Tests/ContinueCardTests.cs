using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>The library's continue card: its button acts on the card's own instance, never on another instance's run.</summary>
public sealed class ContinueCardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-continue-card-" + Guid.NewGuid().ToString("N"));

    public ContinueCardTests()
    {
        foreach (var id in new[] { "a", "b" })
        {
            var dir = Path.Combine(_root, "versions", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".json"), "{\"id\":\"" + id + "\",\"mainClass\":\"Example\",\"libraries\":[]}");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void AnotherInstancesRunDoesNotTakeOverTheButton() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings
        {
            GameDirectory = _root,
            SavePathForTest = Path.Combine(_root, "settings.json"),
            PlayerName = "",
            AutoJavaDownload = false,
        });
        service.Accounts.AddOffline("Steve");
        var home = new HomeViewModel(service, new LogViewModel());
        Dispatcher.UIThread.RunJobs();
        var runA = home.AddPreparingRunForTest("a");

        // A is preparing: the engine's own button would cancel it, but B's label is B's start label.
        Assert.Equal(home.PrimaryLabelFor("a"), home.PrimaryLabel);
        Assert.NotEqual(home.PrimaryLabelFor("a"), home.PrimaryLabelFor("b"));
        Assert.Equal(L.T("home.launch"), home.PrimaryLabelFor("b"));

        // Pressing B's button never cancels A (it starts B, which asks about running two games).
        _ = home.PrimaryForAsync("b");
        Dispatcher.UIThread.RunJobs();
        Assert.False(HomeViewModel.CancelledForTest(runA));

        // The library's continue card showing B: its label and button are B's.
        service.SetCurrentInstance("b");
        var library = new LibraryViewModel(service, home, new ActivityViewModel(service));
        library.Refresh();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((!library.IsLoaded || library.Continue?.Id != "b") && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.Equal("b", library.Continue?.Id);
        Assert.Equal(L.T("home.launch"), library.ContinueLabel);
        library.PlayContinueCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(HomeViewModel.CancelledForTest(runA));

        // A's own button cancels A.
        _ = home.PrimaryForAsync("a");
        Assert.True(HomeViewModel.CancelledForTest(runA));
    });
}
