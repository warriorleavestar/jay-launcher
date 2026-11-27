using Xunit;

namespace Amber.Core.Tests;

/// <summary>
/// The name of the computer account is personal data. Jay reads it only to remove it from the text of error
/// reports, never to put it into something the user shares (an exported pack, an invite, a log bundle).
/// </summary>
public class PersonalDataTests
{
    private static readonly string Src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>The only readers of the account name: both pass it to the redaction of exported reports.</summary>
    private static readonly string[] Redactors =
    [
        Path.Combine("Amber.Core", "Launch", "ErrorReport.cs"),
        Path.Combine("Amber.Core", "Diagnostics", "CrashAnalyzer.cs"),
    ];

    [Fact]
    public void OnlyReportRedactionReadsTheComputerAccountName()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Src, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin") || parts[0].EndsWith(".Tests", StringComparison.Ordinal)) continue;
            if (Redactors.Contains(relative)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains("Environment.UserName", StringComparison.Ordinal))
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0, "The computer account name is read outside report redaction:\n" + string.Join("\n", offenders));
    }
}
