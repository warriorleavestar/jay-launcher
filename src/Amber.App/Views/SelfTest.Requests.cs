using Amber.App.Controls.Safety;
using Amber.App.ViewModels;
using Amber.Core.Activity;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

internal sealed partial class SelfTest
{
    /// <summary>
    /// Knocks and incoming files in sync across the room panel, the request bar and the topmost popup (official §6.15),
    /// with a stand-in host room: no component, no connection. Also the locked "receive a file" card and its program warning.
    /// </summary>
    private async Task MultiplayerRequestsAsync()
    {
        var vm = Page<MultiplayerViewModel>();
        var requests = main.Service.Requests;
        main.Shell.Navigate("play");
        window.Width = 1440;
        window.Height = 900;
        await Task.Delay(400);
        var page = (await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible))!;
        Dispatcher.UIThread.Invoke(() => vm.Mode = "host");
        var book = new InviteBook();
        KnockRequest Knock(string name, byte n)
        {
            var key = new byte[32];
            key[0] = n;
            return new(name, key, "Jay 0.8", false, book.Create(1, null, false, TunnelSettings.Default));
        }

        // A knock is mirrored as a safety request; answering it on the request bar answers the room.
        var first = vm.AskApprovalAsync(Knock("Alex", 1), CancellationToken.None);
        await WaitAsync(() => vm.HasKnocks, 5);
        var card = vm.ApprovalCards[0];
        var handle = vm.RequestFor(card);
        Check("敲门：同时放进请求条（安全类、默认拒绝、带倒计时）", handle is { Spec: { Kind: "knock", IsSafety: true } spec } && spec.Choices.Single(c => c.IsDefault).Id == "deny"
            && handle.Deadline is not null && requests.Pending.Contains(handle));
        main.Shell.Navigate("library");
        await Task.Delay(300);
        Check("敲门：在别的页面时请求条看得见", window.RequestBar.IsEffectivelyVisible
            && window.RequestBar.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == card.Headline));
        await ShotAsync("请求条-敲门");
        requests.Resolve(handle!, "approve", RequestSource.Bar);
        Check("敲门：在请求条点【让 TA 进来】，房间里同步处理", await WaitAsync(() => first.IsCompleted, 3) && first.Result && await WaitAsync(() => !vm.HasKnocks, 3));
        Check("敲门：处理后请求条收起", await WaitAsync(() => !window.RequestBar.IsVisible, 3));

        // The topmost popup: same request, shown without taking the keyboard, 360 wide; answering it answers everywhere.
        var second = vm.AskApprovalAsync(Knock("Bo", 2), CancellationToken.None);
        await WaitAsync(() => vm.HasKnocks, 5);
        var popup = window.Popups!.Show(vm.RequestFor(vm.ApprovalCards[0])!);
        await Task.Delay(600);
        Check("置顶小窗：不抢焦点、置顶、宽 360、最矮 200", popup is { Topmost: true, ShowActivated: false, Width: 360 } && popup.Bounds.Height >= 199
            && popup.Content is SafetyPanel { Title: var title } && title == L.F("multiplayer.knock.wants_in", "Bo"), $"{popup.Bounds.Width}x{popup.Bounds.Height}");
        Check("置顶小窗：默认按钮是【拒绝】", popup.DefaultButton.Content as string == L.T("multiplayer.invite.deny") && popup.DefaultButton.Kind == SafetyKind.Safe);
        await ShotWindowAsync(popup, "置顶小窗-敲门");
        Dispatcher.UIThread.Invoke(() => popup.DefaultButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));
        Check("置顶小窗：点【拒绝】房间里同步拒绝、小窗关掉", await WaitAsync(() => second.IsCompleted, 3) && !second.Result
            && await WaitAsync(() => RequestPopups.Open.Count == 0, 3) && !vm.HasKnocks);

        // Answered in the room: the popup and the bar go away by themselves.
        var third = vm.AskApprovalAsync(Knock("Cy", 3), CancellationToken.None);
        await WaitAsync(() => vm.HasKnocks, 5);
        var thirdHandle = vm.RequestFor(vm.ApprovalCards[0])!;
        window.Popups.Show(thirdHandle);
        await Task.Delay(300);
        vm.DenyFirstKnockCommand.Execute(null);
        Check("在房间里处理：小窗和请求条一起撤掉", await WaitAsync(() => third.IsCompleted && RequestPopups.Open.Count == 0, 3) && thirdHandle.IsResolved
            && thirdHandle.ResolvedBy == RequestSource.Program);

        // Closing the popup with its ✕ counts as "no".
        var fourth = vm.AskApprovalAsync(Knock("Di", 4), CancellationToken.None);
        await WaitAsync(() => vm.HasKnocks, 5);
        var closing = window.Popups.Show(vm.RequestFor(vm.ApprovalCards[0])!);
        await Task.Delay(300);
        Dispatcher.UIThread.Invoke(closing.Close);
        Check("置顶小窗：关窗算拒绝", await WaitAsync(() => fourth.IsCompleted, 3) && !fourth.Result);

        // A file: a locked card at the top of the room, a pointer in the chat, a warning for programs; bar and room in sync.
        main.Shell.Navigate("play");
        await Task.Delay(300);
        page = (await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible))!;
        var alexKey = new byte[32];
        alexKey[0] = 1;
        var exe = new FileOfferItem(new FileOffer("f1", MemberIdentity.FingerprintOf(alexKey), "Alex", "tool.exe", 1234));
        var zip = new FileOfferItem(new FileOffer("f2", MemberIdentity.FingerprintOf(alexKey), "Alex", "map.zip", 38L * 1024 * 1024));
        Dispatcher.UIThread.Invoke(() => { vm.FileOffers.Add(exe); vm.FileOffers.Add(zip); });
        await Task.Delay(400);
        var cards = page.GetVisualDescendants().OfType<SafetyPanel>().Where(p => p.IsEffectivelyVisible && p.DataContext is FileOfferItem).ToList();
        Check("收文件：房间顶部两张锁定卡片", cards.Count == 2 && cards[0].Title == exe.Headline);
        Check("收文件：程序文件额外警告，普通文件写不会自动打开", exe.IsProgram && exe.Warning == L.T("multiplayer.file.program_warning")
            && !zip.IsProgram && zip.Warning == L.T("multiplayer.file.never_runs"));
        Check("收文件：聊天里只留一行指向上面", page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "FileOfferPointer" && t.IsEffectivelyVisible));
        Check("在房间里：请求条不重复放敲门和文件（房间顶部已经有了）", !window.RequestBar.IsVisible && !main.Activity.Requests.Any(r => r.Handle.Spec.Kind is "file"));
        Check("收文件：同时放进请求条，默认拒绝", vm.RequestFor(exe) is { Spec: { Kind: "file", IsSafety: true } fileSpec } && fileSpec.Choices.Single(c => c.IsDefault).Id == "decline");
        await ShotAsync("收文件-1440");
        requests.Resolve(vm.RequestFor(exe)!, "decline", RequestSource.Bar);
        Check("收文件：请求条拒绝，房间里的卡片同步消失", await WaitAsync(() => !vm.FileOffers.Contains(exe), 3) && vm.FileOffers.Contains(zip));
        Press(page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && b.Classes.Contains("file-decline")));
        Check("收文件：房间里拒绝，请求条同步撤掉", await WaitAsync(() => vm.FileOffers.Count == 0, 3) && !requests.Pending.Any(r => r.Spec.Kind == "file"));

        // Room settings: the host can turn mod sync off.
        Dispatcher.UIThread.Invoke(() => vm.RoomSettingsOpen = true);
        await Task.Delay(300);
        Check("房间设置：模组同步开关（默认开）", vm.AllowModSync && page.GetVisualDescendants().OfType<CheckBox>().Any(c => c.Name == "AllowModSyncBox" && c.IsEffectivelyVisible));
        Dispatcher.UIThread.Invoke(() => vm.RoomSettingsOpen = false);

        await ModCheckAsync(vm, page);

        // Settings: the popup can be turned off.
        vm.ShowTabCommand.Execute("settings");
        await Task.Delay(300);
        Check("联机设置：置顶小窗开关（默认开）", vm.RequestPopup && page.GetVisualDescendants().OfType<CheckBox>().Any(c => c.Name == "RequestPopupBox" && c.IsEffectivelyVisible));
        L.SetLanguage("en");
        var fifth = vm.AskApprovalAsync(Knock("Eve", 5), CancellationToken.None);
        await WaitAsync(() => vm.HasKnocks, 5);
        var enPopup = window.Popups.Show(vm.RequestFor(vm.ApprovalCards[0])!);
        await Task.Delay(600);
        await ShotWindowAsync(enPopup, "置顶小窗-en");
        vm.DenyFirstKnockCommand.Execute(null);
        await WaitAsync(() => fifth.IsCompleted, 3);
        L.SetLanguage("zh-Hans");
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.ShowTabCommand.Execute("play");
            vm.Mode = "";
        });
        await Task.Delay(200);
        Check("结束：没有留下的请求和小窗", !requests.Pending.Any(r => r.Spec.Kind is "knock" or "file") && RequestPopups.Open.Count == 0);
    }

    /// <summary>The joiner's “check the mods” card with stand-in mod lists (no connection).</summary>
    private async Task ModCheckAsync(MultiplayerViewModel vm, MultiplayerPage page)
    {
        static ModManifestEntry Mod(string file, char hash, string id, string version) => new(file, new string(hash, 40), 1000, id, id switch
        {
            "create" => "Create",
            "farmersdelight" => "Farmer's Delight",
            "supplementaries" => "Supplementaries",
            "sodium" => "Sodium",
            "jei" => "JEI",
            "journeymap" => "JourneyMap",
            _ => id,
        }, version);
        var mine = new ModManifest([Mod("sodium-0.5.3.jar", '1', "sodium", "0.5.3"), Mod("jei-15.2.jar", '2', "jei", "15.2.0"), Mod("journeymap-5.9.jar", '3', "journeymap", "5.9.18"), Mod("same.jar", 's', "same", "1")]);
        var host = new ModManifest([Mod("sodium-0.5.8.jar", '4', "sodium", "0.5.8"), Mod("jei-15.3.jar", '5', "jei", "15.3.0"), Mod("create-0.5.1.jar", '6', "create", "0.5.1"),
            Mod("farmersdelight-1.2.jar", '7', "farmersdelight", "1.2"), Mod("supplementaries-2.4.jar", '8', "supplementaries", "2.4"), Mod("same.jar", 's', "same", "1")]);
        TextBlock? Shown(string name) => page.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == name && t.IsEffectivelyVisible);
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.Mode = "join";
            vm.TestHostName = "Alex";
            vm.ModCheckInstance = "selftest-modcheck";
            // An earlier step can leave a join target behind (an invite picks the closest instance when the test
            // config has instances); the check would then name that instance instead of the stand-in one.
            vm.TargetInstance = null;
            vm.ModCheck = ModCheckState.Checking;
        });
        await Task.Delay(300);
        Check("对一下模组：比对完之前只写“正在对”，不说一样", vm.ShowModCheck && Shown("ModCheckingText") is not null && Shown("ModCheckSameText") is null);
        await Dispatcher.UIThread.InvokeAsync(() => vm.ShowModCheckAsync(new ModListReply(ModListAnswer.Sent, host), mine));
        await Task.Delay(300);
        Check("对一下模组：缺 3 个、版本不同 2 个、多 1 个（写明小地图没关系）", vm.ModCheck == ModCheckState.Different
            && vm.ModMissingText == L.P("multiplayer.modcheck.missing", 3, 3, string.Join(L.T("common.list_separator"), "Create", "Farmer's Delight", "Supplementaries"))
            && vm.ModDifferentText.Contains("0.5.3") && vm.ModDifferentText.Contains("0.5.8")
            && vm.ModExtraText.Contains("JourneyMap") && vm.ModExtraText.Contains(L.T("multiplayer.modcheck.extra_harmless"))
            && Shown("ModMissingText") is not null && Shown("ModDifferentText") is not null && Shown("ModExtraText") is not null, vm.ModMissingText);
        Check("对一下模组：标题写我的实例和房主的名字", vm.ModCheckTitle == L.F("multiplayer.modcheck.title", "selftest-modcheck", "Alex"));
        Press(page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ModDetailsButton" && b.IsEffectivelyVisible));
        await Task.Delay(200);
        Check("对一下模组：【看看差在哪】列出每一项", vm.ModDetailsOpen && vm.ModDiffLines.Count == 6
            && page.GetVisualDescendants().OfType<ItemsControl>().Any(i => i.Name == "ModDiffList" && i.IsEffectivelyVisible));
        await ShotAsync("对一下模组-1440");
        await ModSyncAsync(vm, page);
        await Dispatcher.UIThread.InvokeAsync(() => vm.ShowModCheckAsync(new ModListReply(ModListAnswer.Sent, host), mine));
        Dispatcher.UIThread.Invoke(() => vm.ModCheckInstance = "selftest-modcheck");
        Press(page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SkipModCheckButton" && b.IsEffectivelyVisible));
        await Task.Delay(200);
        Check("对一下模组：【不管了，直接进】收起卡片并记下（崩了时第一条建议）", !vm.ShowModCheck && main.Service.MetaOf("selftest-modcheck")?.ModsDifferFromHost == "Alex");

        await Dispatcher.UIThread.InvokeAsync(() => vm.ShowModCheckAsync(new ModListReply(ModListAnswer.Sent, host), host));
        await Task.Delay(200);
        Check("对一下模组：一样时打 ✓，并清掉之前的记录", vm.ModCheck == ModCheckState.Same && Shown("ModCheckSameText") is not null
            && main.Service.MetaOf("selftest-modcheck")?.ModsDifferFromHost is null);
        await Dispatcher.UIThread.InvokeAsync(() => vm.ShowModCheckAsync(new ModListReply(ModListAnswer.Off, null)));
        await Task.Delay(200);
        Check("对一下模组：房主关掉同步时写清楚", vm.ModCheck == ModCheckState.Off && Shown("ModCheckNoticeText")?.Text == L.T("multiplayer.modcheck.off"));
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.ModCheck = ModCheckState.None;
            vm.TestHostName = null;
            vm.Mode = "host";
        });
        await Task.Delay(200);
    }

    /// <summary>One-click sync into a new instance with stand-in folders and a runner without network.</summary>
    private async Task ModSyncAsync(MultiplayerViewModel vm, MultiplayerPage page)
    {
        var root = Path.Combine(reportDir, "modsync");
        string DirOf(string id) => Path.Combine(root, id);
        var mineDir = DirOf("selftest-mine");
        Directory.CreateDirectory(mineDir);
        byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);
        static string Sha(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(b));
        var oldJar = Bytes("old version");
        var shared = Bytes("same file");
        var newJar = Bytes("new version");
        var extra = Bytes("only mine");
        File.WriteAllBytes(Path.Combine(mineDir, "thing-1.0.jar"), oldJar);
        File.WriteAllBytes(Path.Combine(mineDir, "shared.jar"), shared);
        File.WriteAllBytes(Path.Combine(mineDir, "my-extra.jar"), extra);
        var host = new ModManifest([new ModManifestEntry("thing-2.0.jar", Sha(newJar), newJar.Length), new ModManifestEntry("shared.jar", Sha(shared), shared.Length)]);
        var asked = new List<string>();
        vm.TestModsDirOf = DirOf;
        vm.TestRunner = _ => new ModSyncRunner
        {
            FindPublic = (_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()),
            Download = (_, _, _) => throw new InvalidOperationException(),
            FromHost = async (sha1, _) =>
            {
                asked.Add(sha1);
                await Task.Delay(300);
                var received = Path.Combine(root, "received.tmp");
                File.WriteAllBytes(received, newJar);
                return received;
            },
            Recycle = File.Delete,
        };
        Dispatcher.UIThread.Invoke(() => vm.ModCheckInstance = "selftest-mine");
        await Dispatcher.UIThread.InvokeAsync(() => vm.ShowModCheckAsync(new ModListReply(ModListAnswer.Sent, host), ModManifest.Read(mineDir)));
        Dispatcher.UIThread.Invoke(() => vm.ModCheckInstance = "selftest-mine");
        await Task.Delay(200);
        Check("一键同步：默认同步到新实例，写明原实例不动", vm.SyncIntoNewInstance && page.GetVisualDescendants().OfType<RadioButton>().Any(r => r.Name == "SyncIntoNewRadio" && r.IsChecked == true && r.IsEffectivelyVisible)
            && page.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SyncModsButton" && b.IsEffectivelyVisible));
        Press(page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SyncModsButton" && b.IsEffectivelyVisible));
        await WaitAsync(() => vm.ModSyncing, 3);
        await Task.Delay(100);
        Check("一键同步：同步时显示进度、按钮收起", vm.ModSyncing && page.GetVisualDescendants().OfType<StackPanel>().Any(p => p.Name == "ModSyncProgress" && p.IsEffectivelyVisible));
        await WaitAsync(() => !vm.ModSyncing, 10);
        var copy = DirOf(L.F("multiplayer.modcheck.new_instance", "Alex"));
        Check("一键同步：新实例里换上房主的版本、去掉可能挡路的模组，原实例不动", File.Exists(Path.Combine(copy, "thing-2.0.jar")) && !File.Exists(Path.Combine(copy, "thing-1.0.jar"))
            && !File.Exists(Path.Combine(copy, "my-extra.jar")) && File.Exists(Path.Combine(mineDir, "thing-1.0.jar")) && File.Exists(Path.Combine(mineDir, "my-extra.jar")),
            string.Join(",", Directory.Exists(copy) ? Directory.GetFiles(copy).Select(Path.GetFileName) : []));
        Check("一键同步：不在公开网站上的文件向房主要，同步后重新对过且一样", asked.SequenceEqual([Sha(newJar)]) && vm.ModCheck is ModCheckState.Same or ModCheckState.Dismissed
            && vm.ModCheckInstance == L.F("multiplayer.modcheck.new_instance", "Alex"), vm.ModCheck.ToString());
        vm.TestModsDirOf = null;
        vm.TestRunner = null;
    }

    /// <summary>Screenshot of another window (the popup).</summary>
    private async Task ShotWindowAsync(Window target, string name)
    {
        await Task.Delay(300);
        Dispatcher.UIThread.Invoke(() =>
        {
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(Math.Max(1, (int)target.Bounds.Width), Math.Max(1, (int)target.Bounds.Height)));
            bitmap.Render(target);
            bitmap.Save(Path.Combine(reportDir, name + ".png"));
        });
    }
}
