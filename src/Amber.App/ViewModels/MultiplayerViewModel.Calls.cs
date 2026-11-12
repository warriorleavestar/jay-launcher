using Amber.Core.Calls;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// The room's side of calls (redesign batch 11, reserved; hidden while calls are off): [Voice] on the banner joins room
/// voice, [Share my screen with them] on a member. Remote help folds into sharing: with calls on, letting someone control
/// is the last step of a call instead of a separate entry.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    /// <summary>Set by the main view model.</summary>
    public CallViewModel? Call
    {
        get;
        set
        {
            if (field is null)
                PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(ShowRoomHeader) or nameof(IsIdle) or nameof(SelectedPerson) or nameof(ShowShareGroup)) NotifyCallButtons();
                };
            field = value;
            if (value is not null)
                value.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(CallViewModel.IsInCall) or nameof(CallViewModel.Available)) NotifyCallButtons(); };
            OnPropertyChanged();
            NotifyCallButtons();
        }
    }

    /// <summary>[Voice] on the room banner: in a room, calls on, not already in room voice.</summary>
    public bool ShowRoomVoice => Call?.Available == true && ShowRoomHeader && !IsIdle;
    public bool CanJoinRoomVoice => ShowRoomVoice && Call?.IsInCall != true;

    /// <summary>[Share my screen with them] on the selected member.</summary>
    public bool ShowShareScreenWith => Call?.Available == true && ShowShareGroup;

    private void NotifyCallButtons()
    {
        OnPropertyChanged(nameof(ShowRoomVoice));
        OnPropertyChanged(nameof(CanJoinRoomVoice));
        OnPropertyChanged(nameof(ShowShareScreenWith));
        OnPropertyChanged(nameof(ShowRemoteHelp));
    }

    [RelayCommand]
    private void JoinRoomVoice()
    {
        if (!CanJoinRoomVoice) return;
        var others = People.Where(p => !p.IsMe).Select(p => p.Name).ToList();
        Call!.JoinRoomVoice(RoomTitleForCall, others, RoomRouteForCall);
    }

    [RelayCommand]
    private void ShareScreenWith()
    {
        if (ShowShareScreenWith && SelectedPerson is { } person) Call!.ShareWith(person.Name, RoomRouteForCall);
    }

    private CallRoute RoomRouteForCall => MyRelayOnly ? CallRoute.Relay : CallRoute.Direct;

    private string RoomTitleForCall => PublishRoomName.Trim().Length > 0 ? PublishRoomName.Trim() : RoomTitle;
}
