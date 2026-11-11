using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Gate;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// A relay share code for relays that go by an IP, with no IP and no passphrase in it: <c>JAYNR1-</c> + Crockford Base32
/// of [1 version][1 network id length][network id][16 locator id][16 signer hash] + 4-byte checksum. The relays themselves
/// are in a signed record (<see cref="SignedRecord"/>) on that public network's directories, put by the sharer's Jay while
/// it is connected there (renewed on every connection, each put lasts <see cref="SignedRecord.MaxLifetime"/>).
/// The reader accepts only a record signed by the key whose hash is in the code. When the sharer isn't on a public
/// network, Jay falls back to the sealed <see cref="RelayNodes.SealedPrefix"/> code with a passphrase.
/// </summary>
public static class RelayRecord
{
    public const string Prefix = "JAYNR1-";
    private const byte Version = 1;
    private const int ChecksumLength = 4;

    /// <summary>The sharer's record key and locator id, kept in one private file (created on first use).</summary>
    public sealed record Identity(RecordSigner Signer, byte[] Id)
    {
        public GateLocator Locator(string networkId) => new(networkId, Id, SignerHash(Signer.PublicKey));
    }

    public static byte[] SignerHash(ReadOnlySpan<byte> signerPublicKey) =>
        SHA256.HashData([.. "jay-relay-record-signer-v1"u8, .. signerPublicKey])[..GateLocator.SignerHashLength];

    public static Identity LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length >= 2)
            {
                try
                {
                    var seed = Convert.FromBase64String(lines[0].Trim());
                    var id = Convert.FromBase64String(lines[1].Trim());
                    if (seed.Length == 32 && id.Length == GateLocator.IdLength) return new Identity(new RecordSigner(seed), id);
                }
                catch (FormatException) { }
            }
        }
        var signer = new RecordSigner();
        var newId = RandomNumberGenerator.GetBytes(GateLocator.IdLength);
        PrivateFile.WriteAllText(path, Convert.ToBase64String(signer.ExportPrivateKey()) + "\n" + Convert.ToBase64String(newId) + "\n");
        return new Identity(signer, newId);
    }

    public static string Encode(GateLocator locator)
    {
        if (!NetworkString.IsValidId(locator.NetworkId)) throw new ArgumentException(L.T("core.mp.network.bad_id"), nameof(locator));
        var network = Encoding.ASCII.GetBytes(locator.NetworkId);
        byte[] body = [Version, (byte)network.Length, .. network, .. locator.Id, .. locator.SignerHash];
        return Prefix + GateInvite.Base32Encode([.. body, .. Checksum(body)]);
    }

    public static bool IsCode(string? text) => text?.Contains(Prefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The locator in some pasted text; null when there is none or it was altered.</summary>
    public static GateLocator? TryParse(string? text)
    {
        if (text is null) return null;
        var start = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var chars = new StringBuilder();
        foreach (var c in text.AsSpan(start + Prefix.Length))
        {
            if (char.IsWhiteSpace(c) || c == '-') continue;
            if (!char.IsAsciiLetterOrDigit(c)) break;
            chars.Append(c);
        }
        var raw = GateInvite.Base32Decode(chars.ToString());
        if (raw is null || raw.Length < 2 + GateLocator.IdLength + GateLocator.SignerHashLength + ChecksumLength) return null;
        var body = raw.AsSpan(0, raw.Length - ChecksumLength);
        if (!CryptographicOperations.FixedTimeEquals(Checksum(body), raw.AsSpan(raw.Length - ChecksumLength)) || body[0] != Version) return null;
        int length = body[1];
        if (body.Length != 2 + length + GateLocator.IdLength + GateLocator.SignerHashLength) return null;
        var network = Encoding.ASCII.GetString(body.Slice(2, length));
        if (!NetworkString.IsValidId(network)) return null;
        var p = 2 + length;
        return new GateLocator(network, body.Slice(p, GateLocator.IdLength).ToArray(), body.Slice(p + GateLocator.IdLength, GateLocator.SignerHashLength).ToArray());
    }

    /// <summary>The record listing these relays (and an optional name), sealed under the locator id.</summary>
    public static SignedRecord Make(Identity identity, string? name, IEnumerable<string> urls, DateTimeOffset now)
    {
        var list = urls.Select(u => u.Trim()).Where(RelayNodes.IsValid).Distinct().ToList();
        if (list.Count == 0) throw new ArgumentException(L.T("core.mp.nodes.empty"), nameof(urls));
        var json = new JsonObject { ["v"] = 1, ["peers"] = new JsonArray([.. list.Select(u => (JsonNode?)u)]) };
        if (!string.IsNullOrWhiteSpace(name)) json["name"] = name.Trim();
        return identity.Signer.Sign(SignedRecord.Lookup(identity.Id), SignedRecord.Seal(identity.Id, Encoding.UTF8.GetBytes(json.ToJsonString())),
            SignedRecord.MaxLifetime, now);
    }

    /// <summary>Looks the relays up. Throws <see cref="IOException"/> with a reason when they aren't there or the record is damaged.</summary>
    public static async Task<(string? Name, List<string> Urls)> ResolveAsync(RoomDirectoryClient directories, GateLocator locator, CancellationToken ct)
    {
        var record = await directories.GetRecordAsync(SignedRecord.Lookup(locator.Id),
            key => CryptographicOperations.FixedTimeEquals(SignerHash(key), locator.SignerHash), ct)
            ?? throw new IOException(L.T("core.mp.nodes.record_not_found"));
        try
        {
            var json = JsonNode.Parse(SignedRecord.Open(locator.Id, record.Blob)) as JsonObject ?? throw new JsonException();
            var urls = (json["peers"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().Where(RelayNodes.IsValid).Distinct().ToList();
            if (urls.Count == 0) throw new JsonException();
            return (json["name"]?.GetValue<string>(), urls);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or InvalidOperationException or FormatException)
        {
            throw new IOException(L.T("core.mp.nodes.record_damaged"));
        }
    }

    private static byte[] Checksum(ReadOnlySpan<byte> body) => SHA256.HashData([.. "jay-relay-record-code-v1"u8, .. body])[..ChecksumLength];
}
