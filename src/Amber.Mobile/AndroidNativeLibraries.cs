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
}
