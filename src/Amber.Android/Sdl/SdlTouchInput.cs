using System.Runtime.InteropServices;
using Android.App;
using Android.Runtime;
using Android.Views;
using Amber.Mobile;

namespace Amber.Android;

internal sealed partial class SdlTouchInput(Activity activity)
{
    private readonly Java.Lang.Class _sdlActivity = Java.Lang.Class.ForName("org.libsdl.app.SDLActivity", true, activity.ClassLoader)!;
    private readonly HashSet<Keycode> _held = [];
    private float _mouseX, _mouseY;
    private int _buttons;

    [LibraryImport("jayjvm", EntryPoint = "jay_java_pointer")]
    private static partial int DispatchMenuPointer(float x, float y, uint action);

    private bool MenuPointer(uint action) => GetFocus() != 0 &&
        DispatchMenuPointer(_mouseX / Math.Max(1, Width), _mouseY / Math.Max(1, Height), action) != 0;

    [LibraryImport("SDL3", EntryPoint = "SDL_SetHint", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SetHint(string name, string value);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetKeyboardFocus")]
    private static partial nint GetFocus();

    [LibraryImport("SDL3", EntryPoint = "SDL_GetWindowRelativeMouseMode")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool IsRelative(nint window);

    public bool InWorld => GetFocus() is var window && window != 0 && IsRelative(window);

    public void Move(double x, double y)
    {
        Key(Keycode.A, x < -0.22);
        Key(Keycode.D, x > 0.22);
        Key(Keycode.W, y < -0.22);
        Key(Keycode.S, y > 0.22);
    }

    private void Key(Keycode code, bool down)
    {
        if (down ? !_held.Add(code) : !_held.Remove(code)) return;
        var method = JNIEnv.GetStaticMethodID(_sdlActivity.Handle, down ? "onNativeKeyDown" : "onNativeKeyUp", "(I)V");
        JNIEnv.CallStaticVoidMethod(_sdlActivity.Handle, method, new JValue((int)code));
    }

    private void Mouse(int button, MotionEventActions action, float x, float y, bool relative)
    {
        var method = JNIEnv.GetStaticMethodID(_sdlActivity.Handle, "onNativeMouse", "(IIFFZ)V");
        JNIEnv.CallStaticVoidMethod(_sdlActivity.Handle, method, new JValue(button), new JValue((int)action),
            new JValue(x), new JValue(y), new JValue(relative));
    }

    private int Width => activity.Window?.DecorView.Width ?? 1;
    private int Height => activity.Window?.DecorView.Height ?? 1;

    public void Look(double dx, double dy) => Mouse(0, MotionEventActions.Move, (float)(dx * Width), (float)(dy * Height), true);

    public void Pointer(double x, double y, bool click)
    {
        _mouseX = (float)(x * Width);
        _mouseY = (float)(y * Height);
        var playing = InWorld;
        if (!playing && !MenuPointer(0)) Mouse(_buttons, MotionEventActions.Move, _mouseX, _mouseY, false);
        if (!click) return;
        var action = playing ? TouchControlAction.SecondaryAction : TouchControlAction.PrimaryAction;
        Action(action, true);
        Action(action, false);
    }

    public void SelectSlot(int slot)
    {
        var key = (Keycode)((int)Keycode.Num1 + slot);
        Key(key, true);
        Key(key, false);
    }

    public void Action(TouchControlAction action, bool pressed)
    {
        if (action is TouchControlAction.PrimaryAction or TouchControlAction.SecondaryAction)
        {
            int mask = action == TouchControlAction.PrimaryAction ? 1 : 2;
            _buttons = pressed ? _buttons | mask : _buttons & ~mask;
            var relative = InWorld;
            if (!relative && mask == 1 && MenuPointer(pressed ? 1u : 2u)) return;
            Mouse(_buttons, pressed ? MotionEventActions.Down : MotionEventActions.Up,
                relative ? 0 : _mouseX, relative ? 0 : _mouseY, relative);
            return;
        }
        if (action is TouchControlAction.PreviousSlot or TouchControlAction.NextSlot)
        {
            if (pressed) Mouse(0, MotionEventActions.Scroll, 0, action == TouchControlAction.PreviousSlot ? 1 : -1, false);
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
}
