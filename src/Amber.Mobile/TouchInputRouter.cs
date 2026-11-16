namespace Amber.Mobile;

public interface ITouchInputSink
{
    void SetAction(TouchControlAction action, bool pressed);
    void SetMovement(double x, double y);
    void AddLook(double x, double y);
    void SetPointer(double x, double y, bool click);
    void SplitInventoryStack(double x, double y, int count);
}

/// <summary>Maps independent Android pointer IDs to Jay controls without coupling input to a View.</summary>
public sealed class TouchInputRouter
{
    private const double DragThreshold = 0.015;
    private const double ScrollStep = 0.035;
    private const double InventorySplitTravel = 0.24;
    private readonly Dictionary<int, PointerState> _pointers = [];
    private readonly ITouchInputSink _sink;
    private readonly Func<double, double, int> _inventoryStackCount;

    public TouchInputRouter(ITouchInputSink sink, TouchControlProfile? profile = null,
        Func<double, double, int>? inventoryStackCount = null)
    {
        _sink = sink;
        Profile = profile ?? new TouchControlProfile();
        _inventoryStackCount = inventoryStackCount ?? ((_, _) => 0);
    }

    public TouchControlProfile Profile { get; }

    public (double X, double Y) Movement { get; private set; }
    public bool SprintLocked { get; private set; }

    public IReadOnlyList<InventorySplitSelection> InventorySplitSelections =>
        _pointers.Values.Where(pointer => pointer.InventoryStackSize > 1)
            .Select(pointer => new InventorySplitSelection(pointer.StartX, pointer.StartY,
                pointer.InventorySelectedCount, pointer.InventoryStackSize)).ToArray();

    public bool IsPressed(TouchControlAction action) =>
        (action == TouchControlAction.Sprint && SprintLocked) ||
        _pointers.Values.Any(pointer => pointer.Action == action || pointer.HeldAction == action);

    public bool Press(int pointerId, double x, double y, bool controlsEnabled = true)
    {
        Release(pointerId);
        var control = Profile.HitTest(x, y, controlsEnabled);
        if (!controlsEnabled && control?.Action is not (TouchControlAction.Menu or TouchControlAction.Keyboard or
            TouchControlAction.Settings))
            control = null;
        if (control is null)
        {
            // Every touch scheme uses the same Bedrock-style interaction: tap to interact,
            // hold to mine and drag to look. Crosshair mode changes aiming, not the gesture set.
            // Menus defer the click so a drag can still become a held mouse gesture. A long
            // press is handled separately as Bedrock-style precise stack selection.
            // Deferring the menu button press is what keeps those gestures from fighting.
            var clickOnRelease = true;
            var pointerHeld = false;
            TouchControlAction? heldAction = null;
            _pointers[pointerId] = new(null, x, y, clickOnRelease, pointerHeld,
                heldAction: heldAction, menuInteraction: !controlsEnabled);
            _sink.SetPointer(x, y, false);
            return true;
        }
        if (control.Action == TouchControlAction.Sprint)
        {
            SprintLocked = !SprintLocked;
            _pointers[pointerId] = new(control.Action, x, y, toggleAction: true);
            _sink.SetAction(control.Action, SprintLocked);
            return true;
        }
        _pointers[pointerId] = new(control.Action, x, y);
        if (control.Action == TouchControlAction.Move)
            Move(pointerId, x, y);
        else
            _sink.SetAction(control.Action, true);
        return true;
    }

