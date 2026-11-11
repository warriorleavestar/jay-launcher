using Amber.Mobile;

namespace Amber.Android;

/// <summary>
/// Stable managed edge for the native GLFW bridge. No-op until a verified runtime session is
/// attached; the overlay never needs to know which renderer or Minecraft version is running.
/// </summary>
internal static class AndroidGameInputBridge
{
    private static ITouchInputSink? _session;

    public static void Attach(ITouchInputSink session) => _session = session;
    public static void Detach(ITouchInputSink session)
    {
        if (ReferenceEquals(_session, session)) _session = null;
    }

    public static void SetAction(TouchControlAction action, bool pressed)
    {
        NativeJvmHost.SetAction(action, pressed);
        _session?.SetAction(action, pressed);
    }

    public static void SetMovement(double x, double y)
    {
        NativeJvmHost.SetMovement(x, y);
        _session?.SetMovement(x, y);
    }
}
