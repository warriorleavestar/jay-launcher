namespace Amber.Mobile;

public interface ITouchInputSink
{
    void SetAction(TouchControlAction action, bool pressed);
    void SetMovement(double x, double y);
    void AddLook(double x, double y);
    void SetPointer(double x, double y, bool click);
}

/// <summary>Maps independent Android pointer IDs to Jay controls without coupling input to a View.</summary>
public sealed class TouchInputRouter
{
    private const double DragThreshold = 0.015;
    private const double ScrollStep = 0.035;
    private readonly Dictionary<int, PointerState> _pointers = [];
    private readonly ITouchInputSink _sink;

    public TouchInputRouter(ITouchInputSink sink, TouchControlProfile? profile = null)
    {
        _sink = sink;
        Profile = profile ?? new TouchControlProfile();
    }

    public TouchControlProfile Profile { get; }

    public (double X, double Y) Movement { get; private set; }
    public bool SprintLocked { get; private set; }

    public bool IsPressed(TouchControlAction action) =>
        (action == TouchControlAction.Sprint && SprintLocked) ||
        _pointers.Values.Any(pointer => pointer.Action == action || pointer.HeldAction == action);

    public bool Press(int pointerId, double x, double y, bool controlsEnabled = true)
    {
        Release(pointerId);
        var control = Profile.HitTest(x, y, controlsEnabled);
        if (!controlsEnabled && control?.Action is not (TouchControlAction.Menu or TouchControlAction.Keyboard or
            TouchControlAction.SecondaryAction or TouchControlAction.Settings))
            control = null;
        if (control is null)
        {
            var clickOnRelease = controlsEnabled && Profile.Mode != TouchControlMode.JoystickAndCrosshair;
            var pointerHeld = !controlsEnabled;
            TouchControlAction? heldAction = pointerHeld
                ? IsMenuSplitModifierHeld() ? TouchControlAction.SecondaryAction : TouchControlAction.PrimaryAction
                : null;
            _pointers[pointerId] = new(null, x, y, clickOnRelease, pointerHeld, heldAction: heldAction);
            _sink.SetPointer(x, y, false);
            if (heldAction is { } action) _sink.SetAction(action, true);
            return true;
        }
        var menuSplitModifier = !controlsEnabled && control.Action == TouchControlAction.SecondaryAction;
        if (control.Action == TouchControlAction.Sprint)
        {
            SprintLocked = !SprintLocked;
            _pointers[pointerId] = new(control.Action, x, y, toggleAction: true);
            _sink.SetAction(control.Action, SprintLocked);
            return true;
        }
        _pointers[pointerId] = new(control.Action, x, y, menuSplitModifier: menuSplitModifier);
        if (control.Action == TouchControlAction.Move)
            Move(pointerId, x, y);
        else if (!menuSplitModifier)
            _sink.SetAction(control.Action, true);
        return true;
    }

    public bool BeginLongPress(int pointerId)
    {
        if (Profile.Mode == TouchControlMode.JoystickAndCrosshair) return false;
        if (!_pointers.TryGetValue(pointerId, out var pointer) ||
            pointer.Action is not null || pointer.HasDragged || pointer.LongPressAction ||
            !pointer.ClickOnRelease)
            return false;

        _sink.SetAction(TouchControlAction.PrimaryAction, true);
        _pointers[pointerId] = pointer with
        {
            ClickOnRelease = false,
            PointerHeld = true,
            HeldAction = TouchControlAction.PrimaryAction,
            LongPressAction = true
        };
        return true;
    }

    public bool IsLongPressCandidate(int pointerId) =>
        _pointers.TryGetValue(pointerId, out var pointer) &&
        pointer.Action is null && !pointer.HasDragged &&
        (pointer.ClickOnRelease || pointer.LongPressAction);

    public bool IsLongPress(int pointerId) =>
        _pointers.TryGetValue(pointerId, out var pointer) && pointer.LongPressAction;

