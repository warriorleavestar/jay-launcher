using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Amber.Core.Multiplayer;

/// <summary>
/// What the Windows guard needs for a remote exit and hotspot sharing. Each call reports side lines through
/// <c>forward(kind, text)</c>: kind "e" is an error or warning for Jay's log, kind "h" is the hotspot state ("on"/"off").
/// A failed step throws <see cref="IOException"/> with the step's message.
/// </summary>
internal interface IExitNetworkHelper : IDisposable
{
    /// <summary>Hotspot sharing off (one retry), then DNS restored. Throws the last failure after trying both.</summary>
    void PreStop(Action<string, string> forward);

    /// <summary>Waits for the capture routes to go, then removes this session's bypass routes.</summary>
    void ReleaseRoutes(Action<string, string> forward);

    /// <summary>Releases the dynamic traffic policy session.</summary>
    void ExitPolicy(Action<string, string> forward);

    void EnterRoutes(ExitNetworkPlan plan, Action<string, string> forward);

    /// <summary>Waits for this session's Wintun adapter and capture routes, then sets DNS and the traffic policy.</summary>
    void Ready(ExitNetworkPlan plan, int corePid, Action<string, string> forward);

    /// <summary>Turns hotspot sharing on or off and returns whether it is now on.</summary>
    bool SetHotspot(bool on, Action<string, string> forward);

    /// <summary>Renews the bypass leases and checks hotspot sharing; called about twice a second.</summary>
    void Tick(Action<string, string> forward);
}

