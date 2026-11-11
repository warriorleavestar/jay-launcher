namespace Amber.Mobile;

public interface ITouchInputSink
{
    void SetAction(TouchControlAction action, bool pressed);
    void SetMovement(double x, double y);
}

/// <summary>Maps independent Android pointer IDs to Jay controls without coupling input to a View.</summary>
public sealed class TouchInputRouter(ITouchInputSink sink)
{
    private readonly Dictionary<int, TouchControlAction> _pointers = [];

    public bool Press(int pointerId, double x, double y)
    {
        if (TouchControlLayout.HitTest(x, y) is not { } control) return false;
        Release(pointerId);
        _pointers[pointerId] = control.Action;
        if (control.Action == TouchControlAction.Move)
            Move(pointerId, x, y);
        else
            sink.SetAction(control.Action, true);
        return true;
    }

    public bool Move(int pointerId, double x, double y)
    {
        if (!_pointers.TryGetValue(pointerId, out var action)) return false;
        if (action == TouchControlAction.Move)
        {
            var vector = TouchControlLayout.JoystickVector(x, y);
            sink.SetMovement(vector.X, vector.Y);
        }
        return true;
    }

    public bool Release(int pointerId)
    {
        if (!_pointers.Remove(pointerId, out var action)) return false;
        if (action == TouchControlAction.Move)
            sink.SetMovement(0, 0);
        else
            sink.SetAction(action, false);
        return true;
    }

    public void ReleaseAll()
    {
        foreach (var pointerId in _pointers.Keys.ToArray()) Release(pointerId);
    }
}
