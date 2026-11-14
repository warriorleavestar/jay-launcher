using System.Diagnostics;
using System.Net;
using System.Text;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Net;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>Jay-only server setup: the files it writes from downloaded or user-edited input.</summary>
public sealed class McServerHostTests : IDisposable
{
    // The server root sits one level inside a folder of the test's own, so a path that escapes it still lands there.
    private readonly string _outer = Directory.CreateTempSubdirectory("jay-mchost-").FullName;
    private string Root => Path.Combine(_outer, "root");

    /// <summary>
    /// Java's Properties loader keeps the last line for a key and accepts "key = v", "key:v" and "key v": every spelling
    /// of a forced key goes, and the forced line is the only one left.
    /// </summary>
    [Fact]
    public void ForcedPropertiesWinOverEverySpellingOfTheKey()
    {
        var host = new McServerHost(Root);
        Directory.CreateDirectory(host.McDirectory);
        var path = Path.Combine(host.McDirectory, "server.properties");
        File.WriteAllLines(path,
        [
            "# server-ip=0.0.0.0",
            "server-ip=127.0.0.1",
            "motd=hi",
            "server-ip=",
            "enable-rcon = true",
            "  enable-rcon:true",
            "enable-query true",
            "online-mode=true",
        ]);
        host.WriteProperties(25570);
        var lines = File.ReadAllLines(path);
        string[] Lines(string key) => [.. lines.Where(l => McServerHost.PropertyKey(l) == key)];
        Assert.Equal(["server-ip=127.0.0.1"], Lines("server-ip"));
        Assert.Equal(["enable-rcon=false"], Lines("enable-rcon"));
        Assert.Equal(["enable-query=false"], Lines("enable-query"));
        Assert.Equal(["server-port=25570"], Lines("server-port"));
        // Keys Jay doesn't force, comments and the owner's own online-mode stay as they were.
        Assert.Contains("motd=hi", lines);
        Assert.Contains("# server-ip=0.0.0.0", lines);
        Assert.Equal(["online-mode=true"], Lines("online-mode"));
    }

    [Theory]
    [InlineData("key=value", "key")]
    [InlineData("  key = value", "key")]
    [InlineData("key:value", "key")]
    [InlineData("\tkey value", "key")]
    [InlineData("key", "key")]
    [InlineData("# key=value", null)]
    [InlineData("! key=value", null)]
    [InlineData("   ", null)]
    public void PropertyKeysAreReadAsJavaReadsThem(string line, string? key) => Assert.Equal(key, McServerHost.PropertyKey(line));

