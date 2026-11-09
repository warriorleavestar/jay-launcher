using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Layout;
using Amber.Core.Localization;
using Amber.Core.Navigation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=settings-layout (UI batch 8b): settings in ten groups. Wide: group list, one group at a time and
/// the note column; medium: the group picker; compact: the group index with summaries, then a group with a back button.
/// Every setting in the catalog is reachable through settings#Field (its group opens) and through the command palette;
/// the settings search lists "Group › Setting" and Enter jumps to the field; "After starting a game" now has a home.
/// Screenshots at three widths in two languages. Nothing goes online.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task SettingsLayoutAsync()
    {
        var settings = Page<SettingsViewModel>();
        try
        {
            window.Width = 1440;
            window.Height = 900;
            main.Shell.Navigate("settings");
            await WaitAsync(() => main.CurrentPage is SettingsViewModel, 5);
            await Task.Delay(500);
            var page = Dispatcher.UIThread.Invoke(() => window.PageHost.GetVisualDescendants().OfType<SettingsPage>().First());
            settings.SelectedGroupIndex = 0;
            await Task.Delay(200);
            string[] Shown() => Dispatcher.UIThread.Invoke(() => page.Form.Children.Where(c => c.IsVisible).Select(c => c.Name ?? "").ToArray());
            Check("宽窗口：左边 10 个分组、右边说明栏、没有下拉", Dispatcher.UIThread.Invoke(() => page.Tier == WidthTier.Wide && page.GroupList.IsEffectivelyVisible
                && page.NotePane.IsEffectivelyVisible && !page.GroupPicker.IsVisible) && settings.Groups.Count == 10);
            Check("一次只显示一组（常规）", Shown() is ["Group_general"], string.Join(",", Shown()));
            await ShotAsync("settings-1440");
            settings.SelectedGroupIndex = 1;
            await Task.Delay(200);
            Check("点“外观”：只显示外观，说明栏跟着换", Shown() is ["Group_appearance"] && settings.SelectedGroupNote.Contains("主题包"), settings.SelectedGroupNote);
            await ShotAsync("settings-appearance-1440");

            // Every setting has a home: settings#Field opens its group and reveals the control.
            var missing = new List<string>();
            foreach (var field in SettingsCatalog.Fields)
            {
                main.Shell.Navigate("settings#" + field.Field);
                var ok = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.RevealedField is { } c
                    && (c.Name == field.Field || c.Name == field.Field + "Box") && c.IsEffectivelyVisible), 3);
                if (!ok || settings.SelectedGroupId != field.Group) missing.Add(field.Field);
            }
            Check($"每个设置项（{SettingsCatalog.Fields.Count} 个）都能用 settings#字段 定位，分组自动打开", missing.Count == 0, string.Join(",", missing));

            // The command palette finds every setting by its label.
            var palette = main.Palette;
            palette.Open();
            var notFound = new List<string>();
            foreach (var field in SettingsCatalog.Fields)
            {
                palette.Query = "#" + L.T(field.LabelKey);
                if (!palette.Results.Any(r => r.Item.Id == "setting:" + field.Field)) notFound.Add(field.Field);
            }
            palette.Close();
            Check("命令面板能找到每个设置项", notFound.Count == 0, string.Join(",", notFound));

            // Search: Ctrl+F, "Group › Setting", Enter jumps to the field.
            settings.SelectedGroupIndex = 0;
            Dispatcher.UIThread.Invoke(() => page.Focus());
            PressKey(page, Key.F, KeyModifiers.Control);
            Check("Ctrl+F：光标进搜索框", Dispatcher.UIThread.Invoke(() => page.SettingsSearchBox.IsFocused));
            settings.SettingsSearch = "jvm";
            await Task.Delay(200);
            Check("搜 jvm：写成“游戏默认 › JVM 参数”", settings.SearchHits.FirstOrDefault()?.Path == "游戏默认 › JVM 参数"
                && Dispatcher.UIThread.Invoke(() => page.SearchPane.IsEffectivelyVisible), settings.SearchHits.FirstOrDefault()?.Path ?? "");
            await ShotAsync("settings-search-1440");
            PressKey(page.SettingsSearchBox, Key.Enter);
            var jumped = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.RevealedField?.Name == "JvmArgsBox" && window.RevealedField.IsEffectivelyVisible), 3);
            Check("Enter：跳到字段，搜索清空", jumped && !settings.HasSearch && settings.SelectedGroupId == SettingsCatalog.Game);
            settings.SettingsSearch = "yxml";
            Check("拼音首字母 yxml 找到游戏目录", settings.SearchHits.FirstOrDefault()?.Field.Field == "GameDirectory");
            settings.SettingsSearch = "完全没有这一项";
            await Task.Delay(200);
            Check("搜不到时写明", settings.SearchEmpty && Dispatcher.UIThread.Invoke(() => page.SearchEmptyText.IsEffectivelyVisible));
            Dispatcher.UIThread.Invoke(() => page.SettingsSearchBox.Focus());
            PressKey(page.SettingsSearchBox, Key.Escape);
            Check("Esc 清空搜索", !settings.HasSearch);

            // General › After starting a game: a setting that only lived in the file now has a control.
            settings.AfterLaunchIndex = 1;
            Check("常规 › 开始游戏后：改成“不动”存进设置", File.ReadAllText(AppSettings.FilePath).Contains("\"AfterLaunch\": \"keep\"", StringComparison.Ordinal));
            settings.AfterLaunchIndex = 0;

            // Medium: the group picker.
            window.Width = 900;
            window.Height = 760;
            await Task.Delay(600);
            Check("中等宽度：分组变成顶部下拉，没有左栏和说明栏", Dispatcher.UIThread.Invoke(() => page.Tier == WidthTier.Medium && page.GroupPicker.IsEffectivelyVisible
                && !page.GroupList.IsVisible && !page.NotePane.IsVisible));
            settings.SelectedGroupIndex = 2;
            await ShotAsync("settings-900");

            // Compact: the group index, then one group with a back button.
            window.Width = 600;
            window.Height = 640;
            settings.BackToGroupsCommand.Execute(null);
            await Task.Delay(600);
            Check("紧凑：第一屏是分组列表（带摘要），表单不显示", Dispatcher.UIThread.Invoke(() => page.Tier == WidthTier.Compact && page.GroupIndex.IsEffectivelyVisible
                && !page.FormScroll.IsVisible && !page.GroupBackButton.IsVisible));
            await ShotAsync("settings-600-index");
            settings.OpenGroupCommand.Execute(settings.Groups[4]);
            await Task.Delay(400);
            Check("紧凑：点进“存储”，标题换成分组名，有返回", Dispatcher.UIThread.Invoke(() => page.FormScroll.IsEffectivelyVisible && !page.GroupIndex.IsVisible
                && page.GroupBackButton.IsEffectivelyVisible) && settings.HeaderTitle == settings.Groups[4].Title && Shown() is ["Group_storage"]);
            await ShotAsync("settings-600-storage");
            settings.BackToGroupsCommand.Execute(null);
            await Task.Delay(300);
            Check("紧凑：返回分组列表", Dispatcher.UIThread.Invoke(() => page.GroupIndex.IsEffectivelyVisible) && settings.HeaderTitle == L.T("instance.settings.title"));
            L.SetLanguage("en");
            await Task.Delay(400);
            await ShotAsync("settings-600-index-en");
            window.Width = 1440;
            window.Height = 900;
            settings.SelectedGroupIndex = 2;
            await Task.Delay(600);
            await ShotAsync("settings-1440-en");
        }
        finally
        {
            L.SetLanguage("zh-Hans");
            settings.SettingsSearch = "";
            settings.SelectedGroupIndex = 0;
            window.Width = 1440;
            window.Height = 900;
        }
    }
}
