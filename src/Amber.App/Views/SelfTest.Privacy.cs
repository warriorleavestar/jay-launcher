using Amber.App.Controls.Safety;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Launch;
using Amber.Core.Layout;
using Amber.Core.Localization;
using Amber.Core.Net;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=privacy (UI batch 8c): "About and privacy" is its own locked page with the five-category network
/// list and how each stands now; Settings › Privacy shows the same list; "Change ›" goes to the setting; "For a child"
/// turns on its six items, hides what they forbid in multiplayer and shows the time of the last change; opening every
/// page sends no request outside the network list. Screenshots at two widths in two languages.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task PrivacyAsync()
    {
        var about = Page<AboutViewModel>();
        var settings = Page<SettingsViewModel>();
        var mp = main.Multiplayer;
        var unclassifiedBefore = NetworkPolicy.UnclassifiedCount;
        void Seen(NetworkCategory? c, Uri? u) => Log($"NET {c?.ToString() ?? "UNCLASSIFIED"} {u?.Host}{u?.AbsolutePath}");
        NetworkPolicy.Requested += Seen;
        try
        {
            window.Width = 1440;
            window.Height = 900;
            main.Shell.Navigate("about");
            await WaitAsync(() => main.CurrentPage is AboutViewModel, 5);
            await Task.Delay(500);
            var page = Dispatcher.UIThread.Invoke(() => window.PageHost.GetVisualDescendants().OfType<AboutPage>().First());
            Check("“关于和隐私”有自己的页，路由 about", main.CurrentPage is AboutViewModel && main.Shell.Title == L.T("about.title"));
            Check("整页是锁定的安全外观，开头是“Jay 安全提示”", Dispatcher.UIThread.Invoke(() => page.Surface is SafetySurface
                && page.Promise.BadgeText == L.T("safety.title") && page.Surface.Bounds.Width <= 641), Dispatcher.UIThread.Invoke(() => page.Surface.Bounds.Width.ToString("0")));
            Check("联网清单正好 5 类，编号 1–5", about.List.Rows.Select(r => r.Number).SequenceEqual(["1", "2", "3", "4", "5"]));
            Check("现在的状态：没登录、没填更新地址、没在联机", about.List.Rows[1].Status == L.T("about.net.login.none")
                && about.List.Rows[3].Status == L.T("about.net.update.no_feed") && about.List.Rows[4].Status == L.T("about.net.play.idle"),
                string.Join(" | ", about.List.Rows.Select(r => r.Status)));
            Check("宽窗口：状态在右边一栏", Dispatcher.UIThread.Invoke(() => page.Tier == WidthTier.Wide) && !about.List.IsCompact);
            Check("存在本机的写明凭据怎么加密", about.StoredText.Contains(NetworkList.CredentialMethod, StringComparison.Ordinal));
            await ShotAsync("about-1440");
            await VerifyDownloadAsync(about);

            // Very wide: two reading columns instead of one narrow column that scrolls.
            window.Width = 1920;
            window.Height = 1080;
            await Task.Delay(600);
            Check("很宽的窗口：分成两栏（清单在左，其余在右），每栏还是阅读宽度",
                Dispatcher.UIThread.Invoke(() => page.TwoColumns && page.LeftColumn.Bounds.Width <= 641 && page.RightColumn.Bounds.Width <= 641
                    && page.RightColumn.Bounds.X > page.LeftColumn.Bounds.Right && Math.Abs(page.RightColumn.Bounds.Y - page.LeftColumn.Bounds.Y) < 1),
                Dispatcher.UIThread.Invoke(() => $"{page.LeftColumn.Bounds} | {page.RightColumn.Bounds}"));
            await ShotAsync("about-1920");
            window.Width = 1440;
            window.Height = 900;
            await Task.Delay(600);
            Check("窄回来：又是一栏", Dispatcher.UIThread.Invoke(() => !page.TwoColumns && page.RightColumn.Bounds.Y > page.LeftColumn.Bounds.Bottom));

            // Status follows the settings: an update address makes it "only when you click".
            var feed = AppSettingsFeed;
            AppSettingsFeed = "https://example.invalid/jay/latest.json";
            about.Refresh();
            Check("填了更新地址：第 4 项写“只在你点检查更新时”", about.List.Rows[3].Status == L.T("about.net.update.manual"));
            AppSettingsFeed = feed;

            // "Change ›" goes to the setting.
            about.List.ChangeCommand.Execute(about.List.Rows[3]);
            var revealed = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.RevealedField?.Name == "UpdateFeedBox" && window.RevealedField.IsEffectivelyVisible), 3);
            Check("“改 ›”跳到 设置 › 下载 › 更新地址", revealed && main.CurrentPage is SettingsViewModel);

            // Settings › Privacy shows the same list.
            settings.SelectGroup("privacy");
            await Task.Delay(300);
            Check("设置 › 隐私与安全 显示同一张清单", settings.NetworkList.Rows.Select(r => r.Title).SequenceEqual(about.List.Rows.Select(r => r.Title)));

            // For a child.
            Check("儿童模式默认关，联机里什么都不藏", !settings.ChildMode && mp.ShowPublicNetworks && mp.ShowRemoteControl && mp.ShowNetworkExit && mp.ShowInviteAutoApprove);
            settings.ChildMode = true;
            Check("打开“给孩子用”：六项都打开", settings.ChildNoRemote && settings.ChildNoPublic && settings.ChildNoExit && settings.ChildFriendsFiles && settings.ChildAlwaysAsk && settings.ChildQuietNotify);
            Check("联机：远程控制、公共网络、经我/经 TA、免确认邀请都藏起来", !mp.ShowPublicNetworks && !mp.ShowRemoteControl && !mp.ShowNetworkExit && !mp.ShowInviteAutoApprove && !mp.CanRemoteControl);
            Check("写着上次改动的时间，存进设置", settings.ChildModeStatusText.Contains(L.T("settings.child.on"), StringComparison.Ordinal)
                && File.ReadAllText(AppSettings.FilePath).Contains("\"ChildMode\"", StringComparison.Ordinal)
                && File.ReadAllText(AppSettings.FilePath).Contains("\"ChangedAt\"", StringComparison.Ordinal), settings.ChildModeStatusText);
            settings.ChildNoRemote = false;
            Check("单独关掉“关掉远程控制”：只有远程控制回来", mp.ShowRemoteControl && !mp.ShowPublicNetworks && !mp.ShowNetworkExit);
            main.Shell.Navigate("settings#ChildMode");
            var child = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.RevealedField?.Name == "ChildModeBox" && window.RevealedField.IsEffectivelyVisible), 3);
            Check("settings#ChildMode 定位到总开关", child);
            await ShotAsync("settings-privacy-child-1440");
            main.Shell.Navigate("about");
            await Task.Delay(300);
            Check("关于页写“给孩子用：开 · 上次改动…”", about.ChildModeText == settings.ChildModeStatusText, about.ChildModeText);
            settings.ChildMode = false;
            Check("关掉总开关：联机全部恢复", mp.ShowPublicNetworks && mp.ShowRemoteControl && mp.ShowNetworkExit && mp.ShowInviteAutoApprove);

            // Compact: the status goes under each item.
            window.Width = 600;
            window.Height = 640;
            await Task.Delay(600);
            page = Dispatcher.UIThread.Invoke(() => window.PageHost.GetVisualDescendants().OfType<AboutPage>().First());
            Check("紧凑：状态换到每项下面", Dispatcher.UIThread.Invoke(() => page.Tier == WidthTier.Compact) && about.List.IsCompact);
            await ShotAsync("about-600");
            L.SetLanguage("en");
            await Task.Delay(400);
            Check("换英文：清单跟着换", about.List.Rows[0].Title == "Downloads you start", about.List.Rows[0].Title);
            await ShotAsync("about-600-en");
            window.Width = 1440;
            window.Height = 900;
            await Task.Delay(600);
            await ShotAsync("about-1440-en");
            L.SetLanguage("zh-Hans");

            // Every page: no request outside the network list.
            foreach (var route in (string[])["library", "library/new", "discover", "play", "people", "me", "settings", "about"])
            {
                main.Shell.Navigate(route);
                await Task.Delay(700);
            }
            Check("打开每一页：没有不在联网清单里的请求", NetworkPolicy.UnclassifiedCount == unclassifiedBefore, $"{NetworkPolicy.UnclassifiedCount - unclassifiedBefore}");
        }
        finally
        {
            NetworkPolicy.Requested -= Seen;
            L.SetLanguage("zh-Hans");
            settings.ChildMode = false;
            window.Width = 1440;
            window.Height = 900;
        }
    }

    /// <summary>
    /// "Verify a Jay download" (privacy plan item 2): a release signed by 2 of 3 test trustees passes; the same file
    /// changed, one signature only, and a file without SHA256SUMS.txt are refused. Local files only.
    /// </summary>
    private async Task VerifyDownloadAsync(AboutViewModel about)
    {
        var trustees = Enumerable.Range(0, 3).Select(_ => Amber.Core.Multiplayer.Public.TrustSigner.Generate(out byte[] _)).ToArray();
        var list = new TrustList([.. trustees.Select(t => t.PublicKey)], 2);
        var dir = Path.Combine(reportDir, "verify-download");
        Directory.CreateDirectory(dir);
        var package = Path.Combine(dir, "Jay-test.zip");
        File.WriteAllText(package, "self-check package");
        var sums = ReleaseSignature.BuildSums([package]);
        File.WriteAllText(Path.Combine(dir, ReleaseSignature.SumsFileName), sums);
        var statement = new ReleaseStatement("v9.9.9", 1, new string('0', 40),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sums))), list.Hash, ReleaseStatement.NoLog);
        var sigs = Path.Combine(dir, ReleaseSignature.SignatureFileName);

        File.WriteAllText(sigs, ReleaseSignature.Cosign(ReleaseSignature.Create(statement), trustees[0]));
        await about.VerifyDownloadAsync(package, list);
        Check("Verify download: one trustee signature is refused", !about.VerifyPassed && about.VerifyText!.Contains(UpdateCheck.Explain(ReleaseVerdict.NotEnoughSignatures), StringComparison.Ordinal), about.VerifyText);

        File.WriteAllText(sigs, ReleaseSignature.Cosign(File.ReadAllText(sigs), trustees[2]));
        await about.VerifyDownloadAsync(package, list);
        await Task.Delay(200);
        Check("Verify download: 2 of 3 trustees pass and the result is shown", about.VerifyPassed
            && Dispatcher.UIThread.Invoke(() => window.PageHost.GetVisualDescendants().OfType<AboutPage>().First().VerifyResult.IsEffectivelyVisible), about.VerifyText);
        Dispatcher.UIThread.Invoke(() => window.PageHost.GetVisualDescendants().OfType<AboutPage>().First().VerifyResult.BringIntoView());
        await Task.Delay(300);
        await ShotAsync("about-verify-ok");

        File.AppendAllText(package, "!");
        await about.VerifyDownloadAsync(package, list);
        Check("Verify download: a changed file is refused", !about.VerifyPassed && about.VerifyText!.Contains(UpdateCheck.Explain(ReleaseVerdict.HashMismatch), StringComparison.Ordinal), about.VerifyText);

        File.Delete(Path.Combine(dir, ReleaseSignature.SumsFileName));
        await about.VerifyDownloadAsync(package, list);
        Check("Verify download: no SHA256SUMS.txt next to it is refused", !about.VerifyPassed, about.VerifyText);
        about.VerifyText = null;
    }

    private string? AppSettingsFeed
    {
        get => Page<HomeViewModel>().Service.Settings.UpdateFeedUrl;
        set => Page<HomeViewModel>().Service.Settings.UpdateFeedUrl = value;
    }
}
