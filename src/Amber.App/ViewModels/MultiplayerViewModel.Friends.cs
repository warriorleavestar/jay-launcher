using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Identity;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Contacts;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// The friend list and the Jay account: back it up locked with the account, restore it on a new computer, and say hello
/// again from the new device so friends who know the account reconnect without comparing emoji.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    public const string RelinkFileName = "friends-relink.json";

    /// <summary>Where to save a backup (the page shows a save dialog; tests set a path).</summary>
    internal Func<Task<string?>>? PickFriendBackupSave { get; set; }

    /// <summary>Which backup to restore (the page shows an open dialog; tests set a path).</summary>
    internal Func<Task<string?>>? PickFriendBackupOpen { get; set; }

    private bool _relinking;

    private string RelinkPath => Path.Combine(ContactsDir, RelinkFileName);

    /// <summary>The selected Minecraft account's Jay account, opened (dispose it). Says why on the status bar when there is none.</summary>
    private JayAccount? OpenSelectedJayAccount()
    {
        if (_service.Accounts.Store.Selected?.JayAddress is not { } address)
        {
            SetStatus(StatusMessage.Warning("multiplayer.friends.needs_jay"));
            return null;
        }
        var account = _service.JayAccounts.Open(address);
        if (account is null) SetStatus(StatusMessage.Warning("multiplayer.friends.cant_open"));
        return account;
    }

    [RelayCommand]
    private async Task BackupFriendsAsync()
    {
        using var account = OpenSelectedJayAccount();
        if (account is null || PickFriendBackupSave is not { } pick) return;
        var count = ContactList.Count;
        if (FriendBackup.Export(account, Contacts.All, DateTimeOffset.UtcNow, Contacts.RemovedDevices) is not { } data)
        {
            SetStatus(StatusMessage.Warning("multiplayer.friends.too_big"));
            return;
        }
        if (await pick() is not { Length: > 0 } path) return;
        try
        {
            await File.WriteAllBytesAsync(path, data);
            SetStatus(StatusMessage.Success("multiplayer.friends.backed_up", count));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.friends.backup_failed", e, ErrorText.Of(e)));
        }
    }

    [RelayCommand]
    private async Task RestoreFriendsAsync()
    {
        using var account = OpenSelectedJayAccount();
        if (account is null || PickFriendBackupOpen is not { } pick) return;
        if (await pick() is not { Length: > 0 } path) return;
        byte[] data;
        try
        {
            if (new FileInfo(path).Length > FriendBackup.MaxSize) throw new InvalidDataException();
            data = await File.ReadAllBytesAsync(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus(StatusMessage.Warning("multiplayer.friends.not_backup"));
            return;
        }
        if (!FriendBackup.LooksLikeBackup(data))
        {
            SetStatus(StatusMessage.Warning("multiplayer.friends.not_backup"));
            return;
        }
        if (FriendBackup.Import(account, data) is not { } backup)
        {
            SetStatus(StatusMessage.Warning("multiplayer.friends.wrong_account"));
            return;
        }
        // Say how old the backup is and who comes back before doing anything: an old backup brings back people removed since.
        var contacts = backup.Contacts;
        var people = contacts.Where(c => !Contacts.IsVerified(c.PublicKey))
            .GroupBy(c => c.JayAccount ?? Convert.ToHexStringLower(c.PublicKey))
            .Select(g => g.First().Name is { Length: > 0 } n ? n : L.T("multiplayer.contacts.no_name"))
            .ToList();
        if (people.Count > 0)
        {
            const int Shown = 8;
            var names = string.Join(L.T("multiplayer.friends.name_separator"), people.Take(Shown)) + (people.Count > Shown ? " \u2026" : "");
            if (!await Confirm(L.T("multiplayer.friends.restore_title"),
                    L.F("multiplayer.friends.restore_body", backup.Created.ToLocalTime().ToString("d"), people.Count, names),
                    L.T("multiplayer.friends.restore_confirm")))
                return;
        }
        var added = Contacts.Import(contacts, backup.Removed);
        if (added == 0)
        {
            SetStatus(StatusMessage.Info("multiplayer.friends.restored_none"));
            return;
        }
        // Every friend in the backup gets a hello from this device, now or once the public network is connected.
        var keys = LoadRelink();
        foreach (var c in contacts) keys.Add(Convert.ToHexStringLower(c.PublicKey));
        SaveRelink(keys);
        SetStatus(StatusMessage.Success("multiplayer.friends.restored", added));
        await RelinkFriendsAsync();
    }

    /// <summary>Mails the hello from this device to every friend still waiting for one (after a restore).</summary>
    internal async Task RelinkFriendsAsync()
    {
        if (_relinking) return;
        var keys = LoadRelink();
        if (keys.Count == 0 || Post is not { } post || Contacts.MyCertificate is null) return;
        _relinking = true;
        var sent = 0;
        try
        {
            var me = Identity.PublicKey;
            foreach (var hex in keys.ToList())
            {
                var key = Convert.FromHexString(hex);
                if (Contacts.StartRelink(me, key, ChatName) is { } hello)
                {
                    try
                    {
                        await post.SendAsync(key, hello, default);
                        sent++;
                    }
                    catch (Exception e) when (IsMailError(e))
                    {
                        Contacts.Dismiss(key);
                        _diag.Add(L.T("multiplayer.public.diagnostics_category"), e.Message);
                        continue;
                    }
                }
                keys.Remove(hex);
            }
            SaveRelink(keys);
            if (sent > 0) SetStatus(StatusMessage.Info("multiplayer.friends.relinked", sent));
        }
        finally { _relinking = false; }
    }

    private HashSet<string> LoadRelink()
    {
        try
        {
            if (File.Exists(RelinkPath) && JsonNode.Parse(File.ReadAllText(RelinkPath)) is JsonArray array)
                return [.. array.Select(n => (string?)n).Where(h => h is { Length: 64 } && h.All(char.IsAsciiHexDigit)).Select(h => h!.ToLowerInvariant())];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        return [];
    }

    private void SaveRelink(HashSet<string> keys)
    {
        try
        {
            if (keys.Count == 0)
            {
                File.Delete(RelinkPath);
                return;
            }
            Directory.CreateDirectory(ContactsDir);
            File.WriteAllText(RelinkPath, new JsonArray([.. keys.Select(k => (JsonNode)k)]).ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
