using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Launch;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>1c 设置和键盘：即改即存、全角数字、“4G”内存、引号没配对不保存、恢复默认、Tab 顺序、Ctrl+Enter 和 Enter 提交。</summary>
internal sealed partial class SelfTest
{
    private async Task SettingsAndKeyboardAsync()
    {
        var settings = Page<SettingsViewModel>();
        main.CurrentPage = settings;
        await Task.Delay(600);
        var file = AppSettings.FilePath;

        // 全角宽度：输入“１９２０”，离开输入框后存成 1920。
        var width = await FindAsync<TextBox>(t => t.Name == "WindowWidthBox" && t.IsEffectivelyVisible);
        var jvm = await FindAsync<TextBox>(t => t.Name == "JvmArgsBox" && t.IsEffectivelyVisible);
        Dispatcher.UIThread.Invoke(() => { width!.Focus(); width.Text = "１９２０"; jvm!.Focus(); });
        await WaitAsync(() => Dispatcher.UIThread.Invoke(() => width!.Text) == "1920", 3);
        Check("设置：全角“１９２０”离开输入框后存成 1920，框里显示 1920", File.ReadAllText(file).Contains("\"WindowWidth\": 1920", StringComparison.Ordinal) && Dispatcher.UIThread.Invoke(() => width!.Text) == "1920", Dispatcher.UIThread.Invoke(() => width!.Text ?? ""));

        // JVM 参数引号没配对：离开时不保存，字段下面红字提示，输入还在。
        var before = new AppSettingsProbe().JvmArgs;
        Dispatcher.UIThread.Invoke(() => { jvm!.Focus(); jvm.Text = "-Dfoo=\"a b"; width!.Focus(); });
        await WaitAsync(() => settings.HasJvmArgsError, 3);
        Dispatcher.UIThread.Invoke(() => { if (jvm!.FindAncestorOfType<ScrollViewer>() is { } sv) sv.Offset = sv.Offset.WithY(sv.Offset.Y + 160); });
        await Task.Delay(300);
        await ShotAsync("settings-jvm-error");
        var saved = new AppSettingsProbe().JvmArgs;
        Check("设置：JVM 参数引号没配对时不保存，提示在字段下面，输入保留",
            settings.HasJvmArgsError && saved == before && Dispatcher.UIThread.Invoke(() => jvm!.Text) == "-Dfoo=\"a b", $"文件里 {saved ?? "(空)"}");

        // Ctrl+Enter 在 JVM 参数框里不启动游戏。
        var home = Page<HomeViewModel>();
        var handled = Dispatcher.UIThread.Invoke(() =>
        {
            jvm!.Focus();
            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control, Source = jvm };
            jvm.RaiseEvent(args);
            return args.Handled;
        });
        Check("键盘：在 JVM 参数框里按 Ctrl+Enter 不会启动游戏", !handled && home.RunCount == 0);

        // ↺ 恢复默认：JVM 参数清空，提示消失。
        var reset = await FindAsync<Button>(b => ReferenceEquals(b.Command, settings.ResetFieldCommand) && b.CommandParameter as string == "jvm" && b.IsEffectivelyVisible);
        Check("设置：↺ 恢复默认清掉 JVM 参数和提示", Press(reset) && await WaitAsync(() => !settings.HasJvmArgsError && settings.JvmArgs.Length == 0, 3));
        Dispatcher.UIThread.Invoke(() => settings.ResetFieldCommand.Execute("window"));

        // Tab 顺序和视觉顺序一致：第一张卡从游戏目录开始往下走，纵坐标不往回跳。
        var order = Dispatcher.UIThread.Invoke(() =>
        {
            var start = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "GameDirectoryBox");
            Border? Card(Visual c) => c.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card"));
            var card = Card(start);
            var ys = new List<double>();
            start.Focus(NavigationMethod.Tab);
            // 像用户一样按 Tab：在当前焦点上发 Tab 键，看焦点落到哪里。
            for (var i = 0; i < 12 && window.FocusManager?.GetFocusedElement() is Control v && Card(v) == card; i++)
            {
                if (v.TranslatePoint(new Point(0, 0), card) is { } p) ys.Add(Math.Round(p.Y));
                v.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, Source = v });
            }
            return ys;
        });
        Check("键盘：设置页 Tab 顺序从上到下", order.Count >= 5 && order.Zip(order.Skip(1)).All(p => p.Second >= p.First - 1), string.Join(",", order));

        await ShotAsync("settings-saved");

        // 实例设置：内存写“4G”存成 4096。
        var instance = Page<InstanceViewModel>();
        var service = home.Service;
        if (service.InstalledVersions().FirstOrDefault() is { } id)
        {
            main.CurrentPage = instance;
            instance.SelectedId = id;
            await Task.Delay(300);
            instance.TabIndex = (int)InstanceTab.Settings;
            await Task.Delay(500);
            Dispatcher.UIThread.Invoke(() => { instance.MemoryModeIndex = 2; instance.MemoryText = "4G"; });
            var mb = InstanceSettings.Load(service.Directory, id).MaxMemoryMb;
            Check("实例设置：内存写“4G”存成 4096", mb == 4096 && !instance.HasMemoryError, $"{id}：{mb}");
            Dispatcher.UIThread.Invoke(() => { instance.MemoryText = "lots"; });
            await ShotAsync("instance-memory-error");
            Check("实例设置：认不出的内存不保存并提示", instance.HasMemoryError && InstanceSettings.Load(service.Directory, id).MaxMemoryMb == 4096);
            Dispatcher.UIThread.Invoke(() => { instance.MemoryText = ""; instance.MemoryModeIndex = 0; });
        }
        else Log("SKIP 实例设置内存：测试目录里没有实例");

        // 单字段 Enter 提交：离线名框里按 Enter 添加账号。
        var accounts = Page<AccountsViewModel>();
        main.CurrentPage = accounts;
        accounts.LoginTab = 0;
        var nameBox = await FindAsync<TextBox>(t => t.Name == "OfflineNameBox" && t.IsEffectivelyVisible);
        var count = service.Accounts.Store.Accounts.Count;
        Dispatcher.UIThread.Invoke(() =>
        {
            nameBox!.Focus();
            nameBox.Text = "KbdTest";
            nameBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = nameBox });
        });
        var added = service.Accounts.Store.Accounts.FirstOrDefault(a => a.Name == "KbdTest");
        Check("键盘：离线名框按 Enter 添加账号", added is not null && service.Accounts.Store.Accounts.Count == count + 1);
        // 输入法组字的回车（ImeProcessed）不提交。
        Dispatcher.UIThread.Invoke(() =>
        {
            nameBox!.Text = "ImeTest";
            nameBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.ImeProcessed, Source = nameBox });
        });
        Check("键盘：输入法组字时的回车不提交", service.Accounts.Store.Accounts.All(a => a.Name != "ImeTest"));
        Dispatcher.UIThread.Invoke(() =>
        {
            nameBox!.Text = "";
            if (added is not null) service.Accounts.Store.Remove(added);
            service.NotifyAccountsChanged();
        });
        accounts.LoginTab = 1;
    }

    /// <summary>直接读设置文件（不经界面）。</summary>
    private sealed class AppSettingsProbe
    {
        public string? JvmArgs { get; } = AppSettings.Load().JvmArgs;
    }
}
