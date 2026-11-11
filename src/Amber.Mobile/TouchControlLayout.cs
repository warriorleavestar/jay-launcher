namespace Amber.Mobile;

public enum TouchControlAction
{
    Move,
    PrimaryAction,
    SecondaryAction,
    Jump,
    Inventory,
    Menu,
    Keyboard,
    Sneak,
    Sprint,
    Drop,
    PreviousSlot,
    NextSlot
}

public sealed record TouchControlDefinition(
    TouchControlAction Action,
    double CenterX,
    double CenterY,
    double Radius,
    string Label);

/// <summary>
/// Resolution-independent controls for Jay's Android game surface. Android rendering and
/// the future GLFW bridge consume the same normalized layout, so hit testing stays testable.
/// </summary>
public static class TouchControlLayout
{
    public static IReadOnlyList<TouchControlDefinition> Default { get; } =
    [
        new(TouchControlAction.Menu, 0.07, 0.10, 0.045, "Esc"),
        new(TouchControlAction.Keyboard, 0.17, 0.10, 0.045, "ABC"),
        new(TouchControlAction.Move, 0.16, 0.74, 0.125, ""),
        new(TouchControlAction.Sprint, 0.31, 0.72, 0.045, "Run"),
        new(TouchControlAction.Inventory, 0.92, 0.12, 0.050, "E"),
        new(TouchControlAction.Drop, 0.82, 0.12, 0.040, "Q"),
        new(TouchControlAction.SecondaryAction, 0.79, 0.72, 0.065, "Use"),
        new(TouchControlAction.PrimaryAction, 0.90, 0.73, 0.075, "Hit"),
        new(TouchControlAction.Jump, 0.88, 0.52, 0.065, "Jump"),
        new(TouchControlAction.Sneak, 0.72, 0.52, 0.045, "Sneak"),
        new(TouchControlAction.PreviousSlot, 0.43, 0.10, 0.035, "<"),
        new(TouchControlAction.NextSlot, 0.57, 0.10, 0.035, ">")
    ];

    public static TouchControlDefinition? HitTest(double x, double y) =>
        Default.LastOrDefault(control => Distance(control, x, y) <= control.Radius);

    public static (double X, double Y) JoystickVector(double x, double y)
    {
        var move = Default.Single(control => control.Action == TouchControlAction.Move);
        var dx = (x - move.CenterX) / move.Radius;
        var dy = (y - move.CenterY) / move.Radius;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length > 1)
        {
            dx /= length;
            dy /= length;
        }
        return (dx, dy);
    }

    private static double Distance(TouchControlDefinition control, double x, double y)
    {
        var dx = x - control.CenterX;
        var dy = y - control.CenterY;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
