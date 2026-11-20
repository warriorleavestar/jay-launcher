namespace Amber.Mobile;

internal static class AndroidNativeLibraries
{
    private static readonly string[] Java21 =
    [
        "liblwjgl.so",
        "liblwjgl_opengl.so",
        "liblwjgl_stb.so",
        "libfreetype.so",
        "libopenal.so",
        "libmobileglues.so",
    ];

    private static readonly string[] Java25 =
    [
        .. Java21,
        "liblwjgl_vma.so",
        "libshaderc.so",
        "libspirv-cross.so",
    ];

    public static IReadOnlyList<string> RequiredForJava(int javaMajor) =>
        javaMajor >= 25 ? Java25 : Java21;

    public static string ResolveRoot(string runtimeRoot, string packagedRoot, int javaMajor, bool allowPackagedFallback)
    {
        var runtimeNative = Path.Combine(runtimeRoot, "native");
        if (!allowPackagedFallback || RequiredForJava(javaMajor).All(name => File.Exists(Path.Combine(runtimeNative, name))))
            return runtimeNative;
        return packagedRoot;
    }

    public static string ResolveLibrary(string name, string preferredRoot, string packagedRoot)
    {
        var preferred = Path.Combine(preferredRoot, name);
        return File.Exists(preferred) ? preferred : Path.Combine(packagedRoot, name);
    }
}
