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
    private readonly Dictionary<int, PointerState> _pointers = [];

    public bool Press(int pointerId, double x, double y, bool controlsEnabled = true)
    {
        Release(pointerId);
        var control = TouchControlLayout.HitTest(x, y);
        if (!controlsEnabled && control?.Action != TouchControlAction.Menu) control = null;
        if (control is null)
        {
            var clickOnRelease = controlsEnabled;
            _pointers[pointerId] = new(null, x, y, clickOnRelease);
            sink.SetPointer(x, y, !clickOnRelease);
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
            sink.AddLook(x - pointer.X, y - pointer.Y);
            sink.SetPointer(x, y, false);
            var totalX = x - pointer.StartX;
            var totalY = y - pointer.StartY;
            var dragged = pointer.HasDragged ||
                totalX * totalX + totalY * totalY >= DragThreshold * DragThreshold;
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
            if (pointer.ClickOnRelease && !pointer.HasDragged)
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
        double StartX,
        double StartY,
        bool HasDragged)
    {
        public PointerState(TouchControlAction? action, double x, double y, bool clickOnRelease = true)
            : this(action, x, y, clickOnRelease, x, y, false) { }
    }
}
