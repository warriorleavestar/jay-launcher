namespace Amber.Core.Launch;

/// <summary>
/// The strong sandbox's loopback bookkeeping as plain rules (no Windows calls), so every case is unit-tested:
/// <paramref name="exempt"/> is the loopback exemption, <paramref name="filters"/> the inbound filters' state
/// (true / false / unknown, see <see cref="WindowsLoopbackInbound.Check"/>), and <paramref name="recorded"/> whether this
/// Jay recorded a successful loopback setup.
/// </summary>
public static class LoopbackSetupRules
{
    /// <summary>The admin helper's exit codes besides 0 (done), 1 (a step failed) and 2 (bad command line).</summary>
    public const int ExitUnexpected = 3;
    /// <summary>Undo: the exemption and folders were undone, but the filter engine could not be reached to remove the filters.</summary>
    public const int ExitFiltersNotConfirmed = 4;

    /// <summary>Strong mode may use loopback without asking: exempt, and the filters are there (or unknown but recorded).</summary>
    public static bool Ready(bool exempt, bool? filters, bool recorded) => exempt && (filters is true || filters is null && recorded);

    /// <summary>Something to undo: the exemption, filters that are there, or an unknown state after a recorded setup.</summary>
    public static bool HasSetup(bool exempt, bool? filters, bool recorded) => exempt || filters is true || filters is null && recorded;

    /// <summary>What "loopback set up" should be after the helper ran: set by a loopback setup that exited 0, cleared by an
    /// undo that removed everything, kept otherwise (including an undo whose filters could not be confirmed removed).</summary>
    public static bool RecordedAfter(bool recorded, bool undo, bool loopback, int exitCode) =>
        !loopback ? recorded
        : undo ? exitCode == 0 ? false : recorded
        : exitCode == 0 || recorded;

    /// <summary>
    /// How Jay shows a failed helper exit code: an HRESULT from the filter engine (facility FWP, or a wrapped Win32 code)
    /// as "WFP 0x…", anything else as "exit N".
    /// </summary>
    public static string DescribeExit(int exitCode)
    {
        var code = unchecked((uint)exitCode);
        return (code & 0xFFFF0000) is 0x80320000 or 0x80070000 ? $"WFP 0x{code:X8}" : $"exit {exitCode}";
    }

    /// <summary>A WFP API result as an HRESULT: Win32 codes (e.g. from FwpmEngineOpen0) are wrapped as 0x8007xxxx.</summary>
    public static uint AsHResult(uint result) => result is 0 or >= 0x80000000 ? result : 0x80070000 | (result & 0xFFFF);
}
