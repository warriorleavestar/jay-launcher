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
        new(TouchControlAction.Menu, 0.07, 0.10, 0.045, "菜单"),
        new(TouchControlAction.Keyboard, 0.18, 0.10, 0.045, "键盘"),
        new(TouchControlAction.Move, 0.16, 0.74, 0.125, "移动"),
        new(TouchControlAction.Sprint, 0.31, 0.72, 0.048, "疾跑"),
        new(TouchControlAction.Inventory, 0.92, 0.12, 0.052, "背包"),
        new(TouchControlAction.Drop, 0.80, 0.12, 0.045, "丢弃"),
        new(TouchControlAction.SecondaryAction, 0.77, 0.72, 0.068, "使用"),
        new(TouchControlAction.PrimaryAction, 0.90, 0.73, 0.078, "攻击"),
        new(TouchControlAction.Jump, 0.88, 0.50, 0.068, "跳跃"),
        new(TouchControlAction.Sneak, 0.72, 0.51, 0.050, "潜行"),
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
