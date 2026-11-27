namespace Amber.Mobile;

/// <summary>Selects one of Jay's verified Android JVM bundles for a game's required Java major.</summary>
public static class AndroidRuntimeChoice
{
    public const int LegacyMaximumJavaMajor = 21;
    public const int ModernJavaMajor = 25;

    public static bool IsSupported(int requiredJava) =>
        requiredJava is >= 1 and <= ModernJavaMajor;

    /// <summary>
    /// Java 25 runs class files produced for Java 22-25 as well as Java 25 itself. Keeping those
    /// versions on the Java 21 bundle makes an otherwise compatible custom instance fail only
    /// after its runtime has already been downloaded.
    /// </summary>
    public static bool UsesJava25(int requiredJava) =>
        requiredJava > LegacyMaximumJavaMajor;
}
