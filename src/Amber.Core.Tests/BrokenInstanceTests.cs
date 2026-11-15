using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Game;
using Amber.Core.GameLibrary;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>An instance whose version files cannot be read: shown as broken, never a crash, and fixable from its own page.</summary>
public sealed class BrokenInstanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-broken-instance-" + Guid.NewGuid().ToString("N"));

    public BrokenInstanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private LauncherService NewService() => new(new AppSettings
    {
        GameDirectory = _root,
        SavePathForTest = Path.Combine(_root, "settings.json"),
        PlayerName = "",
        AutoJavaDownload = false,
    });

    /// <summary>A version folder whose JSON is a folder: opening it is denied (not an IO or JSON error).</summary>
    private void DeniedInstance(string id) => Directory.CreateDirectory(Path.Combine(_root, "versions", id, id + ".json"));

    private void CorruptInstance(string id)
    {
        var dir = Path.Combine(_root, "versions", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), "{ not json");
    }

    [Fact]
    public void UnreadableMeansWhatStateOfCallsBroken()
    {
        Assert.True(GameDirectory.IsUnreadable(new IOException()));
        Assert.True(GameDirectory.IsUnreadable(new UnauthorizedAccessException()));
        Assert.True(GameDirectory.IsUnreadable(new System.Text.Json.JsonException()));
        Assert.True(GameDirectory.IsUnreadable(new InvalidDataException()));
        Assert.True(GameDirectory.IsUnreadable(new NotSupportedException()));
        Assert.False(GameDirectory.IsUnreadable(new InvalidOperationException()));
        Assert.False(GameDirectory.IsUnreadable(new NullReferenceException()));
    }

    [Fact]
    public void SelectingAnInstanceThatCannotBeOpenedDoesNotThrow() => UiTestThread.Run(() =>
    {
        DeniedInstance("a");
        using var service = NewService();
        Assert.Equal(InstanceState.Broken, service.Directory.StateOf("a"));
        var home = new HomeViewModel(service, new LogViewModel());
        var vm = new InstanceViewModel(service);
        vm.AttachHome(home);
        vm.SelectedId = "a";
        home.SelectedVersion = "a";
        Assert.Null(vm.Current);
        Assert.NotEqual("", vm.InfoText);
        Assert.NotEqual("", home.VersionInfo);
    });

    [Fact]
    public void ABrokenInstanceStillShowsItsHeroWithRepair() => UiTestThread.Run(() =>
    {
        CorruptInstance("broken");
        using var service = NewService();
        var home = new HomeViewModel(service, new LogViewModel());
        var vm = new InstanceViewModel(service);
        vm.AttachHome(home);
        vm.SelectedId = "broken";
        Assert.False(vm.HasInstance);
        Assert.True(vm.ShowHero);
        Assert.Equal("broken", vm.BrokenId);
        Assert.Equal("broken", vm.HeroName);
        Assert.Equal("", vm.HeroVersion);
        // Without an account the button asks for one first; with one it is Repair, with Open folder next to it.
        Assert.NotNull(vm.Start);
        Assert.True(vm.Start!.Kind is StartKind.Repair or StartKind.NeedAccount);
        Assert.Equal(StartKind.Repair, StartButton.For(null, true, service.Directory.StateOf("broken")).Kind);
        Assert.True(vm.OpenFolderCommand.CanExecute(null));
        Assert.True(vm.DeleteVersionCommand.CanExecute(null));
    });

    [Fact]
    public void ARepairedInstanceLoadsAgain() => UiTestThread.Run(() =>
    {
        CorruptInstance("b");
        using var service = NewService();
        var vm = new InstanceViewModel(service);
        vm.SelectedId = "b";
        Assert.Equal("b", vm.BrokenId);
        File.WriteAllText(Path.Combine(_root, "versions", "b", "b.json"), """{"id":"b","mainClass":"Example","libraries":[]}""");
        service.NotifyInstanceStateChanged("b");
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.Current);
        Assert.Null(vm.BrokenId);
        Assert.True(vm.ShowHero);
    });

    [Fact]
    public void ALoadedInstanceIsNotBroken() => UiTestThread.Run(() =>
    {
        CorruptInstance("x");
        var dir = Path.Combine(_root, "versions", "ok");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ok.json"), """{"id":"ok","mainClass":"Example","libraries":[]}""");
        using var service = NewService();
        var vm = new InstanceViewModel(service);
        vm.SelectedId = "x";
        Assert.Equal("x", vm.BrokenId);
        vm.SelectedId = "ok";
        Assert.Null(vm.BrokenId);
        Assert.NotNull(vm.Current);
    });

    [Fact]
    public void TheLibraryListsABrokenInstanceUnderNeedsYou() => UiTestThread.Run(() =>
    {
        CorruptInstance("broken");
        using var service = NewService();
        var home = new HomeViewModel(service, new LogViewModel());
        var library = new LibraryViewModel(service, home, new ActivityViewModel(service));
        library.Refresh();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!library.IsLoaded && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(library.IsLoaded);
        var card = Assert.Single(library.AllCards);
        Assert.True(card.Entry.Unreadable);
        Assert.Contains(library.Needs, n => n.Text.Contains("broken", StringComparison.Ordinal) && n.IsError);
    });

    [Fact]
    public void AnUnfinishedInstallIsListedAsUnfinishedNotBroken() => UiTestThread.Run(() =>
    {
        // The install stopped before the version file was complete: the instance cannot be read, but it is unfinished.
        CorruptInstance("half");
        File.WriteAllText(Path.Combine(_root, "versions", "half", GameDirectory.IncompleteMarker), "");
        using var service = NewService();
        var home = new HomeViewModel(service, new LogViewModel());
        var library = new LibraryViewModel(service, home, new ActivityViewModel(service));
        library.Refresh();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!library.IsLoaded && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(library.IsLoaded);
        var need = Assert.Single(library.Needs);
        Assert.Equal(Amber.Core.Localization.L.F("library.need.incomplete", "half"), need.Text);
        Assert.False(need.IsError);
    });
}
