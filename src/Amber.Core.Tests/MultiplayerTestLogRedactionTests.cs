using System.Diagnostics;
using System.Text;

namespace Amber.Core.Tests;

/// <summary>
/// tools/multiplayer-test.ps1 writes the output of the CLI it runs into a log in the repository folder. Room codes,
/// invite codes and the other join credentials in that output must never reach the log, in any branch of the script.
/// The script's own functions run in PowerShell against synthetic output (Windows PowerShell on Windows, pwsh
/// elsewhere when it is installed).
/// </summary>
public sealed class MultiplayerTestLogRedactionTests
{
    private static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string Script = File.ReadAllText(Path.Combine(Repo, "tools", "multiplayer-test.ps1"));

    // Synthetic values in the shapes the CLI prints; none of them is a real code.
    private const string A = "ABCDEFGHJKMNPQRSTVWXYZ0123";
    private const string B = "Z9Y8X7W6V5T4S3R2Q1P0NMKJHG";
    private const string Net = "QWERTYUIOPAS";
    private const string RoomSecret = "MNBVCXZLKJHGFDSA2468";
    private const string Passphrase = "orbitlanternmaplequiet";
    // EasyTier's own secret is derived from the room code, so it never appears inside one.
    private const string EtSecret = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";
    private const string WrappedRoom = $"AMB-T2D-{Net}-{RoomSecret}-25565";
    private const string PlainRoom = $"AMB-{Net}-{RoomSecret}-25566";
    private const string Invite = $"JAY2-{A}-{B}";
    private const string InviteWithGame = $"JAY2-T1D-{A}-{B}-X-GABC12";

    private static readonly string[] Harmless =
    [
        "LOCAL 127.0.0.1:40000",
        "PATH direct 12ms",
        "RPC 15888",
        "  et| network_secret_digest: Some([1, 2, 3])",
        "  et| network_name: \"jay-room\"",
        @"CLI C:\work\Jay-launcher\easytier-cli.exe",
    ];

    /// <summary>The forms EasyTier's verbose output and the CLI print its secret in (see NetDiagnostics.SecretConfigPattern).</summary>
    private static readonly string[] EasyTierSecretLines =
    [
        $"  et| network_secret = Some(\"{EtSecret}\")",
        $"  et| NetworkIdentity {{ network_name: \"jay-room\", network_secret: Some(\"{EtSecret}\"), network_secret_digest: Some([1]) }}",
        $"  et| network_secret: '{EtSecret}'",
        $"  et| network_secret={EtSecret}",
        $"ARGS --network-name jay-room --network-secret {EtSecret}",
    ];

    [Fact]
    public void TheScriptRoutesEveryLogWriteThroughRedaction()
    {
        Assert.Contains(". (Join-Path $PSScriptRoot \"redact.ps1\")", Script);
        Assert.Contains("(Hide-Credentials $text $secrets)", Script);
        Assert.Contains("Add-RedactedFile $f $log $secrets", Script);
        Assert.Contains("Remove-RawOutput $work", Script);
        // No shortened codes in result lines: a fixed-length prefix still says too much about some formats.
        Assert.DoesNotContain("$room.Substring", Script);
        Assert.DoesNotContain("$invite.Substring", Script);
        // Every other direct write to the log goes through redaction (Write-Log writes the $line it redacted above).
        foreach (var line in Script.Split('\n').Where(l => l.Contains("Add-Content -Path $log") && !l.Contains("-Value $line ")))
            Assert.Contains("Hide-Credentials", line);
    }

