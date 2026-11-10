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
