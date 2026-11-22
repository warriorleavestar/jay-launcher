using System.Text;

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

    /// <summary>
    /// SDL starts text input when Minecraft focuses an editor, including editors that screens
    /// focus automatically while opening. Android should only show its IME after the player's
    /// touch actually landed on that editor; otherwise opening the world list covers it with a
    /// keyboard even though the player tapped the Singleplayer button on the preceding screen.
    /// </summary>
    public static bool ShouldOpenAfterTouch(bool textInputActive,
        bool touchedFocusedEditorBeforeClick, bool focusedEditorAtTouchAfterClick) =>
        textInputActive && (touchedFocusedEditorBeforeClick || focusedEditorAtTouchAfterClick);
}

/// <summary>Keeps IME composition until Android explicitly commits or finishes it.</summary>
internal sealed class AndroidCompositionBuffer
{
    private string _pending = "";

    public void Set(string? text) => _pending = text ?? "";

    public void Clear() => _pending = "";

    public string Take()
    {
        var value = _pending;
        _pending = "";
        return value;
    }
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

/// <summary>
/// Mirrors the text currently owned by Android's IME so SDL games that do not redraw their own
/// editor after an <c>adjustResize</c> can still show the player what they are typing. This is a
/// presentation buffer only; the authoritative text continues to be delivered to Minecraft.
/// </summary>
internal sealed class SoftKeyboardPreviewBuffer
{
    private readonly List<Rune> _committed = [];
    private string _composing = "";

    public bool IsActive { get; private set; }
    public string Text => string.Concat(_committed.Select(rune => rune.ToString())) + _composing;

    public void Begin()
    {
        IsActive = true;
        ClearText();
    }

    public void End()
    {
        IsActive = false;
        ClearText();
    }

    public void SetComposingText(string? text) =>
        _composing = FilterDisplayText(text);

    public void Commit(IEnumerable<Rune> runes)
    {
        _composing = "";
        foreach (var rune in runes)
        {
            if (rune.Value is '\r' or '\n' or '\t') continue;
            if (!Rune.IsControl(rune)) _committed.Add(rune);
        }
    }

    public void Backspace(int count)
    {
        _composing = "";
        while (count-- > 0 && _committed.Count > 0)
            _committed.RemoveAt(_committed.Count - 1);
    }

    public void ClearText()
    {
        _committed.Clear();
        _composing = "";
    }

    private static string FilterDisplayText(string? text) => string.Concat(
        (text ?? "").EnumerateRunes()
            .Where(rune => rune.Value is not ('\r' or '\n' or '\t') && !Rune.IsControl(rune))
            .Select(rune => rune.ToString()));
}
