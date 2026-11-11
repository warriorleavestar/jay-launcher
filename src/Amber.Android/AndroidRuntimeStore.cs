using Android.Content;

namespace Amber.Android;

/// <summary>
/// Owns Jay's in-app Android runtime. Runtime components are kept separate from the APK so
/// their licenses, sources and hashes can be shown before the user-initiated download.
/// </summary>
internal static class AndroidRuntimeStore
{
    private const string ReadyMarker = "runtime.ready";

    public static string Root(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "game-runtime");

    public static bool IsInstalled(Context context) => File.Exists(Path.Combine(Root(context), ReadyMarker));
}
