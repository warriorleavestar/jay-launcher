namespace Amber.Mobile;

public enum TouchControlAction
{
    Move, PrimaryAction, SecondaryAction, Jump, Inventory, Menu, Keyboard,
    Sneak, Sprint, Drop, PreviousSlot, NextSlot, PickBlock, Settings, Close
}

public enum TouchControlMode { JoystickAndTap, DPadAndTap, JoystickAndCrosshair, ExternalKeyboardMouse }

public enum TouchAttackArea { Anywhere, RightSide, Crosshair }

public sealed record TouchControlDefinition(
    TouchControlAction Action, double CenterX, double CenterY, double Radius, string Label,
    bool GameplayVisible = true, bool MenuVisible = false);

/// <summary>A mutable, resolution-independent touch profile shared by rendering and hit testing.</summary>
public sealed class TouchControlProfile
{
    private readonly Dictionary<(TouchControlMode Mode, TouchControlAction Action), (double X, double Y)> _positions = [];

    public TouchControlMode Mode { get; set; } = TouchControlMode.DPadAndTap;
    public TouchAttackArea AttackArea { get; set; } = TouchAttackArea.Anywhere;
    public double Scale { get; set; } = 1;
    public double LookSensitivity { get; set; } = 1.65;
    public bool ForcedSprint { get; set; } = true;
    public double ViewportAspectRatio { get; set; } = 16d / 9d;
    public int ViewportWidthPixels { get; set; }
    public int ViewportHeightPixels { get; set; }
    public int MinecraftGuiScale { get; set; }

    public IReadOnlyList<TouchControlDefinition> Controls => TouchControlLayout.For(Mode)
        .Select(Apply).ToArray();

    public TouchControlDefinition? HitTest(double x, double y, bool gameplayControlsActive = true) =>
        Controls.LastOrDefault(control =>
            (gameplayControlsActive ? control.GameplayVisible : control.MenuVisible) &&
            TouchControlLayout.Contains(control, x, y, ViewportAspectRatio));

    public bool AllowsAttackAt(double x, double y) => AttackArea switch
    {
        TouchAttackArea.Anywhere => true,
        TouchAttackArea.RightSide => x >= 0.34,
        TouchAttackArea.Crosshair => Math.Pow((x - 0.5) * ViewportAspectRatio, 2) +
                                     Math.Pow(y - 0.5, 2) <= 0.12 * 0.12,
        _ => true
    };

    public (double X, double Y) MovementVector(double x, double y)
    {
        var move = Controls.Single(control => control.Action == TouchControlAction.Move);
        var vector = TouchControlLayout.MovementVector(move, x, y, ViewportAspectRatio);
        if (Mode != TouchControlMode.DPadAndTap) return vector;
        var length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y);
        if (length < 0.20) return (0, 0);
        var angle = Math.Atan2(vector.Y, vector.X);
        var snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
        return (Math.Cos(snapped), Math.Sin(snapped));
    }

    public void SetPosition(TouchControlAction action, double x, double y) =>
        SetPosition(Mode, action, x, y);

    public void SetPosition(TouchControlMode mode, TouchControlAction action, double x, double y)
    {
        // Inventory is part of the Minecraft HUD, not a free-floating action button. Keeping a
        // saved coordinate here detaches it from slot 9 whenever aspect ratio or scale changes.
        if (action == TouchControlAction.Inventory)
        {
            _positions.Remove((mode, action));
            return;
        }
        _positions[(mode, action)] = (Math.Clamp(x, 0.04, 0.96), Math.Clamp(y, 0.04, 0.96));
    }

    public bool TryGetPosition(TouchControlAction action, out (double X, double Y) position) =>
        TryGetPosition(Mode, action, out position);

    public bool TryGetPosition(TouchControlMode mode, TouchControlAction action,
        out (double X, double Y) position)
    {
        if (action != TouchControlAction.Inventory)
            return _positions.TryGetValue((mode, action), out position);
        position = default;
        return false;
    }

    public void ResetPositions()
    {
        foreach (var key in _positions.Keys.Where(key => key.Mode == Mode).ToArray()) _positions.Remove(key);
    }

    private TouchControlDefinition Apply(TouchControlDefinition control)
    {
        var radius = Math.Clamp(control.Radius * Scale, 0.018, 0.15);
        var position = default((double X, double Y));
        var hasCustomPosition = control.Action != TouchControlAction.Inventory &&
            _positions.TryGetValue((Mode, control.Action), out position);
        var positioned = hasCustomPosition
            ? control with { CenterX = position.X, CenterY = position.Y }
            : control;
        if (control.Action == TouchControlAction.Inventory)
        {
            // Minecraft's automatic GUI scale can make the hotbar more than twice as wide as
            // scale 3 on a tablet. Anchor to the live framebuffer/GUI scale rather than a fixed
            // aspect-ratio estimate, and make this face exactly one Minecraft slot high.
            var hud = ViewportWidthPixels > 0 && ViewportHeightPixels > 0 && MinecraftGuiScale > 0
                ? TouchHudGeometry.ForViewport(
                    ViewportWidthPixels, ViewportHeightPixels, MinecraftGuiScale, radius)
                : TouchHudGeometry.ForAspectRatio(ViewportAspectRatio, radius);
            return control with
            {
                CenterX = hud.InventoryX,
                CenterY = hud.CenterY,
                Radius = hud.InventoryRadius
            };
        }
        return positioned with { Radius = radius };
    }
}

