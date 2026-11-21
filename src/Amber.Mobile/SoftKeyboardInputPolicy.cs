namespace Amber.Mobile;

/// <summary>
/// Decides when Android may expose an input connection to the soft keyboard. Minecraft screens
/// such as the ordinary and hanging sign editors handle text at screen level instead of focusing
/// an EditBox, so an explicit keyboard-button request must remain a valid fallback even when the
/// reflection bridge can positively report that no text widget is focused.
/// </summary>
internal static class SoftKeyboardInputPolicy
{
    public static bool AcceptsConnection(int focusState, bool manuallyRequested) =>
        focusState > 0 || manuallyRequested;
}
