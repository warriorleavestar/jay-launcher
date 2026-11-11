namespace Amber.Core.Tests;

/// <summary>
/// The Windows relay node script installs a program that a scheduled task runs at every boot. Its folder must not be
/// writable by ordinary users (ProgramData is, by default). The ACL calls themselves only run on a real Windows install
/// (real-device test list); this keeps the script from losing them.
/// </summary>
public sealed class RelayNodeScriptSecurityTests
{
    private static readonly string Script = File.ReadAllText(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "node", "install-windows.ps1")));

    [Fact]
    public void TheNodeFolderIsLockedBeforeAnythingIsCopiedIntoIt()
    {
        Assert.Contains("SetAccessRuleProtection($true, $false)", Script);
        // SYSTEM and Administrators may write; LOCAL SERVICE (the task's account) only reads and runs.
        Assert.Contains("@(\"S-1-5-18\", \"FullControl\")", Script);
        Assert.Contains("@(\"S-1-5-32-544\", \"FullControl\")", Script);
        Assert.Contains("@(\"S-1-5-19\", \"ReadAndExecute\")", Script);
        Assert.DoesNotContain("S-1-5-32-545", Script);
        Assert.DoesNotContain("New-Item -ItemType Directory -Force $Dir", Script);
        var call = Script.IndexOf("    Initialize-NodeDir", StringComparison.Ordinal);
        Assert.True(call > 0);
        Assert.True(call < Script.IndexOf("Copy-Item -Destination $Dir", StringComparison.Ordinal));
    }

    [Fact]
    public void AFolderSomeoneElseMadeStopsTheInstall()
    {
        Assert.Contains("ReparsePoint", Script);
        Assert.Contains("GetOwner([Security.Principal.SecurityIdentifier])", Script);
        Assert.Contains("$trusted -notcontains $owner", Script);
    }
}
