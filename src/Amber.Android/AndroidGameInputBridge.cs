using Amber.Mobile;

namespace Amber.Android;

/// <summary>
/// Stable managed edge for the native GLFW bridge: forwards touch input to the native JVM host,
/// so the overlay never needs to know which renderer or Minecraft version is running.
/// </summary>
internal static class AndroidGameInputBridge
{
    public static void SetAction(TouchControlAction action, bool pressed)
    {
        NativeJvmHost.SetAction(action, pressed);
    }

    public static void SetMovement(double x, double y)
    {
        NativeJvmHost.SetMovement(x, y);
    }

    public static void AddLook(double x, double y)
    {
        NativeJvmHost.AddLook(x, y);
    }

    public static void SetPointer(double x, double y, bool click)
    {
        NativeJvmHost.SetPointer(x, y, click);
    }
}