/// <summary>
/// Runs the fixed exit and hotspot PowerShell setup (<see cref="SetupScript"/>) in one child Windows PowerShell for the
/// guard's lifetime. The script comes from Jay's own binary and goes in through standard input, so the command line is
/// short and fixed (no encoded command). Requests are JSON lines on standard input; replies are lines on standard output
/// that start with <see cref="Prefix"/>. Nothing is written to disk. The child inherits the guard's kill-on-close job.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ExitNetworkHelper : IExitNetworkHelper
{
    internal const string Prefix = "JAYEXIT ";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(90);

    private readonly Process _process;
    private readonly StreamReader _replies;

    internal static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// The fixed command line. "-Command -" reads statements from standard input. Execution policy only matters because the
    /// network cmdlets' modules load formatting files, which a Restricted policy refuses.
    /// </summary>
    internal const string CommandLine = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -";

    public ExitNetworkHelper()
    {
        var info = new ProcessStartInfo(PowerShellPath, CommandLine)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
        };
        _process = Process.Start(info) ?? throw new IOException("Exit network helper did not start");
        _replies = _process.StandardOutput;
        // Drain standard error so a chatty failure can never block the child; the guard reports failures from the replies.
        _ = _process.StandardError.ReadToEndAsync();
        _process.StandardInput.NewLine = "\n";
        // The whole program is one dot-sourced block followed by a blank line, so PowerShell runs it as one statement. Requests
        // are only sent after the block says it is ready: from then on the block, not the host, reads standard input.
        _process.StandardInput.Write(Program.Replace("\r\n", "\n") + "\n\n");
        _process.StandardInput.Flush();
        Read((_, _) => { });
    }

    public void PreStop(Action<string, string> forward) => Call("pre-stop", forward);

    public void ReleaseRoutes(Action<string, string> forward) => Call("release-routes", forward);

    public void ExitPolicy(Action<string, string> forward) => Call("exit-policy", forward);

    public void EnterRoutes(ExitNetworkPlan plan, Action<string, string> forward) => Call("enter-routes", forward, plan);

    public void Ready(ExitNetworkPlan plan, int corePid, Action<string, string> forward) => Call("ready", forward, plan, corePid);

    public bool SetHotspot(bool on, Action<string, string> forward) => Call("hotspot", forward, on: on) == "on";

    public void Tick(Action<string, string> forward) => Call("tick", forward);

    private string Call(string op, Action<string, string> forward, ExitNetworkPlan? plan = null, int? pid = null, bool? on = null)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("op", op);
            if (plan is not null)
            {
                json.WritePropertyName("network");
                JsonSerializer.Serialize(json, plan, AmberJsonContext.Default.ExitNetworkPlan);
            }
            if (pid is { } p) json.WriteNumber("pid", p);
            if (on is { } o) json.WriteBoolean("on", o);
            json.WriteEndObject();
        }
        try
        {
            _process.StandardInput.WriteLine(Utf8.GetString(buffer.ToArray()));
            _process.StandardInput.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new IOException("Exit network helper is not running", e);
        }
        return Read(forward);
    }

    /// <summary>Reads reply lines until "ok" or "fail"; other output from PowerShell is ignored.</summary>
    private string Read(Action<string, string> forward)
    {
        using var deadline = new CancellationTokenSource(CallTimeout);
        while (true)
        {
            string? line;
            try { line = _replies.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException e) { throw new IOException("Exit network helper did not answer", e); }
            if (line is null) throw new IOException("Exit network helper exited");
            if (!line.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            var parts = line[Prefix.Length..].Split(' ', 2);
            var text = parts.Length == 2 ? Decode(parts[1]) : "";
            switch (parts[0])
            {
                case "ok": return text;
                case "fail": throw new IOException(text.Length > 0 ? text : "Exit network step failed");
                case "e" or "h": forward(parts[0], text); break;
            }
        }
    }

    private static string Decode(string value)
    {
        try { return Utf8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException) { return ""; }
    }

    public void Dispose()
    {
        try { _process.StandardInput.Close(); }
        catch (IOException) { }
        if (!_process.WaitForExit(5000))
        {
            try { _process.Kill(); }
            catch (InvalidOperationException) { }
        }
        _process.Dispose();
    }

    /// <summary>The fixed setup: exit routing, DNS, traffic policy and hotspot sharing. Loaded only by this helper.</summary>
    internal const string SetupScript = ExitRoutingScript.Script + "\n" + ExitDnsScript.Script + "\n" + ExitTrafficPolicyScript.Script
        + "\n" + HotspotTrafficPolicyScript.Script + "\n" + HotspotSharingScript.Script;

    /// <summary>The setup plus a request loop. Every step and its order are the ones the old PowerShell guard ran.</summary>
    internal const string Program = ". {\n$ErrorActionPreference = 'Stop'\n" + SetupScript + "\n" + Dispatcher + "\n}";

    private const string Dispatcher = """
        $script:activeExitPlan = $null
        $jayEnc = New-Object System.Text.UTF8Encoding($false)
        $jayHotspotChecked = [DateTime]::MinValue
        function Send-JayLine([string]$kind, [string]$text) {
            [Console]::Out.WriteLine('JAYEXIT ' + $kind + ' ' + [Convert]::ToBase64String($jayEnc.GetBytes($text)))
            [Console]::Out.Flush()
        }
        function Send-JayWarning($item) {
            if ($item -is [System.Management.Automation.WarningRecord]) { Send-JayLine 'e' ([string]$item) }
        }
        function Send-JayHotspot { Send-JayLine 'h' $(if ($script:HotspotSharingActive) { 'on' } else { 'off' }) }
        Send-JayLine 'ok' 'ready'
        while ($null -ne ($jayLine = [Console]::In.ReadLine())) {
            if ($jayLine.Length -eq 0) { continue }
            try {
                $jayMsg = $jayLine | ConvertFrom-Json
                $jayResult = ''
                switch ($jayMsg.op) {
                    'pre-stop' {
                        $failure = $null
                        try { Exit-HotspotSharing }
                        catch {
                            # Retry once while the public Wintun connection still exists; COM may be finishing a sharing transition.
                            Start-Sleep -Milliseconds 100
                            try { Exit-HotspotSharing }
                            catch { $failure = $_.Exception.Message; Send-JayLine 'e' $failure }
                        }
                        $script:activeExitPlan = $null
                        try { Exit-ExitDns 3>&1 | ForEach-Object { Send-JayWarning $_ } }
                        catch { $failure = $_.Exception.Message; Send-JayLine 'e' $failure }
                        if ($failure) { throw $failure }
                    }
                    'release-routes' { Wait-ExitReleased; Exit-ExitRoutes }
                    'exit-policy' { Exit-ExitTrafficPolicy }
                    'enter-routes' { Enter-ExitRoutes $jayMsg.network }
                    'ready' {
                        $core = [System.Diagnostics.Process]::GetProcessById([int]$jayMsg.pid)
                        $adapter = Wait-ExitReady $jayMsg.network $core
                        Enter-ExitDns $adapter 3>&1 | ForEach-Object { Send-JayWarning $_ }
                        Enter-ExitTrafficPolicy $adapter $jayMsg.network
                        $script:activeExitPlan = $jayMsg.network
                    }
                    'hotspot' {
                        if ($jayMsg.on -eq $true) {
                            if (-not $script:activeExitPlan -or -not $script:exitAdapter) { throw 'Select a remote exit in full LAN mode before sharing a hotspot' }
                            Enter-HotspotSharing $script:exitAdapter
                        } else { Exit-HotspotSharing }
                        $jayResult = $(if ($script:HotspotSharingActive) { 'on' } else { 'off' })
                    }
                    'tick' {
                        Renew-ExitRoutes
                        if ($script:HotspotSharingActive -and ([DateTime]::UtcNow - $jayHotspotChecked).TotalSeconds -ge 2) {
                            $jayHotspotChecked = [DateTime]::UtcNow
                            try { Assert-HotspotSharing }
                            catch {
                                Send-JayLine 'e' $_.Exception.Message
                                Exit-HotspotSharing
                                Send-JayHotspot
                            }
                        }
                    }
                    default { throw 'Unknown exit network request' }
                }
                Send-JayLine 'ok' ([string]$jayResult)
            } catch {
                Send-JayLine 'fail' $_.Exception.Message
            }
        }
        """;
}
