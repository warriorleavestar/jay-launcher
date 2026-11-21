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

/// <summary>
/// Tracks one explicit soft-keyboard request across the short transition from gameplay into a
/// Minecraft text screen. Once that screen has actually been observed, returning to gameplay
/// ends the fallback so hardware movement keys cannot reopen the IME.
/// </summary>
internal sealed class SoftKeyboardInputSession
{
    private const long OpeningTimeoutMilliseconds = 2_000;
    private readonly Func<long> _uptimeMilliseconds;
    private bool _manuallyRequested;
    private bool _enteredTextScreen;
    private long _openingDeadline;

    public SoftKeyboardInputSession(Func<long>? uptimeMilliseconds = null) =>
        _uptimeMilliseconds = uptimeMilliseconds ?? (() => Environment.TickCount64);

    public bool AcceptsConnection(int focusState) =>
        SoftKeyboardInputPolicy.AcceptsConnection(focusState, _manuallyRequested);

    public void Request(bool gameplayActive, int focusState)
    {
        _manuallyRequested = true;
        _enteredTextScreen = !gameplayActive || focusState > 0;
        _openingDeadline = _enteredTextScreen
            ? 0
            : _uptimeMilliseconds() + OpeningTimeoutMilliseconds;
    }

    public void Observe(bool gameplayActive, int focusState)
    {
        if (!_manuallyRequested) return;
        if (!gameplayActive || focusState > 0)
        {
            _enteredTextScreen = true;
            _openingDeadline = 0;
            return;
        }

        if (_enteredTextScreen ||
            (_openingDeadline > 0 && _uptimeMilliseconds() >= _openingDeadline))
            Clear();
    }

    public void Clear()
    {
        _manuallyRequested = false;
        _enteredTextScreen = false;
        _openingDeadline = 0;
    }
}
