using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amber.Core.Content;

namespace Amber.Core.Multiplayer;

/// <summary>One enabled mod in an instance: file name, SHA-1 (what Modrinth and CurseForge look files up by), size and what its metadata says.</summary>
public sealed record ModManifestEntry(string FileName, string Sha1, long Size, string? ModId = null, string? Name = null, string? Version = null)
{
    /// <summary>What to call it in the UI: the mod's own name, else the file name.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FileName : Name;

    /// <summary>Same mod across versions: by mod id when both sides have one, else by file name without the version-ish tail.</summary>
    [JsonIgnore]
    public string Identity => !string.IsNullOrWhiteSpace(ModId) ? "id:" + ModId.ToLowerInvariant() : "file:" + ModSync.StemOf(FileName);
}

/// <summary>
/// The list of enabled mods in an instance. The host sends it over the room's encrypted channel after approving a knock
/// so the joiner can compare (<see cref="ModSync"/>); a short <see cref="Fingerprint"/> of it travels in the invite.
/// </summary>
public sealed record ModManifest(IReadOnlyList<ModManifestEntry> Mods)
{
    /// <summary>Limits for a manifest that came from someone else.</summary>
    public const int MaxMods = 2000;
    public const int MaxText = 200;

    public static ModManifest Empty { get; } = new([]);

    private static readonly ConcurrentDictionary<string, (long Size, DateTime Modified, string Sha1)> HashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SHA-256 over the sorted SHA-1s, first 6 bytes. Same mod files (in any order, under any names) → same fingerprint.</summary>
    public byte[] Fingerprint
    {
        get
        {
            var text = string.Join('\n', Mods.Select(m => m.Sha1.ToLowerInvariant()).Order(StringComparer.Ordinal));
            return SHA256.HashData(Encoding.ASCII.GetBytes(text))[..InviteGame.FingerprintLength];
        }
    }

    /// <summary>Reads the enabled mods of a mods folder (hashes are cached by size and modified time).</summary>
    public static ModManifest Read(string modsDir)
    {
        var mods = new ModManager(modsDir).List().Where(m => m.Enabled).Select(m =>
        {
            var info = new FileInfo(m.Path);
            return new ModManifestEntry(m.FileName, Sha1Of(info), info.Length, m.ModId, m.Name, m.Version);
        });
        return new ModManifest([.. mods.OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>The invite's game segment for an instance with this manifest.</summary>
    public InviteGame ToInviteGame(string mcVersion, string? loader) => new(mcVersion, loader, Mods.Count, Fingerprint);

    private static string Sha1Of(FileInfo file)
    {
        if (HashCache.TryGetValue(file.FullName, out var hit) && hit.Size == file.Length && hit.Modified == file.LastWriteTimeUtc) return hit.Sha1;
        using var stream = file.OpenRead();
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(stream));
        HashCache[file.FullName] = (file.Length, file.LastWriteTimeUtc, sha1);
        return sha1;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a manifest someone sent; null when it is malformed or over the limits. File names are reduced to a bare name.</summary>
    public static ModManifest? FromJson(string json)
    {
        ModManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        if (manifest?.Mods is not { Count: <= MaxMods } mods) return null;
        var clean = new List<ModManifestEntry>(mods.Count);
        foreach (var m in mods)
        {
            if (m is null || m.Size < 0 || m.Sha1 is not { Length: 40 } || !m.Sha1.All(char.IsAsciiHexDigit)) return null;
            var name = Path.GetFileName(m.FileName?.Replace('\\', '/') ?? "");
            if (name.Length is 0 or > MaxText || name is "." or ".." || name.Any(char.IsControl)) return null;
            clean.Add(new ModManifestEntry(name, m.Sha1.ToLowerInvariant(), m.Size, Clip(m.ModId), Clip(m.Name), Clip(m.Version)));
        }
        return new ModManifest(clean);
    }

    private static string? Clip(string? text) =>
        text is null ? null : new string([.. text.Where(c => !char.IsControl(c)).Take(MaxText)]);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