/// <summary>Built-in schemes modelled after the interaction choices familiar on mobile Minecraft.</summary>
public static class TouchControlLayout
{
    // TouchMajor is the contact ellipse's full major axis in pixels. Leave clearance
    // beyond it, including when the digitizer supplies no useful contact measurement.
    public static float MiningRingRadius(float density, float shortEdge, float touchMajor)
    {
        var minimum = Math.Max(80f * density, shortEdge * 0.085f);
        var contact = float.IsFinite(touchMajor) ? Math.Clamp(touchMajor, 0, 160f * density) : 0;
        return Math.Max(minimum, contact / 2f + 18f * density);
    }

    private static readonly TouchControlDefinition[] Shared =
    [
        new(TouchControlAction.Keyboard, 0.475, 0.050, 0.026, "聊天与指令", MenuVisible: true),
        new(TouchControlAction.Menu, 0.525, 0.050, 0.026, "菜单"),
        new(TouchControlAction.Settings, 0.575, 0.050, 0.026, "触控设置", MenuVisible: true),
        new(TouchControlAction.Close, 0.945, 0.075, 0.040, "关闭", GameplayVisible: false, MenuVisible: true),
        // Keep both actions low in the landscape right-thumb arc, using the same reach as the
        // familiar mobile Minecraft layouts instead of floating them near the top edge.
        new(TouchControlAction.Jump, 0.920, 0.805, 0.052, "跳跃"),
        new(TouchControlAction.Sneak, 0.840, 0.885, 0.046, "潜行"),
        // Match Bedrock mobile: one slot-sized key, flush with the hotbar's right edge and
        // vertically centred on the slots. The Android activity runs immersive fullscreen, so
        // lifting this for the system gesture area would visibly detach it from the hotbar.
        new(TouchControlAction.Inventory, 0.607, 0.981, 0.024, "背包")
    ];

    private static readonly IReadOnlyDictionary<TouchControlMode, IReadOnlyList<TouchControlDefinition>> Layouts =
        new Dictionary<TouchControlMode, IReadOnlyList<TouchControlDefinition>>
        {
            [TouchControlMode.JoystickAndTap] = [.. Shared,
                new(TouchControlAction.Move, 0.130, 0.800, 0.105, "移动")],
            [TouchControlMode.DPadAndTap] = [.. Shared.Where(control => control.Action != TouchControlAction.Sneak),
                new(TouchControlAction.Move, 0.130, 0.800, 0.115, "方向键"),
                new(TouchControlAction.Sneak, 0.130, 0.800, 0.036, "潜行")],
            [TouchControlMode.JoystickAndCrosshair] = [.. Shared,
                new(TouchControlAction.Move, 0.130, 0.800, 0.105, "移动")],
            // External keyboard/mouse mode keeps only settings during gameplay and the close
            // affordance in menus, so players can switch back without disconnecting peripherals.
            [TouchControlMode.ExternalKeyboardMouse] = [.. Shared.Where(control =>
                control.Action is TouchControlAction.Settings or TouchControlAction.Close)]
        };

    public static IReadOnlyList<TouchControlDefinition> Default => For(TouchControlMode.DPadAndTap);
    public static IReadOnlyList<TouchControlDefinition> For(TouchControlMode mode) => Layouts[mode];
    public static TouchControlDefinition? HitTest(double x, double y, bool gameplayControlsActive = true) =>
        new TouchControlProfile().HitTest(x, y, gameplayControlsActive);
    public static (double X, double Y) JoystickVector(double x, double y) =>
        new TouchControlProfile().MovementVector(x, y);

