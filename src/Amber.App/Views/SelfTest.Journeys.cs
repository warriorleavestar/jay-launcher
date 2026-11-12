using System.IO.Compression;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Diagnostics;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Navigation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=journeys (official §7.9): walks the key journeys through the real window and counts clicks
/// (a click, Enter or a drop is 1; opening a drop-down and picking is 2; typing, pasting and waiting are free) and page
/// changes (the content area switching to another page; sidebar navigation, panels, drawers and context sub-views with a
/// way back don't count). Every journey is compared with its target from §7.
///
/// The offline journeys run at 600×420, 1024×700 and 1440×900 in all four languages. The ones that need Modrinth or the
/// version list (J3, J3′, J4′) run at the three sizes in Simplified Chinese only. The last step of a journey that
/// would download or start the game is counted and checked (visible, inside the window, enabled) but not pressed.
/// Uses a throwaway game directory in the report folder. Results also go to <c>journeys.txt</c>.
/// </summary>
internal sealed partial class SelfTest
{
    private sealed class Journey(string id, int target)
    {
        public string Id { get; } = id;
        public int Target { get; } = target;
        public int Clicks { get; set; }
        public int Pages { get; set; }
        public bool NextIsNavigation { get; set; }
        public List<string> Steps { get; } = [];
        public List<string> Problems { get; } = [];
        public string? LastPage { get; set; }
    }

    private Journey? _journey;
    private readonly List<string> _journeyTable = [];

    private static readonly (int W, int H)[] JourneySizes = [(600, 420), (1024, 700), (1440, 900)];

