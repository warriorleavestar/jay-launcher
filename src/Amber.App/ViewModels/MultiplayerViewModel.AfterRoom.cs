namespace Amber.App.ViewModels;

/// <summary>
/// Room options a room opened with stay fixed while it is open (the transport, the whole LAN, what members may route
/// through me). "Reset all to defaults" in Settings › Multiplayer while a room is open puts them back once it closes.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private Action? _afterRoom;

    /// <summary>Runs now when no room is open, else when the room closes (a later call replaces an earlier one).</summary>
    internal void ApplyNowOrAfterRoom(Action apply)
    {
        if (IsIdle) apply();
        else _afterRoom = apply;
    }

    /// <summary>A reset is waiting for the open room to close.</summary>
    internal bool HasPendingAfterRoom => _afterRoom is not null;

    private void RunAfterRoom()
    {
        if (!IsIdle || _afterRoom is not { } apply) return;
        _afterRoom = null;
        apply();
    }
}
