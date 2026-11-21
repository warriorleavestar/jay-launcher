using System.Runtime.InteropServices;
using Android.App;
using Android.Runtime;
using Android.Views;
using Amber.Mobile;

namespace Amber.Android;

internal sealed partial class SdlTouchInput(Activity activity)
{
    private readonly Java.Lang.Class _sdlActivity = Java.Lang.Class.ForName("org.libsdl.app.SDLActivity", true, activity.ClassLoader)!;
    private readonly Java.Lang.Class _sdlInputConnection = Java.Lang.Class.ForName("org.libsdl.app.SDLInputConnection", true, activity.ClassLoader)!;
    private readonly HashSet<Keycode> _held = [];
    private float _mouseX, _mouseY;
    private int _buttons;

    [LibraryImport("jayjvm", EntryPoint = "jay_java_pointer")]
    private static partial int DispatchMenuPointer(float x, float y, uint action);

    [LibraryImport("jayjvm", EntryPoint = "jay_inventory_stack_count")]
    private static partial int GetInventoryStackCount(float x, float y);

    [LibraryImport("jayjvm", EntryPoint = "jay_java_inventory_split")]
    private static partial int DispatchInventorySplit(float x, float y, int count);

    private bool MenuPointer(uint action) => GetFocus() != 0 &&
        DispatchMenuPointer(_mouseX / Math.Max(1, Width), _mouseY / Math.Max(1, Height), action) != 0;

    public int InventoryStackCount(double x, double y) => InventorySlotState(x, y).Count;
    public InventorySlotState InventorySlotState(double x, double y) =>
        Amber.Mobile.InventorySlotState.FromPacked(GetInventoryStackCount((float)x, (float)y));
    public void SplitInventoryStack(double x, double y, int count) =>
        DispatchInventorySplit((float)x, (float)y, count);

    [LibraryImport("SDL3", EntryPoint = "SDL_SetHint", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SetHint(string name, string value);

    [LibraryImport("SDL3", EntryPoint = "SDL_GetKeyboardFocus")]
    private static partial nint GetFocus();

    [LibraryImport("SDL3", EntryPoint = "SDL_GetWindowRelativeMouseMode")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool IsRelative(nint window);

    [LibraryImport("SDL3", EntryPoint = "SDL_TextInputActive")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool IsTextInputActive(nint window);

    public bool InWorld => GetFocus() is var window && window != 0 && IsRelative(window);
    public bool TextInputActive => GetFocus() is var window && window != 0 && IsTextInputActive(window);

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
        SendKey(code, down);
    }

    private void SendKey(Keycode code, bool down)
    {
        var method = JNIEnv.GetStaticMethodID(_sdlActivity.Handle, down ? "onNativeKeyDown" : "onNativeKeyUp", "(I)V");
        JNIEnv.CallStaticVoidMethod(_sdlActivity.Handle, method, new JValue((int)code));
    }

    public void KeyEvent(Keycode code, bool pressed, bool repeat = false)
    {
        if (pressed && repeat) SendKey(code, true);
        else Key(code, pressed);
    }

    public void CommitText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var value = new Java.Lang.String(text);
        var method = JNIEnv.GetStaticMethodID(_sdlInputConnection.Handle, "nativeCommitText", "(Ljava/lang/String;I)V");
        JNIEnv.CallStaticVoidMethod(_sdlInputConnection.Handle, method, new JValue(value), new JValue(1));
    }

    public void Backspace() { Key(Keycode.Del, true); Key(Keycode.Del, false); }
    public void ForwardDelete() { Key(Keycode.ForwardDel, true); Key(Keycode.ForwardDel, false); }
    public void Enter() { Key(Keycode.Enter, true); Key(Keycode.Enter, false); }
    public void Tab() { Key(Keycode.Tab, true); Key(Keycode.Tab, false); }

    public void ReleaseAll()
    {
        foreach (var code in _held.ToArray()) Key(code, false);
        if ((_buttons & 1) != 0) Action(TouchControlAction.PrimaryAction, false);
        if ((_buttons & 2) != 0) Action(TouchControlAction.SecondaryAction, false);
        if ((_buttons & 4) != 0) Action(TouchControlAction.PickBlock, false);
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
        // Mobile taps are contextual: use the targeted block, otherwise attack.
        if (playing && MenuPointer(3)) return;
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
        if (action is TouchControlAction.PrimaryAction or TouchControlAction.SecondaryAction or
            TouchControlAction.PickBlock)
        {
            int mask = action switch
            {
                TouchControlAction.PrimaryAction => 1,
                TouchControlAction.SecondaryAction => 2,
                _ => 4
            };
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
