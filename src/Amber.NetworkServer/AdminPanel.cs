using System.Reflection;

namespace Amber.NetworkServer;

/// <summary>
/// The node's local web panel, served by the admin API at <c>http://127.0.0.1:47669/</c>. Plain static files embedded in
/// the assembly (no external scripts, fonts or images); the page itself holds no data and calls the same JSON API with
/// the admin token the operator pastes in (or opens via <c>jay-server node panel</c>, which passes it in the URL fragment).
/// </summary>
public static class AdminPanel
{
    public sealed record File(string ContentType, byte[] Bytes);

    /// <summary>Only our own files, no inline code, no framing, no form posts elsewhere.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly Dictionary<string, File> Files = Load();

    private static Dictionary<string, File> Load()
    {
        var assembly = typeof(AdminPanel).Assembly;
        File Read(string name, string type)
        {
            using var stream = assembly.GetManifestResourceStream("Panel." + name) ?? throw new InvalidOperationException("missing panel file " + name);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return new File(type, memory.ToArray());
        }
        var index = Read("index.html", "text/html; charset=utf-8");
        return new(StringComparer.Ordinal)
        {
            ["/"] = index,
            ["/index.html"] = index,
            ["/panel.js"] = Read("panel.js", "text/javascript; charset=utf-8"),
            ["/panel.css"] = Read("panel.css", "text/css; charset=utf-8"),
        };
    }

    /// <summary>The panel file at <paramref name="path"/>, or null.</summary>
    public static File? Find(string path) => Files.GetValueOrDefault(path);

    /// <summary>The panel URL that logs straight in (the token rides in the fragment, which browsers never send to a server).</summary>
    public static string Url(int port, string token) => $"http://127.0.0.1:{port}/#token={token}";
}
