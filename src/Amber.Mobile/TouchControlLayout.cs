namespace Amber.Mobile;

public enum TouchControlAction
{
    Move, PrimaryAction, SecondaryAction, Jump, Inventory, Menu, Keyboard,
    Sneak, Sprint, Drop, PreviousSlot, NextSlot, Settings
}

public enum TouchControlMode { JoystickAndTap, DPadAndTap, JoystickAndCrosshair }

public sealed record TouchControlDefinition(
    TouchControlAction Action, double CenterX, double CenterY, double Radius, string Label,
    bool GameplayVisible = true, bool MenuVisible = false);

/// <summary>A mutable, resolution-independent touch profile shared by rendering and hit testing.</summary>
public sealed class TouchControlProfile
{
    private readonly Dictionary<(TouchControlMode Mode, TouchControlAction Action), (double X, double Y)> _positions = [];

    public TouchControlMode Mode { get; set; } = TouchControlMode.JoystickAndTap;
    public double Scale { get; set; } = 1;
    public bool ForcedSprint { get; set; } = true;

    public IReadOnlyList<TouchControlDefinition> Controls => TouchControlLayout.For(Mode)
        .Select(Apply).ToArray();

    public TouchControlDefinition? HitTest(double x, double y, bool gameplayControlsActive = true) =>
        Controls.LastOrDefault(control =>
            (gameplayControlsActive ? control.GameplayVisible : control.MenuVisible) &&
            TouchControlLayout.Distance(control, x, y) <= control.Radius);

    public (double X, double Y) MovementVector(double x, double y)
    {
        var move = Controls.Single(control => control.Action == TouchControlAction.Move);
        var vector = TouchControlLayout.MovementVector(move, x, y);
        if (Mode != TouchControlMode.DPadAndTap) return vector;
        var length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y);
        if (length < 0.20) return (0, 0);
        var angle = Math.Atan2(vector.Y, vector.X);
        var snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
        return (Math.Cos(snapped), Math.Sin(snapped));
    }

    public void SetPosition(TouchControlAction action, double x, double y) =>
        SetPosition(Mode, action, x, y);

    public void SetPosition(TouchControlMode mode, TouchControlAction action, double x, double y) =>
        _positions[(mode, action)] = (Math.Clamp(x, 0.04, 0.96), Math.Clamp(y, 0.04, 0.96));

    public bool TryGetPosition(TouchControlAction action, out (double X, double Y) position) =>
        TryGetPosition(Mode, action, out position);

    public bool TryGetPosition(TouchControlMode mode, TouchControlAction action,
        out (double X, double Y) position) => _positions.TryGetValue((mode, action), out position);

    public void ResetPositions()
    {
        foreach (var key in _positions.Keys.Where(key => key.Mode == Mode).ToArray()) _positions.Remove(key);
    }

    private TouchControlDefinition Apply(TouchControlDefinition control)
    {
        var positioned = _positions.TryGetValue((Mode, control.Action), out var position)
            ? control with { CenterX = position.X, CenterY = position.Y }
            : control;
        return positioned with { Radius = Math.Clamp(positioned.Radius * Scale, 0.018, 0.15) };
    }
}

/// <summary>Built-in schemes modelled after the interaction choices familiar on mobile Minecraft.</summary>
public static class TouchControlLayout
{
    private static readonly TouchControlDefinition[] Shared =
    [
        new(TouchControlAction.Keyboard, 0.475, 0.050, 0.026, "聊天与指令", MenuVisible: true),
        new(TouchControlAction.Menu, 0.525, 0.050, 0.026, "菜单", MenuVisible: true),
        new(TouchControlAction.Settings, 0.575, 0.050, 0.026, "触控设置", MenuVisible: true),
        // Keep both actions in the landscape right-thumb arc instead of the unreachable top edge.
        new(TouchControlAction.Jump, 0.940, 0.780, 0.036, "跳跃"),
        new(TouchControlAction.Sneak, 0.860, 0.860, 0.036, "潜行"),
        // The square face starts exactly where the vanilla hotbar ends and shares its centre line.
        new(TouchControlAction.Inventory, 0.608, 0.975, 0.025, "背包"),
        new(TouchControlAction.SecondaryAction, 0.940, 0.455, 0.044, "拆分",
            GameplayVisible: false, MenuVisible: true)
    ];

    private static readonly IReadOnlyDictionary<TouchControlMode, IReadOnlyList<TouchControlDefinition>> Layouts =
        new Dictionary<TouchControlMode, IReadOnlyList<TouchControlDefinition>>
        {
            [TouchControlMode.JoystickAndTap] = [.. Shared,
                new(TouchControlAction.Move, 0.145, 0.735, 0.105, "移动")],
            [TouchControlMode.DPadAndTap] = [.. Shared.Where(control => control.Action != TouchControlAction.Sneak),
                new(TouchControlAction.Move, 0.145, 0.735, 0.105, "方向键"),
                new(TouchControlAction.Sneak, 0.145, 0.735, 0.032, "潜行")],
            [TouchControlMode.JoystickAndCrosshair] = [.. Shared,
                new(TouchControlAction.Move, 0.145, 0.735, 0.105, "移动"),
                new(TouchControlAction.PrimaryAction, 0.845, 0.710, 0.048, "攻击"),
                new(TouchControlAction.SecondaryAction, 0.925, 0.610, 0.048, "使用")]
        };

    public static IReadOnlyList<TouchControlDefinition> Default => For(TouchControlMode.JoystickAndTap);
    public static IReadOnlyList<TouchControlDefinition> For(TouchControlMode mode) => Layouts[mode];
    public static TouchControlDefinition? HitTest(double x, double y, bool gameplayControlsActive = true) =>
        new TouchControlProfile().HitTest(x, y, gameplayControlsActive);
    public static (double X, double Y) JoystickVector(double x, double y) =>
        new TouchControlProfile().MovementVector(x, y);

    internal static (double X, double Y) MovementVector(TouchControlDefinition move, double x, double y)
    {
        var dx = (x - move.CenterX) / move.Radius;
        var dy = (y - move.CenterY) / move.Radius;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length > 1) { dx /= length; dy /= length; }
        return (dx, dy);
    }

    internal static double Distance(TouchControlDefinition control, double x, double y)
    {
        var dx = x - control.CenterX;
        var dy = y - control.CenterY;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