    public bool Move(int pointerId, double x, double y)
    {
        if (!_pointers.TryGetValue(pointerId, out var pointer)) return false;
        if (pointer.Action == TouchControlAction.Move)
        {
            var vector = Profile.MovementVector(x, y);
            Movement = vector;
            _sink.SetMovement(vector.X, vector.Y);
            SetAutoSprint(Profile.ForcedSprint && vector.Y <= -0.88);
        }
        else if (pointer.Action is null)
        {
            _sink.SetPointer(x, y, false);
            var totalX = x - pointer.StartX;
            var totalY = y - pointer.StartY;
            var dragged = pointer.HasDragged ||
                totalX * totalX + totalY * totalY >= DragThreshold * DragThreshold;
            if (pointer.LongPressAction)
            {
                if (dragged)
                {
                    _sink.SetAction(TouchControlAction.PrimaryAction, false);
                    _sink.AddLook(x - pointer.X, y - pointer.Y);
                    pointer = pointer with
                    {
                        PointerHeld = false,
                        HeldAction = null,
                        LongPressAction = false
                    };
                }
            }
            else if (pointer.HeldAction == TouchControlAction.SecondaryAction)
            {
                // Inventory split mode is a real held right mouse button. Moving across slots
                // must remain a right-drag so Minecraft can distribute one item per slot.
            }
            else if (pointer.PointerHeld || pointer.Scrolling)
            {
                var verticalScroll = pointer.Scrolling ||
                    (dragged && Math.Abs(totalY) > Math.Abs(totalX) * 1.25);
                if (verticalScroll)
                {
                    if (!pointer.Scrolling)
                        if (pointer.HeldAction is { } heldAction) _sink.SetAction(heldAction, false);
                    var remainder = pointer.ScrollRemainder + (y - pointer.Y);
                    while (Math.Abs(remainder) >= ScrollStep)
                    {
                        var action = remainder > 0
                            ? TouchControlAction.PreviousSlot
                            : TouchControlAction.NextSlot;
                        _sink.SetAction(action, true);
                        _sink.SetAction(action, false);
                        remainder -= Math.CopySign(ScrollStep, remainder);
                    }
                    pointer = pointer with { Scrolling = true, PointerHeld = false, ScrollRemainder = remainder };
                }
            }
            else
            {
                _sink.AddLook(x - pointer.X, y - pointer.Y);
            }
            _pointers[pointerId] = pointer with { X = x, Y = y, HasDragged = dragged };
        }
        else if (CanDragLook(pointer.Action.Value))
        {
            _sink.AddLook(x - pointer.X, y - pointer.Y);
            _pointers[pointerId] = pointer with { X = x, Y = y, HasDragged = true };
        }
        return true;
    }

    public bool Release(int pointerId)
    {
        if (!_pointers.Remove(pointerId, out var pointer)) return false;
        if (pointer.Action == TouchControlAction.Move)
        {
            Movement = (0, 0);
            _sink.SetMovement(0, 0);
            SetAutoSprint(false);
        }
        else if (pointer.Action is null)
        {
            if (pointer.PointerHeld && pointer.HeldAction is { } heldAction)
                _sink.SetAction(heldAction, false);
            else if (pointer.ClickOnRelease && !pointer.HasDragged)
                _sink.SetPointer(pointer.X, pointer.Y, true);
        }
        else if (!pointer.MenuSplitModifier && !pointer.ToggleAction && pointer.Action is { } action)
            _sink.SetAction(action, false);
        return true;
    }

    public void ReleaseAll()
    {
        foreach (var pointerId in _pointers.Keys.ToArray()) Release(pointerId);
    }

    private bool IsMenuSplitModifierHeld() =>
        _pointers.Values.Any(pointer => pointer.MenuSplitModifier);

    private void SetAutoSprint(bool active)
    {
        if (SprintLocked == active) return;
        SprintLocked = active;
        _sink.SetAction(TouchControlAction.Sprint, active);
    }

    private static bool CanDragLook(TouchControlAction action) => action is
        TouchControlAction.PrimaryAction or TouchControlAction.SecondaryAction or
        TouchControlAction.Jump or TouchControlAction.Sneak or TouchControlAction.Sprint;

    private readonly record struct PointerState(
        TouchControlAction? Action,
        double X,
        double Y,
        bool ClickOnRelease,
        bool PointerHeld,
        double StartX,
        double StartY,
        bool HasDragged,
        bool Scrolling,
        double ScrollRemainder,
        TouchControlAction? HeldAction,
        bool MenuSplitModifier,
        bool ToggleAction,
        bool LongPressAction)
    {
        public PointerState(TouchControlAction? action, double x, double y,
            bool clickOnRelease = true, bool pointerHeld = false,
            TouchControlAction? heldAction = null, bool menuSplitModifier = false,
            bool toggleAction = false)
            : this(action, x, y, clickOnRelease, pointerHeld, x, y, false, false, 0,
                heldAction, menuSplitModifier, toggleAction, false) { }
    }
}
