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

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0.3.0", false)]
    [InlineData("0.3.1", false)]
    [InlineData("0.3.2", true)]
    [InlineData("0.3.2-dev", true)]
    [InlineData("0.4", true)]
    [InlineData("1.0.0+abc", true)]
    [InlineData("dev", false)]
    public void Bake_SharedCacheIsReadOnly_OnlyForLoadersThatWriteOnlyTheInstance(string? version, bool readOnly)
    {
        Assert.Equal(readOnly, Amber.Core.Bake.BakeRuntime.LaunchWritesOnlyInstance(version));
        var runtime = new Amber.Core.Bake.BakeRuntime(Path.Combine(_root, "rt"));
        var grants = runtime.SandboxGrantsFor(version);
        Assert.Equal(!readOnly, grants.Single(g => g.Path == runtime.CacheRoot).Writable);
        Assert.DoesNotContain(grants, g => g.Writable && g.Path != runtime.CacheRoot);
        // Without a version (0.3.1, the pin) nothing changes.
        Assert.Equal(runtime.SandboxGrants, runtime.SandboxGrantsFor("0.3.1"));
    }

    [Fact]
    public void NativeExtract_GoesIntoTheInstance_AndOnlyForAsciiFolders()
    {
        var instance = Dir("versions", "1.21.1");
        var args = GameSandbox.NativeExtractArguments(instance);
        Assert.Equal(
            ["-Dorg.lwjgl.system.SharedLibraryExtractPath=" + Path.Combine(instance, ".jay-natives", "lwjgl"), "-Djna.tmpdir=" + Path.Combine(instance, ".jay-natives", "jna")],
            args);
        Assert.True(Directory.Exists(Path.Combine(instance, ".jay-natives", "jna")));
        Assert.All(args, a => Assert.True(GameSandbox.IsInside(a[(a.IndexOf('=') + 1)..], instance)));
        Assert.Empty(GameSandbox.NativeExtractArguments(Dir("实例")));
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

    [Fact]
    public void Windows_SocketFolder_InsideTheWritableFolder_OnlyWhenShortAndAscii()
    {
        if (!OperatingSystem.IsWindows()) return;
        var game = Dir("g");
        var folder = WindowsSandbox.SocketFolder(game);
        if (Path.Combine(game, ".jay-sock").Length <= WindowsSandbox.MaxSocketFolderLength)
        {
            Assert.Equal(Path.Combine(game, ".jay-sock"), folder);
            Assert.True(Directory.Exists(folder));
        }
        Assert.Null(WindowsSandbox.SocketFolder(Dir(new string('x', 80))));
        Assert.Null(WindowsSandbox.SocketFolder(Path.Combine(Path.GetPathRoot(game)!, "游戏")));
    }

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
    public void Windows_FileSystemJar_HoldsOnlyTheProvider_AndArgumentsFitTheJava()
    {
        if (!OperatingSystem.IsWindows() || !WindowsSandbox.HasFileSystemJar) return;
        var folder = Path.Combine(_root, "libraries", "jay", "jay-fs");
        var jar = WindowsSandbox.FileSystemJar(folder);
        Assert.NotNull(jar);
        Assert.Equal(jar, WindowsSandbox.FileSystemJar(folder));
        using (var zip = System.IO.Compression.ZipFile.OpenRead(jar))
            // The compile-time stand-in for the JDK class must never ship: it would replace the real one.
            Assert.Equal(["sun/nio/fs/JaySandboxFileSystemProvider.class"], zip.Entries.Select(e => e.FullName).Where(n => !n.EndsWith('/')));
        Assert.Equal(["-Xbootclasspath/a:" + jar, "-Djava.nio.file.spi.DefaultFileSystemProvider=" + WindowsSandbox.FileSystemProvider],
            WindowsSandbox.FileSystemArguments(8, jar));
        Assert.Equal(["--patch-module=java.base=" + jar, "-Djava.nio.file.spi.DefaultFileSystemProvider=" + WindowsSandbox.FileSystemProvider],
            WindowsSandbox.FileSystemArguments(21, jar));
    }

    [Fact]
    public void Windows_StandardLevel_TakesTheContainersRightsOffAgain()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Dir("granted");
        File.WriteAllText(Path.Combine(folder, "f.txt"), "x");
        // Any app container SID will do; this one is never created.
        var sid = new System.Security.Principal.SecurityIdentifier("S-1-15-2-1-2-3-4-5-6-7");
        bool Named(string path) => new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .OfType<System.Security.AccessControl.FileSystemAccessRule>().Any(r => r.IdentityReference == sid);
        WindowsSandbox.Grant(folder, sid, write: true);
        Assert.True(Named(Path.Combine(folder, "f.txt")));
        WindowsSandbox.Ungrant(folder, sid);
        Assert.False(Named(Path.Combine(folder, "f.txt")));
        WindowsSandbox.Ungrant(folder, sid); // nothing left to take off
    }

    // ---------------------------------------------------------------- the strong sandbox's admin step

    private static System.Security.AccessControl.DirectorySecurity Acl(string folder) => new DirectoryInfo(folder).GetAccessControl();

    private static string Sddl(string folder) => Acl(folder).GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);

    private static List<System.Security.AccessControl.FileSystemAccessRule> PackageRules(string folder) =>
    [
        .. Acl(folder).GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .OfType<System.Security.AccessControl.FileSystemAccessRule>().Where(r => r.IdentityReference == WindowsSandboxSetup.AllPackages),
    ];

    [Fact]
    public void Windows_SetupStep_OpensOnlyThatFolder_KeepsTheAclCanonical_AndUndoesExactly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parent = Dir("parent");
        var child = Dir("parent", "child");
        File.WriteAllText(Path.Combine(parent, "name.txt"), "x");
        var before = Sddl(parent);
        var childBefore = Sddl(child);

        // Run as the elevated helper would (the folder is ours, so no admin is needed here); loopback left alone.
        Assert.Equal(0, WindowsSandboxSetup.Run([WindowsSandboxSetup.SetupArgument, "--no-loopback", parent]));
        Assert.True(Acl(parent).AreAccessRulesCanonical);
        var ours = Assert.Single(PackageRules(parent));
        // Names only (list, traverse, attributes), this folder only: nothing inherited below, no contents.
        Assert.Equal(System.Security.AccessControl.InheritanceFlags.None, ours.InheritanceFlags);
        Assert.Equal(WindowsSandboxSetup.ListMask, (int)ours.FileSystemRights);
        Assert.True(WindowsSandboxSetup.ContainersCanList(parent));
        Assert.Equal(childBefore, Sddl(child));
        // Still inheriting from above (the auto-inherit mark survived the rewrite).
        Assert.False(Acl(parent).AreAccessRulesProtected);
        Assert.Contains("AI", Sddl(parent));

        // Twice is still one entry.
        WindowsSandboxSetup.AddListing(parent);
        Assert.Single(PackageRules(parent));

        // A low-integrity process (the standard level) can still list it: an entry naming one specific container would stop that.
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in (string[])["/c", "dir", "/b", parent]) psi.ArgumentList.Add(arg);
        using (var process = WindowsSandbox.Start(psi, WindowsSandboxLevel.Standard))
        {
            var output = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(30_000));
            Assert.Contains("name.txt", output);
        }

        Assert.Equal(0, WindowsSandboxSetup.Run([WindowsSandboxSetup.UndoArgument, "--no-loopback", parent]));
        Assert.Equal(before, Sddl(parent));
        Assert.True(Acl(parent).AreAccessRulesCanonical);
        WindowsSandboxSetup.RemoveListing(parent); // nothing left to take off
        Assert.Equal(before, Sddl(parent));
    }

    [Fact]
    public void Windows_SetupStep_UndoLeavesOtherEntriesForTheSameGroup()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Dir("shared");
        // Someone else's entry for the same group (read and execute, inherited below): undo must not take it.
        var acl = Acl(folder);
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(WindowsSandboxSetup.AllPackages,
            System.Security.AccessControl.FileSystemRights.ReadAndExecute,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(acl);
        var before = Sddl(folder);
        WindowsSandboxSetup.AddListing(folder);
        Assert.Equal(2, PackageRules(folder).Count);
        WindowsSandboxSetup.RemoveListing(folder);
        Assert.Equal(before, Sddl(folder));
    }

    [Fact]
    public void Windows_SetupStep_RefusesCommandLinesJayWouldNotWrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Dir("ok");
        Assert.False(WindowsSandboxSetup.IsInvocation([]));
        Assert.False(WindowsSandboxSetup.IsInvocation(["--theme-dev", folder]));
        Assert.True(WindowsSandboxSetup.IsInvocation([WindowsSandboxSetup.UndoArgument]));
        Assert.Equal(2, WindowsSandboxSetup.Run([WindowsSandboxSetup.SetupArgument]));
        Assert.Equal(2, WindowsSandboxSetup.Run([WindowsSandboxSetup.SetupArgument, folder]));
        Assert.Equal(2, WindowsSandboxSetup.Run([WindowsSandboxSetup.SetupArgument, "--no-loopback", "relative\\folder"]));
        Assert.Equal(2, WindowsSandboxSetup.Run([WindowsSandboxSetup.SetupArgument, "--no-loopback", Path.Combine(_root, "missing")]));
        Assert.Empty(PackageRules(folder));
    }

    [Fact]
    public void Windows_SetupStep_CommandRunsJayElevatedWithTheFoldersQuoted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var psi = WindowsSandboxSetup.Command(@"C:\Jay\Jay.exe", undo: false, loopback: true, [@"D:\", @"D:\My Games\"]);
        Assert.Equal("runas", psi.Verb);
        Assert.True(psi.UseShellExecute);
        Assert.Equal(@"C:\Jay\Jay.exe", psi.FileName);
        Assert.Equal(@"--jay-sandbox-setup --loopback D:\ ""D:\My Games\\""", psi.Arguments);
        Assert.Equal(["--jay-sandbox-undo", "--no-loopback"], WindowsSandboxSetup.Arguments(undo: true, loopback: false, []));
    }

    [Fact]
    public void Windows_FoldersToOpen_AreTheClosedParentsOutsideTheGrants()
    {
        if (!OperatingSystem.IsWindows()) return;
        var game = Dir("games", "mc");
        var java = Dir("java", "jdk");
        var games = Path.Combine(_root, "games");
        var lists = WindowsSandboxSetup.FoldersToOpen([game, Path.Combine(game, "versions"), java, null, "relative"]);
        // Inside a grant: never (the launch grants it to the container itself).
        Assert.DoesNotContain(lists, f => GameSandbox.IsInside(f, game));
        Assert.Contains(games, lists);
        Assert.Contains(Path.Combine(_root, "java"), lists);
        Assert.Contains(_root, lists);
        Assert.Equal(lists.Count, lists.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        WindowsSandboxSetup.AddListing(games);
        Assert.DoesNotContain(games, WindowsSandboxSetup.FoldersToOpen([game]));
        WindowsSandboxSetup.RemoveListing(games);
        Assert.Contains(games, WindowsSandboxSetup.FoldersToOpen([game]));
        Assert.Equal(Path.GetPathRoot(game), WindowsSandboxSetup.Parents(game).Last());
    }

    /// <summary>A JDK for the real-Java tests: JAVA_HOME, else an Adoptium install; null skips them.</summary>
    private static string? TestJava()
    {
        var homes = new List<string?> { Environment.GetEnvironmentVariable("JAVA_HOME"), Environment.GetEnvironmentVariable("JAVA_HOME_21_X64") };
        var adoptium = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium");
        if (Directory.Exists(adoptium)) homes.AddRange(Directory.GetDirectories(adoptium, "jdk-*").OrderDescending());
        return homes.Where(h => !string.IsNullOrEmpty(h)).Select(h => Path.Combine(h!, "bin", "java.exe"))
            .FirstOrDefault(j => File.Exists(j) && WindowsSandbox.JavaMajor(j) >= 11);
    }

    [Fact]
    public void Windows_StandardLevel_JavaSeesItsWritableFilesAsWritable_WithJaysProvider()
    {
        // Java's Files.isWritable ignores the integrity label, so inside the sandbox it called every file read-only and
        // Fabric could not write its remapped game jar (zip file systems open read-only then).
        if (!OperatingSystem.IsWindows() || !WindowsSandbox.HasFileSystemJar || TestJava() is not { } java) return;
        var instance = Dir("instance");
        var jar = WindowsSandbox.FileSystemJar(Dir("libraries"))!;
        File.WriteAllText(Path.Combine(instance, "Probe.java"), """
            import java.nio.file.*;
            import java.util.Map;
            public class Probe {
                public static void main(String[] a) throws Exception {
                    Path jar = Path.of("probe.jar");
                    try (FileSystem zip = FileSystems.newFileSystem(jar, Map.of("create", "true"))) {
                        Files.writeString(zip.getPath("x.txt"), "x");
                    }
                    System.out.println("writable=" + Files.isWritable(jar) + " dir=" + Files.isWritable(Path.of(".")));
                }
            }
            """);
        WindowsSandbox.Prepare(new SandboxGrants { ReadWrite = [instance], ReadOnly = [Path.GetDirectoryName(jar)!] }, WindowsSandboxLevel.Standard);
        var psi = new ProcessStartInfo(java)
        {
            WorkingDirectory = instance, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in WindowsSandbox.FileSystemArguments(WindowsSandbox.JavaMajor(java), jar)) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("Probe.java");
        using var process = WindowsSandbox.Start(psi, WindowsSandboxLevel.Standard);
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));
        Assert.Contains("writable=true dir=true", output);
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
