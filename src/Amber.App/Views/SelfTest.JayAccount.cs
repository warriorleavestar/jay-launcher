using Amber.App.ViewModels;
using Amber.Core.Identity;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=jay-account: Jay account on the accounts page. Add an offline account → offered a Jay account →
/// 12 words → backup check (wrong answer refused, right answer creates and binds) → copy address → forget → restore from the
/// same words gives the same address and binds again; a typo names the word's position. No network.
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

        // Leave the test data dir as it was: unbind, forget and remove the offline account.
        Dispatcher.UIThread.Invoke(() =>
        {
            accounts.RemoveJayCommand.Execute(accounts.JayAccounts[0]);
            if (accounts.Accounts.FirstOrDefault(a => a.Name == "JaySelfTest") is { } item) accounts.RemoveCommand.Execute(item);
        });
        _ = steve;
    }
}
