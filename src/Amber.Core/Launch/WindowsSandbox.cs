using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Amber.Core.Localization;
using Microsoft.Win32.SafeHandles;

namespace Amber.Core.Launch;

/// <summary>How strongly the game is sandboxed on Windows.</summary>
public enum WindowsSandboxLevel
{
    /// <summary>
    /// Low integrity, no admin needed. Jay's data folder and the files it hands over are labelled so a low-integrity
    /// process cannot read them; the instance folder, cache and temp folder are labelled so it can write there. A
    /// low-integrity process cannot open Jay's (medium) process either. Network and loopback work as before.
    /// </summary>
    Standard,

    /// <summary>
    /// AppContainer: the game sees only the folders granted to Jay's game container (and system files every app may
    /// read): no Jay data, no browser profiles, nothing else of the player's. Loopback to Jay needs a one-time
    /// exemption (one admin prompt), and Java's name lookups go around the DNS client service (<see cref="WindowsSandbox.DnsArguments"/>).
    /// </summary>
    Strong,
}

/// <summary>
/// The Windows game sandbox (privacy plan item 15). <see cref="Start"/> creates the Java process itself (low-integrity
/// token or AppContainer) and hands back an ordinary <see cref="Process"/>: exit code, exit event, output lines and
/// <see cref="Process.Kill()"/> work as for <see cref="Process.Start(ProcessStartInfo)"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsSandbox
{
    /// <summary>Jay's game container. One per Windows user; every sandboxed game runs in it.</summary>
    public const string ContainerName = "Jay.Game";

    private const string ContainerDisplayName = "Jay game";
    private const string ContainerDescription = "Minecraft started by the Jay launcher";

    /// <summary>internetClient, internetClientServer, privateNetworkClientServer: the game talks to servers, hosts LAN worlds and reaches the LAN.</summary>
    internal static readonly string[] CapabilitySids = ["S-1-15-3-1", "S-1-15-3-2", "S-1-15-3-3"];

    // ---------------------------------------------------------------- labels and grants

    /// <summary>
    /// The mandatory label (SDDL) Jay puts on a path. Folders inherit it to everything inside. An explicit label comes
    /// before one inherited from a parent and is the one Windows checks, so a hidden folder inside a granted one (or a
    /// handed-over file inside a hidden one) keeps its own. (Protecting the SACL instead would need the audit privilege.)
    /// <list type="bullet">
    /// <item>writable: Low, so a low-integrity game may write (it can always read);</item>
    /// <item>hidden: Medium with no-read-up, so a low-integrity game cannot read or run it;</item>
    /// <item>otherwise: Medium, readable (a file Jay hands over from inside a hidden folder).</item>
    /// </list>
    /// </summary>
    internal static string Label(bool folder, bool writable, bool hidden)
    {
        var inherit = folder ? "OICI" : "";
        var policy = hidden ? "NWNRNX" : "NW";
        var level = writable && !hidden ? "LW" : "ME";
        return $"S:(ML;{inherit};{policy};;;{level})";
    }

    /// <summary>
    /// Labels the grants and hidden folders (and, for the strong level, adds the container's access to the grants).
    /// Labels stay on disk: they change nothing for Jay or other normal programs, and the next launch finds them in place.
    /// Throws <see cref="SandboxUnavailableException"/> when a folder that must be hidden cannot be labelled.
    /// </summary>
    public static void Prepare(SandboxGrants grants, WindowsSandboxLevel level)
    {
        grants = grants.Normalized();
        var container = level == WindowsSandboxLevel.Strong ? ContainerSid() : null;
        // Grants first, hidden folders after: a hidden folder inside a granted one keeps its own label.
        foreach (var path in grants.ReadWrite)
        {
            if (!Exists(path)) Directory.CreateDirectory(path);
            SetLabel(path, writable: true, hidden: false, mustWork: false);
            if (container is not null) Grant(path, container, write: true);
        }
        foreach (var path in grants.ReadOnly.Where(Exists))
        {
            if (grants.Hidden.Any(h => GameSandbox.IsInside(path, h))) SetLabel(path, writable: false, hidden: false, mustWork: true);
            if (container is not null) Grant(path, container, write: false);
        }
        foreach (var path in grants.Hidden.Where(Exists))
        {
            SetLabel(path, writable: false, hidden: true, mustWork: true);
            // A grant inside a hidden folder (the no-trace log config, the authlib-injector jar) is relabelled after it.
            foreach (var inside in grants.ReadOnly.Where(p => GameSandbox.IsInside(p, path) && !GameSandbox.SamePath(p, path) && Exists(p)))
                SetLabel(inside, writable: false, hidden: false, mustWork: true);
            foreach (var inside in grants.ReadWrite.Where(p => GameSandbox.IsInside(p, path) && !GameSandbox.SamePath(p, path) && Exists(p)))
                SetLabel(inside, writable: true, hidden: false, mustWork: true);
        }
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    /// <summary>The label SDDL now on a path, for diagnostics.</summary>
    public static string? CurrentLabelPublic(string path) => CurrentLabel(path);

    /// <summary>The label SDDL now on a path ("" when it has none), or null when the volume keeps no labels.</summary>
    internal static string? CurrentLabel(string path)
    {
        var error = GetNamedSecurityInfoW(path, SE_FILE_OBJECT, LABEL_SECURITY_INFORMATION, null, null, null, null, out var sd);
        if (error != 0) return null;
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, LABEL_SECURITY_INFORMATION, out var text, out _)) return null;
            try { return Marshal.PtrToStringUni(text) ?? ""; }
            finally { LocalFree(text); }
        }
        finally { LocalFree(sd); }
    }

    private static void SetLabel(string path, bool writable, bool hidden, bool mustWork)
    {
        var sddl = Label(Directory.Exists(path), writable, hidden);
        var current = CurrentLabel(path);
        if (current is not null && Same(current, sddl)) return;
        var error = ApplyLabel(path, sddl);
        // Setting a label needs the take-ownership right. Folders the player made outside the profile (D:\Games) often
        // give only "Modify"; as the owner Jay may add that right for the player's own account, then label again.
        if (error == ERROR_ACCESS_DENIED && AllowTakeOwnership(path)) error = ApplyLabel(path, sddl);
        if (error == 0) return;
        // FAT, exFAT and some network drives keep no labels: nothing to do for a writable grant (anyone may write there),
        // but a folder that must stay hidden cannot be protected there.
        if (!mustWork && !hidden) return;
        throw new SandboxUnavailableException(L.F("core.launch.sandbox.win.label_failed", path, new Win32Exception(error).Message));
    }

    /// <summary>Label SDDL as Windows writes it back: the first (explicit) ACE, maybe with policy letters reordered.</summary>
    internal static bool Same(string current, string wanted) =>
        string.Equals(Normalize(current), Normalize(wanted), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string sddl)
    {
        var start = sddl.IndexOf('(');
        if (start < 0) return sddl;
        var end = sddl.IndexOf(')', start);
        var ace = sddl[(start + 1)..(end < 0 ? sddl.Length : end)].Split(';');
        if (ace.Length < 6) return sddl;
        // Policy letters can come back in another order (NWNRNX / NRNWNX).
        var policy = string.Concat(Enumerable.Range(0, ace[2].Length / 2).Select(i => ace[2].Substring(i * 2, 2)).Order());
        return $"{ace[0]}|{ace[1]}|{policy}|{ace[5]}";
    }

    private const int ERROR_ACCESS_DENIED = 5;

    /// <summary>Gives the current user the take-ownership right on a path they own (and, for a folder, everything in it).</summary>
    private static bool AllowTakeOwnership(string path)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not { } user) return false;
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                var acl = info.GetAccessControl(AccessControlSections.Access);
                acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.TakeOwnership, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(acl);
            }
            else
            {
                var info = new FileInfo(path);
                var acl = info.GetAccessControl(AccessControlSections.Access);
                acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.TakeOwnership, AccessControlType.Allow));
                info.SetAccessControl(acl);
            }
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or InvalidOperationException) { return false; }
    }

    private static int ApplyLabel(string path, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var sd, out _)) return Marshal.GetLastWin32Error();
        try
        {
            if (!GetSecurityDescriptorSacl(sd, out var present, out var sacl, out _) || !present) return Marshal.GetLastWin32Error();
            return SetNamedSecurityInfoW(path, SE_FILE_OBJECT, LABEL_SECURITY_INFORMATION, 0, 0, 0, sacl);
        }
        finally { LocalFree(sd); }
    }

    /// <summary>Gives the container read (or read-write) access to a folder or file, unless it already has it.</summary>
    internal static void Grant(string path, SecurityIdentifier sid, bool write)
    {
        var rights = write ? FileSystemRights.Modify | FileSystemRights.Synchronize : FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                var acl = info.GetAccessControl(AccessControlSections.Access);
                if (Has(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)), sid, rights)) return;
                acl.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(acl);
            }
            else
            {
                var info = new FileInfo(path);
                var acl = info.GetAccessControl(AccessControlSections.Access);
                if (Has(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)), sid, rights)) return;
                acl.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
                info.SetAccessControl(acl);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            throw new SandboxUnavailableException(L.F("core.launch.sandbox.win.grant_failed", path, e.Message), e);
        }
    }

    /// <summary>"ALL APPLICATION PACKAGES": system folders and Program Files already let every container read them.</summary>
    private static readonly SecurityIdentifier AllApplicationPackages = new("S-1-15-2-1");

    private static bool Has(AuthorizationRuleCollection rules, SecurityIdentifier sid, FileSystemRights rights) =>
        rules.OfType<FileSystemAccessRule>().Any(r => r.AccessControlType == AccessControlType.Allow
            && (r.IdentityReference == sid || r.IdentityReference == AllApplicationPackages) && (r.FileSystemRights & rights) == rights);

    // ---------------------------------------------------------------- the container

    /// <summary>Jay's game container SID, creating the container on first use (no admin needed).</summary>
    public static SecurityIdentifier ContainerSid() => ContainerSid(ContainerName);

    internal static SecurityIdentifier ContainerSid(string name)
    {
        var caps = Capabilities(out var buffers);
        try
        {
            var hr = CreateAppContainerProfile(name, ContainerDisplayName, ContainerDescription, caps, (uint)CapabilitySids.Length, out var sid);
            if (hr == HRESULT_ALREADY_EXISTS) hr = DeriveAppContainerSidFromAppContainerName(name, out sid);
            if (hr != 0) throw new SandboxUnavailableException(L.F("core.launch.sandbox.win.container_failed", new Win32Exception(hr).Message));
            try { return new SecurityIdentifier(sid); }
            finally { FreeSid(sid); }
        }
        finally
        {
            foreach (var b in buffers) LocalFree(b);
            Marshal.FreeHGlobal((nint)caps);
        }
    }

    /// <summary>Removes a container profile (tests only; Jay keeps its own).</summary>
    internal static void DeleteContainer(string name) => DeleteAppContainerProfile(name);

    private static SID_AND_ATTRIBUTES* Capabilities(out List<nint> buffers)
    {
        buffers = [];
        var caps = (SID_AND_ATTRIBUTES*)Marshal.AllocHGlobal(sizeof(SID_AND_ATTRIBUTES) * CapabilitySids.Length);
        for (var i = 0; i < CapabilitySids.Length; i++)
        {
            if (!ConvertStringSidToSidW(CapabilitySids[i], out var sid)) throw new Win32Exception();
            buffers.Add(sid);
            caps[i] = new SID_AND_ATTRIBUTES { Sid = sid, Attributes = SE_GROUP_ENABLED };
        }
        return caps;
    }

    /// <summary>The container's own folder (…\Packages\Jay.Game\AC); its Temp folder is where the game's temp files go.</summary>
    public static string ContainerFolder(SecurityIdentifier sid)
    {
        var text = sid.Value;
        if (!ConvertStringSidToSidW(text, out var raw)) throw new Win32Exception();
        try
        {
            var hr = GetAppContainerFolderPath(text, out var path);
            if (hr != 0) throw new SandboxUnavailableException(L.F("core.launch.sandbox.win.container_failed", new Win32Exception(hr).Message));
            try { return Marshal.PtrToStringUni(path)!; }
            finally { CoTaskMemFree(path); }
        }
        finally { LocalFree(raw); }
    }

    /// <summary>
    /// True when Jay's game container may use loopback (the exemption the one admin prompt adds). Reading the list needs
    /// no admin. False when the container does not exist yet.
    /// </summary>
    public static bool LoopbackExempt() => LoopbackExempt(ContainerName);

    internal static bool LoopbackExempt(string name)
    {
        if (DeriveAppContainerSidFromAppContainerName(name, out var raw) != 0) return false;
        string sid;
        try { sid = new SecurityIdentifier(raw).Value; }
        finally { FreeSid(raw); }
        // The list Windows returns is allocated by the firewall API; it is small and read rarely, so it is not freed.
        if (NetworkIsolationGetAppContainerConfig(out var count, out var list) != 0 || list is null) return false;
        for (var i = 0; i < count; i++)
            if (new SecurityIdentifier(list[i].Sid) == new SecurityIdentifier(sid)) return true;
        return false;
    }

    /// <summary>
    /// The command that adds the loopback exemption: CheckNetIsolation, run elevated. Jay starts it only after the player
    /// agreed; Windows then shows its admin prompt. Jay itself never asks for admin otherwise.
    /// </summary>
    public static ProcessStartInfo LoopbackExemptCommand() => new(Path.Combine(Environment.SystemDirectory, "CheckNetIsolation.exe"),
        $"LoopbackExempt -a -p={ContainerSid().Value}")
    {
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Hidden,
    };

    /// <summary>What the admin prompt for the loopback exemption would be like for this Windows user.</summary>
    public static AdminPrompt Elevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return AdminPrompt.Elevated;
        var token = identity.Token;
        int type, size;
        if (GetTokenInformation(token, TokenElevationType, &type, sizeof(int), &size) && type == TokenElevationTypeLimited) return AdminPrompt.Prompt;
        return AdminPrompt.NoAdmin;
    }

    // ---------------------------------------------------------------- DNS inside the container

    /// <summary>
    /// Inside an AppContainer the DNS client service may refuse to resolve for the game (seen in the spike on a "Public"
    /// network with the router as DNS server), while plain UDP to the same DNS server works. These JVM arguments let Java
    /// ask the system's DNS servers itself when the normal lookup fails: Java 8 through its built-in JNDI DNS provider,
    /// Java 18+ through Jay's own resolver (a small jar built from <c>tools/jay-dns</c> in this repository). The normal
    /// lookup still goes first, so the hosts file, VPN and corporate DNS keep working where it works. Java 9–17 have
    /// neither hook, so they get null and use the standard level. Also null without DNS servers or when the Java lacks
    /// the provider.
    /// </summary>
    public static IReadOnlyList<string>? DnsArguments(int javaMajor, string javaHome, IReadOnlyList<string> servers, string? resolverJar)
    {
        if (servers.Count == 0) return null;
        var list = string.Join(',', servers);
        if (javaMajor is > 0 and <= 8)
        {
            if (!File.Exists(Path.Combine(javaHome, "lib", "ext", "dnsns.jar"))) return null;
            // Providers are tried in order: the system lookup, then the DNS servers directly.
            return ["-Dsun.net.spi.nameservice.provider.1=default", "-Dsun.net.spi.nameservice.provider.2=dns,sun",
                "-Dsun.net.spi.nameservice.nameservers=" + list];
        }
        if (javaMajor >= 18 && resolverJar is not null)
            return ["-Xbootclasspath/a:" + resolverJar, "-Djay.dns.servers=" + list];
        return null;
    }

    /// <summary>The DNS servers of the network adapters that are up (the site-local placeholders Windows lists without IPv6 DNS are skipped).</summary>
    public static IReadOnlyList<string> SystemDnsServers()
    {
        var servers = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                foreach (var dns in props.DnsAddresses)
                {
                    if (dns.AddressFamily == AddressFamily.InterNetworkV6 && dns.IsIPv6SiteLocal) continue;
                    var text = dns.ToString();
                    if (!servers.Contains(text)) servers.Add(text);
                }
            }
        }
        catch (NetworkInformationException) { }
        return servers;
    }

    /// <summary>The Java major version from the release file next to bin, or 0 when unknown.</summary>
    public static int JavaMajor(string javaPath)
    {
        var release = Path.Combine(GameSandbox.JavaHome(javaPath), "release");
        try
        {
            foreach (var line in File.ReadLines(release))
                if (line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                    return Java.JavaFinder.ParseMajor(line["JAVA_VERSION=".Length..].Trim('"'));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return 0;
    }

    /// <summary>The resolver jar's resource; missing when the build had no JDK (then Java 18+ uses the standard level).</summary>
    internal const string ResolverResource = "Amber.Core.jay-dns.jar";

    public static bool HasResolver => typeof(WindowsSandbox).Assembly.GetManifestResourceInfo(ResolverResource) is not null;

    /// <summary>Writes the resolver jar into <paramref name="folder"/> (once per content) and returns its path, or null when this build has none.</summary>
    public static string? ResolverJar(string folder)
    {
        using var stream = typeof(WindowsSandbox).Assembly.GetManifestResourceStream(ResolverResource);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..12];
        var path = Path.Combine(folder, $"jay-dns-{hash}.jar");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        return path;
    }

    // ---------------------------------------------------------------- starting the game

    /// <summary>
    /// Starts <paramref name="psi"/> (file name, arguments, working folder, environment, redirected output) sandboxed.
    /// The temp folder is moved to one the sandboxed game may write: LocalLow\Jay\temp (standard) or the container's
    /// Temp folder (strong). Every handle but the output pipes stays in Jay.
    /// </summary>
    public static Process Start(ProcessStartInfo psi, WindowsSandboxLevel level, string? tempFolder = null)
    {
        SecurityIdentifier? container = level == WindowsSandboxLevel.Strong ? ContainerSid() : null;
        var temp = tempFolder ?? (container is not null ? Path.Combine(ContainerFolder(container), "Temp") : LowTempFolder());
        Directory.CreateDirectory(temp);
        psi.Environment["TEMP"] = temp;
        psi.Environment["TMP"] = temp;

        var commandLine = new StringBuilder(CommandLine.Quote(psi.FileName));
        foreach (var arg in psi.ArgumentList) commandLine.Append(' ').Append(CommandLine.Quote(arg));
        var environment = EnvironmentBlock(psi.Environment);
        var redirect = psi.RedirectStandardOutput || psi.RedirectStandardError;

        nint outRead = 0, outWrite = 0, errRead = 0, errWrite = 0, nul = 0, token = 0;
        var attributes = (nint)0;
        var capsBuffers = new List<nint>();
        SID_AND_ATTRIBUTES* caps = null;
        nint containerSid = 0;
        var started = false;
        PROCESS_INFORMATION pi = default;
        try
        {
            var handles = stackalloc nint[3];
            var handleCount = 0;
            var si = new STARTUPINFOEXW();
            si.StartupInfo.cb = sizeof(STARTUPINFOEXW);
            if (redirect)
            {
                var sa = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), bInheritHandle = 1 };
                if (!CreatePipe(out outRead, out outWrite, &sa, 0) || !CreatePipe(out errRead, out errWrite, &sa, 0)) throw new Win32Exception();
                SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0);
                SetHandleInformation(errRead, HANDLE_FLAG_INHERIT, 0);
                nul = CreateFileW("NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_EXISTING, 0, 0);
                if (nul == -1) throw new Win32Exception();
                si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                si.StartupInfo.hStdInput = nul;
                si.StartupInfo.hStdOutput = outWrite;
                si.StartupInfo.hStdError = errWrite;
                handles[handleCount++] = nul;
                handles[handleCount++] = outWrite;
                handles[handleCount++] = errWrite;
            }

            var attributeCount = (handleCount > 0 ? 1 : 0) + (container is not null ? 1 : 0);
            SECURITY_CAPABILITIES securityCaps = default;
            if (attributeCount > 0)
            {
                nint size = 0;
                InitializeProcThreadAttributeList(0, attributeCount, 0, ref size);
                attributes = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size)) throw new Win32Exception();
                if (handleCount > 0 && !UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, handleCount * sizeof(nint), 0, 0))
                    throw new Win32Exception();
                if (container is not null)
                {
                    var sidText = container.Value;
                    if (!ConvertStringSidToSidW(sidText, out containerSid)) throw new Win32Exception();
                    caps = Capabilities(out capsBuffers);
                    securityCaps = new SECURITY_CAPABILITIES { AppContainerSid = containerSid, Capabilities = caps, CapabilityCount = (uint)CapabilitySids.Length };
                    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, &securityCaps, sizeof(SECURITY_CAPABILITIES), 0, 0))
                        throw new Win32Exception();
                }
                si.lpAttributeList = attributes;
            }

            var flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED | (psi.CreateNoWindow ? CREATE_NO_WINDOW : 0);
            var cwd = string.IsNullOrEmpty(psi.WorkingDirectory) ? null : psi.WorkingDirectory;
            bool ok;
            fixed (char* env = environment)
            {
                if (container is not null)
                {
                    ok = CreateProcessW(null, commandLine, 0, 0, handleCount > 0 ? 1 : 0, flags, env, cwd, &si, out pi);
                }
                else
                {
                    token = LowIntegrityToken();
                    ok = CreateProcessAsUserW(token, null, commandLine, 0, 0, handleCount > 0 ? 1 : 0, flags, env, cwd, &si, out pi);
                }
            }
            if (!ok) throw new Win32Exception();

            var process = Adopt(pi.hProcess, pi.dwProcessId,
                redirect ? Reader(outRead, psi.StandardOutputEncoding) : null,
                redirect ? Reader(errRead, psi.StandardErrorEncoding) : null);
            outRead = errRead = 0;
            started = true;
            ResumeThread(pi.hThread);
            return process;
        }
        finally
        {
            if (!started && pi.hProcess != 0)
            {
                TerminateProcess(pi.hProcess, 1);
                CloseHandle(pi.hProcess);
            }
            if (pi.hThread != 0) CloseHandle(pi.hThread);
            foreach (var h in new[] { outRead, errRead, outWrite, errWrite, nul, token })
                if (h != 0 && h != -1) CloseHandle(h);
            if (attributes != 0)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            foreach (var b in capsBuffers) LocalFree(b);
            if (caps is not null) Marshal.FreeHGlobal((nint)caps);
            if (containerSid != 0) LocalFree(containerSid);
        }
    }

    /// <summary>LocalLow\Jay\temp: LocalLow carries the low label, so a low-integrity game may write there.</summary>
    public static string LowTempFolder()
    {
        var hr = SHGetKnownFolderPath(LocalAppDataLow, 0, 0, out var path);
        if (hr != 0) throw new Win32Exception(hr);
        try { return Path.Combine(Marshal.PtrToStringUni(path)!, "Jay", "temp"); }
        finally { CoTaskMemFree(path); }
    }

    private static readonly Guid LocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    private static nint LowIntegrityToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ASSIGN_PRIMARY, out var own)) throw new Win32Exception();
        try
        {
            if (!DuplicateTokenEx(own, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ASSIGN_PRIMARY, 0, SecurityImpersonation, TokenPrimary, out var low))
                throw new Win32Exception();
            if (!ConvertStringSidToSidW("S-1-16-4096", out var lowSid))
            {
                CloseHandle(low);
                throw new Win32Exception();
            }
            try
            {
                var label = new TOKEN_MANDATORY_LABEL { Label = new SID_AND_ATTRIBUTES { Sid = lowSid, Attributes = SE_GROUP_INTEGRITY } };
                if (!SetTokenInformation(low, TokenIntegrityLevel, &label, sizeof(TOKEN_MANDATORY_LABEL) + GetLengthSid(lowSid)))
                {
                    var error = Marshal.GetLastWin32Error();
                    CloseHandle(low);
                    throw new Win32Exception(error);
                }
                return low;
            }
            finally { LocalFree(lowSid); }
        }
        finally { CloseHandle(own); }
    }

    private static StreamReader Reader(nint handle, Encoding? encoding) =>
        new(new FileStream(new SafeFileHandle(handle, true), FileAccess.Read, 4096, false), encoding ?? Encoding.UTF8);

    // Process has no public way to wrap a process someone else created. These private members are what
    // Process.Start itself fills in; ProcessAdoptionTests guards that they still exist on every .NET update.
    internal static readonly MethodInfo? SetProcessHandle = typeof(Process).GetMethod("SetProcessHandle", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(SafeProcessHandle)]);
    internal static readonly MethodInfo? SetProcessId = typeof(Process).GetMethod("SetProcessId", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(int)]);
    internal static readonly FieldInfo? StandardOutputField = typeof(Process).GetField("_standardOutput", BindingFlags.NonPublic | BindingFlags.Instance);
    internal static readonly FieldInfo? StandardErrorField = typeof(Process).GetField("_standardError", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>A <see cref="Process"/> for a process Jay created with CreateProcess: Id, exit code, Exited, Kill and Begin…ReadLine work.</summary>
    internal static Process Adopt(nint processHandle, int processId, StreamReader? output, StreamReader? error)
    {
        if (SetProcessHandle is null || SetProcessId is null || StandardOutputField is null || StandardErrorField is null)
            throw new SandboxUnavailableException(L.T("core.launch.sandbox.win.adopt_failed"));
        var process = new Process();
        SetProcessHandle.Invoke(process, [new SafeProcessHandle(processHandle, true)]);
        SetProcessId.Invoke(process, [processId]);
        if (output is not null) StandardOutputField.SetValue(process, output);
        if (error is not null) StandardErrorField.SetValue(process, error);
        return process;
    }

    /// <summary>A Unicode environment block, sorted the way Windows expects.</summary>
    internal static string EnvironmentBlock(IDictionary<string, string?> environment)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in environment.Where(e => e.Value is not null).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(key).Append('=').Append(value).Append('\0');
        if (sb.Length == 0) sb.Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    // ---------------------------------------------------------------- native

    private const int SE_FILE_OBJECT = 1;
    private const uint LABEL_SECURITY_INFORMATION = 0x10;
    private const int HRESULT_ALREADY_EXISTS = unchecked((int)0x800700B7);
    private const uint SE_GROUP_ENABLED = 0x4;
    private const uint SE_GROUP_INTEGRITY = 0x20;
    private const uint TOKEN_DUPLICATE = 0x2, TOKEN_QUERY = 0x8, TOKEN_ADJUST_DEFAULT = 0x80, TOKEN_ASSIGN_PRIMARY = 0x1;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;
    private const int TokenIntegrityLevel = 25, TokenElevationType = 18, TokenElevationTypeLimited = 3;
    private const uint HANDLE_FLAG_INHERIT = 1;
    private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const int STARTF_USESTDHANDLES = 0x100;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_SUSPENDED = 0x4, CREATE_NO_WINDOW = 0x08000000;
    private const nint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = 0x20009;

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES { public nint Sid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_CAPABILITIES { public nint AppContainerSid; public SID_AND_ATTRIBUTES* Capabilities; public uint CapabilityCount; public uint Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public nint lpSecurityDescriptor; public int bInheritHandle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW { public STARTUPINFOW StartupInfo; public nint lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetNamedSecurityInfoW(string name, int type, uint info, nint* owner, nint* group, nint* dacl, nint* sacl, out nint sd);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetNamedSecurityInfoW(string name, int type, uint info, nint owner, nint group, nint dacl, nint sacl);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint sd, out uint size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(nint sd, uint revision, uint info, out nint sddl, out uint length);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(nint sd, out bool present, out nint sacl, out bool defaulted);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool ConvertStringSidToSidW(string sid, out nint psid);

    [DllImport("advapi32.dll")]
    private static extern int GetLengthSid(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint FreeSid(nint sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int impersonation, int type, out nint newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(nint token, int infoClass, void* info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint token, int infoClass, void* info, int length, int* returned);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(nint token, string? app, StringBuilder commandLine, nint pa, nint ta, int inherit, uint flags,
        char* environment, string? cwd, STARTUPINFOEXW* si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessW(string? app, StringBuilder commandLine, nint pa, nint ta, int inherit, uint flags,
        char* environment, string? cwd, STARTUPINFOEXW* si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, void* value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out nint read, out nint write, SECURITY_ATTRIBUTES* attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateFileW(string name, uint access, uint share, SECURITY_ATTRIBUTES* attributes, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint process, uint code);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(nint memory);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CreateAppContainerProfile(string name, string displayName, string description, SID_AND_ATTRIBUTES* capabilities, uint count, out nint sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DeleteAppContainerProfile(string name);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetAppContainerFolderPath(string sid, out nint path);

    [DllImport("FirewallAPI.dll")]
    private static extern int NetworkIsolationGetAppContainerConfig(out uint count, out SID_AND_ATTRIBUTES* sids);
}

/// <summary>Windows command-line quoting (the rules CommandLineToArgvW and the C runtime use to split it again).</summary>
internal static class CommandLine
{
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
