using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Help;
using Amber.Core.Localization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// Part of AMBER_SELFTEST_SCOPE=help: the "What is this?" marks (official design §6.24). The new-instance panel shows
/// instance / loader / modpack, the multiplayer page safety code / friend code / relay server. Each has the plain
/// sentence as tooltip and screen-reader help, opens a card on click, and "Learn more" goes to the help article.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task TermHintsAsync()
    {
        List<TermHint> Hints() => Dispatcher.UIThread.Invoke(() => window.GetVisualDescendants().OfType<TermHint>().ToList());

        main.Shell.Navigate("library/new");
        await Task.Delay(600);
        var panel = Hints();
        var instance = panel.FirstOrDefault(h => h.Term == "instance");
        var download = Page<DownloadViewModel>();
        download.SourceIndex = (int)NewInstanceSource.PackFile;
        await Task.Delay(400);
        var packTerms = Hints().Where(h => Dispatcher.UIThread.Invoke(() => h.IsEffectivelyVisible)).Select(h => h.Term).ToList();
        download.SourceIndex = (int)NewInstanceSource.Version;
        await Task.Delay(300);
        Check("Terms: new-instance panel marks instance, loader and modpack",
            instance is not null && panel.Any(h => h.Term == "loader") && packTerms.Contains("modpack"),
            string.Join(",", panel.Select(h => h.Term)) + " / " + string.Join(",", packTerms));
        if (instance is not null)
        {
            var (tip, name, helpText, visible) = Dispatcher.UIThread.Invoke(() => (ToolTip.GetTip(instance) as string, AutomationProperties.GetName(instance),
                AutomationProperties.GetHelpText(instance), instance.IsEffectivelyVisible));
            Check("Terms: the tooltip is the plain sentence", visible && tip == L.T("term.instance.hint") && helpText == tip, tip ?? "");
            Check("Terms: screen readers hear \"What is ...?\"", name == L.F("term.what_is", L.T("term.instance")), name ?? "");
            Check("Terms: the mark is at least the minimum hit size", Dispatcher.UIThread.Invoke(() => instance.Bounds.Width >= 32 && instance.Bounds.Height >= 32),
                Dispatcher.UIThread.Invoke(() => $"{instance.Bounds.Width:0}x{instance.Bounds.Height:0}"));
            Dispatcher.UIThread.Invoke(instance.OpenCard);
            await Task.Delay(300);
            Check("Terms: a click opens the card with \"Learn more\"", Dispatcher.UIThread.Invoke(() => instance.IsCardOpen));
            await ShotAsync("terms-card-1440");
            Dispatcher.UIThread.Invoke(instance.LearnMore);
            await Task.Delay(500);
            Check("Terms: \"Learn more\" opens the instance article",
                main.CurrentPage is HelpCenterViewModel c && c.Article?.Article.Id == Terms.Get("instance")!.Article && !Dispatcher.UIThread.Invoke(() => instance.IsCardOpen),
                Page<HelpCenterViewModel>().Article?.Article.Id ?? "none");
        }

        main.Shell.Navigate(Amber.Core.Navigation.LegacyRoutes.NetworkSettings);
        await Task.Delay(600);
        var relay = Hints().FirstOrDefault(h => h.Term == "relay");
        Check("Terms: Settings › Network marks the relay server", relay is not null && Dispatcher.UIThread.Invoke(() => relay.IsEffectivelyVisible));
        main.Shell.Navigate("play/public");
        await Task.Delay(600);
        var all = Hints().Select(h => h.Term).ToList();
        Check("Terms: multiplayer page has safety code and device code marks", all.Contains("safety_code") && all.Contains("device_code"), string.Join(",", all));
        var articles = HelpLibrary.LoadLanguage("en").Select(a => a.Id).ToHashSet();
        Check("Terms: every mark has a help article", Terms.All.All(t => articles.Contains(t.Article)));
        main.Shell.Navigate("library");
        await Task.Delay(300);
    }
}