    public bool BeginLongPress(int pointerId)
    {
        if (!_pointers.TryGetValue(pointerId, out var pointer) ||
            pointer.Action is not null || pointer.HasDragged || pointer.LongPressAction ||
            !pointer.ClickOnRelease)
            return false;

        if (pointer.MenuInteraction)
        {
            var stackSize = Math.Clamp(_inventoryStackCount(pointer.StartX, pointer.StartY), 0, 64);
            if (stackSize <= 1) return false;
            _pointers[pointerId] = pointer with
            {
                ClickOnRelease = false,
                LongPressAction = true,
                InventoryStackSize = stackSize,
                InventorySelectedCount = 1
            };
            return true;
        }

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
            var totalX = x - pointer.StartX;
            var totalY = y - pointer.StartY;
            var dragged = pointer.HasDragged ||
                totalX * totalX + totalY * totalY >= DragThreshold * DragThreshold;
            if (pointer.LongPressAction)
            {
                if (pointer.MenuInteraction)
                {
                    // Like Bedrock mobile, the finger starts at one item and a rightward swipe
                    // selects progressively more of the source stack. Sliding left returns to 1.
                    var progress = Math.Clamp((x - pointer.StartX) / InventorySplitTravel, 0, 1);
                    var selected = 1 + (int)Math.Round(progress * (pointer.InventoryStackSize - 1));
                    pointer = pointer with { InventorySelectedCount = selected };
                }
                else if (dragged)
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
            else if (pointer.MenuInteraction && pointer.ClickOnRelease && dragged)
            {
                var verticalScroll = Math.Abs(totalY) > Math.Abs(totalX) * 1.25;
                if (verticalScroll)
                {
                    var remainder = pointer.ScrollRemainder + (y - pointer.Y);
                    while (Math.Abs(remainder) >= ScrollStep)
                    {
                        var action = remainder > 0
                            ? TouchControlAction.PreviousSlot
                            : TouchControlAction.NextSlot;
                        TapAction(action);
                        remainder -= Math.CopySign(ScrollStep, remainder);
                    }
                    _sink.SetPointer(x, y, false);
                    pointer = pointer with
                    {
                        ClickOnRelease = false,
                        Scrolling = true,
                        ScrollRemainder = remainder
                    };
                }
                else
                {
                    // Start the held click at the original touch point before following the
                    // finger, so sliders and crafting slots see the same sequence as a mouse.
                    _sink.SetPointer(pointer.StartX, pointer.StartY, false);
                    _sink.SetAction(TouchControlAction.PrimaryAction, true);
                    _sink.SetPointer(x, y, false);
                    pointer = pointer with
                    {
                        ClickOnRelease = false,
                        PointerHeld = true,
                        HeldAction = TouchControlAction.PrimaryAction
                    };
                }
            }
            else if (pointer.PointerHeld || pointer.Scrolling)
            {
                _sink.SetPointer(x, y, false);
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
                _sink.SetPointer(x, y, false);
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
            if (pointer.MenuInteraction && pointer.InventoryStackSize > 1)
                _sink.SplitInventoryStack(pointer.StartX, pointer.StartY,
                    pointer.InventorySelectedCount);
            else if (pointer.PointerHeld && pointer.HeldAction is { } heldAction)
                _sink.SetAction(heldAction, false);
            else if (pointer.ClickOnRelease && !pointer.HasDragged)
                _sink.SetPointer(pointer.X, pointer.Y, true);
        }
        else if (!pointer.ToggleAction && pointer.Action is { } action)
            _sink.SetAction(action, false);
        return true;
    }

    public void ReleaseAll()
    {
        foreach (var pointerId in _pointers.Keys.ToArray()) Release(pointerId);
    }

    private void TapAction(TouchControlAction action)
    {
        _sink.SetAction(action, true);
        _sink.SetAction(action, false);
    }

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
        bool MenuInteraction,
        bool ToggleAction,
        bool LongPressAction,
        int InventoryStackSize,
        int InventorySelectedCount)
    {
        public PointerState(TouchControlAction? action, double x, double y,
            bool clickOnRelease = true, bool pointerHeld = false,
            TouchControlAction? heldAction = null, bool menuInteraction = false,
            bool toggleAction = false)
            : this(action, x, y, clickOnRelease, pointerHeld, x, y, false, false, 0,
                heldAction, menuInteraction, toggleAction, false, 0, 0) { }
    }
}

public readonly record struct InventorySplitSelection(double X, double Y, int Count, int Total);
