using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Amber.Core.Themes;

/// <summary>
/// 已安装的主题：每个主题解压在 {数据目录}/themes/{id}/。只解压读取器认可的文件，路径已经核对过。
/// </summary>
public sealed class ThemeStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>导入一个 .jaytheme：先完整读一遍（不合格的整个拒绝），再解压到临时目录，最后换上去。同 id 的旧版本会被替换。</summary>
    public ThemePack Install(string packPath)
    {
        var info = new FileInfo(packPath);
        if (!info.Exists) throw new ThemePackException("not_found", packPath);
        if (info.Length > ThemePackReader.MaxPackBytes) throw new ThemePackException("pack_too_large", $"{info.Length}");
        Dictionary<string, byte[]> files;
        ThemePack pack;
        using (var stream = info.OpenRead())
        {
            files = ThemePackReader.ReadEntries(stream);
            stream.Position = 0;
            pack = ThemePackReader.Read(stream);
        }
        Directory.CreateDirectory(Root);
        var target = Path.Combine(Root, pack.Id);
        var temp = Path.Combine(Root, $".{pack.Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            foreach (var (name, bytes) in files)
            {
                var full = Path.GetFullPath(Path.Combine(temp, name));
                if (!full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new ThemePackException("unsafe_path", name);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
            }
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(temp, target);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
        pack.Directory = target;
        return pack;
    }

    /// <summary>列出已安装的主题；读不了的跳过（不让一个坏主题拖垮设置页）。</summary>
    public List<ThemePack> List()
    {
        var result = new List<ThemePack>();
        if (!Directory.Exists(Root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || !ThemePackReader.IsValidId(name)) continue;
            if (TryLoad(name) is { } pack) result.Add(pack);
        }
        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public ThemePack? TryLoad(string id)
    {
        if (!ThemePackReader.IsValidId(id)) return null;
        var dir = Path.Combine(Root, id);
        if (!Directory.Exists(dir)) return null;
        try
        {
            var pack = ThemePackReader.ReadDirectory(dir);
            return pack.Id == id ? pack : null;
        }
        catch (Exception e) when (e is ThemePackException or IOException or UnauthorizedAccessException) { return null; }
    }

    public bool Remove(string id)
    {
        if (!ThemePackReader.IsValidId(id)) return false;
        var dir = Path.Combine(Root, id);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, true);
        return true;
    }
}

/// <summary>
/// 启动保护：套用主题前记一笔，界面正常显示后清掉。连着两次没清掉（主题让 Jay 启动就崩），下次就用默认主题启动。
/// </summary>
public sealed class ThemeStartupGuard(string path)
{
    public const int MaxFailures = 2;

    /// <summary>准备套用 id。返回 false 表示这个主题已经连着失败太多次，这次别用。</summary>
    public bool Begin(string id)
    {
        var (lastId, pending) = Read();
        var count = lastId == id ? pending : 0;
        if (count >= MaxFailures) return false;
        Write(id, count + 1);
        return true;
    }

    /// <summary>界面已经正常显示：清掉记录。</summary>
    public void Succeeded()
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>用户手动重新选了主题，给它一次新的机会。</summary>
    public void Reset() => Succeeded();

    private (string? Id, int Pending) Read()
    {
        try
        {
            if (!File.Exists(path)) return (null, 0);
            var parts = File.ReadAllText(path).Split('\n');
            return parts.Length >= 2 && int.TryParse(parts[1], out var n) ? (parts[0], n) : (null, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, 0); }
    }

    private void Write(string id, int pending)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, $"{id}\n{pending}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>把一套配色写成 .jaytheme（“导出当前主题”，做主题的人从这里改起）。</summary>
public static class ThemePackWriter
{
    public static void Write(string path, string id, string name, IReadOnlyDictionary<string, uint> dark, IReadOnlyDictionary<string, uint> light,
        IReadOnlyDictionary<string, double> numbers, IReadOnlyDictionary<string, double> radii)
    {
        if (!ThemePackReader.IsValidId(id)) throw new ThemePackException("bad_id", id);
        var options = new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        byte[] json(Action<Utf8JsonWriter> body)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, options)) { w.WriteStartObject(); body(w); w.WriteEndObject(); }
            return ms.ToArray();
        }
        var manifest = json(w =>
        {
            w.WriteNumber("format", ThemePackReader.FormatVersion);
            w.WriteString("id", id);
            w.WriteString("name", name);
            w.WriteString("version", "1.0.0");
        });
        var tokens = json(w =>
        {
            w.WriteStartObject("colors");
            foreach (var (key, set) in new[] { ("dark", dark), ("light", light) })
            {
                w.WriteStartObject(key);
                foreach (var k in ThemePackReader.ColorKeys)
                    if (set.TryGetValue(k, out var c)) w.WriteString(k, ThemePackReader.FormatColor(c));
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartObject("sizes");
            foreach (var (k, v) in numbers) w.WriteNumber(k, v);
            w.WriteEndObject();
            w.WriteStartObject("radius");
            foreach (var (k, v) in radii) w.WriteNumber(k, v);
            w.WriteEndObject();
        });
        var temp = path + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var (entryName, bytes) in new[] { ("theme.json", manifest), ("tokens.json", tokens), ("README.txt", Encoding.UTF8.GetBytes(Readme)) })
            {
                using var s = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                s.Write(bytes);
            }
        }
        File.Move(temp, path, true);
    }

    private const string Readme =
        "Jay theme pack. Edit tokens.json (colors / sizes / radius / fonts / background), zip the folder and import it in Jay settings.\n" +
        "Fonts go in fonts/ (.ttf/.otf), images in assets/ (.png/.jpg, max 4096x4096). No code or network URLs are allowed.\n";
}
