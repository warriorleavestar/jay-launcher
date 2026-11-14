using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// A long-lived record on the room directories (ops <c>rec.put</c> / <c>rec.get</c>): an opaque blob under a 32-byte
/// lookup, signed with Ed25519. The first signer owns a lookup; only it can replace the record (higher <see cref="Seq"/>)
/// until it expires (at most <see cref="MaxLifetime"/>, renewed by putting it again).
/// <para>
/// The directory checks the signature and ownership but never interprets the blob. Codes carry an id; the lookup and
/// the seal key are derived from it (<see cref="Lookup"/>, <see cref="Seal"/>), so the directory sees neither the id
/// nor what the record says. The reader checks <see cref="Signer"/> against what its code expects (for example a hash
/// of the signer key), so a directory can withhold a record but never substitute one.
/// </para>
/// </summary>
public sealed record SignedRecord(byte[] LookupKey, byte[] Signer, long Seq, DateTimeOffset Expires, byte[] Blob, byte[] Signature)
{
    public const int LookupLength = 32;
    public const int SignerLength = 32;
    public const int SignatureLength = 64;

    /// <summary>Largest blob the directory keeps.</summary>
    public const int MaxBlob = 4096;

    /// <summary>Largest plaintext that still fits in <see cref="MaxBlob"/> after <see cref="Seal"/>.</summary>
    public const int MaxPlaintext = MaxBlob - NonceLength - TagLength;

    /// <summary>Longest a record may live without being put again.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7);

    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly byte[] Domain = "jay-rec-v1"u8.ToArray();
    private static readonly byte[] LookupInfo = "jay-rec-lookup-v1"u8.ToArray();
    private static readonly byte[] SealInfo = "jay-rec-seal-v1"u8.ToArray();

    /// <summary>The directory lookup for a code's <paramref name="id"/> (at least 16 bytes).</summary>
    public static byte[] Lookup(ReadOnlySpan<byte> id)
    {
        CheckId(id);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, id.ToArray(), LookupLength, [], LookupInfo);
    }

    /// <summary>Seals <paramref name="plaintext"/> so only holders of <paramref name="id"/> can read it. The lookup is bound as associated data.</summary>
    public static byte[] Seal(ReadOnlySpan<byte> id, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > MaxPlaintext) throw new ArgumentException("Record too large", nameof(plaintext));
        var lookup = Lookup(id);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, id.ToArray(), 32, [], SealInfo);
        try
        {
            using var aead = new ChaCha20Poly1305(key);
            var output = new byte[NonceLength + plaintext.Length + TagLength];
            RandomNumberGenerator.Fill(output.AsSpan(0, NonceLength));
            aead.Encrypt(output.AsSpan(0, NonceLength), plaintext, output.AsSpan(NonceLength, plaintext.Length), output.AsSpan(NonceLength + plaintext.Length), lookup);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Opens a blob sealed by <see cref="Seal"/>. Throws <see cref="CryptographicException"/> when it was altered or isn't for this id.</summary>
    public static byte[] Open(ReadOnlySpan<byte> id, ReadOnlySpan<byte> blob)
    {
        if (blob.Length < NonceLength + TagLength || blob.Length > MaxBlob) throw new CryptographicException("Record size");
        var lookup = Lookup(id);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, id.ToArray(), 32, [], SealInfo);
        try
        {
            using var aead = new ChaCha20Poly1305(key);
            var length = blob.Length - NonceLength - TagLength;
            var plaintext = new byte[length];
            aead.Decrypt(blob[..NonceLength], blob.Slice(NonceLength, length), blob[(NonceLength + length)..], plaintext, lookup);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The exact bytes the signature covers.</summary>
    public byte[] SignedBytes()
    {
        var output = new byte[Domain.Length + LookupKey.Length + Signer.Length + 8 + 8 + 4 + Blob.Length];
        var span = output.AsSpan();
        Domain.CopyTo(span);
        span = span[Domain.Length..];
        LookupKey.CopyTo(span);
        span = span[LookupKey.Length..];
        Signer.CopyTo(span);
        span = span[Signer.Length..];
        BinaryPrimitives.WriteInt64BigEndian(span, Seq);
        BinaryPrimitives.WriteInt64BigEndian(span[8..], Expires.ToUnixTimeSeconds());
        BinaryPrimitives.WriteInt32BigEndian(span[16..], Blob.Length);
        Blob.CopyTo(span[20..]);
        return output;
    }

    /// <summary>Sizes are right and the signature verifies (says nothing about expiry or who the signer should be).</summary>
    public bool IsWellFormed() =>
        LookupKey.Length == LookupLength && Signer.Length == SignerLength && Signature.Length == SignatureLength
        && Blob.Length is > 0 and <= MaxBlob && Seq > 0 && RoomSigner.Verify(Signer, SignedBytes(), Signature);

    public JsonObject ToJson() => new()
    {
        ["lookup"] = Convert.ToHexStringLower(LookupKey),
        ["signer"] = Convert.ToBase64String(Signer),
        ["seq"] = Seq,
        ["expires"] = Expires.ToUnixTimeSeconds(),
        ["blob"] = Convert.ToBase64String(Blob),
        ["sig"] = Convert.ToBase64String(Signature),
    };

    /// <summary>Parses and checks <see cref="IsWellFormed"/>; null for anything malformed or badly signed.</summary>
    public static SignedRecord? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        try
        {
            var lookup = o["lookup"]?.GetValue<string>();
            var blob = o["blob"]?.GetValue<string>();
            if (lookup is not { Length: LookupLength * 2 } || blob is null || blob.Length > (MaxBlob + 2) / 3 * 4) return null;
            var record = new SignedRecord(
                Convert.FromHexString(lookup),
                Convert.FromBase64String(o["signer"]?.GetValue<string>() ?? ""),
                o["seq"]?.GetValue<long>() ?? 0,
                DateTimeOffset.FromUnixTimeSeconds(o["expires"]?.GetValue<long>() ?? 0),
                Convert.FromBase64String(blob),
                Convert.FromBase64String(o["sig"]?.GetValue<string>() ?? ""));
            return record.IsWellFormed() ? record : null;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException) { return null; }
    }

    private static void CheckId(ReadOnlySpan<byte> id)
    {
        if (id.Length < 16) throw new ArgumentException("Record id too short", nameof(id));
    }
}

