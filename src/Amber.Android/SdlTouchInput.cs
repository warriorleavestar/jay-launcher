using Android.App;
using Android.Runtime;
using Android.Views;
using Amber.Mobile;

namespace Amber.Android;

/// <summary>
/// Sends input through SDL's Android event queue for Minecraft versions that own an SDL window.
/// Calling Minecraft's mouse handlers directly from ART works for the older GLFW client, but the
/// SDL client consumes its input on the render thread and silently drops those foreign-thread calls.
/// </summary>
internal sealed class SdlTouchInput(Activity activity)
{
    private readonly HashSet<Keycode> _held = [];
    private float _mouseX;
    private float _mouseY;
    private int _buttons;
    private bool _pointerRouteReported;

    private int Width => Math.Max(1, activity.Window?.DecorView.Width ?? 1);
    private int Height => Math.Max(1, activity.Window?.DecorView.Height ?? 1);

    public void Move(double x, double y)
    {
        Key(Keycode.A, x < -0.22);
        Key(Keycode.D, x > 0.22);
        Key(Keycode.W, y < -0.22);
        Key(Keycode.S, y > 0.22);
    }

    public void Look(double dx, double dy) =>
        Mouse(0, MotionEventActions.Move, (float)(dx * Width), (float)(dy * Height), true);

    public void Pointer(double x, double y, bool click)
    {
        var normalizedX = (float)Math.Clamp(x, 0, 1);
        var normalizedY = (float)Math.Clamp(y, 0, 1);
        _mouseX = normalizedX * Width;
        _mouseY = normalizedY * Height;
        var gameplay = NativeJvmHost.GameplayControlsActive;
        if (!click) return;

        if (gameplay)
        {
            var contextual = NativeJvmHost.HasBlockTarget
                ? TouchControlAction.SecondaryAction
                : TouchControlAction.PrimaryAction;
            Action(contextual, true);
            Action(contextual, false);
            return;
        }

        // This path originates from a finger, not an external mouse. SDL's Android backend
        // consumes normalized touch coordinates and synthesizes the matching menu mouse click.
        // Feeding a fake pixel-coordinate mouse left the cursor positioned but did not activate
        // buttons on the SDL 3 client.
        var javaDelivered = NativeJvmHost.DispatchJavaPointer(normalizedX, normalizedY, 1);
        if (!_pointerRouteReported)
        {
            _pointerRouteReported = true;
            global::Android.Util.Log.Info("JayTouch",
                $"SDL menu pointer x={normalizedX:F3} y={normalizedY:F3} java={javaDelivered}");
        }
        if (!javaDelivered)
        {
            Touch(MotionEventActions.Down, normalizedX, normalizedY, 1);
            Touch(MotionEventActions.Up, normalizedX, normalizedY, 0);
        }
    }

    public void SelectSlot(int slot)
    {
        var key = (Keycode)((int)Keycode.Num1 + Math.Clamp(slot, 0, 8));
        Key(key, true);
        Key(key, false);
    }

    public void Action(TouchControlAction action, bool pressed)
    {
        if (action is TouchControlAction.PrimaryAction or TouchControlAction.SecondaryAction)
        {
            var mask = action == TouchControlAction.PrimaryAction ? 1 : 2;
            _buttons = pressed ? _buttons | mask : _buttons & ~mask;
            var relative = NativeJvmHost.GameplayControlsActive;
            Mouse(_buttons, pressed ? MotionEventActions.Down : MotionEventActions.Up,
                relative ? 0 : _mouseX, relative ? 0 : _mouseY, relative);
            return;
        }

        if (action is TouchControlAction.PreviousSlot or TouchControlAction.NextSlot)
        {
            if (pressed)
                Mouse(0, MotionEventActions.Scroll, 0,
                    action == TouchControlAction.PreviousSlot ? 1 : -1, false);
            return;
        }

        var key = action switch
        {
            TouchControlAction.Jump => Keycode.Space,
            TouchControlAction.Inventory => Keycode.E,
            TouchControlAction.Menu => Keycode.Escape,
            TouchControlAction.Keyboard => Keycode.T,
            TouchControlAction.Sneak => Keycode.ShiftLeft,
            TouchControlAction.Sprint => Keycode.CtrlLeft,
            TouchControlAction.Drop => Keycode.Q,
            _ => Keycode.Unknown
        };
        if (key != Keycode.Unknown) Key(key, pressed);
    }

    public void KeyEvent(Keycode code, bool pressed, bool repeat = false)
    {
        if (pressed && repeat) SendKey(code, true);
        else Key(code, pressed);
    }

    public void CommitText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Do not retain a Java.Lang.Class wrapper across the embedded JVM/SDL startup. Its JNI
        // peer can be invalidated when SDL replaces the activity class-loader context.
        using var inputConnection = Java.Lang.Class.ForName(
            "org.libsdl.app.SDLInputConnection", true, activity.ClassLoader)!;
        using var value = new Java.Lang.String(text);
        var method = JNIEnv.GetStaticMethodID(inputConnection.Handle,
            "nativeCommitText", "(Ljava/lang/String;I)V");
        JNIEnv.CallStaticVoidMethod(inputConnection.Handle, method,
            new JValue(value), new JValue(1));
    }

    public void Backspace() { Key(Keycode.Del, true); Key(Keycode.Del, false); }
    public void Enter() { Key(Keycode.Enter, true); Key(Keycode.Enter, false); }

    public void ReleaseAll()
    {
        foreach (var key in _held.ToArray()) Key(key, false);
        if ((_buttons & 1) != 0) Action(TouchControlAction.PrimaryAction, false);
        if ((_buttons & 2) != 0) Action(TouchControlAction.SecondaryAction, false);
    }

    private void Key(Keycode code, bool down)
    {
        if (down ? !_held.Add(code) : !_held.Remove(code)) return;
        SendKey(code, down);
    }

    private void SendKey(Keycode code, bool down)
    {
        using var sdlActivity = Java.Lang.Class.ForName(
            "org.libsdl.app.SDLActivity", true, activity.ClassLoader)!;
        var method = JNIEnv.GetStaticMethodID(sdlActivity.Handle,
            down ? "onNativeKeyDown" : "onNativeKeyUp", "(I)V");
        JNIEnv.CallStaticVoidMethod(sdlActivity.Handle, method, new JValue((int)code));
    }

    private void Mouse(int button, MotionEventActions action, float x, float y, bool relative)
    {
        using var sdlActivity = Java.Lang.Class.ForName(
            "org.libsdl.app.SDLActivity", true, activity.ClassLoader)!;
        var method = JNIEnv.GetStaticMethodID(sdlActivity.Handle,
            "onNativeMouse", "(IIFFZ)V");
        JNIEnv.CallStaticVoidMethod(sdlActivity.Handle, method,
            new JValue(button), new JValue((int)action), new JValue(x), new JValue(y),
            new JValue(relative));
    }

    private void Touch(MotionEventActions action, float x, float y, float pressure)
    {
        using var sdlActivity = Java.Lang.Class.ForName(
            "org.libsdl.app.SDLActivity", true, activity.ClassLoader)!;
        var method = JNIEnv.GetStaticMethodID(sdlActivity.Handle,
            "onNativeTouch", "(IIIFFF)V");
        JNIEnv.CallStaticVoidMethod(sdlActivity.Handle, method,
            new JValue(0), new JValue(0), new JValue((int)action),
            new JValue(x), new JValue(y), new JValue(pressure));
    }
}
