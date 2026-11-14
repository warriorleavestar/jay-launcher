using Amber.App.Localization;
using Amber.Core.Calls;

namespace Amber.App.ViewModels;

/// <summary>
/// Settings › Multiplayer › Calls and screen (reserved, redesign batch 11). The group shows only while calls are switched
/// on; the values are kept for when calls exist. "For a child" hides the camera and control settings.
/// </summary>
public sealed partial class SettingsViewModel
{
    internal static readonly string[] CallsQualityKeys = ["auto", "sharp", "smooth"];

    public bool CallsVisible => CallFeature.Enabled;

    private Amber.Core.Safety.ChildModeEffects CallsChild => _service.Settings.ChildMode?.Effects ?? default;

    public bool CallsShowCamera => CallRules.Offered(CallStep.Camera, CallsChild);
    public bool CallsShowControl => CallRules.Offered(CallStep.Control, CallsChild);

    public OptionItem[] CallsWhoOptions { get; } = OptionItem.Keys("settings.calls.who.friends_room", "settings.calls.who.room", "settings.calls.who.nobody");

    /// <summary>Only the system default until calls can list devices.</summary>
    public OptionItem[] CallsDeviceOptions { get; } = OptionItem.Keys("settings.calls.system_default");

    public OptionItem[] CallsQualityOptions { get; } = OptionItem.Keys("settings.calls.quality.auto", "settings.calls.quality.sharp", "settings.calls.quality.smooth");

    public int CallsWhoIndex
    {
        get => (int)CallRules.Parse(_service.Settings.CallsWhoCanCall);
        set => SaveCalls(s => s.CallsWhoCanCall = ((WhoCanCall)Math.Clamp(value, 0, 2)).ToSetting());
    }

    public int CallsMicIndex { get => 0; set { } }
    public int CallsCameraIndex { get => 0; set { } }

    public bool CallsNoise
    {
        get => _service.Settings.CallsNoiseSuppression;
        set => SaveCalls(s => s.CallsNoiseSuppression = value);
    }

    public bool CallsPushToTalk
    {
        get => _service.Settings.CallsPushToTalk;
        set => SaveCalls(s => s.CallsPushToTalk = value);
    }

    public int CallsQualityIndex
    {
        get => Math.Max(0, Array.IndexOf(CallsQualityKeys, _service.Settings.CallsVideoQuality));
        set => SaveCalls(s => s.CallsVideoQuality = CallsQualityKeys[Math.Clamp(value, 0, CallsQualityKeys.Length - 1)]);
    }

    public bool CallsShareGameOnly
    {
        get => _service.Settings.CallsShareGameOnly;
        set => SaveCalls(s => s.CallsShareGameOnly = value);
    }

    public bool CallsAllowControl
    {
        get => _service.Settings.CallsAllowControlRequests;
        set => SaveCalls(s => s.CallsAllowControlRequests = value);
    }

    public bool CallsCustomized => AdvancedCustomized.Contains(Amber.Core.Multiplayer.AdvancedSettings.Calls);

    private void SaveCalls(Action<Amber.App.Services.AppSettings> change)
    {
        change(_service.Settings);
        _service.TrySaveSettings();
        NotifyCalls();
        RefreshAdvanced();
    }

    private void NotifyCalls()
    {
        foreach (var name in (string[])[nameof(CallsWhoIndex), nameof(CallsNoise), nameof(CallsPushToTalk), nameof(CallsQualityIndex),
                     nameof(CallsShareGameOnly), nameof(CallsAllowControl), nameof(CallsShowCamera), nameof(CallsShowControl), nameof(CallsVisible)])
            OnPropertyChanged(name);
    }

    /// <summary>Calls were switched on or off while Jay runs (self-checks): the group and its tag look again.</summary>
    internal void RefreshCalls()
    {
        NotifyCalls();
        RefreshAdvanced();
    }

    /// <summary>[Reset all to defaults] puts the call settings back too.</summary>
    private void ResetCalls(Amber.Core.Multiplayer.AdvancedValues d)
    {
        var s = _service.Settings;
        s.CallsWhoCanCall = d.CallsWho;
        s.CallsNoiseSuppression = d.CallsNoise;
        s.CallsPushToTalk = d.CallsPushToTalk;
        s.CallsVideoQuality = d.CallsQuality;
        s.CallsShareGameOnly = d.CallsShareGameOnly;
        s.CallsAllowControlRequests = d.CallsAllowControl;
        _service.TrySaveSettings();
        NotifyCalls();
    }
}
