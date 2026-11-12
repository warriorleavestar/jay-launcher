namespace Amber.Mobile;

public interface ITouchInputSink
{
    void SetAction(TouchControlAction action, bool pressed);
    void SetMovement(double x, double y);
    void AddLook(double x, double y);
    void SetPointer(double x, double y, bool click);
}

/// <summary>Maps independent Android pointer IDs to Jay controls without coupling input to a View.</summary>
public sealed class TouchInputRouter(ITouchInputSink sink)
{
    private const double DragThreshold = 0.015;
    private const double ScrollStep = 0.035;
    private readonly Dictionary<int, PointerState> _pointers = [];

    public bool Press(int pointerId, double x, double y, bool controlsEnabled = true)
    {
        Release(pointerId);
        var control = TouchControlLayout.HitTest(x, y);
        if (!controlsEnabled && control?.Action is not (TouchControlAction.Menu or TouchControlAction.Keyboard))
            control = null;
        if (control is null)
        {
            var clickOnRelease = controlsEnabled;
            var pointerHeld = !controlsEnabled;
            _pointers[pointerId] = new(null, x, y, clickOnRelease, pointerHeld);
            sink.SetPointer(x, y, false);
            if (pointerHeld) sink.SetAction(TouchControlAction.PrimaryAction, true);
            return true;
        }
        _pointers[pointerId] = new(control.Action, x, y);
        if (control.Action == TouchControlAction.Move)
            Move(pointerId, x, y);
        else
            sink.SetAction(control.Action, true);
        return true;
    }

    public bool Move(int pointerId, double x, double y)
    {
        if (!_pointers.TryGetValue(pointerId, out var pointer)) return false;
        if (pointer.Action == TouchControlAction.Move)
        {
            var vector = TouchControlLayout.JoystickVector(x, y);
            sink.SetMovement(vector.X, vector.Y);
        }
        else if (pointer.Action is null)
        {
            sink.SetPointer(x, y, false);
            var totalX = x - pointer.StartX;
            var totalY = y - pointer.StartY;
            var dragged = pointer.HasDragged ||
                totalX * totalX + totalY * totalY >= DragThreshold * DragThreshold;
            if (pointer.PointerHeld || pointer.Scrolling)
            {
                var verticalScroll = pointer.Scrolling ||
                    (dragged && Math.Abs(totalY) > Math.Abs(totalX) * 1.25);
                if (verticalScroll)
                {
                    if (!pointer.Scrolling)
                        sink.SetAction(TouchControlAction.PrimaryAction, false);
                    var remainder = pointer.ScrollRemainder + (y - pointer.Y);
                    while (Math.Abs(remainder) >= ScrollStep)
                    {
                        var action = remainder > 0
                            ? TouchControlAction.PreviousSlot
                            : TouchControlAction.NextSlot;
                        sink.SetAction(action, true);
                        sink.SetAction(action, false);
                        remainder -= Math.CopySign(ScrollStep, remainder);
                    }
                    pointer = pointer with { Scrolling = true, PointerHeld = false, ScrollRemainder = remainder };
                }
            }
            else
            {
                sink.AddLook(x - pointer.X, y - pointer.Y);
            }
            _pointers[pointerId] = pointer with { X = x, Y = y, HasDragged = dragged };
        }
        return true;
    }

    public bool Release(int pointerId)
    {
        if (!_pointers.Remove(pointerId, out var pointer)) return false;
        if (pointer.Action == TouchControlAction.Move)
            sink.SetMovement(0, 0);
        else if (pointer.Action is null)
        {
            if (pointer.PointerHeld)
                sink.SetAction(TouchControlAction.PrimaryAction, false);
            else if (pointer.ClickOnRelease && !pointer.HasDragged)
                sink.SetPointer(pointer.X, pointer.Y, true);
        }
        else if (pointer.Action is { } action)
            sink.SetAction(action, false);
        return true;
    }

    public void ReleaseAll()
    {
        foreach (var pointerId in _pointers.Keys.ToArray()) Release(pointerId);
    }

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
        double ScrollRemainder)
    {
        public PointerState(TouchControlAction? action, double x, double y,
            bool clickOnRelease = true, bool pointerHeld = false)
            : this(action, x, y, clickOnRelease, pointerHeld, x, y, false, false, 0) { }
    }
}