    internal static (double X, double Y) MovementVector(TouchControlDefinition move, double x, double y,
        double aspectRatio)
    {
        // Controls are drawn with a pixel radius based on the viewport's short edge. Normalised
        // X therefore covers aspectRatio times as many pixels as normalised Y in landscape.
        // Measure both hit testing and joystick travel in the same square pixel space as the UI.
        var dx = (x - move.CenterX) * Math.Clamp(aspectRatio, 1.2, 3.0) / move.Radius;
        var dy = (y - move.CenterY) / move.Radius;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length > 1) { dx /= length; dy /= length; }
        return (dx, dy);
    }

    internal static double Distance(TouchControlDefinition control, double x, double y,
        double aspectRatio)
    {
        var dx = (x - control.CenterX) * Math.Clamp(aspectRatio, 1.2, 3.0);
        var dy = y - control.CenterY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    internal static bool Contains(TouchControlDefinition control, double x, double y,
        double aspectRatio)
    {
        if (control.Action != TouchControlAction.Inventory)
            return Distance(control, x, y, aspectRatio) <= control.Radius;

        // The inventory key is flush with the ninth hotbar slot. Match its rendered rectangular
        // face exactly so its larger circular accessibility target cannot steal taps from slot 9.
        var safeAspect = Math.Clamp(aspectRatio, 1.2, 3.0);
        var halfHeight = control.Radius * 0.82;
        var halfWidth = halfHeight / safeAspect;
        return Math.Abs(x - control.CenterX) <= halfWidth &&
               Math.Abs(y - control.CenterY) <= halfHeight;
    }
}

/// <summary>Aspect-ratio aware geometry shared by Android rendering and hit testing.</summary>
public readonly record struct TouchHudGeometry(
    double HotbarLeft, double HotbarRight, double HotbarTop, double HotbarBottom,
    double CenterY, double InventoryX, double InventoryRadius)
{
    public static TouchHudGeometry ForAspectRatio(double aspectRatio, double inventoryRadius = 0.024)
    {
        var safeAspect = Math.Clamp(aspectRatio, 1.2, 3.0);
        var halfWidth = 0.17 / safeAspect;
        // Action buttons are drawn from the shorter viewport edge. Convert the inventory
        // button's visible half-width back to normalised X so its left border meets the
        // ninth slot exactly instead of leaving a small tablet-dependent gap.
        var inventoryHalfHeight = inventoryRadius * 0.82;
        var inventoryHalfWidthX = inventoryHalfHeight / safeAspect;
        // Minecraft anchors the hotbar itself to the bottom edge. 0.953 was the centre of the
        // whole HUD band (hearts included), which left the inventory key visibly one slot too
        // high on 16:10 tablets. The slot centre is about 0.981 of the viewport height.
        var centerY = Math.Min(0.981, 1 - inventoryHalfHeight);
        return new(0.5 - halfWidth, 0.5 + halfWidth, 0.875, 1,
            centerY, 0.5 + halfWidth + inventoryHalfWidthX, inventoryRadius);
    }

    public static TouchHudGeometry ForViewport(int width, int height, int guiScale,
        double fallbackInventoryRadius = 0.024)
    {
        if (width <= 0 || height <= 0 || guiScale <= 0)
            return ForAspectRatio(width > 0 && height > 0 ? width / (double)height : 16d / 9d,
                fallbackInventoryRadius);

        // Java Edition's hotbar texture is 182x22 GUI pixels. Its nine selectable cells occupy
        // the inner 180 pixels; the one-pixel texture border remains between slot 9 and Jay's
        // attached inventory face. The face itself matches a 20x20 slot.
        var scale = Math.Clamp(guiScale, 1, Math.Max(1, Math.Min(width / 182, height / 22)));
        var hotbarHalfWidth = 90d * scale / width;
        var textureHalfWidth = 91d * scale / width;
        var inventoryHalfWidth = 10d * scale / width;
        var inventoryHalfHeight = 10d * scale / height;
        var radius = inventoryHalfHeight / 0.82;
        var centerY = 1d - 11d * scale / height;
        return new(
            0.5 - hotbarHalfWidth,
            0.5 + hotbarHalfWidth,
            1d - 22d * scale / height,
            1,
            centerY,
            0.5 + textureHalfWidth + inventoryHalfWidth,
            radius);
    }

    public bool TryGetSlot(double x, double y, out int slot)
    {
        if (y < HotbarTop || y > HotbarBottom || x < HotbarLeft || x >= HotbarRight)
        {
            slot = -1;
            return false;
        }
        slot = Math.Clamp((int)((x - HotbarLeft) / ((HotbarRight - HotbarLeft) / 9)), 0, 8);
        return true;
    }
}
