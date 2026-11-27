namespace Amber.Mobile;

/// <summary>
/// Recovers the changed mouse button from Android drivers that leave MotionEvent.ActionButton
/// empty. ButtonState is the state after the transition, so releases must compare it with the
/// preceding event instead of assuming every packet is a primary click.
/// </summary>
public sealed class HardwareMouseButtonTracker
{
    public const int Primary = 1;
    public const int Secondary = 2;
    public const int Tertiary = 4;
    private const int KnownButtons = Primary | Secondary | Tertiary;
    private int _buttons;

    public int Resolve(int actionButton, int buttonState, bool pressed, bool primaryFallback)
    {
        var current = buttonState & KnownButtons;
        var resolved = actionButton & KnownButtons;
        if (resolved == 0)
        {
            var changed = pressed ? current & ~_buttons : _buttons & ~current;
            resolved = First(changed);
        }
        if (resolved == 0 && primaryFallback) resolved = Primary;
        _buttons = current;
        return resolved;
    }

    public void Reset() => _buttons = 0;

    private static int First(int buttons) =>
        (buttons & Primary) != 0 ? Primary :
        (buttons & Secondary) != 0 ? Secondary :
        (buttons & Tertiary) != 0 ? Tertiary : 0;
}