    [Fact]
    public async Task NoBranchWritesAJoinCodeToTheLog()
    {
        var pwsh = FindPowerShell();
        if (pwsh is null) return;
        var work = Directory.CreateTempSubdirectory("amber-mp-redact-").FullName;
        try
        {
            string[] hostOut =
            [
                "TRANSPORT Wrapped disguise=True server=- socks=-", .. Harmless, $"ROOM {WrappedRoom}",
                $"  et| network_secret = \"{RoomSecret}\"", $"  et| {{\"network_secret\":\"{RoomSecret}\"}}",
                .. EasyTierSecretLines,
            ];
            Write(work, "room-host.out.txt", hostOut);
            Write(work, "room-host.err.txt", [$"error: room {WrappedRoom} closed"]);
            Write(work, "room-guest.out.txt", [.. Harmless, $"  tunnel| joined {WrappedRoom}"]);
            Write(work, "room-guest.err.txt", []);
            Write(work, "direct-host.out.txt",
                [.. Harmless, $"INVITE {InviteWithGame}", $"SEALED JAYSS1-{A}{B}", $"PASSPHRASE {Passphrase}", $"REPLY JAY2R-{A}"]);
            Write(work, "direct-host.err.txt", []);
            Write(work, "direct-guest.out.txt", [$"  lobby| knocking with {InviteWithGame}", "DIRECT", "LOCAL 127.0.0.1:40001"]);
            Write(work, "direct-guest.err.txt", [$"Unhandled exception: invite {InviteWithGame} expired"]);
            // Early exit: the host printed its codes after the script stopped waiting, so no code was captured.
            Write(work, "early.out.txt", [$"ROOM {PlainRoom}", $"INVITE {Invite}", $"  lobby| retry {Invite}", .. Harmless, .. EasyTierSecretLines]);
            Write(work, "early.err.txt", [$"crash while serving {PlainRoom}"]);

            var harness = $$"""
                $ErrorActionPreference = 'Stop'
                $work = '{{work}}'
                $log = Join-Path $work 'log.txt'
                $results = New-Object System.Collections.ArrayList
                $secrets = New-Object System.Collections.ArrayList
                . '{{Path.Combine(Repo, "tools", "redact.ps1")}}'
                $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{Path.Combine(Repo, "tools", "multiplayer-test.ps1")}}', [ref]$null, [ref]$null)
                foreach ($name in 'Write-Log', 'Add-Result', 'Save-Output', 'Wait-Line') {
                    $f = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true) | Select-Object -First 1
                    if (-not $f) { throw "missing $name" }
                    . ([scriptblock]::Create($f.Extent.Text))
                }
                function Proc($name) { @{ Process = [pscustomobject]@{ HasExited = $true }; Out = (Join-Path $work "$name.out.txt"); Err = (Join-Path $work "$name.err.txt"); Name = $name } }

                # Room case: the code still reaches the join step in full, then the outputs are saved.
                $h = Proc 'room-host'; $g = Proc 'room-guest'
                $room = Wait-Line $h 'ROOM ' 5
                if ($room -ne '{{WrappedRoom}}') { throw "Wait-Line returned '$room'" }
                [void]$secrets.Add($room)
                Add-Result 'room' $true 'got a room code'
                Write-Log "joining with $room"
                Save-Output $h; Save-Output $g

                # Direct case, same shape.
                $h = Proc 'direct-host'; $g = Proc 'direct-guest'
                $invite = Wait-Line $h 'INVITE ' 5
                if ($invite -ne '{{InviteWithGame}}') { throw "Wait-Line returned '$invite'" }
                [void]$secrets.Add($invite)
                Add-Result 'direct' $false "join failed for $invite"
                Save-Output $g; Save-Output $h

                # Early exit and the catch block: nothing captured, the patterns alone must hide the codes.
                $secrets.Clear()
                Save-Output (Proc 'early')
                Write-Log "error: lan-direct-join {{Invite}} failed"
                foreach ($r in $results) { Write-Log $r }
                Write-Output 'PASS'
                """;
            var output = await RunAsync(pwsh, work, harness);
            Assert.Contains("PASS", output);

            var log = await File.ReadAllTextAsync(Path.Combine(work, "log.txt"));
            foreach (var secret in new[] { A, B, RoomSecret, Passphrase, Net, WrappedRoom, PlainRoom, Invite, InviteWithGame, EtSecret })
            {
                Assert.DoesNotContain(secret, log);
                Assert.DoesNotContain(secret, output);
            }
            foreach (var prefix in new[] { "ROOM", "INVITE", "SEALED", "PASSPHRASE", "REPLY" })
                Assert.Contains($"  {prefix} <hidden>", log);
            Assert.Contains("network_secret = \"<hidden>\"", log);
            Assert.Contains("network_secret = Some(\"<hidden>\")", log);
            Assert.Contains("network_secret: Some(\"<hidden>\"), network_secret_digest", log);
            Assert.Contains("ARGS --network-name jay-room --network-secret <hidden>", log);
            foreach (var line in Harmless) Assert.Contains("  " + line, log);
            Assert.Contains("PASS room", log);
            Assert.Contains("FAIL direct join failed for <hidden>", log);
        }
        finally { Directory.Delete(work, true); }
    }

    private static void Write(string folder, string name, string[] lines) =>
        File.WriteAllLines(Path.Combine(folder, name), lines, new UTF8Encoding(true));

    private static string? FindPowerShell()
    {
        if (OperatingSystem.IsWindows()) return "powershell.exe";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, "pwsh"))) return Path.Combine(dir, "pwsh");
        return null;
    }

    private static async Task<string> RunAsync(string pwsh, string work, string script)
    {
        var path = Path.Combine(work, "harness.ps1");
        await File.WriteAllTextAsync(path, script, new UTF8Encoding(true));
        var info = new ProcessStartInfo(pwsh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        var output = await stdout;
        Assert.True(process.ExitCode == 0, output + "\n" + await stderr);
        return output;
    }
}
