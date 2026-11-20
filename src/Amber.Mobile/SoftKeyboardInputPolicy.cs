namespace Amber.Mobile;

/// <summary>
/// Decides when Android may expose an input connection to the soft keyboard. An unknown
/// reflection state may use the explicit keyboard button, but must not wake the IME on its own.
/// </summary>
internal static class SoftKeyboardInputPolicy
{
    public static bool AcceptsConnection(int focusState, bool manuallyRequested) =>
        focusState > 0 || (focusState < 0 && manuallyRequested);
}
