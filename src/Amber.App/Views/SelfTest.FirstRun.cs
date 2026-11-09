using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=firstrun (UI batch 6c): the first-run wizard of a brand-new Jay (fresh AMBER_DATA_DIR). It covers
/// the shell, switches language in place, refuses [Continue] without a name, adds an offline account, offers four starts
/// with one amber button, a found game folder, tips, the locked privacy block, Esc that never leaves, [Skip] and
/// "pick another version". Uses a throwaway game directory in the report folder and only fetches the version list.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task FirstRunAsync()
    {
        var service = main.Service;
        var wizard = main.FirstRun;
        var originalDir = service.Settings.GameDirectory;
        var game = Path.Combine(reportDir, "fr-game");
        var other = Path.Combine(reportDir, "fr-other", ".minecraft");
        Directory.CreateDirectory(game);
        try
        {
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            window.Width = 1440;
            window.Height = 900;
            Check("全新的 Jay：一打开就是首次启动向导，盖住整个外壳", await WaitAsync(() => wizard.IsOpen && Dispatcher.UIThread.Invoke(() =>
            {
                var layer = window.FirstRunLayer;
                return layer.IsEffectivelyVisible && layer.Bounds.Width >= window.ClientSize.Width - 1 && layer.Bounds.Height >= window.ClientSize.Height - 1;
            }), 5), Dispatcher.UIThread.Invoke(() => $"open={wizard.IsOpen} layer={window.FirstRunLayer.Bounds} client={window.ClientSize}"));
            var layer = window.FirstRunLayer;
            Check("第 1 步：名字框拿到焦点", await WaitAsync(() => Dispatcher.UIThread.Invoke(() => layer.NameBox.IsFocused), 3));
            Check("锁定的隐私块写着 Jay 安全提示", Dispatcher.UIThread.Invoke(() => layer.PrivacyPanel.IsEffectivelyVisible && layer.PrivacyPanel.BadgeText == L.T("safety.title")));
            Dispatcher.UIThread.Invoke(() => wizard.ToggleNetworkListCommand.Execute(null));
            Check("联网清单 › 就地展开", await WaitAsync(() => Dispatcher.UIThread.Invoke(() => layer.NetworkList.IsEffectivelyVisible), 2));
            await ShotAsync("firstrun-account-1440");

            // Language: top right, applied in place.
            var english = Array.FindIndex(wizard.LanguageOptions, o => o.Label == "English");
            Dispatcher.UIThread.Invoke(() => wizard.LanguageIndex = english);
            Check("右上角换语言：马上换成英文", await WaitAsync(() => L.Language == "en" && Dispatcher.UIThread.Invoke(() => layer.SkipButton.Content as string) == "Skip, look around first", 3),
                L.Language);
            await ShotAsync("firstrun-account-1440-en");
            Dispatcher.UIThread.Invoke(() => wizard.LanguageIndex = Array.FindIndex(wizard.LanguageOptions, o => o.Label == "简体中文"));
            await WaitAsync(() => L.Language == "zh-Hans", 3);

            // Esc never leaves the wizard.
            Dispatcher.UIThread.Invoke(() => PressKey(layer.NameBox, Key.Escape));
            await Task.Delay(200);
            Check("Esc 不会退出向导", wizard.IsOpen);

            // [Continue] with nothing: focus back to the name, a reason below.
            Dispatcher.UIThread.Invoke(() => layer.ContinueButton.Focus());
            Press(layer.ContinueButton);
            Check("名字为空点【继续】：焦点回到名字框，下面写原因，不往下走",
                await WaitAsync(() => wizard.HasContinueHint && Dispatcher.UIThread.Invoke(() => layer.NameBox.IsFocused), 3) && wizard.Step == 1, wizard.ContinueHint);
            Dispatcher.UIThread.Invoke(() => wizard.Onboarding.PlayerName = "a b");
            Press(layer.ContinueButton);
            Check("名字不合法：写原因，还在第 1 步", wizard.Step == 1 && wizard.Onboarding.NameProblem.Length > 0, wizard.Onboarding.NameProblem);

            // A good name + Enter.
            Dispatcher.UIThread.Invoke(() => { wizard.Onboarding.PlayerName = "Steve"; PressKey(layer.NameBox, Key.Enter); });
            Check("填好名字按 Enter：加上离线账号，进第 2 步", await WaitAsync(() => wizard.Step == 2, 3)
                && service.Accounts.Store.Accounts.Any(a => a.Name == "Steve"));

            // Step 2: the latest release, the default choice, one amber button.
            Check("第 2 步：默认选“玩原版”，写着最新正式版", await WaitAsync(() => wizard.Onboarding.LatestRelease is not null, 30)
                && wizard.Choice == FirstRunChoice.Vanilla && wizard.Options[0].Title.Contains(wizard.Onboarding.LatestRelease!), wizard.Options[0].Title);
            Check("第 2 步只有一个琥珀按钮【安装并开始游戏】", AmberButtons() == 1 && Dispatcher.UIThread.Invoke(() => layer.InstallPlayButton.IsEffectivelyVisible), $"{AmberButtons()}");
            Check("选项列表拿到焦点，方向键换选项", await WaitAsync(() => Dispatcher.UIThread.Invoke(() =>
            {
                if (!layer.ChoiceList.IsKeyboardFocusWithin) layer.ChoiceList.Focus();
                PressKey(layer.ChoiceList, Key.Down);
                return wizard.Choice == FirstRunChoice.Fabric;
            }), 3), wizard.Choice.ToString());

            // A game found on this computer.
            WriteLibraryVersion(other, "1.20.1", "[]");
            wizard.Onboarding.Candidates = () => [other];
            await wizard.Onboarding.DetectAsync();
            Check("发现了你电脑上的游戏：给【加进游戏库】", await WaitAsync(() => Dispatcher.UIThread.Invoke(() => layer.DetectedCard.IsEffectivelyVisible), 3));
            await ShotAsync("firstrun-start-1440");

            // Widths and the compact header.
            foreach (var (w, h) in new[] { (900, 700), (600, 640), (600, 420) })
            {
                window.Width = w;
                window.Height = h;
                await Task.Delay(600);
                var ok = Dispatcher.UIThread.Invoke(() => (w < 700 ? layer.StepLine.IsVisible && !layer.StepDots.IsVisible : layer.StepDots.IsVisible)
                    && layer.InstallPlayButton.IsEffectivelyVisible && layer.InstallPlayButton.Bounds.Width > 0
                    && (h >= FirstRunWizard.ShortBelow || !layer.TipCard.IsVisible));
                Check($"{w}×{h}：{(w < 700 ? "写“第 2 步，共 2 步”，按钮整宽吸底" : "步骤点，按钮在右下")}{(h < FirstRunWizard.ShortBelow ? "，矮窗口不显示小知识" : "")}", ok);
                await ShotAsync($"firstrun-start-{w}x{h}");
            }
            L.SetLanguage("en");
            await Task.Delay(400);
            await ShotAsync("firstrun-start-600-en");
            L.SetLanguage("zh-Hans");
            Dispatcher.UIThread.Invoke(() => wizard.Step = 1);
            await Task.Delay(300);
            await ShotAsync("firstrun-account-600");
            window.Width = 1440;
            window.Height = 900;
            Dispatcher.UIThread.Invoke(() => wizard.Step = 2);

            // Tips page by hand.
            var tip = wizard.TipText;
            wizard.NextTipCommand.Execute(null);
            Check("小知识手动翻页", wizard.TipText != tip && wizard.TipCounter.Contains('2'), wizard.TipCounter);

            // "Pick another version…" opens the new-instance panel and the wizard is done.
            Dispatcher.UIThread.Invoke(() => wizard.ChoiceIndex = (int)FirstRunChoice.Other);
            Check("选“选别的版本…”：按钮变成【打开新建实例】", wizard.InstallLabel == L.T("firstrun.start.open_new"));
            Press(layer.InstallPlayButton);
            Check("打开新建实例面板，向导收起并记住已经走过", await WaitAsync(() => !wizard.IsOpen && main.CurrentPage is DownloadViewModel, 5)
                && service.Settings.FirstRunDone && !wizard.ShouldOpen());

            // [Add to library] on the found game: Jay uses that folder and lands in the library.
            Dispatcher.UIThread.Invoke(wizard.Open);
            Check("已经有账号时再打开直接是第 2 步", wizard.IsOpen && wizard.Step == 2);
            var add = await FindAsync<Button>(b => b.Name == "AddDetectedButton" && b.IsEffectivelyVisible);
            Check("【加进游戏库】：用那个文件夹，向导收起，落到游戏库",
                Press(add) && await WaitAsync(() => !wizard.IsOpen && main.CurrentPage is LibraryViewModel, 5) && service.Settings.GameDirectory == other,
                service.Settings.GameDirectory);

            // [Skip].
            Dispatcher.UIThread.Invoke(wizard.Open);
            Press(layer.SkipButton);
            Check("【跳过，先看看】：向导收起，落到游戏库", await WaitAsync(() => !wizard.IsOpen && main.CurrentPage is LibraryViewModel, 3));
        }
        finally
        {
            L.SetLanguage("zh-Hans");
            if (wizard.IsOpen) Dispatcher.UIThread.Invoke(() => wizard.SkipCommand.Execute(null));
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
            service.NotifyInstalledChanged();
        }

        int AmberButtons() => Dispatcher.UIThread.Invoke(() =>
            window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("play") && b.IsEffectivelyVisible));
    }
}
