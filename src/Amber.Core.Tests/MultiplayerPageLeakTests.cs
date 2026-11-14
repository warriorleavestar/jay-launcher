using System.Collections.ObjectModel;
using System.Reflection;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.App.Views;

namespace Amber.Core.Tests;

/// <summary>The multiplayer view model lives as long as Jay; a page shown and closed must not leave its handlers on it.</summary>
public class MultiplayerPageLeakTests
{
    private static int Handlers<T>(ObservableCollection<T> collection) =>
        (typeof(ObservableCollection<T>).GetField("CollectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(collection) as Delegate)?.GetInvocationList().Length ?? 0;

    [Fact]
    public void ClosedPagesLeaveNoHandlersBehind() => UiTestThread.Run(() =>
    {
        var resources = Avalonia.Application.Current!.Resources;
        if (!resources.ContainsKey("RadiusMd"))
            resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Jay/"))
            {
                Source = new Uri("avares://Jay/Themes/Tokens.axaml"),
            });
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service);
        var before = Handlers(vm.ChatLines);
        int? shown = null;
        for (var visit = 0; visit < 3; visit++)
        {
            var page = new MultiplayerPage { DataContext = vm };
            var window = new Avalonia.Controls.Window { Content = page };
            window.Show();
            // The page's own handler plus the list bindings: the same on every visit, never one more per visit.
            shown ??= Handlers(vm.ChatLines);
            Assert.True(shown > before);
            Assert.Equal(shown, Handlers(vm.ChatLines));
            window.Close();
        }
        // The page's own handler is gone once it is closed (list bindings let go of theirs on their own schedule).
        Assert.True(Handlers(vm.ChatLines) < shown);
    });
}