    private async Task JourneysAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var originalRecent = service.Settings.RecentInstances;
        var originalLanguage = L.Language;
        var game = Path.Combine(reportDir, "journeys-game");
        var files = Path.Combine(reportDir, "journeys-files");
        var other = Path.Combine(reportDir, "journeys-other", ".minecraft");
        const string id = "fabric-1.20.1";
        Directory.CreateDirectory(files);
        var home = main.Pages.OfType<HomeViewModel>().First();
        var accounts = main.Pages.OfType<AccountsViewModel>().First();
        var mp = Page<MultiplayerViewModel>();
        var download = Page<DownloadViewModel>();
        var wasComponentReady = mp.IsComponentReady;
        var addedAccount = false;
        main.Shell.Router.Navigated += OnJourneyNavigated;
        try
        {
            WriteLibraryVersion(game, "1.20.1", "[]");
            Directory.CreateDirectory(Path.Combine(game, "versions", id));
            File.WriteAllText(Path.Combine(game, "versions", id, id + ".json"),
                $$"""{"id":"{{id}}","inheritsFrom":"1.20.1","type":"release","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9"}]}""");
            WriteLibraryVersion(game, "1.21.4", "[]");
            WriteLibraryVersion(other, "1.12.2", "[]");
            service.Settings.Instances = new()
            {
                [id] = new() { DisplayName = "Survival+", LastPlayed = DateTimeOffset.Now.AddHours(-2), PlayMinutes = 600 },
            };
            service.Settings.RecentInstances = [id];
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            var mods = Path.Combine(service.WorkDirFor(id), "mods");
            Directory.CreateDirectory(mods);
            File.WriteAllBytes(Path.Combine(mods, "sodium-fabric-0.5.11.jar"), []);
            service.SetCurrentInstance(id);
            service.NotifyInstalledChanged();
            download.SetOtherCandidatesForTest(other);
            if (!service.Accounts.Store.Accounts.Any())
            {
                accounts.OfflineName = "Steve";
                accounts.AddOfflineCommand.Execute(null);
                addedAccount = true;
            }
            home.SelectedAccount = service.Accounts.Store.Selected ?? service.Accounts.Store.Accounts.FirstOrDefault();
            // J5a counts from "the multiplayer component is installed".
            mp.IsComponentReady = true;
            // Hand-written versions carry no Minecraft version; tell the invite picker what the instance is.
            mp.TestCandidates = () => [new(id, "1.20.1", "fabric", 1)];

            var modpack = Path.Combine(files, "journey.mrpack");
            WriteZip(modpack, ("modrinth.index.json",
                """{"formatVersion":1,"game":"minecraft","versionId":"1.0","name":"Journey pack","files":[],"dependencies":{"minecraft":"1.20.1","fabric-loader":"0.16.9"}}"""));
            var themePack = Path.Combine(files, "journey.jaytheme");
            ThemeSelfTest.WritePack(themePack, """{ "id": "journey-green", "name": "Journey green" }""", """{ "seed": { "accent": "#2E9E5B" } }""");
            // A returning user has already closed "Jay has a new layout".
            await ToLibraryAsync();
            if (Dispatcher.UIThread.Invoke(() => window.MovedNotice.IsVisible)) Press(window.MovedNoticeClose);
            var invite = JayInvite.Create(new byte[16], Amber.Core.Multiplayer.Transport.TunnelSettings.Default,
                new Amber.Core.Multiplayer.ModManifest([new Amber.Core.Multiplayer.ModManifestEntry("sodium-fabric-0.5.11.jar", new string('a', 40), 0)])
                    .ToInviteGame("1.20.1", "fabric")).ToString();

            // AMBER_JOURNEYS_QUICK=1: only the smallest window, in Simplified Chinese and English (while working on a journey).
            var quick = Environment.GetEnvironmentVariable("AMBER_JOURNEYS_QUICK") == "1";
            foreach (var (w, h) in quick ? JourneySizes[..1] : JourneySizes)
            {
                window.Width = w;
                window.Height = h;
                foreach (var lang in quick ? ["zh-Hans", "en"] : Amber.Core.Help.HelpLibrary.Languages)
                {
                    L.SetLanguage(lang);
                    await Task.Delay(300);
                    var where = $"{w}×{h} {lang}";
                    await J1Async(where);
                    await J2Async(where);
                    await J4Async(where, modpack);
                    await J5aAsync(where, mp);
                    await J5bAsync(where, mp, invite);
                    await J6Async(where, home, id, mods);
                    await J7Async(where, themePack);
                    await J8Async(where, download, other);
                    if (lang == "zh-Hans")
                    {
                        await J3Async(where, download);
                        await J3PrimeAsync(where, id);
                        await J4PrimeAsync(where);
                    }
                }
            }
            File.WriteAllLines(Path.Combine(reportDir, "journeys.txt"), _journeyTable);
        }
        finally
        {
            main.Shell.Router.Navigated -= OnJourneyNavigated;
            _journey = null;
            L.SetLanguage(originalLanguage);
            mp.IsComponentReady = wasComponentReady;
            mp.TestCandidates = null;
            window.Width = 1440;
            window.Height = 900;
            if (addedAccount && service.Accounts.Store.Accounts.FirstOrDefault(a => a.Name == "Steve") is { } steve)
            {
                service.Accounts.Store.Remove(steve);
                service.Accounts.Store.Save();
                service.NotifyAccountsChanged();
            }
            service.Settings.Instances = originalMeta;
            service.Settings.RecentInstances = originalRecent;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
            service.NotifyInstalledChanged();
        }
    }

    // ---------------------------------------------------------------- the journeys

    /// <summary>J1 (offline): name → [Continue] → [▶ Install and play]. Target 2.</summary>
    private async Task J1Async(string where)
    {
        var wizard = main.FirstRun;
        var service = main.Service;
        var before = service.Accounts.Store.Accounts.Select(a => a.Id).ToHashSet();
        await ToLibraryAsync();
        Dispatcher.UIThread.Invoke(() => { wizard.Open(); wizard.Step = 1; });
        await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.FirstRunLayer.IsEffectivelyVisible), 3);
        var j = Begin("J1", 2);
        var layer = window.FirstRunLayer;
        Dispatcher.UIThread.Invoke(() => wizard.Onboarding.PlayerName = "Alex");
        if (await TapAsync(layer.ContinueButton, "continue") && !await WaitAsync(() => wizard.Step == 2, 3)) j.Problems.Add("still on step 1");
        if (!await WaitAsync(() => wizard.Onboarding.LatestRelease is not null, 20))
        {
            // Offline: the version list didn't come; the journey is about the clicks, so use a known release.
            Dispatcher.UIThread.Invoke(() => wizard.Onboarding.LatestRelease = "1.21.4");
            await Task.Delay(200);
        }
        if (wizard.Choice != FirstRunChoice.Vanilla) j.Problems.Add($"default choice is {wizard.Choice}, not vanilla");
        await FinalAsync(layer.InstallPlayButton, "install and play");
        await EndAsync(where);
        Dispatcher.UIThread.Invoke(() => wizard.SkipCommand.Execute(null));
        foreach (var added in service.Accounts.Store.Accounts.Where(a => !before.Contains(a.Id)).ToList()) service.Accounts.Store.Remove(added);
        service.Accounts.Store.Save();
        service.NotifyAccountsChanged();
    }

    /// <summary>J2: the library's "continue playing" is the last game → [▶ Play]. Target 1.</summary>
    private async Task J2Async(string where)
    {
        await ToLibraryAsync();
        var page = await PageAsync<LibraryPage>();
        var j = Begin("J2", 1);
        if (Dispatcher.UIThread.Invoke(() => page?.ContinueName.Text) != "Survival+") j.Problems.Add("continue playing isn't the last game");
        await FinalAsync(page?.ContinuePlay, "play");
        await EndAsync(where);
    }

    /// <summary>J4: drop a .mrpack on the window → preview → [▶ Import and play]. Target 2.</summary>
    private async Task J4Async(string where, string modpack)
    {
        await ToLibraryAsync();
        var download = Page<DownloadViewModel>();
        Begin("J4", 2);
        await DropFileAsync(modpack, "drop the modpack");
        if (!await WaitAsync(() => main.CurrentPage is DownloadViewModel && download.HasPackPreview, 5)) _journey!.Problems.Add("no modpack preview");
        await FinalAsync(await PageAsync<DownloadPage>() is { } page ? page.ImportPackPlayButton : null, "import and play");
        await EndAsync(where);
        Dispatcher.UIThread.Invoke(() => download.ClearPackCommand.Execute(null));
        Dispatcher.UIThread.Invoke(() => main.Drop.End());
    }

    /// <summary>
    /// J5a (component installed): [Multiplayer] → [▶ Play and open a room] → in-game "Open to LAN" (not counted) → the room
    /// opens and the invite is copied → [Let them in] on a knock. Target 3.
    /// </summary>
    private async Task J5aAsync(string where, MultiplayerViewModel mp)
    {
        await ToLibraryAsync();
        var j = Begin("J5a", 3);
        // Starting the game is the last real step here: count the press, but don't start a game.
        var started = new List<string>();
        var startInstance = mp.StartInstance;
        Dispatcher.UIThread.Invoke(() => { mp.StartInstance = started.Add; mp.SelectedLanGame = null; mp.ManualPort = ""; });
        try
        {
            await TapNavAsync("multiplayer");
            // The page re-checks the component when it opens; this journey is "component installed".
            Dispatcher.UIThread.Invoke(() => { mp.ShowTabCommand.Execute("play"); mp.IsComponentReady = true; });
            await Task.Delay(300);
            var host = await FindAsync<Button>(b => b.Name == "LobbyHostButton");
            if (!Dispatcher.UIThread.Invoke(() => mp.StartsGameToHost)) j.Problems.Add("the lobby button doesn't start the game (no [Play and open a room])");
            await TapAsync(host, "play and open a room");
            if (started.Count != 1) j.Problems.Add("the game wasn't started");
            var panel = await FindAsync<Border>(b => b.Name == "WaitingForLanPanel", 1);
            if (panel is null || !Dispatcher.UIThread.Invoke(() => panel.IsEffectivelyVisible)) j.Problems.Add("no in-game steps while waiting for Open to LAN");
            // In the game: enter a world → Esc → Open to LAN (not counted); Jay opens the room and copies the invite (0).
            // The knock: one press on the locked panel (focus starts on Deny; checked by the room scope).
            j.Clicks++;
            j.Steps.Add("let them in (knock panel)");
            await EndAsync(where);
        }
        finally
        {
            Dispatcher.UIThread.Invoke(() => { mp.CancelWaitForLanCommand.Execute(null); mp.StartInstance = startInstance; });
        }
    }

    /// <summary>J5b: Ctrl+V the invite anywhere (keyboard, free) → it is recognised and an instance picked → [▶ Join]. Target 2.</summary>
    private async Task J5bAsync(string where, MultiplayerViewModel mp, string invite)
    {
        await ToLibraryAsync();
        var j = Begin("J5b", 2);
        j.NextIsNavigation = true;
        var pasted = Dispatcher.UIThread.Invoke(() => main.PasteCode(invite));
        j.Steps.Add("Ctrl+V");
        if (!pasted || main.Shell.CurrentSection != Section.Play || mp.JoinCode != invite) j.Problems.Add("Ctrl+V didn't fill the join box");
        if (!await WaitAsync(() => mp.JoinMatch is not null, 5)) j.Problems.Add("no instance picked for the invite");
        await FinalAsync(await FindAsync<Button>(b => b.Name == "LobbyJoinButton"), "join");
        await EndAsync(where);
        Dispatcher.UIThread.Invoke(() => mp.JoinCode = "");
    }

    /// <summary>J6: the game crashes → Jay shows the instance's last session → [Disable X] → [▶ Try again]. Target 2.</summary>
    private async Task J6Async(string where, HomeViewModel home, string id, string mods)
    {
        await ToLibraryAsync();
        var instance = Page<InstanceViewModel>();
        var j = Begin("J6", 2);
        // Jay goes to the instance by itself: not the user's page change.
        j.NextIsNavigation = true;
        var crash = CrashAnalyzer.Analyze("""
            Mixin apply for mod sodium failed sodium.mixins.json:core.MixinWindow
            Suspected Mods: Sodium (sodium)
            """);
        Dispatcher.UIThread.Invoke(() => home.SimulateCrashForTest(id, crash));
        if (!await WaitAsync(() => main.Shell.Router.Current.ToString().StartsWith("library/" + id, StringComparison.Ordinal), 3))
        {
            j.Problems.Add("Jay didn't go to the instance after the crash");
            j.NextIsNavigation = false;
            j.Clicks++;
            j.Steps.Add("open the instance");
            Dispatcher.UIThread.Invoke(() => main.Shell.Navigate("library/" + id));
        }
        await WaitAsync(() => instance.Current?.Id == id && instance.ShowLastRun, 5);
        var fixes = instance.LastRunCauses.SelectMany(c => c.Fixes).ToList();
        var fix = fixes.FirstOrDefault(f => f.Fix.Kind == CrashFixKind.DisableMod) ?? fixes.FirstOrDefault();
        var button = fix is null ? null : await FindAsync<Button>(b => ReferenceEquals(b.Command, instance.ApplyFixCommand) && ReferenceEquals(b.CommandParameter, fix));
        if (fix is null) j.Problems.Add("no fix next to the cause");
        await TapAsync(button, fix?.Label ?? "fix");
        await FinalAsync((await PageAsync<InstancePage>())?.StartButton, "try again");
        await EndAsync(where);
        Dispatcher.UIThread.Invoke(() => { instance.DismissLastRunCommand.Execute(null); home.Crash = null; });
        foreach (var off in Directory.GetFiles(mods, "*.disabled")) File.Move(off, off[..^".disabled".Length], overwrite: true);
    }

    /// <summary>J7: dark mode (target 2), language (target 3), a theme pack dropped on the window (target 2).</summary>
    private async Task J7Async(string where, string themePack)
    {
        var settings = Page<SettingsViewModel>();
        var lang = L.Language;
        var theme = settings.SelectedThemeIndex;

        await ToLibraryAsync();
        Begin("J7 dark", 2);
        if (await OpenMeAsync() is { } mePage)
        {
            await TapAsync(mePage.MeThemeDark, "theme: dark");
            if (!await WaitAsync(() => Dispatcher.UIThread.Invoke(() => Application.Current!.ActualThemeVariant == ThemeVariant.Dark), 3)) _journey!.Problems.Add("not dark");
        }
        await EndAsync(where);
        Dispatcher.UIThread.Invoke(() => settings.SelectedThemeIndex = theme);

        await ToLibraryAsync();
        Begin("J7 language", 3);
        if (await OpenMeAsync() is { } languagePage)
        {
            var target = lang == "en" ? "zh-Hans" : "en";
            // LanguageOptions: "system", then L.Languages.
            var index = 1 + L.Languages.Select((l, i) => (l, i)).First(p => p.l.Code == target).i;
            var original = settings.SelectedLanguageIndex;
            await PickAsync(languagePage.MeLanguage, index, "language: " + target);
            if (!await WaitAsync(() => L.Language != lang, 3)) _journey!.Problems.Add("language didn't change");
            if (Dispatcher.UIThread.Invoke(() => settings.SelectedLanguageIndex) != index) _journey!.Problems.Add("settings page not in sync");
            await EndAsync(where);
            Dispatcher.UIThread.Invoke(() => settings.SelectedLanguageIndex = original);
            L.SetLanguage(lang);
            await Task.Delay(200);
        }
        else await EndAsync(where);

        await ToLibraryAsync();
        Begin("J7 theme pack", 2);
        await DropFileAsync(themePack, "drop the theme pack");
        if (!await WaitAsync(() => settings.SelectedThemePack?.Id == "journey-green", 5)) _journey!.Problems.Add("theme pack not applied");
        var preview = await FindAsync<Button>(b => b.Name == "UseThemeButton", 1);
        if (preview is not null) await TapAsync(preview, "use this theme");
        await EndAsync(where);
        Dispatcher.UIThread.Invoke(() => { settings.ResetThemePackCommand.Execute(null); main.Drop.End(); });
    }

    /// <summary>J8: [+ New instance] → "From another launcher" → that folder → [Add as a library location]. Target 4.</summary>
    private async Task J8Async(string where, DownloadViewModel download, string other)
    {
        await ToLibraryAsync();
        var library = await PageAsync<LibraryPage>();
        Begin("J8", 4);
        await TapAsync(library?.NewInstanceButton, "new instance");
        var page = await PageAsync<DownloadPage>();
        await TapItemAsync(page?.SourceList, (int)NewInstanceSource.Import, "from another launcher");
        await WaitAsync(() => !download.IsScanning && download.OtherDirectories.Count > 0, 5);
        var use = await FindAsync<Button>(b => ReferenceEquals(b.Command, download.UseGameDirectoryCommand) && b.CommandParameter as string == other);
        // One button per folder: picking the folder and adding it is the same press.
        await FinalAsync(use, "use this folder");
        await EndAsync(where);
        Dispatcher.UIThread.Invoke(() => download.SourceIndex = 0);
    }

    /// <summary>J3: [+ New instance] (latest release picked) → Fabric → add three mods in the panel → [Install] → [▶ Play]. Target 8.</summary>
    private async Task J3Async(string where, DownloadViewModel download)
    {
        await ToLibraryAsync();
        var library = await PageAsync<LibraryPage>();
        var j = Begin("J3", 8);
        await TapAsync(library?.NewInstanceButton, "new instance");
        var page = await PageAsync<DownloadPage>();
        Dispatcher.UIThread.Invoke(() => download.SourceIndex = 0);
        if (!await WaitAsync(() => download.LatestRelease is not null && download.Items.Count > 0, 30)) j.Problems.Add("no version list (offline?)");
        if (download.Target is null && page is not null) await TapAsync(page.HotLatestButton, "latest release");
        await WaitAsync(() => download.Target is not null, 3);
        var fabric = download.LoaderChoices.FirstOrDefault(c => c.Kind == Amber.Core.Install.LoaderKind.Fabric);
        if (page is not null && fabric is not null) await TapItemAsync(page.LoaderSegment, Array.IndexOf(download.LoaderChoices, fabric), "Fabric");
        var addMods = await FindAsync<Expander>(c => c.Name == "NewInstanceModsExpander", 1);
        if (addMods is null) j.Problems.Add("no \"add mods\" in the new-instance panel (§6.11)");
        else
        {
            Dispatcher.UIThread.Invoke(() => { addMods.IsExpanded = false; download.PickedMods.Clear(); });
            await TapAsync(addMods, "add mods (open)");
            foreach (var query in new[] { "sodium", "iris", "journeymap" })
            {
                // Typing is free; the list searches by itself.
                Dispatcher.UIThread.Invoke(() => download.ModQuery = query);
                await Task.Delay(500);
                if (!await WaitAsync(() => !download.IsSearchingMods && download.ModResults.Count > 0, 20))
                {
                    j.Problems.Add($"no results for \"{query}\"");
                    continue;
                }
                await Task.Delay(200);
                var box = await FindAsync<CheckBox>(c => c.DataContext is ExtraModItem { IsPicked: false } && c.IsEffectivelyVisible, 1);
                await TapAsync(box, "tick " + query);
            }
            if (Dispatcher.UIThread.Invoke(() => download.PickedMods.Count) != 3) j.Problems.Add($"{download.PickedMods.Count} mods picked, not 3");
        }
        await FinalAsync(page?.InstallButton, "install");
        j.Clicks++;
        j.Steps.Add("play (result card)");
        await EndAsync(where);
        Dispatcher.UIThread.Invoke(() => { download.PickedMods.Clear(); download.ModQuery = ""; download.SourceIndex = 0; });
    }

    /// <summary>J3′: Mods tab [+ Add mods] → Discover in context → [+ Queue] ×3 → [Install 3] → [Done, go back]. Target 6.</summary>
    private async Task J3PrimeAsync(string where, string id)
    {
        var content = Page<ContentViewModel>();
        Dispatcher.UIThread.Invoke(() => main.Shell.Navigate($"library/{id}/mods"));
        await Task.Delay(600);
        var instancePage = await PageAsync<InstancePage>();
        Dispatcher.UIThread.Invoke(() => content.ClearQueueCommand.Execute(null));
        var j = Begin("J3′", 6);
        var add = instancePage?.AddModButton;
        await TapAsync(add, "add mods");
        if (Dispatcher.UIThread.Invoke(() => main.CurrentPage is not ContentViewModel))
        {
            // The button opened its menu: "From Modrinth / CurseForge".
            var online = Dispatcher.UIThread.Invoke(() => (add?.Flyout as MenuFlyout)?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Command is not null));
            Dispatcher.UIThread.Invoke(() => add?.Flyout?.Hide());
            j.Clicks++;
            j.Steps.Add("menu: online");
            Dispatcher.UIThread.Invoke(() => online?.Command?.Execute(online.CommandParameter));
        }
        if (!await WaitAsync(() => main.CurrentPage is ContentViewModel && content.HasReturn, 5)) j.Problems.Add("Discover didn't open in context");
        foreach (var query in new[] { "sodium", "iris", "journeymap" })
        {
            Dispatcher.UIThread.Invoke(() => { content.Query = query; content.SearchCommand.Execute(null); });
            await Task.Delay(300);
            if (!await WaitAsync(() => !content.IsSearching && content.Results.Count > 0 && content.Results[0].Title.Contains(query, StringComparison.OrdinalIgnoreCase), 30))
            {
                j.Problems.Add($"no results for {query} ({content.Results.Count}: {content.Results.FirstOrDefault()?.Title}; {content.Status})");
                continue;
            }
            var hit = content.Results[0];
            await TapAsync(await FindAsync<Button>(b => ReferenceEquals(b.Command, content.ToggleQueueCommand) && ReferenceEquals(b.CommandParameter, hit)), "queue " + query);
            await WaitAsync(() => hit.Queued, 20);
        }
        await FinalAsync(await FindAsync<Button>(b => b.Name == "InstallQueueButton"), "install 3");
        await TapAsync(await FindAsync<Button>(b => b.Name == "DoneReturnButton"), "done, go back");
        if (!await WaitAsync(() => main.CurrentPage is InstanceViewModel, 3)) j.Problems.Add("didn't come back to the mods tab");
        await EndAsync(where);
        Dispatcher.UIThread.Invoke(() => { content.ClearQueueCommand.Execute(null); content.Query = ""; });
    }

    /// <summary>J4′: [Discover] (modpacks, popular listed) → a modpack → [▶ Install and play]. Target 3.</summary>
    private async Task J4PrimeAsync(string where)
    {
        var content = Page<ContentViewModel>();
        await ToLibraryAsync();
        var j = Begin("J4′", 3);
        await TapNavAsync("discover");
        var page = await PageAsync<ContentPage>();
        if (content.TypeIndex != (int)ContentType.Modpack)
        {
            j.Problems.Add("Discover doesn't start on modpacks");
            if (page is not null && Dispatcher.UIThread.Invoke(() => page.TypeChips.IsEffectivelyVisible)) await TapItemAsync(page.TypeChips, (int)ContentType.Modpack, "modpacks");
            else if (page is not null) await PickAsync(page.TypeCombo, (int)ContentType.Modpack, "modpacks");
        }
        if (!await WaitAsync(() => !content.IsSearching && content.Results.Count > 0, 30)) j.Problems.Add("no modpacks listed (offline?)");
        else
        {
            var first = content.Results[0];
            var row = Dispatcher.UIThread.Invoke(() => page?.ResultsList.ContainerFromItem(first) as ListBoxItem);
            await TapAsync(row, "a modpack");
            await WaitAsync(() => content.HasSelection, 5);
            var play = await FindAsync<Button>(b => b.Name == "InstallPlayButton" && b.FindAncestorOfType<ContentPage>() is not null, 30);
            await WaitAsync(() => play?.Command?.CanExecute(null) == true, 30);
            await FinalAsync(play, "install and play");
        }
        await EndAsync(where);
        Dispatcher.UIThread.Invoke(() => content.CloseDetailCommand.Execute(null));
    }

    // ---------------------------------------------------------------- counting

    private Journey Begin(string id, int target)
    {
        _journey = new Journey(id, target) { LastPage = PageKey(main.Shell.Router.Current) };
        return _journey;
    }

    private async Task EndAsync(string where)
    {
        if (_journey is not { } j) return;
        _journey = null;
        var ok = j.Problems.Count == 0 && j.Clicks <= j.Target && j.Pages == 0;
        var line = $"{j.Id} · {where}: {j.Clicks} clicks (target {j.Target}), {j.Pages} page changes (target 0)";
        var detail = string.Join(" → ", j.Steps) + (j.Problems.Count > 0 ? " | " + string.Join("; ", j.Problems) : "");
        _journeyTable.Add($"{(ok ? "OK  " : "OVER")} {line} | {detail}");
        Check(line, ok, detail);
        if (!ok) await ShotAsync($"journey-{j.Id.Replace(' ', '-')}-{where.Replace(' ', '-').Replace('×', 'x')}");
    }

    /// <summary>A page is the first two route segments ("library/fabric-1.20.1"); tabs, query and fragment don't count.</summary>
    private static string PageKey(Route route) => string.Join('/', route.Segments.Take(2));

    private void OnJourneyNavigated(Route route, NavigationKind kind)
    {
        if (_journey is not { } j) return;
        var key = PageKey(route);
        var from = j.LastPage;
        j.LastPage = key;
        if (key == from) return;
        if (j.NextIsNavigation || kind is NavigationKind.Section or NavigationKind.Return)
        {
            j.NextIsNavigation = false;
            return;
        }
        // The new-instance panel and context sub-views (with a way back) are not pages of their own.
        if (key == "library/new" || from == "library/new" || main.Shell.Router.CurrentReturn is not null || Page<ContentViewModel>().HasReturn) return;
        j.Pages++;
        j.Steps.Add("page → " + key);
    }

    /// <summary>The control can be clicked: visible and enabled once scrolled into view, its middle inside the window.</summary>
    private async Task<bool> ReachableAsync(Control? c) => await UnreachableAsync(c) is null;

    /// <summary>Why the control can't be clicked, or null when it can.</summary>
    private async Task<string?> UnreachableAsync(Control? c)
    {
        if (c is null) return "not found";
        Dispatcher.UIThread.Invoke(() => c.BringIntoView());
        await Task.Delay(250);
        return Dispatcher.UIThread.Invoke(() =>
        {
            if (!c.IsEffectivelyVisible) return "hidden";
            if (!c.IsEffectivelyEnabled) return "disabled";
            var mid = new Point(c.Bounds.Width / 2, c.Bounds.Height / 2);
            // A menu or flyout lives in its own popup: it only has to be inside that popup.
            if (TopLevel.GetTopLevel(c) is { } root && !ReferenceEquals(root, window))
                return c.TranslatePoint(mid, root) is { } q && q.X >= 0 && q.Y >= 0 && q.X <= root.ClientSize.Width && q.Y <= root.ClientSize.Height
                    ? null : "outside its popup";
            if (c.TranslatePoint(mid, window) is not { } p) return "not in the window";
            return p.X >= 0 && p.Y >= 0 && p.X <= window.ClientSize.Width && p.Y <= window.ClientSize.Height ? null : $"outside the window at {p.X:0},{p.Y:0}";
        });
    }

    /// <summary>One click on a button or a list item.</summary>
    private async Task<bool> TapAsync(Control? c, string label)
    {
        var j = _journey!;
        if (await UnreachableAsync(c) is { } why)
        {
            j.Problems.Add($"can't reach \"{label}\" ({why})");
            return false;
        }
        j.Clicks++;
        j.Steps.Add(label);
        var done = c switch
        {
            RadioButton r => Dispatcher.UIThread.Invoke(() => { r.IsChecked = true; return true; }),
            CheckBox box => Dispatcher.UIThread.Invoke(() =>
            {
                if (box.Command is { } command) { command.Execute(box.CommandParameter); return true; }
                box.IsChecked = box.IsChecked != true;
                return true;
            }),
            Expander e => Dispatcher.UIThread.Invoke(() => { e.IsExpanded = !e.IsExpanded; return true; }),
            Button b => Press(b),
            ListBoxItem item => Dispatcher.UIThread.Invoke(() => item.IsSelected = true),
            _ => false,
        };
        await Task.Delay(300);
        if (!done) j.Problems.Add($"\"{label}\" did nothing");
        return done;
    }

    /// <summary>The last step (download or start the game): counted and checked, not pressed.</summary>
    private async Task<bool> FinalAsync(Button? button, string label)
    {
        var j = _journey!;
        j.Clicks++;
        j.Steps.Add(label + " ▸");
        var why = await UnreachableAsync(button) ?? (Dispatcher.UIThread.Invoke(() => button!.Command?.CanExecute(button.CommandParameter) != false) ? null : "can't run now");
        var ok = why is null;
        if (!ok) j.Problems.Add($"\"{label}\" isn't ready to press ({why})");
        return ok;
    }

    private async Task TapItemAsync(ListBox? list, int index, string label)
    {
        if (list is not null) Dispatcher.UIThread.Invoke(() => list.ScrollIntoView(index));
        await Task.Delay(200);
        var item = list is null ? null : Dispatcher.UIThread.Invoke(() => list.ContainerFromIndex(index) as ListBoxItem);
        await TapAsync(item, label);
    }

    /// <summary>A drop-down: open it and pick (2).</summary>
    private async Task PickAsync(ComboBox? box, int index, string label)
    {
        var j = _journey!;
        if (await UnreachableAsync(box) is { } why)
        {
            j.Problems.Add($"can't reach \"{label}\" ({why})");
            return;
        }
        j.Clicks += 2;
        j.Steps.Add(label + " (drop-down)");
        Dispatcher.UIThread.Invoke(() => box!.SelectedIndex = index);
        await Task.Delay(300);
    }

    /// <summary>A sidebar section (navigation, not a page change).</summary>
    private async Task TapNavAsync(string sectionId)
    {
        _journey!.NextIsNavigation = true;
        var index = Dispatcher.UIThread.Invoke(() => main.Shell.Sections.Select(s => s.Id).ToList().IndexOf(sectionId));
        if (index < 0)
        {
            _journey.Problems.Add("no sidebar entry " + sectionId);
            return;
        }
        await TapItemAsync(window.NavList, index, "sidebar: " + sectionId);
        await Task.Delay(300);
    }

    /// <summary>The sidebar avatar opens the "Me" page (1 click; a sidebar move, not a page change).</summary>
    private async Task<AccountsPage?> OpenMeAsync()
    {
        if (!await TapAsync(window.AccountButton, "me")) return null;
        if (await FindAsync<AccountsPage>(p => p.IsEffectivelyVisible) is { } page && main.CurrentPage is AccountsViewModel) return page;
        _journey!.Problems.Add("the Me page didn't open");
        return null;
    }

    /// <summary>Settings (sidebar footer) → a group: a list item when the list shows, else the drop-down.</summary>
    private async Task<bool> OpenSettingsGroupAsync(string group)
    {
        _journey!.NextIsNavigation = true;
        if (!await TapAsync(window.SettingsButton, "settings")) return false;
        var page = await PageAsync<SettingsPage>();
        if (page is null) return false;
        await WaitAsync(() => Dispatcher.UIThread.Invoke(() => page.GroupList.IsEffectivelyVisible || page.GroupIndex.IsEffectivelyVisible || page.GroupPicker.IsEffectivelyVisible || page.GroupBackButton.IsEffectivelyVisible), 3);
        // Compact settings reopen on the last group: back to the list first.
        if (Dispatcher.UIThread.Invoke(() => page.GroupBackButton.IsEffectivelyVisible)) await TapAsync(page.GroupBackButton, "back to the groups");
        var index = SettingsCatalog.Groups.Select((g, i) => (g, i)).First(p => p.g.Id == group).i;
        if (Dispatcher.UIThread.Invoke(() => page.GroupList.IsEffectivelyVisible)) await TapItemAsync(page.GroupList, index, "group " + group);
        else if (Dispatcher.UIThread.Invoke(() => page.GroupIndex.IsEffectivelyVisible))
        {
            // The compact index opens a group on tap (like a link), not on selection.
            await TapItemAsync(page.GroupIndex, index, "group " + group);
            Dispatcher.UIThread.Invoke(() => { if (page.GroupIndex.SelectedItem is { } item) Page<SettingsViewModel>().OpenGroupCommand.Execute(item); });
            await Task.Delay(300);
        }
        else await PickAsync(page.GroupPicker, index, "group " + group);
        await Task.Delay(300);
        return true;
    }

    private async Task DropFileAsync(string path, string label)
    {
        var j = _journey!;
        var data = new DataTransfer();
        if (await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path)) is { } file) data.Add(DataTransferItem.CreateFile(file));
        Dispatcher.UIThread.Invoke(() => window.RaiseEvent(new DragEventArgs(DragDrop.DragEnterEvent, data, window, new Point(10, 10), KeyModifiers.None)));
        await Task.Delay(300);
        var zone = Dispatcher.UIThread.Invoke(() => window.DropTargets.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("drop-target") && b.IsEffectivelyVisible));
        if (zone is null)
        {
            j.Problems.Add("no drop target for " + Path.GetFileName(path));
            return;
        }
        var at = Dispatcher.UIThread.Invoke(() => zone.TranslatePoint(new Point(zone.Bounds.Width / 2, zone.Bounds.Height / 2), window) ?? default);
        Dispatcher.UIThread.Invoke(() => window.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, new DataTransfer(), window, at, KeyModifiers.None)));
        Dispatcher.UIThread.Invoke(() => window.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, window, at, KeyModifiers.None)));
        j.Clicks++;
        j.Steps.Add(label);
        await Task.Delay(500);
    }

    private async Task ToLibraryAsync()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (main.FirstRun.IsOpen) main.FirstRun.SkipCommand.Execute(null);
            main.Shell.Navigate("library");
        });
        await WaitAsync(() => Page<LibraryViewModel>().IsLoaded, 10);
        await Task.Delay(300);
    }

    private async Task<T?> PageAsync<T>() where T : Control
    {
        T? found = null;
        await WaitAsync(() => (found = Dispatcher.UIThread.Invoke(() => window.PageHost.GetVisualDescendants().OfType<T>().FirstOrDefault(p => p.IsEffectivelyVisible))) is not null, 5);
        return found;
    }

    private static void WriteZip(string path, params (string Entry, string Text)[] entries)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(text);
        }
    }
}
