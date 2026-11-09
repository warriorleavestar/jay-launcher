using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Navigation;
using Avalonia.Input;
using Avalonia.Threading;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=palette (UI batch 8a): the command palette in a throwaway game directory inside the report
/// folder with two instances. Ctrl+K opens it with the cursor in the box, pinyin initials and old page names find
/// things, # narrows to settings and Enter reveals the field, the recent list, the two-step "Start game…" asks for an
/// instance and Esc steps back, a pasted invite comes first and only fills the join box, the no-results line, and Esc
/// gives focus back. Screenshots wide and narrow in two languages. Nothing goes online and the game is never started.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task PaletteAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var game = Path.Combine(reportDir, "palette-game");
        var library = main.Pages.OfType<LibraryViewModel>().First();
        var multiplayer = main.Multiplayer;
        var palette = main.Palette;
        try
        {
            WriteLibraryVersion(game, "1.20.1", "[]");
            WriteLibraryVersion(game, "1.19.4", "[]");
            service.Settings.Instances = new()
            {
                ["1.20.1"] = new() { DisplayName = "生存整合包" },
                ["1.19.4"] = new() { DisplayName = "创造测试" },
            };
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            main.Shell.Navigate("library");
            window.Width = 1440;
            window.Height = 900;
            Dispatcher.UIThread.Invoke(library.Refresh);
            await WaitAsync(() => library.AllCards.Count == 2, 10);

            PressKey(window, Key.K, KeyModifiers.Control);
            await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.PaletteBox.IsFocused), 3);
            Check("Ctrl+K 打开命令面板，光标在输入框", palette.IsOpen && Dispatcher.UIThread.Invoke(() => window.PaletteBox.IsFocused));
            Check("空的时候：常用操作", palette.Results.Any(r => r.Item.Id == "action:start") && !palette.ShowsRecent,
                string.Join(" | ", palette.Results.Select(r => r.Title)));

            palette.Query = "sczhb";
            Check("拼音首字母 sczhb 找到“生存整合包”", palette.Results.FirstOrDefault()?.Item.Id == "instance:1.20.1",
                string.Join(" | ", palette.Results.Select(r => r.Title)));
            palette.Query = "gjx";
            Check("gjx 找到工具箱", palette.Results.Any(r => r.Item.Id == "page:" + LegacyRoutes.Tools));
            palette.Query = "Downloads";
            Check("旧页面名 Downloads 找到新建实例", palette.Results.Any(r => r.Item.Id == "action:new-instance"));
            palette.Query = "インスタンス";
            Check("别的语言的名字也找得到", palette.Results.Any(r => r.Item.Kind is CommandKind.Page or CommandKind.Action));
            palette.Query = "@";
            Check("@ 只列实例", palette.Results.Count == 2 && palette.Results.All(r => r.Item.Kind == CommandKind.Instance));
            palette.Query = "zzqqxx没有";
            await Task.Delay(200);
            Check("找不到时写明，提示拼音首字母和 ?", palette.IsEmpty && Dispatcher.UIThread.Invoke(() => window.PaletteEmpty.IsEffectivelyVisible));

            palette.Query = "#yxml";
            Check("# 只列设置，yxml 找到游戏目录", palette.Results.Count > 0 && palette.Results.All(r => r.Item.Kind == CommandKind.Setting)
                && palette.Results[0].Item.Id == "setting:GameDirectory", string.Join(" | ", palette.Results.Select(r => r.Title)));
            await ShotAsync("palette-1440");
            PressKey(window.PaletteBox, Key.Enter);
            var revealed = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.RevealedField?.Name == "GameDirectoryBox"), 3);
            Check("Enter：去设置并定位到游戏目录", !palette.IsOpen && revealed && main.Shell.CurrentSection == Section.Settings);

            PressKey(window, Key.K, KeyModifiers.Control);
            Check("再打开：最近用过排在最前", palette.IsOpen && palette.ShowsRecent && palette.Results.FirstOrDefault()?.Item.Id == "setting:GameDirectory");

            // Two-step: "Start game…" asks for the instance; Esc steps back, then closes.
            palette.Query = "开始游戏";
            PressKey(window.PaletteBox, Key.Enter);
            Check("“开始游戏…”：第二步选实例", palette.IsOpen && palette.IsPicking && palette.Results.Count == 2
                && palette.Results.All(r => r.Item.Kind == CommandKind.Instance) && palette.PickingText.Contains("实例"), palette.PickingText);
            palette.Query = "czcs";
            Check("第二步也能用首字母筛", palette.Results.Count == 1 && palette.Results[0].Title == "创造测试");
            PressKey(window.PaletteBox, Key.Escape);
            Check("Esc：先退回第一步", palette.IsOpen && !palette.IsPicking);
            PressKey(window.PaletteBox, Key.Escape);
            Check("再按 Esc：关上", !palette.IsOpen && Dispatcher.UIThread.Invoke(() => !window.PaletteBox.IsFocused));

            // Tab fills a parameter: a two-step action goes to its instance step.
            PressKey(window, Key.K, KeyModifiers.Control);
            palette.Query = "tjmz";
            PressKey(window.PaletteBox, Key.Tab);
            Check("Tab：“添加模组…”进到选实例", palette.IsPicking && palette.Pending?.Item.Id == "action:add-mods");
            PressKey(window.PaletteBox, Key.Escape);

            // A pasted invite comes first and only fills the join box.
            const string code = "JAY2-PALETTETESTCODE";
            palette.Query = code;
            Check("贴邀请码：识别成邀请，排第一", palette.Results.FirstOrDefault()?.Item.Kind == CommandKind.Invite);
            PressKey(window.PaletteBox, Key.Enter);
            await Task.Delay(200);
            Check("Enter：去联机页，只填好邀请码（不自动敲门）", main.Shell.CurrentSection == Section.Play && multiplayer.JoinCode == code && !multiplayer.IsJoined);
            multiplayer.JoinCode = "";

            // Narrow: full width.
            main.Shell.Navigate("library");
            window.Width = 600;
            window.Height = 640;
            await Task.Delay(600);
            PressKey(window, Key.K, KeyModifiers.Control);
            palette.Query = "s";
            await Task.Delay(300);
            Check("窄窗口：面板铺满宽度", Dispatcher.UIThread.Invoke(() => window.PalettePanel.Bounds.Width > 600 - 48), Dispatcher.UIThread.Invoke(() => window.PalettePanel.Bounds.Width.ToString("0")));
            await ShotAsync("palette-600");
            L.SetLanguage("en");
            PressKey(window.PaletteBox, Key.Escape);
            PressKey(window, Key.K, KeyModifiers.Control);
            palette.Query = "set";
            await Task.Delay(400);
            Check("英文界面：Settings 找得到", palette.Results.Any(r => r.Item.Id == "page:settings"));
            await ShotAsync("palette-600-en");
            PressKey(window.PaletteBox, Key.Escape);
        }
        finally
        {
            L.SetLanguage("zh-Hans");
            palette.Close();
            service.Settings.Instances = originalMeta;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
            window.Width = 1440;
            window.Height = 900;
        }
    }
}
