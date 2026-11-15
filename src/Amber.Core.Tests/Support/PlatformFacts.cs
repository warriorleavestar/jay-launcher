namespace Amber.Core.Tests;

// Tests that can only run on some machines. xunit 2 cannot skip from inside a test, so these attributes set Skip
// up front: the report shows the test as skipped instead of a silent pass that never ran its body.
// Tests keep their OperatingSystem check as the first line too: the platform analyzer (CA1416) reads that check,
// not these attributes, to know the rest of the test only runs on that system.

/// <summary>A fact that runs only on Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows only"; }
}

/// <summary>A theory that runs only on Windows.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows only"; }
}

/// <summary>A fact that runs only on Linux and macOS.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "Linux and macOS only"; }
}

/// <summary>A theory that runs only on Linux and macOS.</summary>
public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute() { if (OperatingSystem.IsWindows()) Skip = "Linux and macOS only"; }
}

/// <summary>A fact that runs only on Linux.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Linux only"; }
}

/// <summary>A fact that runs only on macOS.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute() { if (!OperatingSystem.IsMacOS()) Skip = "macOS only"; }
}

/// <summary>
/// A fact that runs only when an environment variable is set, or set to <c>value</c> when one is given
/// (an opt-in test that needs something outside the repo, such as the network or a real EasyTier).
/// </summary>
public sealed class EnvFactAttribute : FactAttribute
{
    public EnvFactAttribute(string variable, string? value = null)
    {
        var actual = Environment.GetEnvironmentVariable(variable);
        if (value is null ? string.IsNullOrEmpty(actual) : actual != value)
            Skip = value is null ? $"set {variable} to run" : $"set {variable}={value} to run";
    }
}
