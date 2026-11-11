using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amber.Core;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>仅运行未提权的假核心和守护；热点的 COM、网卡和 BFE 边界全部不接触真实系统。</summary>
public sealed class HotspotGuardTests
{
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public void HotspotRequestContainsOnlyTheExplicitSwitch()
    {
        var json = JsonSerializer.Serialize(new GuardCommand("hotspot", 7, "", null, Hotspot: true), AmberJsonContext.Default.GuardCommand);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("hotspot", document.RootElement.GetProperty("cmd").GetString());
        Assert.True(document.RootElement.GetProperty("hotspot").GetBoolean());
        Assert.Equal(7, document.RootElement.GetProperty("n").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("network").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("env").ValueKind);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task NoExitRejectsSharingWithoutStoppingTheExistingCore()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var guard = ElevatedGuard.Start(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "Write-Output 'core-ready'; Start-Sleep 60"],
            new Dictionary<string, string>(), elevate: false);
        await guard.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var error = await Assert.ThrowsAsync<IOException>(() => guard.SetHotspotSharingAsync(true).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("remote exit", error.Message);
        Assert.False(guard.HasExited);
        Assert.False(guard.HotspotSharingActive);
        Assert.False(guard.StartPending);
        await guard.SetHotspotSharingAsync(false).WaitAsync(TimeSpan.FromSeconds(10));
        await guard.SetHotspotSharingAsync(false).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(guard.HotspotSharingActive);
        guard.Dispose();
        Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(10)));
    }

    /// <summary>Records the guard's exit network steps; can fail the hotspot restore.</summary>
    private sealed class FakeExitHelper(List<string> events, bool failSharingCleanup) : IExitNetworkHelper
    {
        public void PreStop(Action<string, string> forward)
        {
            events.Add("hotspot");
            if (failSharingCleanup)
            {
                events.Add("hotspot");
                forward("e", "mock sharing restore failed");
            }
            events.Add("dns");
            if (failSharingCleanup) throw new IOException("mock sharing restore failed");
        }

        public void ReleaseRoutes(Action<string, string> forward) { events.Add("routes-ready"); events.Add("routes"); }
        public void ExitPolicy(Action<string, string> forward) => events.Add("policy");
        public void EnterRoutes(ExitNetworkPlan plan, Action<string, string> forward) => events.Add("enter");
        public void Ready(ExitNetworkPlan plan, int corePid, Action<string, string> forward) => events.Add("ready");
        public bool SetHotspot(bool on, Action<string, string> forward) => on;
        public void Tick(Action<string, string> forward) { }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TeardownRestoresSharingBeforeDestroyingTunAndAlwaysReleasesTheCore(bool failSharingCleanup)
    {
        if (!OperatingSystem.IsWindows()) return;
        var events = new List<string>();
        var ack = new MemoryStream();
        using var session = new WindowsElevatedGuardHost.GuardSession(Stream.Null, Stream.Null, Stream.Null, ack, PowerShell,
            () => new FakeExitHelper(events, failSharingCleanup)) { Trace = events.Add };
        // Load the helper the way a start with a remote exit does, then tear down.
        typeof(WindowsElevatedGuardHost.GuardSession).GetField("_helper", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(session, new FakeExitHelper(events, failSharingCleanup));
        var failed = false;
        try { session.StopSession(); }
        catch (IOException) { failed = true; }
        Assert.Equal(failSharingCleanup, failed);
        var expected = failSharingCleanup ? "hotspot,hotspot,dns,core,routes-ready,routes,policy" : "hotspot,dns,core,routes-ready,routes,policy";
        Assert.Equal(expected, string.Join(',', events));
        Assert.Contains("hotspot off", Encoding.UTF8.GetString(ack.ToArray()));
    }

    /// <summary>
    /// The real exit helper: one Windows PowerShell child fed the fixed setup through standard input (no encoded command).
    /// Only steps that touch nothing are used: the lease renewal right after load, and requests it must refuse.
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ExitHelperLoadsTheSetupFromStandardInputAndAnswers()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.DoesNotContain("Encoded", ExitNetworkHelper.CommandLine, StringComparison.OrdinalIgnoreCase);
        using var helper = new ExitNetworkHelper();
        var forwarded = new List<string>();
        helper.Tick((kind, text) => forwarded.Add(kind + ":" + text));
        var refused = Assert.Throws<IOException>(() => helper.SetHotspot(true, (kind, text) => forwarded.Add(kind + ":" + text)));
        Assert.Contains("remote exit", refused.Message);
        // Still answering after a failed request.
        helper.Tick((kind, text) => forwarded.Add(kind + ":" + text));
        Assert.Empty(forwarded);
    }
}