using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Diagnostics;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

/// <summary>Fixes from the 2026-11-09 security review (first batch): crash bundle redaction, crafted server invites, private key files, account edits on a running node.</summary>
public sealed class SecurityHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-sec-" + Guid.NewGuid().ToString("N")[..8]);

    public SecurityHardeningTests() => Directory.CreateDirectory(_dir);

    private static Dictionary<string, string> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public void CrashBundleNeverCarriesTheAccessToken()
    {
        const string token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0LXBsYXllci0xMjM0NSJ9.c2lnbmF0dXJlLXRlc3QtMTIzNDU2";
        const string yggdrasil = "0123456789abcdef0123456789abcdef";
        var hsErr = Path.Combine(_dir, "hs_err_pid4242.log");
        File.WriteAllText(hsErr,
            "# A fatal error has been detected by the Java Runtime Environment:\n" +
            $"java_command: net.minecraft.client.main.Main --username Steve --version 1.20.1 --accessToken {token} --userType msa\n" +
            "jvm_args: -Xmx4G\n");
        var zipPath = Path.Combine(_dir, "report.zip");

        CrashAnalyzer.ExportBundle(zipPath, [hsErr], [$"[Jay] launching with --accessToken {yggdrasil}"],
            [("latest.log", $"[12:00:00] [main/INFO]: Setting user: Steve, --accessToken {yggdrasil}")]);

        var entries = ReadZip(zipPath);
        Assert.Equal(["hs_err_pid4242.log", "latest.log", "launcher-output.txt"], entries.Keys.Order());
        foreach (var text in entries.Values)
        {
            Assert.DoesNotContain(token, text);
            Assert.DoesNotContain(yggdrasil, text);
        }
        Assert.Contains("--accessToken <redacted>", entries["hs_err_pid4242.log"]);
        // The rest of the crash file is still there to help.
        Assert.Contains("jvm_args: -Xmx4G", entries["hs_err_pid4242.log"]);
    }

    [Fact]
    public void CraftedServerInviteIsRejectedNotThrown()
    {
        // A valid checksum around a body whose host length byte points past the end.
        var body = new List<byte> { 1, 200, (byte)'a', 0x63, 0xDD };
        body.AddRange(new byte[GateInvite.KeyHashLength + GateInvite.TokenLength]);
        var bytes = body.ToArray();
        var checksum = SHA256.HashData([.. "jay-invite-v2"u8, .. bytes])[..4];
        var text = GateInvite.Prefix + GateInvite.Base32Encode([.. bytes, .. checksum]);

        var invite = GateInvite.TryParse(text, out var error);

        Assert.Null(invite);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void PrivateFileIsOwnerOnlyFromTheStart()
    {
        var path = Path.Combine(_dir, "secrets", "login.key");
        PrivateFile.WriteAllText(path, "first\n");
        PrivateFile.WriteAllText(path, "second\n");
        Assert.Equal("second\n", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        if (OperatingSystem.IsWindows())
        {
            // The file and the folder it made carry their own access list: nothing inherited, only this user, SYSTEM and Administrators.
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            string[] allowed = [me, "S-1-5-18", "S-1-5-32-544"];
            System.Security.AccessControl.FileSystemSecurity[] lists =
                [new FileInfo(path).GetAccessControl(), new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl()];
            foreach (var security in lists)
            {
                Assert.True(security.AreAccessRulesProtected);
                var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                    .OfType<System.Security.AccessControl.FileSystemAccessRule>().ToList();
                Assert.NotEmpty(rules);
                foreach (var rule in rules) Assert.Contains(rule.IdentityReference.Value, allowed);
            }
            return;
        }
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void AccountChangesFromTheCommandLineReachARunningNode()
    {
        var path = Path.Combine(_dir, "accounts.json");
        var cli = new AccountStore(path);
        cli.Set("alex", "correct horse battery");
        var node = new AccountStore(path);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(LoginResult.Ok, node.Check("alex", "correct horse battery", now));

        // Another process (jay-server account disable) changes the file while the node runs.
        Thread.Sleep(20);
        new AccountStore(path).SetDisabled("alex", true);
        Assert.Equal(LoginResult.Disabled, node.Check("alex", "correct horse battery", now));

        Thread.Sleep(20);
        new AccountStore(path).Remove("alex");
        Assert.Equal(LoginResult.Wrong, node.Check("alex", "correct horse battery", now));
        Assert.Empty(node.List());
    }

    [Fact]
    public void BrokenAccountFileKeepsTheLastGoodList()
    {
        var path = Path.Combine(_dir, "accounts.json");
        var node = new AccountStore(path);
        node.Set("alex", "correct horse battery");
        Thread.Sleep(20);
        File.WriteAllText(path, "{ not json", Encoding.UTF8);
        Assert.Equal(LoginResult.Ok, node.Check("alex", "correct horse battery", DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