    /// <summary>The version id comes from the downloaded manifest; one that is not a plain file name writes nothing.</summary>
    [Theory]
    [InlineData("../gate/state")]
    [InlineData("../../escaped")]
    [InlineData("a/b")]
    public async Task AVersionIdThatIsNotAFileNameIsRefused(string id)
    {
        var manifest = $$"""{"latest":{"release":"{{id}}"},"versions":[{"id":"{{id}}","type":"release","url":"https://meta.test/v.json","sha1":null}]}""";
        var requests = new List<string>();
        var handler = new Handler(url =>
        {
            lock (requests) requests.Add(url);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(url.Contains("version_manifest") ? manifest : "{}") };
        });
        var downloader = new Downloader(new HttpClient(handler), new SourceChain([new OfficialSource()])) { RetriesPerSource = 1 };
        var host = new McServerHost(Root);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.SetupAsync(downloader, "latest", 1024, _ => { }));
        lock (requests) Assert.DoesNotContain(requests, r => r.Contains("v.json", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(Root, "gate")));
        Assert.Empty(Directory.EnumerateFiles(_outer, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(Path.Combine(Root, "launcher-cache"), StringComparison.Ordinal)));
    }

    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request.RequestUri!.ToString()));
    }

    /// <summary>
    /// server.properties as Java reads it: continued lines, escaped keys and repeated keys never leave a forced value
    /// swallowed or overridden, and every other entry keeps the value it had. Checked with a reader written separately
    /// from the one under test, and with java.util.Properties itself when Java is installed.
    /// </summary>
    [Theory]
    [MemberData(nameof(TrickyProperties))]
    public void ForcedPropertiesTakeEffectAsJavaReadsTheFile(string name, string content)
    {
        var host = new McServerHost(Root);
        Directory.CreateDirectory(host.McDirectory);
        var path = Path.Combine(host.McDirectory, "server.properties");
        File.WriteAllText(path, content);
        var before = JavaLoad(content);
        host.WriteProperties(25570);
        var once = File.ReadAllText(path);
        host.WriteProperties(25570);
        var twice = File.ReadAllText(path);

        foreach (var text in new[] { once, twice })
        {
            var after = JavaLoad(text);
            Assert.True(after.TryGetValue("server-ip", out var ip) && ip == "127.0.0.1", $"{name}: server-ip is '{ip}'");
            Assert.Equal("25570", after["server-port"]);
            Assert.Equal("false", after["enable-query"]);
            Assert.Equal("false", after["enable-rcon"]);
            Assert.Equal("false", after["online-mode"]);
            string[] forced = ["server-ip", "server-port", "enable-query", "enable-rcon", "online-mode", "enforce-secure-profile"];
            foreach (var (key, value) in before.Where(p => !forced.Contains(p.Key)))
                Assert.True(after.TryGetValue(key, out var kept) && kept == value, $"{name}: {key} changed from '{value}' to '{kept}'");
            Assert.Empty(after.Keys.Except(before.Keys).Except(forced));
        }
        if (JavaProperties([content, once, twice]) is { } java)
        {
            Assert.Equal(before, java[0]);
            Assert.Equal(JavaLoad(once), java[1]);
            Assert.Equal(JavaLoad(twice), java[2]);
        }
    }

    public static TheoryData<string, string> TrickyProperties() => new()
    {
        { "continued motd swallows a server-ip line", "motd=hello\\\nserver-ip=127.0.0.1\nserver-ip=127.0.0.1\nonline-mode=false\n" },
        { "server-ip only inside a continuation", "motd=a\\\n   server-ip=0.0.0.0\nonline-mode=false\n" },
        { "even backslashes do not continue", "motd=a\\\\\nserver-ip=0.0.0.0\nonline-mode=false\n" },
        { "three backslashes continue", "motd=a\\\\\\\nserver-ip=0.0.0.0\nserver-ip=0.0.0.0\nonline-mode=false\n" },
        { "escaped keys", "server\\-ip=0.0.0.0\n\\u0073erver-ip=0.0.0.0\nenable\\u002drcon=true\nonline-mode=false\n" },
        { "forced key continued over lines", "server-ip=0.0.\\\n  0.0\nenable-rcon=tr\\\n ue\nonline-mode=false\n" },
        { "continuation line starting with a comment marker", "motd=a\\\n# not a comment\nserver-ip=0.0.0.0\nonline-mode=false\n" },
        { "comment ending in a backslash does not continue", "# note \\\nserver-ip=0.0.0.0\nonline-mode=false\n" },
        { "last line continues at the end of the file", "online-mode=false\nmotd=x\\" },
        { "empty line ends a continuation", "motd=x\\\n\nserver-ip=0.0.0.0\nonline-mode=false\n" },
        { "separators and duplicates", "server-ip 0.0.0.0\nserver-ip:0.0.0.0\n  server-ip = 0.0.0.0\nlevel-name=world\nonline-mode=false\r\nmotd=crlf\r\n" },
    };

    /// <summary>
    /// java.util.Properties.load, written from its documentation as a character reader, separately from McServerHost.
    /// </summary>
    private static Dictionary<string, string> JavaLoad(string content)
    {
        var result = new Dictionary<string, string>();
        var pos = 0;
        string? NextNaturalLine()
        {
            if (pos >= content.Length) return null;
            var start = pos;
            while (pos < content.Length && content[pos] is not ('\r' or '\n')) pos++;
            var line = content[start..pos];
            // A line ends at \n, \r or \r\n.
            if (pos < content.Length && content[pos] == '\r') pos++;
            if (pos < content.Length && content[pos] == '\n' && (pos == start + line.Length || content[pos - 1] == '\r')) pos++;
            return line;
        }
        static bool IsSpace(char c) => c is ' ' or '\t' or '\f';
        static int TrailingBackslashes(StringBuilder b) { var n = 0; while (n < b.Length && b[b.Length - 1 - n] == '\\') n++; return n; }
        while (NextNaturalLine() is { } natural)
        {
            var at = 0;
            while (at < natural.Length && IsSpace(natural[at])) at++;
            if (at == natural.Length || natural[at] is '#' or '!') continue;
            var logical = new StringBuilder(natural[at..]);
            while (TrailingBackslashes(logical) % 2 == 1)
            {
                logical.Length--;
                if (NextNaturalLine() is not { } next) break;
                var skip = 0;
                while (skip < next.Length && IsSpace(next[skip])) skip++;
                if (skip == next.Length) break;
                logical.Append(next, skip, next.Length - skip);
            }
            var text = logical.ToString();
            var key = new StringBuilder();
            var i = 0;
            for (; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length) { key.Append('\\').Append(text[++i]); continue; }
                if (c is '=' or ':' || IsSpace(c)) break;
                key.Append(c);
            }
            while (i < text.Length && IsSpace(text[i])) i++;
            if (i < text.Length && text[i] is '=' or ':') i++;
            while (i < text.Length && IsSpace(text[i])) i++;
            result[Decode(key.ToString())] = Decode(text[i..]);
        }
        return result;
    }

    private static string Decode(string s)
    {
        var b = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { b.Append(s[i]); continue; }
            if (++i >= s.Length) break;
            b.Append(s[i] switch
            {
                'u' when i + 4 < s.Length => (char)Convert.ToInt32(s.Substring((i += 4) - 3, 4), 16),
                't' => '\t',
                'n' => '\n',
                'r' => '\r',
                'f' => '\f',
                var c => c,
            });
        }
        return b.ToString();
    }

    /// <summary>Loads each text with java.util.Properties when a Java runtime is on this machine; null otherwise.</summary>
    private Dictionary<string, string>[]? JavaProperties(string[] texts)
    {
        var java = FindJava();
        if (java is null) return null;
        var dir = Directory.CreateDirectory(Path.Combine(_outer, "java-check")).FullName;
        var source = Path.Combine(dir, "LoadProperties.java");
        File.WriteAllText(source, """
            import java.io.*;
            import java.nio.charset.StandardCharsets;
            import java.util.*;
            public class LoadProperties {
                public static void main(String[] args) throws Exception {
                    PrintStream out = new PrintStream(System.out, true, "UTF-8");
                    for (String file : args) {
                        Properties p = new Properties();
                        try (Reader r = new InputStreamReader(new FileInputStream(file), StandardCharsets.UTF_8)) { p.load(r); }
                        for (String k : new TreeSet<>(p.stringPropertyNames())) out.println(esc(k) + "\t" + esc(p.getProperty(k)));
                        out.println("--");
                    }
                }
                static String esc(String s) { return s.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r"); }
            }
            """);
        var info = new ProcessStartInfo(java) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        info.ArgumentList.Add(source);
        for (var i = 0; i < texts.Length; i++)
        {
            var file = Path.Combine(dir, $"{i}.properties");
            File.WriteAllText(file, texts[i]);
            info.ArgumentList.Add(file);
        }
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "java did not finish");
        Assert.True(process.ExitCode == 0, stderr.Result);
        var maps = new List<Dictionary<string, string>> { new() };
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line == "--") { maps.Add([]); continue; }
            if (line.Length == 0) continue;
            var tab = line.IndexOf('\t');
            maps[^1][Unesc(line[..tab])] = Unesc(line[(tab + 1)..]);
        }
        return [.. maps.Take(texts.Length)];
        static string Unesc(string s) => s.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
    }

    private static string? FindJava()
    {
        var name = OperatingSystem.IsWindows() ? "java.exe" : "java";
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home && File.Exists(Path.Combine(home, "bin", name)))
            return Path.Combine(home, "bin", name);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_outer, true); }
        catch (IOException) { }
    }
}
