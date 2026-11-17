using Amber.Core.Multiplayer.Gate;
using Amber.Server;
using System.Diagnostics;

namespace Amber.Core.Tests;

/// <summary>jay-server 的参数解析：--help 放在哪里都显示用法，不会因为“后面没有值”而崩。</summary>
public sealed class JayServerCliTests
{
    [Fact]
    public async Task RecordsOnUsesBuiltInNetworkEvenWhenWorkingDirectoryContainsJayFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-records-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "jay"), "not a network configuration");
            GateStore.Initialize(Path.Combine(root, "gate"), "203.0.113.10", 47670, 47670);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "jay-server.dll"));
            foreach (var arg in new[] { "records", "on", "--dir", root }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(); throw; }
            Assert.True(process.ExitCode == 0, $"{await output}\n{await error}");
            Assert.Equal("jay", new GateStore(Path.Combine(root, "gate")).Read().RecordNetwork);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    [InlineData("/?")]
    [InlineData("invite", "--help")]
    [InlineData("run", "-h")]
    public void HelpFlagsAnywhereShowUsage(params string[] args)
    {
        var options = Options.Parse(args);
        Assert.True(options.WantsHelp);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0m", 0)]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData("3d", 4320)]
    public void DurationsIncludingBareZero(string text, double minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), Amber.Server.Program.ParseDuration(text));

    [Fact]
    public void HelpAsUsernameIsNotHelp()
    {
        var options = Options.Parse(["invite", "help"]);
        Assert.False(options.WantsHelp);
        Assert.Equal(["help"], options.Positional);
    }

    [Fact]
    public void MissingValueIsAnArgumentError()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["invite", "Steve", "--ttl"]));
    }

    [Fact]
    public async Task MainReturnsErrorCodeInsteadOfCrashing()
    {
        Assert.Equal(1, await Program.Main(["invite", "Steve", "--ttl"]));
        Assert.Equal(0, await Program.Main(["--help"]));
    }

    [Fact]
    public void RelayAndPrivacyCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-cli-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            GateStore.Initialize(Path.Combine(root, "gate"), "203.0.113.10", 47670, 47670);
            var output = new StringWriter();
            int Run(params string[] args) => Program.Admin(root, Options.Parse(args), output);
            Assert.Throws<ArgumentException>(() => Run("privacy", "plus"));
            Run("relays", "add", "198.51.100.7:47671");
            Run("relays", "add", "203.0.113.10:47672", "--source", "127.0.0.1");
            Assert.Throws<ArgumentException>(() => Run("relays", "add", "no-port"));
            Assert.Equal(0, Run("privacy", "plus"));
            Run("relays", "list");
            Assert.Contains("127.0.0.1", output.ToString());
            var state = new GateStore(Path.Combine(root, "gate")).Read();
            Assert.Equal(PrivacyLevel.Plus, state.Privacy);
            Assert.Equal(2, state.Relays.Count);
            output.GetStringBuilder().Clear();
            Run("invite", "Steve");
            var invite = ReadInvite(output.ToString());
            Assert.Contains($"{invite.Host}:{invite.Port}", new[] { "198.51.100.7:47671", "203.0.113.10:47672" });
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void PlusPlusCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-cli-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            GateStore.Initialize(Path.Combine(root, "gate"), "203.0.113.10", 47670, 47670);
            var output = new StringWriter();
            int Run(params string[] args) => Program.Admin(root, Options.Parse(args), output);
            Run("relays", "add", "198.51.100.7:47671");
            Assert.NotNull(GateRelayCode.TryParse(output.ToString()));
            Run("relays", "add", "198.51.100.8:47671");
            Assert.Equal(0, Run("privacy", "plusplus"));
            output.GetStringBuilder().Clear();
            Run("relays", "code", "198.51.100.8:47671");
            Assert.NotNull(GateRelayCode.TryParse(output.ToString()));
            Assert.Equal(0, Run("status"));
            Assert.Contains("隐私++", output.ToString());
            Run("relays", "revoke", "198.51.100.8:47671");
            var state = new GateStore(Path.Combine(root, "gate")).Read();
            Assert.Equal(1, state.Epoch);
            Assert.Single(state.Relays);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>records on: an IP-only server's invite carries a record locator, no IP and no passphrase.</summary>
    [Fact]
    public void RecordsCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-cli-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            GateStore.Initialize(Path.Combine(root, "gate"), "203.0.113.10", 47670, 47670);
            var output = new StringWriter();
            int Run(params string[] args) => Program.Admin(root, Options.Parse(args), output);
            Assert.Throws<ArgumentException>(() => Run("records", "on", "--network", "not a network"));
            Assert.Equal(0, Run("records", "on"));
            output.GetStringBuilder().Clear();
            Run("invite", "Steve");
            var text = output.ToString();
            Assert.False(GateInvite.IsSealed(text));
            Assert.DoesNotContain("203.0.113.10", text);
            var invite = GateInvite.TryParse(text, out var error)!;
            Assert.Null(error);
            Assert.Equal("jay", invite.Locator!.NetworkId);
            Run("records", "status");
            Assert.Contains("203.0.113.10:47670", output.ToString());
            Run("records", "off");
            output.GetStringBuilder().Clear();
            Run("invite", "Alex");
            Assert.True(GateInvite.IsSealed(output.ToString()));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RotateCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-cli-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            GateStore.Initialize(Path.Combine(root, "gate"), "203.0.113.10", 47670, 47670);
            var output = new StringWriter();
            int Run(params string[] args) => Program.Admin(root, Options.Parse(args), output);
            Assert.Equal(0, Run("rotate", "on", "--window", "5m", "--range", "30000-40000", "--grace", "1d"));
            var store = new GateStore(Path.Combine(root, "gate"));
            Assert.Equal(new GateRotation(300, 30000, 40000), store.Rotation()!.Value.Rule);
            Assert.NotNull(store.Read().Rotation!.FixedUntil);
            Run("rotate", "status");
            Assert.Contains("网关直连", output.ToString());
            Run("invite", "Steve");
            Assert.NotNull(ReadInvite(output.ToString()).Rotation);
            Assert.Throws<ArgumentException>(() => Run("rotate", "on", "--range", "30000-30010"));
            Run("rotate", "off");
            Assert.Null(store.Rotation());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    [Fact]
    public void AccountCommandsAndLoginSection()
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-cli-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = new StringWriter();
            var answers = new Queue<string>(["password1", "password1", "password2", "different"]);
            int Run(params string[] args) => Program.AccountCommand(root, Options.Parse(["account", .. args]), output, _ => answers.Dequeue());
            Assert.Equal(0, Run("add", "alice"));
            Assert.Throws<ArgumentException>(() => Run("add", "alice"));
            Assert.Throws<ArgumentException>(() => Run("passwd", "alice"));
            Assert.Equal(0, Run("2fa", "on", "alice", "--issuer", "nz-friends"));
            Assert.Contains("otpauth://totp/nz-friends%3Aalice", output.ToString());
            Run("disable", "alice");
            Run("list");
            Assert.Contains("alice（停用）（两步验证）", output.ToString());
            Assert.Throws<ArgumentException>(() => Run("remove", "bob"));
            var accounts = new Amber.Core.Multiplayer.Public.AccountStore(Path.Combine(root, "accounts.json"));
            Assert.NotNull(accounts.TotpSecret("alice"));

            var server = Program.DirectoryBuilder(root, Options.Parse(["directory", "--accounts", "--require-2fa", "--factor", "mail:邮件里的码:/bin/false", "--no-admin"]), output, _ => { }).Build();
            var login = server.Login;
            Assert.NotNull(login);
            Assert.Equal(["totp", "mail"], login!.Factors.Select(f => f.Name));
            Assert.True(login.RequireSecondFactor);
            Program.PrintLoginSection(server, output);
            Assert.Contains("\"requireFactor\":true", output.ToString());
            Assert.Contains("（1 个账号）", output.ToString());
            Assert.Null(Program.LoginFactorsFor(Options.Parse(["directory"]), "nz-friends"));
            Assert.Null(Program.DirectoryBuilder(root, Options.Parse(["directory", "--no-admin"]), output, _ => { }).Build().Login);
            Assert.Throws<ArgumentException>(() => Program.LoginFactorsFor(Options.Parse(["directory", "--factor", "Bad:x:y"]), "n"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>The invite the CLI printed: an IP-only test server prints a sealed code and its passphrase.</summary>
    private static GateInvite ReadInvite(string output)
    {
        var passphrase = System.Text.RegularExpressions.Regex.Match(output, @"\d{4} \d{4} \d{4} \d{4}").Value;
        Assert.True(GateInvite.IsSealed(output));
        Assert.DoesNotMatch(@"JAYSS1-\S*\d{1,3}\.\d{1,3}\.", output);
        return GateInvite.TryOpen(output, passphrase, out var error) ?? throw new Xunit.Sdk.XunitException(error);
    }
}
