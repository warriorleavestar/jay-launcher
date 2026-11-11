using System.Diagnostics;
using System.Runtime.InteropServices;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>Privacy plan item 15: what the sandboxed game is granted, and how each system's sandbox is set up from it.</summary>
public sealed class GameSandboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-sandbox-" + Guid.NewGuid().ToString("N")[..8]);

    public GameSandboxTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private string FileAt(string content, params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---------------------------------------------------------------- grants

    [Fact]
    public void Grants_InstanceWritable_GameFilesReadOnly_ArgumentFilesReadOnly()
    {
        var game = Dir("game");
        var instance = Dir("game", "versions", "1.21");
        var java = FileAt("", "java", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        var agent = FileAt("jar", "data", "authlib-injector.jar");
        var log = FileAt("<xml/>", "data", "notrace", "log4j2.xml");
        var grants = GameSandbox.For(instance, game, java, ["-javaagent:" + agent + "=https://example.invalid", "-Dlog4j.configurationFile=" + log, "-Xmx2G"],
            new SandboxOptions { Enabled = true, Hidden = [Path.Combine(_root, "data")], ExtraReadWrite = [Dir("runtime", "bake-cache")] });

        Assert.Contains(instance, grants.ReadWrite);
        Assert.Contains(Path.Combine(_root, "runtime", "bake-cache"), grants.ReadWrite);
        Assert.Contains(Path.Combine(_root, "java"), grants.ReadOnly);
        Assert.Contains(Path.Combine(game, "libraries"), grants.ReadOnly);
        Assert.Contains(Path.Combine(game, "assets"), grants.ReadOnly);
        Assert.Contains(agent, grants.ReadOnly);
        Assert.Contains(log, grants.ReadOnly);
        // Only the named files, never their folder (the data folder) and never the game root as a whole.
        Assert.DoesNotContain(Path.Combine(_root, "data"), grants.ReadOnly.Concat(grants.ReadWrite));
        Assert.DoesNotContain(game, grants.ReadOnly);
        Assert.Null(grants.Problem());
    }

    [Fact]
    public void Grants_ReadWriteWinsOverReadOnly_AndDuplicatesCollapse()
    {
        var a = Dir("a");
        var grants = new SandboxGrants { ReadWrite = [a, a + Path.DirectorySeparatorChar], ReadOnly = [a, Dir("b")] }.Normalized();
        Assert.Equal([a], grants.ReadWrite);
        Assert.Equal([Path.Combine(_root, "b")], grants.ReadOnly);
    }

    [Fact]
    public void Grants_RefuseTheDataFolderItself_AndRelativePaths()
    {
        var data = Dir("data");
        Assert.NotNull(new SandboxGrants { ReadWrite = [data], Hidden = [data] }.Problem());
        Assert.NotNull(new SandboxGrants { ReadOnly = ["relative/path"] }.Problem());
        // A game folder that holds the data folder is fine where the sandbox can hide it inside (all three can).
        Assert.Null(new SandboxGrants { ReadWrite = [_root], Hidden = [data] }.Problem());
        Assert.NotNull(new SandboxGrants { ReadWrite = [_root], Hidden = [data] }.Problem(canHideInside: false));
    }

    [Fact]
    public void JavaHome_IsTheFolderAboveBin()
    {
        var java = FileAt("", "jdk", "bin", "java");
        Assert.Equal(Path.Combine(_root, "jdk"), GameSandbox.JavaHome(java));
    }

    [Fact]
    public void Environment_KeepsDisplayAndJava_DropsSecrets()
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = "x", ["DISPLAY"] = ":0", ["LC_ALL"] = "C", ["MESA_GL_VERSION_OVERRIDE"] = "4.5", ["APPDATA"] = "x",
            ["AMBER_CF_KEY"] = "secret", ["GITHUB_TOKEN"] = "secret", ["HTTPS_PROXY"] = "http://user:pw@proxy", ["SSH_AUTH_SOCK"] = "/run/ssh",
            ["AMBER_DATA_DIR"] = "/data",
        };
        GameSandbox.FilterEnvironment(env);
        Assert.Equal(["APPDATA", "DISPLAY", "LC_ALL", "MESA_GL_VERSION_OVERRIDE", "PATH"], env.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SandboxIsOffUnlessJayTurnsItOn()
    {
        Assert.False(new LaunchOptions { VersionId = "x", JavaPath = "java", Session = null! }.Sandbox.Enabled);
        Assert.True(GameSandbox.IsSupported == (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()));
    }

    // ---------------------------------------------------------------- Linux (bubblewrap)

    private static LinuxSandbox.Host LinuxHost(params string[] existing) => new()
    {
        Home = "/home/p",
        RuntimeDir = "/run/user/1000",
        Display = ":0",
        WaylandDisplay = "wayland-0",
        Exists = p => existing.Contains(p),
        LinkTarget = p => p switch
        {
            "/bin" => "/usr/bin",
            "/lib" => "/usr/lib",
            "/etc/resolv.conf" => "/run/systemd/resolve/stub-resolv.conf",
            _ => null,
        },
        GpuDevices = () => ["/dev/nvidia0"],
    };

    [Fact]
    public void Linux_HomeIsEmpty_GrantsMountedOnTop_DataHidden()
    {
        if (!OperatingSystem.IsLinux()) return;
        var grants = new SandboxGrants
        {
            ReadWrite = ["/home/p/.minecraft/versions/1.21"],
            ReadOnly = ["/home/p/.minecraft/libraries", "/home/p/.local/share/Amber/notrace/x/log4j2.xml"],
            Hidden = ["/home/p/.local/share/Amber"],
        };
        var host = LinuxHost("/usr", "/etc", "/dev/dri", "/sys/devices", "/home/p/.minecraft/versions/1.21", "/home/p/.minecraft/libraries",
            "/home/p/.local/share/Amber/notrace/x/log4j2.xml", "/tmp/.X11-unix/X0", "/run/user/1000/wayland-0", "/run/user/1000/pulse/native",
            "/run/systemd/resolve/stub-resolv.conf");
        var args = LinuxSandbox.Arguments(grants, host, "/home/p/.minecraft/versions/1.21", ["/usr/bin/java", "-version"]);
        var text = string.Join(' ', args);

        Assert.StartsWith("--die-with-parent --new-session --unshare-all --share-net", text);
        Assert.Contains("--symlink usr/bin /bin", text);
        Assert.Contains("--ro-bind /usr /usr", text);
        Assert.Contains("--dev-bind /dev/dri /dev/dri", text);
        Assert.Contains("--dev-bind /dev/nvidia0 /dev/nvidia0", text);
        Assert.Contains("--ro-bind /run/systemd/resolve/stub-resolv.conf /run/systemd/resolve/stub-resolv.conf", text);
        Assert.Contains("--ro-bind /tmp/.X11-unix/X0 /tmp/.X11-unix/X0", text);
        Assert.Contains("--ro-bind /run/user/1000/wayland-0 /run/user/1000/wayland-0", text);
        Assert.Contains("--ro-bind /run/user/1000/pulse/native /run/user/1000/pulse/native", text);
        // The home tmpfs comes before the grants inside it; the data folder itself is never bound.
        Assert.True(args.IndexOf("/home/p") < args.IndexOf("/home/p/.minecraft/versions/1.21"));
        Assert.Contains("--bind /home/p/.minecraft/versions/1.21 /home/p/.minecraft/versions/1.21", text);
        Assert.Contains("--ro-bind /home/p/.local/share/Amber/notrace/x/log4j2.xml /home/p/.local/share/Amber/notrace/x/log4j2.xml", text);
        Assert.DoesNotContain("--bind /home/p/.local/share/Amber ", text);
        Assert.DoesNotContain("--ro-bind /home/p/.local/share/Amber ", text);
        Assert.EndsWith("--chdir /home/p/.minecraft/versions/1.21 -- /usr/bin/java -version", text);
    }

    [Fact]
    public void Linux_DataFolderInsideAGrant_IsCoveredByAnEmptyTmpfs()
    {
        if (!OperatingSystem.IsLinux()) return;
        var grants = new SandboxGrants { ReadWrite = ["/games/mc"], Hidden = ["/games/mc/jay-data"] };
        var args = LinuxSandbox.Arguments(grants, LinuxHost("/games/mc"), "/games/mc", ["java"]);
        var bind = args.IndexOf("/games/mc");
        var hide = args.LastIndexOf("/games/mc/jay-data");
        Assert.True(bind > 0 && hide > bind);
        Assert.Equal("--tmpfs", args[hide - 1]);
    }

    [Theory]
    [InlineData(":0", "0")]
    [InlineData("unix:1.0", "1")]
    [InlineData("localhost:10.0", null)]
    [InlineData("wayland", null)]
    public void Linux_X11DisplayNumber(string display, string? number) => Assert.Equal(number, LinuxSandbox.X11Number(display));

    // ---------------------------------------------------------------- macOS (sandbox-exec)

    [Fact]
    public void Mac_ProfileDeniesDataKeychainAndBrowsers_ThenAllowsHandedOverFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        var grants = new SandboxGrants
        {
            ReadWrite = ["/Users/p/Library/Application Support/minecraft/versions/1.21"],
            ReadOnly = ["/Users/p/Library/Application Support/Amber/authlib-injector/a.jar"],
            Hidden = ["/Users/p/Library/Application Support/Amber"],
        };
        var profile = MacSandbox.Profile(grants, "/Users/p");
        Assert.StartsWith("(version 1)", profile);
        Assert.Contains("(allow default)", profile);
        Assert.Contains("(deny file-read* file-write* (subpath \"/Users/p/Library/Application Support/Amber\"))", profile);
        Assert.Contains("(deny file-read* file-write* (subpath \"/Users/p/Library/Keychains\"))", profile);
        Assert.Contains("(deny file-read* file-write* (subpath \"/Users/p/Library/Application Support/Google/Chrome\"))", profile);
        Assert.Contains("com.apple.SecurityServer", profile);
        // The jar inside the data folder comes after the deny (last match wins); the instance is not inside a denied folder.
        var allow = profile.IndexOf("/Users/p/Library/Application Support/Amber/authlib-injector/a.jar", StringComparison.Ordinal);
        Assert.True(allow > profile.IndexOf("(subpath \"/Users/p/Library/Application Support/Amber\")", StringComparison.Ordinal));
        Assert.DoesNotContain("minecraft/versions", profile);
        Assert.Equal(["-p", profile, "java", "-version"], MacSandbox.Arguments(profile, ["java", "-version"]));
    }

    [Fact]
    public void Mac_QuoteEscapesQuotesAndBackslashes() =>
        Assert.Equal("\"/a \\\"b\\\" \\\\c\"", MacSandbox.Quote("/a \"b\" \\c"));

    // ---------------------------------------------------------------- Windows

    [Theory]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData("")]
    [InlineData("quote\"inside")]
    [InlineData("trailing\\")]
    [InlineData("C:\\Program Files\\Java\\")]
    [InlineData("back\\\\\"slash")]
    [InlineData("-Dx=a b\\\\")]
    public void Windows_CommandLineQuoting_RoundTrips(string arg)
    {
        if (!OperatingSystem.IsWindows()) return;
        var line = "java.exe " + CommandLine.Quote(arg);
        var argv = CommandLineToArgvW(line, out var count);
        try
        {
            Assert.Equal(2, count);
            Assert.Equal(arg, Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, IntPtr.Size)));
        }
        finally { LocalFree(argv); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint CommandLineToArgvW(string line, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    [Fact]
    public void Windows_Labels()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("S:(ML;OICI;NW;;;LW)", WindowsSandbox.Label(folder: true, writable: true, hidden: false));
        Assert.Equal("S:(ML;OICI;NWNRNX;;;ME)", WindowsSandbox.Label(folder: true, writable: false, hidden: true));
        Assert.Equal("S:(ML;;NW;;;ME)", WindowsSandbox.Label(folder: false, writable: false, hidden: false));
        // Windows writes labels back with flags and in its own letter order.
        Assert.True(WindowsSandbox.Same("S:AI(ML;OICI;NRNWNX;;;ME)", "S:(ML;OICI;NWNRNX;;;ME)"));
        Assert.True(WindowsSandbox.Same("S:(ML;OICI;NW;;;LW)(ML;OICIID;NW;;;ME)", "S:(ML;OICI;NW;;;LW)"));
        Assert.False(WindowsSandbox.Same("S:AI(ML;OICIID;NW;;;LW)", "S:(ML;OICI;NW;;;LW)"));
        Assert.False(WindowsSandbox.Same("", "S:(ML;OICI;NW;;;LW)"));
    }

    [Fact]
    public void Windows_EnvironmentBlock_IsSortedAndDoubleNullTerminated()
    {
        if (!OperatingSystem.IsWindows()) return;
        var block = WindowsSandbox.EnvironmentBlock(new Dictionary<string, string?> { ["b"] = "2", ["A"] = "1", ["gone"] = null });
        Assert.Equal("A=1\0b=2\0\0", block);
        Assert.Equal("\0\0", WindowsSandbox.EnvironmentBlock(new Dictionary<string, string?>()));
    }

    [Fact]
    public void Windows_DnsArguments_PerJavaVersion()
    {
        if (!OperatingSystem.IsWindows()) return;
        var java8 = Dir("jre8");
        FileAt("", "jre8", "lib", "ext", "dnsns.jar");
        string[] servers = ["192.168.1.1", "fe80::1%12"];

        var eight = WindowsSandbox.DnsArguments(8, java8, servers, null);
        Assert.NotNull(eight);
        // The system lookup stays first; the DNS servers are asked only when it fails.
        Assert.Equal("-Dsun.net.spi.nameservice.provider.1=default", eight[0]);
        Assert.Equal("-Dsun.net.spi.nameservice.provider.2=dns,sun", eight[1]);
        Assert.Equal("-Dsun.net.spi.nameservice.nameservers=192.168.1.1,fe80::1%12", eight[2]);
        Assert.Null(WindowsSandbox.DnsArguments(8, Dir("jre8-without-dnsns"), servers, null));
        Assert.Null(WindowsSandbox.DnsArguments(17, java8, servers, "C:\\x\\jay-dns.jar"));
        Assert.Equal(["-Xbootclasspath/a:C:\\x\\jay-dns.jar", "-Djay.dns.servers=192.168.1.1,fe80::1%12"],
            WindowsSandbox.DnsArguments(21, java8, servers, "C:\\x\\jay-dns.jar"));
        Assert.Null(WindowsSandbox.DnsArguments(21, java8, servers, null));
        Assert.Null(WindowsSandbox.DnsArguments(21, java8, [], "C:\\x\\jay-dns.jar"));
    }

    [Fact]
    public void Windows_ResolverJarIsEmbedded_AndWrittenOncePerContent()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The build compiles tools/jay-dns with a local JDK; a machine without one skips this (the build warned).
        if (!WindowsSandbox.HasResolver) return;
        var folder = Path.Combine(_root, "libraries", "jay", "jay-dns");
        var first = WindowsSandbox.ResolverJar(folder);
        Assert.NotNull(first);
        Assert.Equal(first, WindowsSandbox.ResolverJar(folder));
        using var zip = System.IO.Compression.ZipFile.OpenRead(first);
        Assert.Contains(zip.Entries, e => e.FullName == "META-INF/services/java.net.spi.InetAddressResolverProvider");
        Assert.Contains(zip.Entries, e => e.FullName == "jay/dns/JayResolverProvider.class");
    }

    [Fact]
    public void Windows_ProcessAdoption_PrivateMembersStillExist()
    {
        // WindowsSandbox.Start wraps the process it creates in a System.Diagnostics.Process through these. A .NET
        // update that renames them must fail here, not at a player's launch.
        Assert.NotNull(WindowsSandbox.SetProcessHandle);
        Assert.NotNull(WindowsSandbox.SetProcessId);
        Assert.NotNull(WindowsSandbox.StandardOutputField);
        Assert.NotNull(WindowsSandbox.StandardErrorField);
    }

    // ---------------------------------------------------------------- the Windows level for a launch

    [Fact]
    public void Policy_StrongByDefault_AsksOnceThenRemembersFallbacks()
    {
        // Strong wanted, exemption not there yet, an admin with UAC: explain and ask.
        var first = WindowsSandboxPolicy.Decide(true, null, null, javaCanUseStrong: true, 21, exempt: false, AdminPrompt.Prompt);
        Assert.Equal(WindowsSandboxLevel.Strong, first.Level);
        Assert.True(first.AskFirst);

        // Exemption present: strong, no question.
        var ready = WindowsSandboxPolicy.Decide(true, null, null, true, 21, exempt: true, AdminPrompt.Prompt);
        Assert.Equal(new WindowsSandboxDecision(WindowsSandboxLevel.Strong), ready);

        // Declined before: standard, with the reason, and no question.
        var declined = WindowsSandboxPolicy.Decide(true, WindowsSandboxPolicy.Refused, null, true, 21, false, AdminPrompt.Prompt);
        Assert.Equal(WindowsSandboxLevel.Standard, declined.Level);
        Assert.False(declined.AskFirst);
        Assert.NotNull(declined.Reason);

        // No admin rights: standard, remembered, never a prompt someone else would have to answer.
        var noAdmin = WindowsSandboxPolicy.Decide(true, null, null, true, 21, false, AdminPrompt.NoAdmin);
        Assert.Equal(WindowsSandboxLevel.Standard, noAdmin.Level);
        Assert.Equal(WindowsSandboxPolicy.NoAdmin, noAdmin.Fallback);
        Assert.False(noAdmin.AskFirst);

        // Java 17: standard for this launch, nothing remembered.
        var java17 = WindowsSandboxPolicy.Decide(true, null, null, javaCanUseStrong: false, 17, false, AdminPrompt.Prompt);
        Assert.Equal(WindowsSandboxLevel.Standard, java17.Level);
        Assert.Null(java17.Fallback);
        Assert.Contains("17", java17.Reason);

        // Strong mode switched off: standard, nothing to explain.
        Assert.Equal(new WindowsSandboxDecision(WindowsSandboxLevel.Standard), WindowsSandboxPolicy.Decide(false, null, null, true, 21, true, AdminPrompt.Elevated));
    }

    [Fact]
    public void Policy_JavaVersionsThatCanResolveInsideTheContainer()
    {
        var java8 = Dir("j8");
        FileAt("", "j8", "lib", "ext", "dnsns.jar");
        Assert.True(WindowsSandboxPolicy.JavaCanUseStrong(8, java8, hasResolver: false));
        Assert.False(WindowsSandboxPolicy.JavaCanUseStrong(8, Dir("j8-bare"), hasResolver: true));
        Assert.False(WindowsSandboxPolicy.JavaCanUseStrong(11, java8, hasResolver: true));
        Assert.False(WindowsSandboxPolicy.JavaCanUseStrong(17, java8, hasResolver: true));
        Assert.True(WindowsSandboxPolicy.JavaCanUseStrong(21, java8, hasResolver: true));
        Assert.False(WindowsSandboxPolicy.JavaCanUseStrong(21, java8, hasResolver: false));
        Assert.False(WindowsSandboxPolicy.JavaCanUseStrong(0, java8, hasResolver: true));
    }

    [Fact]
    public void JavaMajor_FromReleaseFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var java = FileAt("", "jdk17", "bin", "java.exe");
        FileAt("JAVA_VERSION=\"17.0.10\"\nOS_ARCH=\"amd64\"\n", "jdk17", "release");
        Assert.Equal(17, WindowsSandbox.JavaMajor(java));
        var old = FileAt("", "jre8", "bin", "java.exe");
        FileAt("JAVA_VERSION=\"1.8.0_401\"\n", "jre8", "release");
        Assert.Equal(8, WindowsSandbox.JavaMajor(old));
        Assert.Equal(0, WindowsSandbox.JavaMajor(FileAt("", "nothing", "bin", "java.exe")));
    }
}
