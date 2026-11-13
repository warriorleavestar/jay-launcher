using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Gate;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One relay in the server's advanced settings.</summary>
public sealed partial class HostRelayItem(GateRelayEntry entry, GateHealth.Relay? health) : ObservableObject
{
    public string Address { get; } = entry.Address;

    public string State { get; } =
        entry.HeartbeatPsk is null ? L.T("multiplayer.hostserver.adv.relay_no_code")
        : health is { Connected: true } ? L.T("multiplayer.hostserver.adv.relay_connected")
        : entry.PublicKey is null ? L.T("multiplayer.hostserver.adv.relay_waiting")
        : L.T("multiplayer.hostserver.adv.relay_offline");

    /// <summary>Revoking drops every player, so the first press only asks.</summary>
    [ObservableProperty] public partial bool ConfirmRevoke { get; set; }
}

/// <summary>
/// The server's advanced settings, the same ones jay-server sets on the command line: privacy level (basic, Privacy+,
/// Privacy++), relays and their codes, entry port rotation and the signed address record. Changes go to the same data
/// folder and a running server picks them up.
/// </summary>
public sealed partial class HostServerViewModel
{
    public ObservableCollection<HostRelayItem> Relays { get; } = [];

    /// <summary>0 basic, 1 Privacy+, 2 Privacy++ (radio buttons in the view, set from its code-behind).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrivacyBasic), nameof(IsPrivacyPlus), nameof(IsPrivacyPlusPlus), nameof(PrivacyExplanation))]
    public partial int PrivacyChoice { get; set; }

    public bool IsPrivacyBasic => PrivacyChoice == 0;

    public bool IsPrivacyPlus => PrivacyChoice == 1;

    public bool IsPrivacyPlusPlus => PrivacyChoice == 2;

    public string PrivacyExplanation => L.T(PrivacyChoice switch
    {
        1 => "multiplayer.hostserver.adv.privacy_plus_hint",
        2 => "multiplayer.hostserver.adv.privacy_plusplus_hint",
        _ => "multiplayer.hostserver.adv.privacy_basic_hint",
    });

    [ObservableProperty] public partial string NewRelayAddress { get; set; } = "";

    /// <summary>The relay code just issued (shown once; it goes to the relay's operator only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRelayCode))]
    public partial string RelayCode { get; set; } = "";

    [ObservableProperty] public partial string RelayCodeFor { get; set; } = "";
    [ObservableProperty] public partial string RelayCommandLine { get; set; } = "";
    public bool HasRelayCode => RelayCode.Length > 0;

    [ObservableProperty] public partial bool RotationEnabled { get; set; }
    [ObservableProperty] public partial string RotationMinutes { get; set; } = (GateRotation.Default.WindowSeconds / 60).ToString();
    [ObservableProperty] public partial string RotationMinPort { get; set; } = GateRotation.Default.MinPort.ToString();
    [ObservableProperty] public partial string RotationMaxPort { get; set; } = GateRotation.Default.MaxPort.ToString();
    [ObservableProperty] public partial string RotationNow { get; set; } = "";

    [ObservableProperty] public partial bool RecordsEnabled { get; set; }

    /// <summary>Result of the last advanced change (kept apart from the wizard's status line).</summary>
    [ObservableProperty] public partial string AdvancedStatus { get; set; } = "";

    /// <summary>Reads the advanced settings into the editable fields (once, when the page loads).</summary>
    private void LoadAdvanced(GateState state)
    {
        _loadingAdvanced = true;
        try { LoadAdvancedFields(state); }
        finally { _loadingAdvanced = false; }
    }

    private void LoadAdvancedFields(GateState state)
    {
        PrivacyChoice = ChoiceOf(state.Privacy);
        if (state.Rotation is { } r)
        {
            RotationMinutes = Math.Max(1, r.WindowSeconds / 60).ToString();
            RotationMinPort = r.MinPort.ToString();
            RotationMaxPort = r.MaxPort.ToString();
        }
        RotationEnabled = state.Rotation is { Enabled: true };
        RecordsEnabled = state.RecordNetwork is not null;
    }

