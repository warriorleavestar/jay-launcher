using Amber.Core.Activity;
using Amber.App.ViewModels;
using Amber.Core.Identity;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=jay-account: Jay account on the accounts page. Add an offline account → offered a Jay account →
/// 12 words → backup check (wrong answer refused, right answer creates and binds) → copy address → forget → restore from the
/// same words gives the same address and binds again; a typo names the word's position. Then multiplayer (phase 2) with a
/// stand-in room: the knock certificate, the account on the knock card, trusting the account, and the person details. No network.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task JayAccountAsync()
    {
        var accounts = Page<AccountsViewModel>();
        main.CurrentPage = accounts;
        await Task.Delay(500);
        string? copied = null;
        accounts.CopyText = text => { copied = text; return Task.FromResult(true); };

        Dispatcher.UIThread.Invoke(() =>
        {
            accounts.LoginTab = AccountsViewModel.OfflineTab;
            accounts.OfflineName = "JaySelfTest";
            accounts.AddOfflineCommand.Execute(null);
        });
        var steve = Dispatcher.UIThread.Invoke(() => accounts.Accounts.FirstOrDefault(a => a.Name == "JaySelfTest"));
        Check("Jay 账户：加离线账号后提示创建 Jay 账户", await WaitAsync(() => accounts.HasJaySuggestion, 3), accounts.JaySuggestionText);
        await ShotAsync("Jay账户-提示创建");

        var create = await FindAsync<Button>(b => b.Parent?.Parent?.Parent is Border { Name: "JaySuggestion" } && b.Classes.Contains("primary") && b.IsEffectivelyVisible);
        Check("Jay 账户：点【创建】显示 12 个编号的词", Press(create) && await WaitAsync(() => accounts.JayShowingWords && accounts.NewWords.Count == 12, 3),
            $"{accounts.NewWords.Count} 个词");
        var words = Dispatcher.UIThread.Invoke(() => accounts.NewWords.Select(w => w.Word).ToArray());
        Check("Jay 账户：中文界面默认中文词，且是合法助记词", accounts.JayChineseWords && Bip39.IsValid(words));
        await ShotAsync("Jay账户-助记词");
        await LayoutHereAsync("Jay账户助记词");

        Check("Jay 账户：点【我已经抄好了】出 3 道核对题",
            Press(await FindAsync<Button>(b => b.Name == "WordsWrittenButton" && b.IsEffectivelyVisible)) && await WaitAsync(() => accounts.JayQuiz && accounts.QuizQuestions.Count == 3, 3));
        Dispatcher.UIThread.Invoke(() => { foreach (var q in accounts.QuizQuestions) q.Answer = "错"; });
        Press(await FindAsync<Button>(b => b.Name == "FinishJayButton" && b.IsEffectivelyVisible));
        Check("Jay 账户：答错不创建，给出提示", await WaitAsync(() => accounts.HasJayError, 3) && accounts.JayAccountCount == 0, accounts.JayError ?? "");
        await ShotAsync("Jay账户-核对");
        Dispatcher.UIThread.Invoke(() => { foreach (var q in accounts.QuizQuestions) q.Answer = words[q.Position]; });
        Press(await FindAsync<Button>(b => b.Name == "FinishJayButton" && b.IsEffectivelyVisible));
        Check("Jay 账户：答对后创建并绑定到刚加的离线账号",
            await WaitAsync(() => accounts.JayAccountCount == 1 && accounts.JayIdle, 3)
            && Dispatcher.UIThread.Invoke(() => accounts.Accounts.FirstOrDefault(a => a.Name == "JaySelfTest")?.HasJay == true),
            accounts.StatusInfo?.ToString() ?? "");
        var address = Dispatcher.UIThread.Invoke(() => accounts.JayAccounts[0].Address);
        Check("Jay 账户：创建后助记词从内存里的面板清掉", accounts.NewWords.Count == 0 && accounts.QuizQuestions.Count == 0);
        await ShotAsync("Jay账户-已创建");
        await LayoutHereAsync("Jay账户已创建");

        var copy = await FindAsync<Button>(b => b.DataContext is JayAccountItem && b.Content as string == Amber.Core.Localization.L.T("accounts.jay.copy_address") && b.IsEffectivelyVisible);
        Check("Jay 账户：【复制地址】复制的是 jay1 地址", Press(copy) && await WaitAsync(() => copied == address, 3) && JayAddress.IsValid(copied), copied ?? "");

        Dispatcher.UIThread.Invoke(() => accounts.RemoveJayCommand.Execute(accounts.JayAccounts[0]));
        Check("Jay 账户：从这台电脑移除后同时解除绑定",
            await WaitAsync(() => accounts.JayAccountCount == 0, 3)
            && Dispatcher.UIThread.Invoke(() => accounts.Accounts.FirstOrDefault(a => a.Name == "JaySelfTest")?.HasJay == false));

        Press(await FindAsync<Button>(b => b.Name == "RestoreJayButton" && b.IsEffectivelyVisible));
        await WaitAsync(() => accounts.JayRestoring, 3);
        var typo = words.ToArray();
        typo[4] = "zzzz";
        Dispatcher.UIThread.Invoke(() => accounts.RestoreText = string.Join(' ', typo));
        Press(await FindAsync<Button>(b => b.Name == "RestoreJayConfirm" && b.IsEffectivelyVisible));
        Check("Jay 账户：恢复时写错一个词，提示是第几个", await WaitAsync(() => accounts.JayError?.Contains('5') == true, 3), accounts.JayError ?? "");
        await ShotAsync("Jay账户-恢复出错");
        Dispatcher.UIThread.Invoke(() => accounts.RestoreText = string.Concat(words));
        Press(await FindAsync<Button>(b => b.Name == "RestoreJayConfirm" && b.IsEffectivelyVisible));
        Check("Jay 账户：同一组词（不加空格）恢复出同一个地址，并重新绑定",
            await WaitAsync(() => accounts.JayAccountCount == 1, 3) && Dispatcher.UIThread.Invoke(() => accounts.JayAccounts[0].Address) == address
            && Dispatcher.UIThread.Invoke(() => accounts.Accounts.FirstOrDefault(a => a.Name == "JaySelfTest")?.HasJay == true));
        await ShotAsync("Jay账户-已恢复");
        await JayAccountMultiplayerAsync(address);

        // Leave the test data dir as it was: unbind, forget and remove the offline account.
        Dispatcher.UIThread.Invoke(() =>
        {
            accounts.RemoveJayCommand.Execute(accounts.JayAccounts[0]);
            if (accounts.Accounts.FirstOrDefault(a => a.Name == "JaySelfTest") is { } item) accounts.RemoveCommand.Execute(item);
        });
        _ = steve;
    }

    private async Task JayAccountMultiplayerAsync(string? address)
    {
        var vm = Page<MultiplayerViewModel>();
        var cert = Dispatcher.UIThread.Invoke(() => vm.JayCertificate);
        Check("Jay 账户联机：敲门带的证书是这个账户签给本机设备钥匙的",
            cert is not null && cert.Certificate.Account == address
            && Dispatcher.UIThread.Invoke(() => cert.Certificate.Verify(vm.Identity.PublicKey) && cert.Certificate.Names(vm.Identity.Device)),
            cert?.Certificate.Account ?? "none");

        main.Shell.Navigate("play");
        await Task.Delay(400);
        var page = await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible);
        if (page is null || address is null)
        {
            Check("Jay 账户联机：联机页显示", false);
            return;
        }
        T? Named<T>(string name) where T : Control => page.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        var key = new byte[32];
        key[0] = 7;
        Dispatcher.UIThread.Invoke(() => vm.Mode = "host");
        await Task.Delay(300);

        var book = new InviteBook();
        var request = new KnockRequest("Casey", key, "Jay 0.8", false, book.Create(1, null, false, TunnelSettings.Default)) { JayAccount = address };
        var decision = vm.AskApprovalAsync(request, CancellationToken.None);
        await WaitAsync(() => vm.HasKnocks, 5);
        var card = Dispatcher.UIThread.Invoke(() => vm.ApprovalCards.FirstOrDefault());
        Check("Jay 账户联机：敲门卡片写出对方的 Jay 账户",
            card is not null && card.Facts.Contains(JayAddress.Short(address)), card?.Facts ?? "");
        Check("Jay 账户联机：卡片说明信任后 TA 的其他设备也能直接进",
            await WaitAsync(() => Named<TextBlock>("KnockJayHint") is { IsEffectivelyVisible: true } hint && hint.Text == L.T("multiplayer.knock.jay_hint"), 3));
        await ShotAsync("Jay账户-敲门卡片");
        Dispatcher.UIThread.Invoke(() =>
        {
            card!.TrustAfter = true;
            vm.ApproveKnockCommand.Execute(card);
        });
        var knockLeft = await WaitAsync(() => !main.Service.Requests.Pending.Any(r => r.Spec.Kind == "knock"), 3);
        Check("Jay 账户联机：批准后请求条里的敲门也收起", knockLeft, string.Join(", ", main.Service.Requests.Pending.Select(r => r.Spec.Kind + ":" + r.Spec.Title)));
        Check("Jay 账户联机：批准并信任后，信任列表记下的是账户",
            await decision && Dispatcher.UIThread.Invoke(() => vm.TrustedPeople.Any(t => t.Member.JayAccount == address && t.Label.Contains(JayAddress.Short(address)))));

        Dispatcher.UIThread.Invoke(() =>
        {
            vm.Members.Add(new MemberItem("Casey", key, true, true) { JayAccount = address });
            vm.RefreshPeople();
            vm.SelectedPerson = vm.People.FirstOrDefault(p => p.Name == "Casey");
        });
        Check("Jay 账户联机：房间里选中这个人，详情里有完整的 Jay 地址",
            await WaitAsync(() => Named<SelectableTextBlock>("PersonJayAccount") is { IsEffectivelyVisible: true } text && text.Text == address, 3));
        await ShotAsync("Jay账户-房间成员");

        Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var item in vm.TrustedPeople.Where(t => t.Member.JayAccount == address).ToList()) vm.RemoveTrustedCommand.Execute(item);
            vm.Members.Clear();
            vm.Mode = "";
        });
        await JayFriendsAsync(vm, page);
        Check("Jay 账户联机：取消信任时这个账户的设备一起移除", Dispatcher.UIThread.Invoke(() => vm.TrustedPeople.All(t => t.Member.JayAccount != address)));
        main.CurrentPage = Page<AccountsViewModel>();
        await Task.Delay(300);
    }

    /// <summary>
    /// Contacts as people with devices (Jay accounts group devices), removing one device, and the friend list backup:
    /// saved locked with the bound Jay account, restored into an empty list, with a hello from this device queued for later.
    /// </summary>
    private async Task JayFriendsAsync(MultiplayerViewModel vm, MultiplayerPage page)
    {
        Control? Shown(string name) => page.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
        var dir = Path.Combine(reportDir, "jay-friends");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        using var bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        byte[] Key(byte n)
        {
            var k = new byte[32];
            k[0] = n;
            return k;
        }
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.ContactsDir = dir;
            vm.Tab = MultiplayerTab.Settings;
            vm.Contacts.Import([
                new Amber.Core.Multiplayer.Contacts.Contact(Key(1), "Bob", true, DateTimeOffset.UtcNow.AddDays(-3)) { JayAccount = bob.Address },
                new Amber.Core.Multiplayer.Contacts.Contact(Key(2), "Bob", true, DateTimeOffset.UtcNow) { JayAccount = bob.Address },
                new Amber.Core.Multiplayer.Contacts.Contact(Key(3), "Sam", true, DateTimeOffset.UtcNow),
            ]);
        });
        await Task.Delay(400);
        var bobItem = Dispatcher.UIThread.Invoke(() => vm.ContactList.FirstOrDefault(c => c.Name == "Bob"));
        Check("好友：同一个 Jay 账户的两台设备算一个人", Dispatcher.UIThread.Invoke(() => vm.ContactList.Count) == 2 && bobItem is { HasSeveralDevices: true }
            && bobItem.Devices.Count == 2 && bobItem.JayText.Contains(JayAddress.Short(bob.Address)), string.Join(", ", vm.ContactList.Select(c => c.Name)));
        Dispatcher.UIThread.Invoke(() => Shown("ContactListView")?.BringIntoView());
        Check("好友：这个人下面列出每台设备，名下写 Jay 账户", await WaitAsync(() => Shown("ContactDevices") is not null && Shown("ContactJay") is not null, 3));
        await ShotAsync("Jay账户-好友人和设备");

        Dispatcher.UIThread.Invoke(() => vm.RemoveContactDeviceCommand.Execute(bobItem!.DeviceItems.First(d => d.Device.PublicKey[0] == 1)));
        Check("好友：移除一台设备，人还在", await WaitAsync(() => vm.ContactList.FirstOrDefault(c => c.Name == "Bob") is { HasSeveralDevices: false }, 3)
            && vm.ContactList.Count == 2);

        var backup = Path.Combine(dir, "friends" + Amber.Core.Multiplayer.Contacts.FriendBackup.FileExtension);
        vm.PickFriendBackupSave = () => Task.FromResult<string?>(backup);
        vm.PickFriendBackupOpen = () => Task.FromResult<string?>(backup);
        await Dispatcher.UIThread.InvokeAsync(() => vm.BackupFriendsCommand.ExecuteAsync(null));
        Check("好友：【备份联系人】存下一份用 Jay 账户锁住的文件", File.Exists(backup)
            && Amber.Core.Multiplayer.Contacts.FriendBackup.LooksLikeBackup(File.ReadAllBytes(backup))
            && !File.ReadAllText(backup).Contains("Sam"), vm.Status);

        Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var c in vm.ContactList.ToList()) vm.RemoveContactCommand.Execute(c);
        });
        await WaitAsync(() => !vm.HasContacts, 3);
        string? asked = null;
        var confirm = vm.Confirm;
        vm.Confirm = (_, body, _) =>
        {
            asked = body;
            return Task.FromResult(true);
        };
        await Dispatcher.UIThread.InvokeAsync(() => vm.RestoreFriendsCommand.ExecuteAsync(null));
        vm.Confirm = confirm;
        Check("好友：【恢复联系人】先写明备份日期和会回来的人", asked is not null && asked.Contains("Bob") && asked.Contains("Sam")
            && asked.Contains(DateTimeOffset.Now.ToString("d")), asked ?? "");
        Check("好友：【恢复联系人】把人带回来，并记下要从这台设备打招呼",
            await WaitAsync(() => vm.ContactList.Count == 2, 3) && File.Exists(Path.Combine(dir, MultiplayerViewModel.RelinkFileName)), vm.Status);

        // Bob knocks from a computer I've never seen: the knock proved his account live (both account signatures and a fresh
        // device signature), so the device is added without the emoji, and I'm told.
        using (var bobDevice = DeviceKeys.Generate())
        {
            var bobNew = bobDevice.Dh;
            var me = Dispatcher.UIThread.Invoke(() => vm.Contacts.Me);
            var bobBook = new Amber.Core.Multiplayer.Contacts.ContactBook(Path.Combine(dir, "bob-new.json")) { Me = bobNew.PublicKey };
            Dispatcher.UIThread.Invoke(() => vm.Contacts.SeenLive(bobNew.PublicKey, bob.CertifyDevice(bobDevice, "Linux", 0, DateTimeOffset.UtcNow)));
            var notice = L.F("multiplayer.contacts.new_device", "Bob");
            RequestHandle? Notice() => main.Service.Requests.Pending.FirstOrDefault(r => r.Spec.Kind == "contact-new-device");
            Check("好友：认识的人换了新设备，敲门时账户验过就加上，并在请求条里留一条安全提示",
                await WaitAsync(() => Notice() is { Spec.IsSafety: true, Spec.Timeout: null } n && n.Spec.Title == notice
                    && n.Spec.Body?.Contains("Linux") == true && vm.ContactList.FirstOrDefault(c => c.Name == "Bob") is { HasSeveralDevices: true }, 3),
                string.Join(", ", main.Service.Requests.Pending.Select(r => r.Spec.Kind + ":" + r.Spec.Title)));
            Check("好友：新设备在设备列表里标着“待你确认”",
                Dispatcher.UIThread.Invoke(() => vm.ContactList.First(c => c.Name == "Bob").DeviceItems.Any(d => d.Fingerprint.Contains(L.T("multiplayer.contacts.new_device_tag")))));
            await ShotAsync("Jay账户-好友新设备");
            Dispatcher.UIThread.Invoke(() => main.Service.Requests.Resolve(Notice()!, "remove", RequestSource.Bar));
            Check("好友：点“不是 TA？移除这台设备”，设备没了，它再敲门也要重新对表情",
                await WaitAsync(() => vm.ContactList.FirstOrDefault(c => c.Name == "Bob") is { HasSeveralDevices: false }, 3)
                && vm.Contacts.RemovedDevices.Any(r => r.PublicKey.AsSpan().SequenceEqual(bobNew.PublicKey))
                && vm.Contacts.Receive(bobNew.PublicKey, bobBook.StartAdd(bobNew.PublicKey, me, "Bob")!, "Me") is null
                && vm.Contacts.SeenLive(bobNew.PublicKey, bob.CertifyDevice(bobDevice, "Linux", 0, DateTimeOffset.UtcNow)) is null
                && vm.Contacts.Pending.Any(p => p.PublicKey.AsSpan().SequenceEqual(bobNew.PublicKey)), vm.Status);
            vm.Contacts.Dismiss(bobNew.PublicKey);
        }

        var tampered = File.ReadAllBytes(backup);
        tampered[^5] ^= 1;
        File.WriteAllBytes(backup, tampered);
        await Dispatcher.UIThread.InvokeAsync(() => vm.RestoreFriendsCommand.ExecuteAsync(null));
        Check("好友：被改过的备份不认", vm.Status == L.T("multiplayer.friends.wrong_account"), vm.Status);
        await ShotAsync("Jay账户-好友备份");

        Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var c in vm.ContactList.ToList()) vm.RemoveContactCommand.Execute(c);
            vm.Tab = MultiplayerTab.Play;
        });
    }
}
