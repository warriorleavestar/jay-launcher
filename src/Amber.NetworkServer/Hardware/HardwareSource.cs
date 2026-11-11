using System.Diagnostics;
using System.Text;

namespace Amber.NetworkServer.Hardware;

/// <summary>
/// One hardware identifier of the machine a node runs on. <see cref="Value"/> is the raw identifier; it never leaves the
/// machine (only salted HMACs of it do, see <see cref="HardwareBinding"/>).
/// </summary>
/// <param name="Kind">"tpm" (TPM endorsement key), "board" (motherboard / platform UUID or serial), "disk" (system disk serial) or "machine" (OS machine id).</param>
public sealed record HardwareFactor(string Kind, string Value);

/// <summary>Where hardware identifiers come from. Tests inject a fake; nodes use <see cref="SystemHardwareSource"/>.</summary>
public interface IHardwareSource
{
    IReadOnlyList<HardwareFactor> Read();
}

/// <summary>
/// Reads this machine's identifiers without root: TPM endorsement key (via tpm2-tools, needs the tss group on Linux),
/// board serial / platform UUID where readable, the system disk's serial, and the OS machine id.
/// Anything that can't be read is left out; <see cref="HardwareBinding"/> decides whether what is left is enough.
/// </summary>
public sealed class SystemHardwareSource : IHardwareSource
{
    public static readonly SystemHardwareSource Instance = new();

    public IReadOnlyList<HardwareFactor> Read()
    {
        var list = new List<HardwareFactor>();
        void Add(string kind, string? value)
        {
            value = value?.Trim().Trim('\0').Trim();
            if (!string.IsNullOrEmpty(value) && !IsPlaceholder(value) && list.All(f => f.Kind != kind)) list.Add(new HardwareFactor(kind, value));
        }

        if (OperatingSystem.IsLinux())
        {
            Add("tpm", Tpm2Tools.EndorsementKeyId());
            // product_uuid is root-only; board_serial too on most distros. Try both, keep whichever is readable.
            Add("board", ReadFile("/sys/class/dmi/id/product_uuid") ?? ReadFile("/sys/class/dmi/id/board_serial"));
            Add("disk", LinuxSystemDiskSerial());
            Add("machine", ReadFile("/etc/machine-id"));
        }
        else if (OperatingSystem.IsWindows())
        {
            // No EK without admin on Windows; the TPM still binds the keys through the Platform Crypto Provider (TpmSealer).
            Add("board", PowerShell("(Get-CimInstance Win32_ComputerSystemProduct).UUID"));
            Add("disk", PowerShell("(Get-Disk | Where-Object IsBoot | Select-Object -First 1).SerialNumber"));
            Add("machine", Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var io = Run("ioreg", ["-rd1", "-c", "IOPlatformExpertDevice"]);
            Add("board", Between(io, "\"IOPlatformUUID\" = \"", "\""));
            Add("machine", Between(io, "\"IOPlatformSerialNumber\" = \"", "\""));
        }
        return list;
    }

    /// <summary>Vendors ship blank or dummy ids ("To be filled by O.E.M.", all zeros); those identify nothing.</summary>
    internal static bool IsPlaceholder(string value) =>
        value.All(c => c is '0' or '-' or 'F' or 'f' or ' ')
        || value.Contains("filled by", StringComparison.OrdinalIgnoreCase)
        || value.Contains("default string", StringComparison.OrdinalIgnoreCase)
        || value.Equals("None", StringComparison.OrdinalIgnoreCase);

    private static string? ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? LinuxSystemDiskSerial()
    {
        // The disk that holds "/": its parent block device's serial (udev data, readable without root).
        var source = Run("findmnt", ["-no", "SOURCE", "/"])?.Trim();
        if (string.IsNullOrEmpty(source)) return null;
        var parent = Run("lsblk", ["-no", "PKNAME", source])?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var disk = string.IsNullOrEmpty(parent) ? source : "/dev/" + parent;
        return Run("lsblk", ["-dno", "SERIAL", disk])?.Trim() is { Length: > 0 } serial ? serial
            : Run("lsblk", ["-dno", "WWN", disk])?.Trim();
    }

    private static string? PowerShell(string script) => Run("powershell", ["-NoProfile", "-NonInteractive", "-Command", script]);

    private static string? Between(string? text, string start, string end)
    {
        if (text is null) return null;
        var i = text.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return null;
        i += start.Length;
        var j = text.IndexOf(end, i, StringComparison.Ordinal);
        return j < 0 ? null : text[i..j];
    }

    internal static string? Run(string file, IEnumerable<string> args, byte[]? stdin = null) =>
        RunBytes(file, args, stdin) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>Runs a tool with a short timeout. Null when it's missing, fails or times out.</summary>
    internal static byte[]? RunBytes(string file, IEnumerable<string> args, byte[]? stdin = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            if (stdin is not null)
            {
                p.StandardInput.BaseStream.Write(stdin);
                p.StandardInput.Close();
            }
            using var output = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(output);
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return null; }
            copy.Wait(5_000);
            return p.ExitCode == 0 ? output.ToArray() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }
}
