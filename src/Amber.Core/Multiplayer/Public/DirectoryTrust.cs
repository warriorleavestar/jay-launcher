using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// A directory node's certificate: a root key (Ed25519, kept offline by the network's owner) says "this X25519 key is a
/// directory of network N until T". Jay ships only the root's public key, so directory nodes can be added or re-keyed
/// without a new Jay release, and the root private key never has to be on a server.
/// <para>
/// Signed bytes: <c>"jay-directory-cert-v1\n"</c> + the JSON {"key","net","exp"} exactly as sent.
/// On the wire: {"c": base64(that JSON), "r": base64(root public key), "s": base64(signature)}, as the payload of the
/// directory's handshake message (Jay versions that don't know certificates ignore that payload).
/// </para>
/// </summary>
public sealed record DirectoryCertificate(byte[] DirectoryKey, string Network, DateTimeOffset Expires, byte[] Root, byte[] Signature, byte[] Content)
{
    private static readonly byte[] Domain = "jay-directory-cert-v1\n"u8.ToArray();
    public const int MaxSize = 1024;

    public static byte[] Issue(NetworkSigner root, ReadOnlySpan<byte> directoryKey, string network, DateTimeOffset expires)
    {
        if (directoryKey.Length != DirectoryChannel.KeyLength) throw new ArgumentException(null, nameof(directoryKey));
        var content = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["key"] = Convert.ToBase64String(directoryKey),
            ["net"] = network,
            ["exp"] = expires.ToUnixTimeSeconds(),
        }.ToJsonString());
        var signature = root.Sign([.. Domain, .. content]);
        return Encoding.UTF8.GetBytes(new JsonObject
        {
            ["c"] = Convert.ToBase64String(content),
            ["r"] = Convert.ToBase64String(root.PublicKey),
            ["s"] = Convert.ToBase64String(signature),
        }.ToJsonString());
    }

    /// <summary>Reads and checks the signature. Doesn't check the root, network or time (<see cref="DirectoryTrust"/> does).</summary>
    public static DirectoryCertificate? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxSize) return null;
        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject wire) return null;
            var content = Convert.FromBase64String(wire["c"]?.GetValue<string>() ?? "");
            var root = Convert.FromBase64String(wire["r"]?.GetValue<string>() ?? "");
            var signature = Convert.FromBase64String(wire["s"]?.GetValue<string>() ?? "");
            if (root.Length != 32 || signature.Length != 64 || !RoomSigner.Verify(root, [.. Domain, .. content], signature)) return null;
            if (JsonNode.Parse(content) is not JsonObject o) return null;
            var key = Convert.FromBase64String(o["key"]?.GetValue<string>() ?? "");
            var network = o["net"]?.GetValue<string>();
            var exp = o["exp"]?.GetValue<long>();
            if (key.Length != DirectoryChannel.KeyLength || string.IsNullOrEmpty(network) || exp is not { } e) return null;
            return new DirectoryCertificate(key, network, DateTimeOffset.FromUnixTimeSeconds(e), root, signature, content);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>
/// Which directory keys Jay accepts for one network, decided before it sends a directory anything:
/// <list type="number">
/// <item>Keys pinned in the signed connection string ("dirKeys").</item>
/// <item>Keys certified by one of the network's root keys ("dirRoots", or for the built-in network the roots compiled
/// into Jay, <see cref="PublicNetworkProfile.JayDirectoryRoots"/>).</item>
/// <item>A network with neither: trust on first use. The first key seen at each directory address is remembered
/// (<see cref="DirectoryKeyMemory"/>) and from then on only that key is accepted, with no fallback to the unauthenticated
/// protocol for that address.</item>
/// </list>
/// </summary>
public sealed class DirectoryTrust
{
    public required string Network { get; init; }
    public IReadOnlyList<byte[]> Pinned { get; init; } = [];
    public IReadOnlyList<byte[]> Roots { get; init; } = [];
    public DirectoryKeyMemory? Memory { get; init; }
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Pinned keys or roots: every request goes through the authenticated channel and the legacy protocol is never used.</summary>
    public bool IsStrict => Pinned.Count > 0 || Roots.Count > 0;

    /// <summary>Whether to accept <paramref name="key"/> at <paramref name="address"/>, given the certificate the directory sent.</summary>
    public bool Accepts(string address, byte[] key, ReadOnlySpan<byte> certificate)
    {
        if (IsStrict)
        {
            if (Contains(Pinned, key)) return true;
            return Roots.Count > 0 && DirectoryCertificate.Parse(certificate) is { } cert && Contains(Roots, cert.Root)
                && cert.Network == Network && cert.Expires > Clock() && CryptographicOperations.FixedTimeEquals(cert.DirectoryKey, key);
        }
        if (Memory is null) return false;
        if (Memory.Get(Network, address) is { } known) return CryptographicOperations.FixedTimeEquals(known, key);
        Memory.Remember(Network, address, key);
        return true;
    }

    /// <summary>For trust on first use: a key is already remembered for this address, so the legacy protocol is off.</summary>
    public bool Remembers(string address) => Memory?.Get(Network, address) is not null;

    private static bool Contains(IReadOnlyList<byte[]> keys, byte[] key)
    {
        var match = false;
        foreach (var k in keys) match |= k.Length == key.Length && CryptographicOperations.FixedTimeEquals(k, key);
        return match;
    }
}

/// <summary>Directory keys seen first at each (network, address), for networks that pin nothing (directory-keys.json).</summary>
public sealed class DirectoryKeyMemory(string path)
{
    public const string FileName = "directory-keys.json";
    private readonly Lock _gate = new();
    private Dictionary<string, byte[]>? _keys;

    public byte[]? Get(string network, string address)
    {
        lock (_gate) return Load().TryGetValue(network + "|" + address, out var key) ? key : null;
    }

    public void Remember(string network, string address, byte[] key)
    {
        lock (_gate)
        {
            var keys = Load();
            if (!keys.TryAdd(network + "|" + address, (byte[])key.Clone())) return;
            Save(keys);
        }
    }

    /// <summary>Forget a network's keys (the user removed and re-added it after its owner changed the directory key).</summary>
    public void Forget(string network)
    {
        lock (_gate)
        {
            var keys = Load();
            if (keys.Keys.Where(k => k.StartsWith(network + "|", StringComparison.Ordinal)).ToList() is not { Count: > 0 } gone) return;
            foreach (var k in gone) keys.Remove(k);
            Save(keys);
        }
    }

    private Dictionary<string, byte[]> Load()
    {
        if (_keys is not null) return _keys;
        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
                foreach (var (k, v) in o)
                    if (v?.GetValue<string>() is { } b64 && Convert.FromBase64String(b64) is { Length: DirectoryChannel.KeyLength } key) _keys[k] = key;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        return _keys;
    }

    private void Save(Dictionary<string, byte[]> keys)
    {
        try
        {
            var o = new JsonObject();
            foreach (var (k, v) in keys) o[k] = Convert.ToBase64String(v);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, o.ToJsonString());
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