    /// <summary>Refreshes what changes on its own (relay states, current ports) without touching what the user is typing.</summary>
    private void RefreshAdvanced(GateState state)
    {
        var health = s_running?.Health();
        var items = state.Relays.Select(r => new HostRelayItem(r, health?.Relays.FirstOrDefault(h => string.Equals(h.Address, r.Address, StringComparison.OrdinalIgnoreCase)))).ToList();
        if (!items.Select(i => i.Address + i.State).SequenceEqual(Relays.Select(i => i.Address + i.State)))
        {
            Relays.Clear();
            foreach (var item in items) Relays.Add(item);
        }
        PrivacyChoice = ChoiceOf(state.Privacy);
        var store = Store();
        if (store.Rotation(state) is { } rotation)
        {
            var now = DateTimeOffset.UtcNow;
            var ports = new List<string>();
            if (!state.Privacy.IsSplit()) ports.Add(L.F("multiplayer.hostserver.adv.port_direct", rotation.Rule.PortAt(GateRotation.EntrySeed(rotation.Master, GateRotation.DirectEntry), now)));
            ports.AddRange(state.Relays.Select(r => L.F("multiplayer.hostserver.adv.port_relay", r.Address, rotation.Rule.PortAt(GateRotation.EntrySeed(rotation.Master, r.Address), now))));
            var text = L.F("multiplayer.hostserver.adv.rotation_now", string.Join(", ", ports));
            if (state.Rotation?.FixedUntil is { } until && until > now)
                text += " " + L.F("multiplayer.hostserver.adv.rotation_grace", until.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            RotationNow = text;
        }
        else RotationNow = "";
    }

    private static int ChoiceOf(PrivacyLevel level) => level switch
    {
        PrivacyLevel.Plus => 1,
        PrivacyLevel.PlusPlus => 2,
        _ => 0,
    };

    /// <summary>Asks for a privacy level (0 basic, 1 Privacy+, 2 Privacy++); a refused one leaves <see cref="PrivacyChoice"/> as it was.</summary>
    public void ChoosePrivacy(int choice)
    {
        var level = choice switch
        {
            1 => PrivacyLevel.Plus,
            2 => PrivacyLevel.PlusPlus,
            _ => PrivacyLevel.Basic,
        };
        if (choice == PrivacyChoice) return;
        AdvancedChange(() =>
        {
            if (level.IsSplit() && Relays.Count < 2) return L.T("multiplayer.hostserver.adv.plus_needs_relays");
            Store().SetPrivacy(level);
            PrivacyChoice = ChoiceOf(level);
            return L.T(level switch
            {
                PrivacyLevel.Plus => "multiplayer.hostserver.adv.privacy_now_plus",
                PrivacyLevel.PlusPlus => "multiplayer.hostserver.adv.privacy_now_plusplus",
                _ => "multiplayer.hostserver.adv.privacy_now_basic",
            });
        });
    }

    [RelayCommand]
    private void AddRelay()
    {
        var address = GateEndpoint.TryParse(NewRelayAddress.Trim());
        if (address is null)
        {
            AdvancedStatus = L.T("multiplayer.hostserver.adv.bad_relay");
            return;
        }
        AdvancedChange(() =>
        {
            var store = Store();
            store.AddRelay(address, null);
            IssueCode(store, address.ToString());
            NewRelayAddress = "";
            return L.F("multiplayer.hostserver.adv.relay_added", address);
        });
    }

    /// <summary>Issues a fresh code for a relay (the old one stops working).</summary>
    [RelayCommand]
    private void NewRelayCode(HostRelayItem item) => AdvancedChange(() =>
    {
        IssueCode(Store(), item.Address);
        return L.F("multiplayer.hostserver.adv.relay_code_new", item.Address);
    });

    private void IssueCode(GateStore store, string address)
    {
        using var key = store.LoadServerKey();
        RelayCode = store.IssueRelayCode(address, key.PublicKey).Encode();
        RelayCodeFor = L.F("multiplayer.hostserver.adv.relay_code_for", address);
        var config = store.LoadConfig();
        RelayCommandLine = $"jay-server relay --origin {(config.PublicHost.Length > 0 ? config.PublicHost : "<host>")}:{config.ListenPort} --listen {GateEndpoint.TryParse(address)?.Port} --code <...>";
    }

    [RelayCommand]
    private async Task CopyRelayCodeAsync()
    {
        if (RelayCode.Length > 0 && await SensitiveClipboard.Shared.CopyAsync(RelayCode)) AdvancedStatus = L.T("multiplayer.hostserver.adv.relay_code_copied");
    }

    [RelayCommand]
    private void HideRelayCode()
    {
        RelayCode = "";
        RelayCodeFor = "";
        RelayCommandLine = "";
    }

    [RelayCommand]
    private void RemoveRelay(HostRelayItem item) => AdvancedChange(() =>
        Store().RemoveRelay(item.Address) ? L.F("multiplayer.hostserver.adv.relay_removed", item.Address) : L.T("multiplayer.hostserver.adv.relay_missing"));

    /// <summary>First press asks; the second revokes (everyone reconnects with new keys, the relay is out for good).</summary>
    [RelayCommand]
    private void RevokeRelay(HostRelayItem item)
    {
        if (!item.ConfirmRevoke)
        {
            foreach (var other in Relays) other.ConfirmRevoke = false;
            item.ConfirmRevoke = true;
            AdvancedStatus = L.F("multiplayer.hostserver.adv.revoke_confirm", item.Address);
            return;
        }
        AdvancedChange(() =>
            Store().RevokeRelay(item.Address) ? L.F("multiplayer.hostserver.adv.relay_revoked", item.Address) : L.T("multiplayer.hostserver.adv.relay_missing"));
    }

    [RelayCommand]
    private void CancelRevoke(HostRelayItem item)
    {
        item.ConfirmRevoke = false;
        AdvancedStatus = "";
    }

    [RelayCommand]
    private void ApplyRotation()
    {
        if (!int.TryParse(RotationMinutes.Trim(), out var minutes) || !int.TryParse(RotationMinPort.Trim(), out var min) || !int.TryParse(RotationMaxPort.Trim(), out var max)
            || new GateRotation(minutes * 60, min, max) is not { IsValid: true } rule)
        {
            AdvancedStatus = L.T("multiplayer.hostserver.adv.bad_rotation");
            return;
        }
        AdvancedChange(() =>
        {
            Store().EnableRotation(rule, TimeSpan.FromDays(3));
            RotationEnabled = true;
            return L.F("multiplayer.hostserver.adv.rotation_on", minutes, min, max);
        });
    }

    [RelayCommand]
    private void TurnOffRotation() => AdvancedChange(() =>
    {
        Store().DisableRotation();
        RotationEnabled = false;
        return L.T("multiplayer.hostserver.adv.rotation_off");
    });

    partial void OnRecordsEnabledChanged(bool value)
    {
        if (_loadingAdvanced || !JayServerRunner.IsInitialized(DataDirectory)) return;
        var on = Store().Read().RecordNetwork is not null;
        if (on == value) return;
        AdvancedChange(() =>
        {
            var store = Store();
            if (value) store.EnableRecords(Amber.Core.Multiplayer.Public.PublicNetworkProfile.Jay().Id);
            else store.DisableRecords();
            return L.T(value ? "multiplayer.hostserver.adv.records_on" : "multiplayer.hostserver.adv.records_off");
        });
        // A refused change puts the switch back where the store is.
        _loadingAdvanced = true;
        try { RecordsEnabled = Store().Read().RecordNetwork is not null; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        finally { _loadingAdvanced = false; }
    }

    private bool _loadingAdvanced;

    /// <summary>Runs one change against the data folder and reports it; refused changes say why.</summary>
    private void AdvancedChange(Func<string> change)
    {
        try { AdvancedStatus = change(); }
        catch (ArgumentException e) { AdvancedStatus = e.Message; }
        catch (InvalidOperationException e) { AdvancedStatus = e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AdvancedStatus = L.F("common.save_file_failed", JayServerRunner.GateDirectory(DataDirectory), ErrorText.Of(e));
        }
        Refresh();
    }
}
