using System.Collections.ObjectModel;
using Amber.App.Localization;
using Amber.Core.Auth;
using Amber.Core.Identity;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>A Jay account saved on this computer, as the accounts page lists it.</summary>
public sealed class JayAccountItem(JayAccountEntry entry, string boundTo) : LocalizedObservable
{
    public JayAccountEntry Entry { get; } = entry;
    public string Address => Entry.Address;
    public string ShortAddress => Entry.ShortAddress;
    public string Title => Entry.Label.Length > 0 ? Entry.Label : Entry.ShortAddress;

    /// <summary>Four words people read out to each other to confirm they see the same account (not the recovery phrase).</summary>
    public string CheckWords
    {
        get => L.F("accounts.jay.check_words", JayAddress.CheckWordsText(Entry.Address, L.Language));
    }

    public string BoundText => boundTo.Length > 0 ? L.F("accounts.jay.bound_to", boundTo) : L.T("accounts.jay.not_bound");
}

/// <summary>One numbered word of a new recovery phrase.</summary>
public sealed record MnemonicWord(int Number, string Word);

/// <summary>One "word #N?" question of the backup check.</summary>
public sealed partial class QuizQuestion(int position) : ObservableObject
{
    public int Position { get; } = position;
    public string Label => L.F("accounts.jay.quiz_word", Position + 1);

    [ObservableProperty]
    public partial string Answer { get; set; } = "";
}

public enum JayStep { None, ShowWords, Quiz, Restore }

/// <summary>
/// Jay accounts on the accounts page: create (show 12 words → check 3 of them → save and bind to the current MC account),
/// restore from words, bind / unbind, copy the address, forget. The words live only in memory while the panel is open:
/// they are never saved, logged, copied to the clipboard or put in a status message.
/// </summary>
public sealed partial class AccountsViewModel
{
    private string[] _newWords = [];

