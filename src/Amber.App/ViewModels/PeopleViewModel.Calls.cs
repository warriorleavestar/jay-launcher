using Amber.Core.Calls;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// A friend's conversation gets [Voice] [Video] [Share screen] (redesign batch 11, reserved; hidden while calls are off).
/// A call rides on the link messages use, so it is possible exactly when a message is; video is not offered for a child.
/// </summary>
public sealed partial class PeopleViewModel
{
    /// <summary>Set by the main view model.</summary>
    public CallViewModel? Call
    {
        get;
        set
        {
            field = value;
            if (value is not null)
                value.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(CallViewModel.IsInCall) or nameof(CallViewModel.Available) or nameof(CallViewModel.OffersVideo)) NotifyCallButtons();
                    if (e.PropertyName == nameof(CallViewModel.IsInCall)) ShowCallee();
                };
            OnPropertyChanged();
            NotifyCallButtons();
        }
    }

    public bool ShowCallButtons => Call?.Available == true && HasSelected;
    public bool ShowVideoButton => ShowCallButtons && Call?.OffersVideo == true;
    public bool CanCall => ShowCallButtons && CanMessage && Call?.IsInCall != true;

    internal void NotifyCallButtons()
    {
        OnPropertyChanged(nameof(ShowCallButtons));
        OnPropertyChanged(nameof(ShowVideoButton));
        OnPropertyChanged(nameof(CanCall));
    }

    /// <summary>A call with a friend (an answered ring too) opens that friend's conversation, where the panel is.</summary>
    private void ShowCallee()
    {
        if (Call is { ShowInPeople: true, Peer: { Length: > 0 } peer } && Selected?.Name != peer
            && Friends.FirstOrDefault(f => f.Name == peer) is { } row)
            Selected = row;
    }

    [RelayCommand] private void CallVoice() => StartCall(CallStep.Voice);
    [RelayCommand] private void CallVideo() => StartCall(CallStep.Camera);
    [RelayCommand] private void CallShare() => StartCall(CallStep.Screen);

    private void StartCall(CallStep step)
    {
        if (Selected is { } row && CanCall) Call!.StartWithFriend(row.Name, step);
        NotifyCallButtons();
    }
}
