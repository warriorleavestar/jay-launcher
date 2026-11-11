using System.Text.Json;

namespace Amber.Core;

/// <summary>
/// The pins in <c>build/pins.json</c> (embedded in this assembly): every binary Jay bundles or downloads with a fixed
/// hash, with where it comes from. Code that downloads a pinned file reads its pin from here, so the file is the one
/// place a pin is changed. See docs/pins.md.
/// </summary>
public static class Pins
{
    public const string ResourceName = "Amber.Core.pins.json";

    public sealed record Pin(
        string Id,
        string Name,
        string? Version,
        string? Tag,
        string? Commit,
        string? Platform,
        string? File,
        IReadOnlyList<string> Urls,
        string? Source,
        string? License,
        string? Sha256,
        string? Sha1,
        long? Size,
        IReadOnlyList<string> UsedBy);

    private static readonly Lazy<IReadOnlyList<Pin>> s_all = new(() =>
    {
        using var stream = typeof(Pins).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("build/pins.json is not embedded in Amber.Core");
        return Parse(stream);
    });

    /// <summary>Every pin, in file order.</summary>
    public static IReadOnlyList<Pin> All => s_all.Value;

    /// <summary>The pin with this id; throws when there is none (a missing pin is a build mistake, not a runtime state).</summary>
    public static Pin Get(string id) =>
        All.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException($"build/pins.json has no pin '{id}'");

    /// <summary>Reads a pins file. Throws <see cref="InvalidDataException"/> on a wrong schema or a malformed pin.</summary>
    public static IReadOnlyList<Pin> Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = false });
        var root = doc.RootElement;
        if (Str(root, "schema") != "jay-pins@1") throw new InvalidDataException("pins: schema must be jay-pins@1");
        if (!root.TryGetProperty("pins", out var pins) || pins.ValueKind != JsonValueKind.Array) throw new InvalidDataException("pins: no pins array");
        var list = new List<Pin>();
        foreach (var p in pins.EnumerateArray())
        {
            var id = Str(p, "id") ?? throw new InvalidDataException("pins: a pin has no id");
            var pin = new Pin(id, Str(p, "name") ?? id, Str(p, "version"), Str(p, "tag"), Str(p, "commit"), Str(p, "platform"), Str(p, "file"),
                Strings(p, "urls"), Str(p, "source"), Str(p, "license"), Str(p, "sha256"), Str(p, "sha1"),
                p.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : null, Strings(p, "usedBy"));
            if (list.Any(q => q.Id == id)) throw new InvalidDataException($"pins: duplicate id {id}");
            if (pin.Sha256 is null && pin.Sha1 is null) throw new InvalidDataException($"pins: {id} has no hash");
            if (pin.Sha256 is { } h && !IsHex(h, 64)) throw new InvalidDataException($"pins: {id} sha256 is not 64 lowercase hex digits");
            if (pin.Sha1 is { } h1 && !IsHex(h1, 40)) throw new InvalidDataException($"pins: {id} sha1 is not 40 lowercase hex digits");
            if (pin.Urls.Count == 0 || pin.Urls.Any(u => !u.StartsWith("https://", StringComparison.Ordinal)))
                throw new InvalidDataException($"pins: {id} needs at least one https URL");
            list.Add(pin);
        }
        return list;
    }

    private static bool IsHex(string s, int length) => s.Length == length && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static IReadOnlyList<string> Strings(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