    public ObservableCollection<JayAccountItem> JayAccounts { get; } = [];
    public ObservableCollection<MnemonicWord> NewWords { get; } = [];
    public ObservableCollection<QuizQuestion> QuizQuestions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJayAccounts), nameof(ShowJayIntro))]
    public partial int JayAccountCount { get; set; }

    public bool HasJayAccounts => JayAccountCount > 0;
    public bool ShowJayIntro => JayAccountCount == 0 && JayIdle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JayIdle), nameof(JayShowingWords), nameof(JayQuiz), nameof(JayRestoring), nameof(ShowJayIntro))]
    public partial JayStep JayStep { get; set; }

    public bool JayIdle => JayStep == JayStep.None;
    public bool JayShowingWords => JayStep == JayStep.ShowWords;
    public bool JayQuiz => JayStep == JayStep.Quiz;
    public bool JayRestoring => JayStep == JayStep.Restore;

    /// <summary>New phrase in Chinese (default for Chinese UI) or English. Switching makes a new phrase.</summary>
    [ObservableProperty]
    public partial bool JayChineseWords { get; set; }

    [ObservableProperty]
    public partial string NewPassphrase { get; set; } = "";

    [ObservableProperty]
    public partial string NewPassphraseRepeat { get; set; } = "";

    [ObservableProperty]
    public partial string RestoreText { get; set; } = "";

    [ObservableProperty]
    public partial string RestorePassphrase { get; set; } = "";

    /// <summary>Inline error in the create / restore panel (null when fine).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJayError))]
    public partial string? JayError { get; set; }

    public bool HasJayError => JayError is not null;

    /// <summary>"Create a Jay account for Steve?" after adding a Minecraft account that has none; null hides it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJaySuggestion), nameof(JaySuggestionText))]
    public partial Account? JaySuggestionFor { get; set; }

    public bool HasJaySuggestion => JaySuggestionFor is not null && JayIdle;
    public string JaySuggestionText => JaySuggestionFor is { } a ? L.F("accounts.jay.suggest", a.Name) : "";

    partial void OnJayStepChanged(JayStep value) => OnPropertyChanged(nameof(HasJaySuggestion));

    partial void OnJayChineseWordsChanged(bool value)
    {
        if (JayStep == JayStep.ShowWords) NewPhrase();
    }

    private void RebuildJay()
    {
        JayAccounts.Clear();
        foreach (var entry in _service.JayAccounts.Accounts)
        {
            var bound = string.Join(", ", _service.Accounts.Store.Accounts.Where(a => a.JayAddress == entry.Address).Select(a => a.Name));
            JayAccounts.Add(new JayAccountItem(entry, bound));
        }
        JayAccountCount = JayAccounts.Count;
    }

    /// <summary>After adding a Minecraft account: offer a Jay account unless it already has one.</summary>
    internal void OfferJayFor(Account? account)
    {
        if (account is null || account.JayAddress is not null) return;
        // With exactly one Jay account here, binding it is the obvious choice: do it and say so instead of asking.
        if (_service.JayAccounts.Accounts is [var only])
        {
            Bind(account, only.Address);
            return;
        }
        JaySuggestionFor = account;
    }

    [RelayCommand]
    private void DismissJaySuggestion() => JaySuggestionFor = null;

    [RelayCommand]
    private void StartCreateJay()
    {
        ClearJayInputs();
        JayChineseWords = L.Language.StartsWith("zh", StringComparison.Ordinal) || L.Language == "lzh";
        NewPhrase();
        JayStep = JayStep.ShowWords;
    }

    private void NewPhrase()
    {
        _newWords = Bip39.Generate(JayChineseWords ? MnemonicLanguage.ChineseSimplified : MnemonicLanguage.English);
        NewWords.Clear();
        for (var i = 0; i < _newWords.Length; i++) NewWords.Add(new MnemonicWord(i + 1, _newWords[i]));
    }

    [RelayCommand]
    private void WordsWrittenDown()
    {
        if (NewPassphrase != NewPassphraseRepeat)
        {
            JayError = L.T("accounts.jay.error.passphrase_mismatch");
            return;
        }
        JayError = null;
        QuizQuestions.Clear();
        foreach (var position in MnemonicQuiz.Pick(_newWords.Length)) QuizQuestions.Add(new QuizQuestion(position));
        JayStep = JayStep.Quiz;
    }

    [RelayCommand]
    private void BackToWords()
    {
        JayError = null;
        JayStep = JayStep.ShowWords;
    }

    [RelayCommand]
    private void FinishCreateJay()
    {
        if (!MnemonicQuiz.Check(_newWords, [.. QuizQuestions.Select(q => q.Position)], [.. QuizQuestions.Select(q => q.Answer)]))
        {
            JayError = L.T("accounts.jay.error.quiz");
            return;
        }
        using var account = JayAccount.FromMnemonic(_newWords, NewPassphrase.Length > 0 ? NewPassphrase : null);
        SaveAndBind(account, "accounts.status.jay_created");
    }

    [RelayCommand]
    private void StartRestoreJay()
    {
        ClearJayInputs();
        JayStep = JayStep.Restore;
    }

    [RelayCommand]
    private void RestoreJay()
    {
        var words = Bip39.Split(RestoreText);
        try
        {
            using var account = JayAccount.FromMnemonic(words, RestorePassphrase.Length > 0 ? RestorePassphrase : null);
            SaveAndBind(account, "accounts.status.jay_restored");
        }
        catch (MnemonicException e)
        {
            JayError = MnemonicErrorText(e);
        }
    }

    /// <summary>Localized reason for a refused phrase; names the word's position, never echoes other words.</summary>
    public static string MnemonicErrorText(MnemonicException e) => e.Problem switch
    {
        MnemonicProblem.Empty => L.T("accounts.jay.error.empty"),
        MnemonicProblem.WrongLength => L.T("accounts.jay.error.length"),
        MnemonicProblem.UnknownWord => L.F("accounts.jay.error.unknown_word", e.WordIndex + 1),
        MnemonicProblem.MixedLanguages => L.F("accounts.jay.error.mixed", e.WordIndex + 1),
        _ => L.T("accounts.jay.error.checksum"),
    };

    [RelayCommand]
    private void CancelJay()
    {
        ClearJayInputs();
        JayStep = JayStep.None;
    }

    private void SaveAndBind(JayAccount account, string statusKey)
    {
        try
        {
            using var device = MemberIdentity.LoadOrCreate(Amber.App.Services.AppSettings.DataDir);
            _service.JayAccounts.Add(account, "", backedUp: true, device.Device, DeviceName(), DateTimeOffset.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep the words and the step on screen so the user can try again once the folder is free.
            SetStatus(StatusMessage.Failure("common.save_file_failed", e, Amber.App.Services.AppSettings.DataDir, ErrorText.Of(e)));
            return;
        }
        var target = JaySuggestionFor ?? _service.Accounts.Store.Selected;
        ClearJayInputs();
        JayStep = JayStep.None;
        JaySuggestionFor = null;
        if (target is not null && target.JayAddress is null) Bind(target, account.Address, statusKey);
        else
        {
            SetStatus(StatusMessage.Success(statusKey, JayAddress.Short(account.Address)));
            RebuildJay();
        }
    }

    internal static string DeviceName() => JayAccountStore.ThisDeviceName();

    [RelayCommand]
    private void BindJay(JayAccountItem? item)
    {
        if (item is null || _service.Accounts.Store.Selected is not { } account) return;
        Bind(account, item.Address);
    }

    private void Bind(Account account, string address, string statusKey = "accounts.status.jay_bound")
    {
        account.JayAddress = address;
        try { _service.Accounts.Store.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("common.save_file_failed", e, Amber.App.Services.AppSettings.DataDir, ErrorText.Of(e)));
            return;
        }
        JaySuggestionFor = null;
        SetStatus(StatusMessage.Success(statusKey == "accounts.status.jay_bound" ? statusKey : statusKey + "_bound", JayAddress.Short(address), account.Name));
        Changed();
    }

    [RelayCommand]
    private void UnbindJay(AccountItem? item)
    {
        if (item?.Account.JayAddress is null) return;
        item.Account.JayAddress = null;
        _service.Accounts.Store.Save();
        SetStatus(StatusMessage.Success("accounts.status.jay_unbound", item.Name));
        Changed();
    }

    [RelayCommand]
    private async Task CopyJayAddressAsync(JayAccountItem? item)
    {
        if (item is null) return;
        SetStatus(await TryCopyAsync(item.Address)
            ? StatusMessage.Success("accounts.status.jay_address_copied")
            : StatusMessage.Warning("common.copy_failed"));
    }

    /// <summary>Forgets the account on this computer and unbinds it; the recovery phrase still restores it.</summary>
    [RelayCommand]
    private void RemoveJay(JayAccountItem? item)
    {
        if (item is null) return;
        foreach (var account in _service.Accounts.Store.Accounts.Where(a => a.JayAddress == item.Address)) account.JayAddress = null;
        _service.Accounts.Store.Save();
        _service.JayAccounts.Remove(item.Address);
        SetStatus(StatusMessage.Success("accounts.status.jay_removed", item.ShortAddress));
        Changed();
    }

    private void ClearJayInputs()
    {
        // Strings can't be wiped in .NET; dropping every reference is the best we can do.
        _newWords = [];
        NewWords.Clear();
        QuizQuestions.Clear();
        NewPassphrase = NewPassphraseRepeat = RestoreText = RestorePassphrase = "";
        JayError = null;
    }
}
