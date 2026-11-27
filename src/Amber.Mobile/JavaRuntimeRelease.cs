namespace Amber.Mobile;

/// <summary>Reads the Java major from the standard JDK/JRE <c>release</c> file.</summary>
public static class JavaRuntimeRelease
{
    public static int? ReadMajor(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var releasePath = Path.Combine(runtimeRoot, "release");
        if (!File.Exists(releasePath)) return null;
        try
        {
            foreach (var line in File.ReadLines(releasePath))
            {
                if (!line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal)) continue;
                return ParseMajor(line["JAVA_VERSION=".Length..].Trim().Trim('"', '\''));
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    public static int? ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var numbers = version.Split('.', '-', '_', '+')
            .TakeWhile(part => part.Length > 0 && part.All(char.IsDigit))
            .Select(part => int.TryParse(part, out var value) ? value : -1)
            .ToArray();
        if (numbers.Length == 0 || numbers[0] < 0) return null;
        if (numbers[0] == 1)
            return numbers.Length > 1 && numbers[1] > 0 ? numbers[1] : null;
        return numbers[0] > 0 ? numbers[0] : null;
    }
}