/// <summary>An Ed25519 key that signs <see cref="SignedRecord"/>s (for example a gate's record key, kept next to its X25519 key).</summary>
public sealed class RecordSigner
{
    private readonly Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters _key;
    private long _lastSeq;

    public RecordSigner() : this(RandomNumberGenerator.GetBytes(32)) { }

    /// <summary>From a 32-byte private seed (as saved by <see cref="ExportPrivateKey"/>).</summary>
    public RecordSigner(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32) throw new ArgumentException("Ed25519 seed length", nameof(seed));
        _key = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(seed.ToArray());
        PublicKey = _key.GeneratePublicKey().GetEncoded();
    }

    public byte[] PublicKey { get; }

    public byte[] ExportPrivateKey() => _key.GetEncoded();

    /// <summary>Signs <paramref name="blob"/> under <paramref name="lookup"/>, valid for <paramref name="lifetime"/> (capped at <see cref="SignedRecord.MaxLifetime"/>).</summary>
    public SignedRecord Sign(byte[] lookup, byte[] blob, TimeSpan lifetime, DateTimeOffset now)
    {
        if (lookup.Length != SignedRecord.LookupLength) throw new ArgumentException("Lookup length", nameof(lookup));
        if (blob.Length is 0 or > SignedRecord.MaxBlob) throw new ArgumentException("Blob size", nameof(blob));
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (lifetime > SignedRecord.MaxLifetime) lifetime = SignedRecord.MaxLifetime;
        var seq = Math.Max(now.ToUnixTimeMilliseconds(), Interlocked.Read(ref _lastSeq) + 1);
        Interlocked.Exchange(ref _lastSeq, seq);
        var unsigned = new SignedRecord(lookup, PublicKey, seq, DateTimeOffset.FromUnixTimeSeconds((now + lifetime).ToUnixTimeSeconds()), blob, []);
        var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        signer.Init(true, _key);
        var message = unsigned.SignedBytes();
        signer.BlockUpdate(message, 0, message.Length);
        return unsigned with { Signature = signer.GenerateSignature() };
    }
}

/// <summary>
/// The highest <see cref="SignedRecord.Seq"/> this computer has accepted for each lookup and signer, kept in a small file in
/// the data folder. Passed as the floor to <see cref="RoomDirectoryClient.GetRecordAsync"/>, so once a newer record was
/// seen, an older one that hasn't expired yet (put back after a directory restarted, or served by a directory on its own) is
/// not taken again. Kept per signer: a record some other key signed under the same lookup (only a code naming that key
/// takes it) never raises the floor for the signer another code expects. Holds the most recent <see cref="MaxEntries"/>.
/// </summary>
public sealed class RecordSeqMemory(string path)
{
    public const string FileName = "record-seqs.json";
    public const int MaxEntries = 512;
    private readonly Lock _gate = new();
    private Dictionary<string, (long Seq, long At)>? _seen;

    /// <summary>The highest seq accepted for <paramref name="lookup"/> signed by <paramref name="signer"/>, or 0 when none was.</summary>
    public long Floor(byte[] lookup, byte[] signer)
    {
        lock (_gate) return Load().TryGetValue(Key(lookup, signer), out var seen) ? seen.Seq : 0;
    }

    /// <summary>
    /// Notes that a record with <paramref name="seq"/> signed by <paramref name="signer"/> was accepted for
    /// <paramref name="lookup"/> (keeps the highest).
    /// </summary>
    public void Note(byte[] lookup, byte[] signer, long seq)
    {
        lock (_gate)
        {
            var seen = Load();
            var key = Key(lookup, signer);
            if (seen.TryGetValue(key, out var had) && had.Seq >= seq) return;
            // When it was noted, for keeping the most recent: always later than the last one noted.
            var at = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), seen.Count == 0 ? 0 : seen.Values.Max(v => v.At) + 1);
            seen[key] = (seq, at);
            foreach (var old in seen.OrderBy(p => p.Value.At).Take(Math.Max(0, seen.Count - MaxEntries)).Select(p => p.Key).ToList()) seen.Remove(old);
            Save(seen);
        }
    }

    private static string Key(byte[] lookup, byte[] signer) => Convert.ToHexStringLower(lookup) + Convert.ToHexStringLower(signer);

    private Dictionary<string, (long Seq, long At)> Load()
    {
        if (_seen is not null) return _seen;
        _seen = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
                foreach (var (k, v) in o)
                    if (k.Length == (SignedRecord.LookupLength + SignedRecord.SignerLength) * 2 && v is JsonObject e && e["seq"] is JsonValue s && s.TryGetValue<long>(out var seq)
                        && e["at"] is JsonValue a && a.TryGetValue<long>(out var at))
                        _seen[k] = (seq, at);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
        return _seen;
    }

    private void Save(Dictionary<string, (long Seq, long At)> seen)
    {
        try
        {
            var o = new JsonObject();
            foreach (var (k, v) in seen) o[k] = new JsonObject { ["seq"] = v.Seq, ["at"] = v.At };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, o.ToJsonString());
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
