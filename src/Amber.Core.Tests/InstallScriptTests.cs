using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// Runs the real node/install.sh and server/install-network-node.sh under bash with stub system commands first on PATH.
/// The stubs log every call that would touch the machine (users, groups, files outside the test root, systemd) and keep
/// a little state (users, groups, the unit systemd is "running"), so a re-run behaves like one on a real server.
/// Nothing outside the temp root is written. Unix only: Windows has no bash to run them with.
/// </summary>
public sealed class InstallScriptTests : IDisposable
{
    private static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    // Real tools the scripts (and the stubs, through $REAL_BIN) may use. Everything else on the machine stays off PATH,
    // so a ufw or firewall-cmd on the test machine never runs.
    private static readonly string[] RealTools =
        ["bash", "cat", "chmod", "cut", "find", "head", "od", "tr", "rm", "sed", "base64", "dirname", "awk", "grep", "mkdir"];

    private const string LoggedStub = "{ printf '%s' \"${0##*/}\"; printf ' %s' \"$@\"; printf '\\n'; } >> \"$STUB_LOG\"\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-install-scripts-" + Guid.NewGuid().ToString("N"));
    private string State => Path.Combine(_root, "state");
    private string UnitDir => Path.Combine(_root, "systemd");
    private string Log => Path.Combine(_root, "calls.log");

    public InstallScriptTests()
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var dir in new[] { "stubs", "real", "state", "systemd", "pkg" })
            Directory.CreateDirectory(Path.Combine(_root, dir));
        File.WriteAllText(Log, "");
        foreach (var tool in RealTools)
            File.CreateSymbolicLink(Path.Combine(_root, "real", tool), Which(tool));
        WriteStubs();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    // ---- node/install.sh ----

    private static string[] NodeCalls(bool newUser) =>
    [
        "id -u",
        "id amber-node",
        .. newUser ? new[] { "useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin amber-node" } : Array.Empty<string>(),
        "mkdir -p /opt/amber-node",
        "install -m 0755 <root>/state/work/x/pkg/easytier-core /opt/amber-node/easytier-core",
        "install -m 0755 <root>/state/work/x/pkg/easytier-cli /opt/amber-node/easytier-cli",
        "systemctl daemon-reload",
        "systemctl enable amber-node",
        "systemctl restart amber-node",
        "systemctl is-active --quiet amber-node",
    ];

    [Fact]
    public async Task Node_FirstInstall_CreatesUserWritesUnitAndStartsService()
    {
        if (OperatingSystem.IsWindows()) return;
        var run = await RunNode(port: 11010);
        Assert.True(run.Exit == 0, run.Output);
        Assert.Equal(NodeCalls(newUser: true), run.Calls);
        var unit = Path.Combine(UnitDir, "amber-node.service");
        Assert.Contains("tcp://0.0.0.0:11010", File.ReadAllText(unit), StringComparison.Ordinal);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(unit));
        Assert.Contains("tcp://0.0.0.0:11010", Running("amber-node"), StringComparison.Ordinal);
        Assert.Contains("节点已运行 / node is running", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Node_ReinstallWhileRunning_RestartsWithTheNewSettings()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(0, (await RunNode(port: 11010)).Exit);
        var run = await RunNode(port: 11020);
        Assert.True(run.Exit == 0, run.Output);
        Assert.Equal(NodeCalls(newUser: false), run.Calls);
        // A plain `start` would leave the old process (port 11010) running.
        var running = Running("amber-node");
        Assert.Contains("tcp://0.0.0.0:11020", running, StringComparison.Ordinal);
        Assert.DoesNotContain("11010", running, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Node_RestartFailure_FailsTheScript()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(0, (await RunNode(port: 11010)).Exit);
        File.WriteAllText(Path.Combine(State, "fail-restart"), "");
        var run = await RunNode(port: 11020);
        Assert.NotEqual(0, run.Exit);
        Assert.Equal(NodeCalls(newUser: false)[..^1], run.Calls);
        Assert.Contains("Job for amber-node.service failed", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("node is running", run.Output, StringComparison.Ordinal);
    }

    // ---- server/install-network-node.sh ----

    private static string[] NetworkCalls(bool newUser, bool tss) =>
    [
        "id -u",
        "id jay",
        .. newUser ? new[] { "useradd --system --home /var/lib/jay-network --shell /usr/sbin/nologin jay" } : Array.Empty<string>(),
        "install -d -m 0755 /opt/jay-server",
        "install -d -m 0700 -o jay -g jay /var/lib/jay-network",
        "install -m 0755 <root>/pkg/jay-server /opt/jay-server/jay-server",
        "getent group tss",
        .. tss ? new[] { "usermod -aG tss jay" } : Array.Empty<string>(),
        "systemctl daemon-reload",
        "systemctl enable jay-network",
        "systemctl restart jay-network",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Network_FirstInstall_JoinsTssOnlyWhenTheGroupExists(bool tss)
    {
        if (OperatingSystem.IsWindows()) return;
        if (tss) File.WriteAllText(Path.Combine(State, "group-tss"), "");
        var run = await RunNetwork(port: 11010);
        Assert.True(run.Exit == 0, run.Output);
        Assert.Equal(NetworkCalls(newUser: true, tss), run.Calls);
        var unit = File.ReadAllText(Path.Combine(UnitDir, "jay-network.service"));
        Assert.Equal(tss, unit.Contains("\nSupplementaryGroups=tss\n", StringComparison.Ordinal));
        Assert.Equal(tss ? "tss" : "", File.ReadAllText(Path.Combine(State, "groups-jay")));
        Assert.Contains("--listen 11010", Running("jay-network"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_ExistingUser_KeepsItsOtherGroups()
    {
        if (OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(State, "user-jay"), "");
        File.WriteAllText(Path.Combine(State, "groups-jay"), "adm,video");
        File.WriteAllText(Path.Combine(State, "group-tss"), "");
        var run = await RunNetwork(port: 11010);
        Assert.True(run.Exit == 0, run.Output);
        Assert.Equal(NetworkCalls(newUser: false, tss: true), run.Calls);
        Assert.Equal("adm,video,tss", File.ReadAllText(Path.Combine(State, "groups-jay")));
    }

    [Fact]
    public async Task Network_ReinstallWhileRunning_RestartsWithTheNewSettings()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(0, (await RunNetwork(port: 11010)).Exit);
        var run = await RunNetwork(port: 11020);
        Assert.True(run.Exit == 0, run.Output);
        Assert.Equal(NetworkCalls(newUser: false, tss: false), run.Calls);
        var running = Running("jay-network");
        Assert.Contains("--listen 11020", running, StringComparison.Ordinal);
        Assert.DoesNotContain("11010", running, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_RestartFailure_FailsTheScript()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(0, (await RunNetwork(port: 11010)).Exit);
        File.WriteAllText(Path.Combine(State, "fail-restart"), "");
        var run = await RunNetwork(port: 11020);
        Assert.NotEqual(0, run.Exit);
        Assert.Equal(NetworkCalls(newUser: false, tss: false), run.Calls);
        Assert.Contains("Job for jay-network.service failed", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("装好了", run.Output, StringComparison.Ordinal);
    }

    // ---- harness ----

    private sealed record Run(int Exit, string[] Calls, string Output);

    private string Running(string service) => File.ReadAllText(Path.Combine(State, "running-" + service));

    private Task<Run> RunNode(int port) =>
        RunScript(Path.Combine(Repo, "node", "install.sh"), new() { ["PORT"] = port.ToString(), ["PUBLIC_HOST"] = "relay.example.org" });

    private Task<Run> RunNetwork(int port)
    {
        // The script installs the jay-server next to it, so run a copy beside a placeholder binary.
        var script = Path.Combine(_root, "pkg", "install-network-node.sh");
        File.Copy(Path.Combine(Repo, "server", "install-network-node.sh"), script, overwrite: true);
        var server = Path.Combine(_root, "pkg", "jay-server");
        File.WriteAllText(server, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return RunScript(script, new() { ["PORT"] = port.ToString() });
    }

    private async Task<Run> RunScript(string script, Dictionary<string, string> env)
    {
        File.WriteAllText(Log, "");
        var start = new ProcessStartInfo(Path.Combine(_root, "real", "bash"))
        {
            UseShellExecute = false,
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(script);
        start.Environment.Clear();
        start.Environment["PATH"] = Path.Combine(_root, "stubs") + ":" + Path.Combine(_root, "real");
        start.Environment["HOME"] = _root;
        start.Environment["STUB_LOG"] = Log;
        start.Environment["STUB_STATE"] = State;
        start.Environment["REAL_BIN"] = Path.Combine(_root, "real");
        start.Environment["UNIT_DIR"] = UnitDir;
        foreach (var (key, value) in env) start.Environment[key] = value;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var calls = File.ReadAllLines(Log)
            .Select(line => line.Replace("/private" + _root, "<root>", StringComparison.Ordinal).Replace(_root, "<root>", StringComparison.Ordinal))
            .ToArray();
        return new Run(process.ExitCode, calls, await stdout + await stderr);
    }

    private void WriteStubs()
    {
        var sha = EasyTierRelease.For("linux", Architecture.X64).Sha256;
        // Logged: everything that would change the machine or ask it about users and groups.
        Stub("id", LoggedStub + """
            if [ "$#" -eq 1 ] && [ "$1" = -u ]; then echo 0; exit 0; fi
            if [ -e "$STUB_STATE/user-$1" ]; then echo "uid=999($1)"; exit 0; fi
            echo "id: '$1': no such user" >&2; exit 1
            """);
        Stub("useradd", LoggedStub + """
            user="${!#}"
            [ -e "$STUB_STATE/user-$user" ] && exit 9
            : > "$STUB_STATE/user-$user"; : > "$STUB_STATE/groups-$user"
            """);
        // usermod -aG adds to the user's groups; -G without -a replaces them, like the real one.
        Stub("usermod", LoggedStub + """
            append=0; groups=
            while [ "$#" -gt 1 ]; do
                case "$1" in
                    -aG|-Ga) append=1; groups="$2"; shift 2 ;;
                    -a) append=1; shift ;;
                    -G) groups="$2"; shift 2 ;;
                    *) echo "usermod stub: unexpected $1" >&2; exit 2 ;;
                esac
            done
            f="$STUB_STATE/groups-$1"
            [ -e "$STUB_STATE/user-$1" ] || exit 6
            if [ "$append" = 1 ] && [ -s "$f" ]; then printf ',%s' "$groups" >> "$f"; else printf '%s' "$groups" > "$f"; fi
            """);
        Stub("groupadd", LoggedStub);
        Stub("getent", LoggedStub + """
            if [ "$1" = group ] && [ -e "$STUB_STATE/group-$2" ]; then echo "$2:x:59:"; exit 0; fi
            exit 2
            """);
        Stub("install", LoggedStub);
        Stub("mkdir", LoggedStub);
        Stub("journalctl", LoggedStub);
        // systemd: restart (re)loads the unit file; start leaves a running service as it is.
        Stub("systemctl", LoggedStub + """
            svc="${!#}"
            running="$STUB_STATE/running-$svc"
            case "$1" in
                daemon-reload|enable) exit 0 ;;
                start) [ -e "$running" ] || "$REAL_BIN/cat" "$UNIT_DIR/$svc.service" > "$running"; exit 0 ;;
                restart)
                    "$REAL_BIN/rm" -f "$running"
                    if [ -e "$STUB_STATE/fail-restart" ]; then echo "Job for $svc.service failed." >&2; exit 1; fi
                    "$REAL_BIN/cat" "$UNIT_DIR/$svc.service" > "$running"; exit 0 ;;
                is-active) [ -e "$running" ]; exit ;;
            esac
            echo "systemctl stub: unexpected $1" >&2; exit 2
            """);
        // Not logged: plumbing for node/install.sh's download, which the tests fake.
        Stub("uname", "echo x86_64\n");
        Stub("sleep", "exit 0\n");
        Stub("mktemp", "\"$REAL_BIN/mkdir\" -p \"$STUB_STATE/work\"; echo \"$STUB_STATE/work\"\n");
        Stub("curl", """
            while [ "$#" -gt 0 ]; do [ "$1" = -o ] && : > "$2"; shift; done
            """);
        Stub("sha256sum", $"echo \"{sha}  $1\"\n");
        Stub("unzip", """
            while [ "$#" -gt 0 ]; do [ "$1" = -d ] && d="$2"; shift; done
            "$REAL_BIN/mkdir" -p "$d/pkg"; : > "$d/pkg/easytier-core"; : > "$d/pkg/easytier-cli"
            """);
    }

    private void Stub(string name, string body)
    {
        var path = Path.Combine(_root, "stubs", name);
        File.WriteAllText(path, "#!" + Path.Combine(_root, "real", "bash") + "\nset -u\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string Which(string tool)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/usr/bin", "/bin"]);
        return dirs.Select(dir => Path.Combine(dir, tool)).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("missing tool for the install script tests: " + tool);
    }
}
