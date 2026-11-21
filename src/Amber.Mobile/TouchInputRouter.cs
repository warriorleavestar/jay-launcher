namespace Amber.Mobile;

public interface ITouchInputSink
{
    void SetAction(TouchControlAction action, bool pressed);
    /// <summary>
    /// Releases an action because Android cancelled the gesture. Unlike a normal finger-up this
    /// must not synthesize a tap when both edges arrive between two native input polls.
    /// </summary>
    void CancelAction(TouchControlAction action);
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

    /// <summary>
    /// True when this gesture belongs to the rendered game surface rather than a touch-overlay
    /// control. Only surface taps may update Minecraft text-field focus on release.
    /// </summary>
    public bool TargetsGameSurface(int pointerId) =>
        _pointers.TryGetValue(pointerId, out var pointer) && pointer.Action is null;

    public bool Press(int pointerId, double x, double y, bool controlsEnabled = true)
    {
        // A few Android touch drivers can repeat DOWN for an ID whose UP packet was
        // lost while the window was being resized or an IME was opening.  Releasing
        // the stale gesture here would turn it into a click or commit an inventory
        // split even though the player never lifted that finger.  Abandon it with
        // the same no-side-effect semantics as ACTION_CANCEL instead.
        Cancel(pointerId);
        var control = Profile.HitTest(x, y, controlsEnabled);
        if (!controlsEnabled && control?.Action is not (TouchControlAction.Menu or TouchControlAction.Close or
            TouchControlAction.Keyboard or TouchControlAction.Settings))
            control = null;
        if (control is null)
        {
            // Every touch scheme uses the same Bedrock-style interaction: tap to interact,
            // hold to mine and drag to look. Crosshair mode changes aiming, not the gesture set.
            // Menus defer the click so a drag can still become a held mouse gesture. A long
            // press is handled separately as Bedrock-style precise stack selection.
            // Deferring the menu button press is what keeps those gestures from fighting.
            var clickOnRelease = !controlsEnabled || Profile.AllowsAttackAt(x, y);
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
            _pointers[pointerId] = pointer with { X = x, Y = y };
            Movement = vector;
            _sink.SetMovement(vector.X, vector.Y);
            SetAutoSprint(Profile.ForcedSprint && vector.Y <= -0.88);
        }
        else if (pointer.Action is null)
        {
            var totalX = x - pointer.StartX;
            var totalY = y - pointer.StartY;
            var physicalTotalX = totalX * Math.Clamp(Profile.ViewportAspectRatio, 1.2, 3.0);
            var dragged = pointer.HasDragged ||
                physicalTotalX * physicalTotalX + totalY * totalY >= DragThreshold * DragThreshold;
            if (pointer.LongPressAction)
            {
                if (pointer.MenuInteraction)
                {
                    // Like Bedrock mobile, the finger starts at one item and a rightward swipe
                    // selects progressively more of the source stack. Sliding left returns to 1.
                    var progress = Math.Clamp(physicalTotalX / InventorySplitTravel, 0, 1);
                    var selected = 1 + (int)Math.Round(progress * (pointer.InventoryStackSize - 1));
                    pointer = pointer with { InventorySelectedCount = selected };
                }
                else if (dragged)
                {
                    _sink.CancelAction(TouchControlAction.PrimaryAction);
                    AddLook(x - pointer.X, y - pointer.Y);
                    pointer = pointer with
                    {
                        PointerHeld = false,
                        HeldAction = null,
                        LongPressAction = false
                    };
                }
            }
            else if (pointer.DraggingStack || (pointer.MenuInteraction && pointer.ClickOnRelease && dragged &&
                _inventoryStackCount(pointer.StartX, pointer.StartY) > 0))
            {
                // Take the stack on release. Both endpoints are independent of how many
                // Android movement events the render thread processes during the gesture.
                pointer = pointer with { DraggingStack = true, ClickOnRelease = false };
            }
            else if (pointer.MenuInteraction && pointer.ClickOnRelease && dragged)
            {
                var verticalScroll = Math.Abs(totalY) > Math.Abs(physicalTotalX) * 1.25;
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
                    (dragged && Math.Abs(totalY) > Math.Abs(physicalTotalX) * 1.25);
                if (verticalScroll)
                {
                    if (!pointer.Scrolling)
                        if (pointer.HeldAction is { } heldAction) _sink.CancelAction(heldAction);
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
                AddLook(x - pointer.X, y - pointer.Y);
            }
            _pointers[pointerId] = pointer with { X = x, Y = y, HasDragged = dragged };
        }
        else if (CanDragLook(pointer.Action.Value))
        {
            AddLook(x - pointer.X, y - pointer.Y);
            _pointers[pointerId] = pointer with { X = x, Y = y, HasDragged = true };
        }
        return true;
    }

    public bool Release(int pointerId, bool cancelled = false)
    {
        if (!_pointers.Remove(pointerId, out var pointer)) return false;
        if (pointer.Action == TouchControlAction.Move)
        {
            RestoreRemainingMovement(cancelAction: cancelled);
        }
        else if (pointer.Action is null)
        {
            if (pointer.DraggingStack)
            {
                var count = _inventoryStackCount(pointer.StartX, pointer.StartY);
                if (!cancelled && count > 0 && _inventoryStackCount(pointer.X, pointer.Y) >= 0)
                {
                    _sink.SplitInventoryStack(pointer.StartX, pointer.StartY, count);
                    _sink.SetPointer(pointer.X, pointer.Y, true);
                }
            }
            else if (!cancelled && pointer.MenuInteraction && pointer.InventoryStackSize > 1)
                _sink.SplitInventoryStack(pointer.StartX, pointer.StartY,
                    pointer.InventorySelectedCount);
            else if (pointer.PointerHeld && pointer.HeldAction is { } heldAction)
            {
                if (cancelled) _sink.CancelAction(heldAction);
                else _sink.SetAction(heldAction, false);
            }
            else if (!cancelled && pointer.ClickOnRelease && !pointer.HasDragged)
                _sink.SetPointer(pointer.X, pointer.Y, true);
        }
        else if (!pointer.ToggleAction && pointer.Action is { } action)
        {
            if (cancelled) _sink.CancelAction(action);
            else _sink.SetAction(action, false);
        }
        return true;
    }

    public void ReleaseAll()
    {
        foreach (var pointerId in _pointers.Keys.ToArray()) Release(pointerId, cancelled: true);
    }

    /// <summary>
    /// Abandons every active gesture without producing release-time clicks or
    /// inventory transfers. Android sends CANCEL when the Activity loses the
    /// gesture to the system; treating it as UP can interact with the game at a
    /// location the player never released.
    /// </summary>
    public void CancelAll()
    {
        foreach (var pointerId in _pointers.Keys.ToArray()) Cancel(pointerId);
    }

    /// <summary>Abandons one recycled or system-cancelled pointer without release-time effects.</summary>
    public bool Cancel(int pointerId)
    {
        if (!_pointers.Remove(pointerId, out var pointer)) return false;
        if (pointer.Action == TouchControlAction.Move)
        {
            RestoreRemainingMovement(cancelAction: true);
        }
        else if (pointer.Action is null)
        {
            if (pointer.PointerHeld && pointer.HeldAction is { } heldAction)
                _sink.CancelAction(heldAction);
        }
        else if (!pointer.ToggleAction)
        {
            _sink.CancelAction(pointer.Action.Value);
        }
        return true;
    }

    private void TapAction(TouchControlAction action)
    {
        _sink.SetAction(action, true);
        _sink.SetAction(action, false);
    }

    private void AddLook(double x, double y) =>
        _sink.AddLook(x * Profile.LookSensitivity, y * Profile.LookSensitivity);

    private void RestoreRemainingMovement(bool cancelAction = false)
    {
        var remaining = _pointers.Values.LastOrDefault(pointer =>
            pointer.Action == TouchControlAction.Move);
        var vector = remaining.Action == TouchControlAction.Move
            ? Profile.MovementVector(remaining.X, remaining.Y)
            : (0d, 0d);
        Movement = vector;
        _sink.SetMovement(vector.Item1, vector.Item2);
        SetAutoSprint(Profile.ForcedSprint && vector.Item2 <= -0.88, cancelAction);
    }

    private void SetAutoSprint(bool active, bool cancelAction = false)
    {
        if (SprintLocked == active) return;
        SprintLocked = active;
        if (!active && cancelAction)
            _sink.CancelAction(TouchControlAction.Sprint);
        else
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
        int InventorySelectedCount,
        bool DraggingStack = false)
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
