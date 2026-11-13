using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// The authenticated way to talk to a room directory (design: docs/security/directory-identity.md).
/// <para>
/// A directory lives at a fixed address in the public network (10.235.0.1, backups at 10.235.255.252–254). Anyone in the
/// network can take that address while the directory is offline, so the address alone proves nothing. A network that
/// lists directory keys in its signed connection string ("dirKeys") makes Jay check, before sending anything, that the
/// other end holds one of those keys.
/// </para>
/// <para>
/// One request per connection, Noise_XXhfspsk0_25519+MLKEM768_ChaChaPoly_SHA256 (the same implementation the knock and the gate use):
/// <c>-> psk, e, e1</c>, <c>&lt;- e, ee, ekem1, s, es</c>, then Jay compares the directory's static key with the pinned keys and only then
/// sends <c>-> s, se</c> with the JSON request as payload. The reply is one ChaCha20-Poly1305 record under the split keys.
/// Jay's static key in the handshake is a fresh throwaway per connection, so the directory learns nothing new about who asks.
/// The psk comes from the network's EasyTier secret, which is public for public networks: it only keeps handshakes of
/// different networks apart. The authentication comes from the pinned static key.
/// </para>
/// <para>
/// On the wire every message is a 2-byte big-endian length and the bytes. A reply is one encrypted frame holding the
/// JSON, as it always was, so every Jay version can read any reply (an old one still sees "please update"). A snapshot
/// (op <c>snap</c>, which only new versions send) can be megabytes: its reply is one encrypted frame holding the 4-byte
/// big-endian length (first byte 0, which no JSON starts with), then encrypted frames of at most
/// <see cref="ReplyChunk"/> bytes each, padded with spaces. A legacy request is a JSON line starting with
/// '{' (0x7B); a Noise first message is 1248 bytes, so its first length byte is 0x04. The directory tells them apart by that byte.
/// </para>
/// </summary>
public static class DirectoryChannel
{
    private static readonly byte[] Prologue = "jay-directory-v2"u8.ToArray();
    // Room for a request line, or for a handshake message carrying a certificate with several hybrid signatures
    // plus the ML-KEM-768 ciphertext (and its tag).
    private const int MaxMessage = (RoomDirectoryProtocol.MaxLine > DirectoryCertificate.MaxSize ? RoomDirectoryProtocol.MaxLine : DirectoryCertificate.MaxSize)
        + MlKem768.CiphertextSize + 256;
    /// <summary>
    /// The first handshake message with an empty payload: ephemeral key (32) + encrypted ML-KEM-768 encapsulation key
    /// (1184 + 16) + payload tag (16).
    /// </summary>
    private const int FirstMessageLength = X25519KeyPair.Length + MlKem768.EncapsulationKeySize + 2 * CipherState.TagLength;

    /// <summary>Plaintext bytes per reply frame.</summary>
    private const int ReplyChunk = RoomDirectoryProtocol.MaxLine;

    /// <summary>Directory keys are X25519 public keys, 32 bytes.</summary>
    public const int KeyLength = X25519KeyPair.Length;

    /// <summary>First byte of a legacy (unauthenticated) JSON request.</summary>
    public const byte LegacyFirstByte = (byte)'{';

    /// <summary>The psk for a network: HKDF of its EasyTier secret. Not a secret on public networks; domain separation only.</summary>
    public static byte[] Psk(string networkSecret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(networkSecret), 32, Prologue, "jay-directory-psk-v1"u8.ToArray());

    /// <summary>
    /// Jay's side: handshake, check the directory's key against <paramref name="pinned"/>, then send
    /// <paramref name="request"/> and return the reply. A directory holding none of the pinned keys gets nothing:
    /// the request is never written. Throws <see cref="DirectoryImpostorException"/> in that case.
    /// </summary>
    public static Task<JsonObject> CallAsync(Stream stream, IReadOnlyList<byte[]> pinned, string networkSecret, JsonObject request, CancellationToken ct)
    {
        if (pinned.Count == 0) throw new ArgumentException("No directory keys to check against.", nameof(pinned));
        return CallAsync(stream, new DirectoryTrust { Network = "", Pinned = pinned }, "", networkSecret, request, ct);
    }

    /// <summary>
    /// Jay's side with the network's full trust rules (<see cref="DirectoryTrust"/>): pinned keys, keys certified by a
    /// root, or the key remembered for <paramref name="address"/>. The directory's certificate, if it has one, is the
    /// payload of its handshake message. Throws <see cref="DirectoryImpostorException"/> before writing the request when
    /// the key isn't accepted, or <see cref="DirectoryKeyChangedException"/> when it differs from the remembered one.
    /// </summary>
    /// <param name="snapshotLength">Called with a snapshot reply's length once it is known (to set a deadline that fits it).</param>
    public static async Task<JsonObject> CallAsync(Stream stream, DirectoryTrust trust, string address, string networkSecret, JsonObject request, CancellationToken ct,
        Action<int>? snapshotLength = null, Action? sent = null)
    {
        var payload = Encoding.UTF8.GetBytes(request.ToJsonString());
        if (payload.Length > RoomDirectoryProtocol.MaxLine) throw new InvalidDataException(L.T("core.mp.public.too_large"));
        var psk = Psk(networkSecret);
        using var throwaway = X25519KeyPair.Generate();
        using var hs = new NoiseHandshake(NoisePattern.XXhfspsk0, initiator: true, Prologue, psk, throwaway);
        try
        {
            await WriteFrameAsync(stream, hs.WriteMessage([]), ct);
            var certificate = hs.ReadMessage(await ReadFrameAsync(stream, ct));
            var key = hs.RemoteStaticPublicKey ?? throw new CryptographicException(L.T("core.mp.public.bad_reply"));
            var remembered = !trust.IsStrict && trust.Remembers(address);
            if (!trust.Accepts(address, key, certificate))
                throw remembered ? new DirectoryKeyChangedException() : new DirectoryImpostorException();
            await WriteFrameAsync(stream, hs.WriteMessage(payload), ct);
            sent?.Invoke();
            var (send, receive) = hs.Split();
            using (send)
            using (receive)
            {
                var firstFrame = await ReadFrameAsync(stream, ct);
                var first = receive.DecryptWithAd(Prologue, firstFrame);
                if (first.Length != 4 || first[0] != 0) return Parse(first);
                var total = BinaryPrimitives.ReadInt32BigEndian(first);
                if (total is <= 0 or > RoomDirectoryProtocol.MaxReply) throw new InvalidDataException(L.T("core.mp.public.bad_reply"));
                snapshotLength?.Invoke(total);
                var reply = new byte[total];
                for (var at = 0; at < total;)
                {
                    var frame = await ReadFrameAsync(stream, ct);
                    var chunk = receive.DecryptWithAd(Prologue, frame);
                    if (chunk.Length == 0 || chunk.Length > total - at) throw new InvalidDataException(L.T("core.mp.public.bad_reply"));
                    chunk.CopyTo(reply, at);
                    at += chunk.Length;
                }
                return Parse(reply);
            }
        }
        catch (CryptographicException e)
        {
            throw new InvalidDataException(L.T("core.mp.public.bad_reply"), e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
    }

    private static JsonObject Parse(byte[] reply)
    {
        try
        {
            return JsonNode.Parse(reply) as JsonObject ?? throw new InvalidDataException(L.T("core.mp.public.bad_reply"));
        }
        catch (JsonException) { throw new InvalidDataException(L.T("core.mp.public.bad_reply")); }
    }

    /// <summary>
    /// The directory's side, after it read <paramref name="firstByte"/> and found it isn't a legacy request.
    /// Returns the request and a function that sends the reply, or null when the handshake fails (the caller just closes).
    /// </summary>
    public static async Task<(JsonObject Request, DirectoryReply Reply, DirectorySnapshotReply Snapshot)?> AcceptAsync(
        Stream stream, byte firstByte, X25519KeyPair identity, string networkSecret, CancellationToken ct, byte[]? certificate = null)
    {
        var psk = Psk(networkSecret);
        var hs = new NoiseHandshake(NoisePattern.XXhfspsk0, initiator: false, Prologue, psk, identity);
        try
        {
            var first = await ReadFrameAsync(stream, ct, firstByte);
            // Anything else isn't a Jay client; skip the DH work.
            if (first.Length != FirstMessageLength) return null;
            hs.ReadMessage(first);
            // The certificate (if this directory has one) rides in the handshake message, encrypted to the client's
            // ephemeral key; clients that don't know certificates just ignore the payload.
            await WriteFrameAsync(stream, hs.WriteMessage(certificate ?? []), ct);
            var payload = hs.ReadMessage(await ReadFrameAsync(stream, ct));
            JsonObject request;
            try { request = JsonNode.Parse(payload) as JsonObject ?? throw new InvalidDataException(); }
            catch (JsonException) { return null; }
            var (send, receive) = hs.Split();
            receive.Dispose();
            return (request, async (reply, token) =>
            {
                using (send)
                {
                    // Every reply but a snapshot is one frame; a bigger one is a bug on this side, not something to send.
                    var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
                    if (bytes.Length > RoomDirectoryProtocol.MaxLine) throw new InvalidDataException(L.T("core.mp.public.too_large"));
                    await WriteFrameAsync(stream, send.EncryptWithAd(Prologue, bytes), token);
                }
            }, async (json, padTo, stall, token) =>
            {
                using (send)
                {
                    var total = padTo > 0 ? (json.Length + padTo - 1) / padTo * padTo : json.Length;
                    if (total > RoomDirectoryProtocol.MaxReply) throw new InvalidDataException(L.T("core.mp.public.too_large"));
                    var header = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(header, total);
                    await Stalled(send.EncryptWithAd(Prologue, header));
                    // Trailing spaces are JSON whitespace: the client parses the padded reply as it is.
                    var chunk = new byte[ReplyChunk];
                    for (var at = 0; at < total; at += ReplyChunk)
                    {
                        var length = Math.Min(ReplyChunk, total - at);
                        var fromJson = Math.Clamp(json.Length - at, 0, length);
                        json.AsSpan(at < json.Length ? at : 0, fromJson).CopyTo(chunk);
                        chunk.AsSpan(fromJson, length - fromJson).Fill((byte)' ');
                        await Stalled(send.EncryptWithAd(Prologue, chunk.AsSpan(0, length)));
                    }
                }

                // Each frame must go out within the stall time: a reader that stops reading is dropped.
                async Task Stalled(byte[] frame)
                {
                    // Out of time between frames (or shutting down): stop before touching a stream that may be closed.
                    token.ThrowIfCancellationRequested();
                    using var frameDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    frameDeadline.CancelAfter(stall);
                    // Not every stream honours cancellation of a pending write; closing it always ends the write.
                    var close = frameDeadline.Token.Register(stream.Dispose);
                    try { await WriteFrameAsync(stream, frame, frameDeadline.Token); }
                    finally { await close.DisposeAsync(); }
                    // The deadline may have fired just as the write finished: the stream is closed then, so stop here.
                    frameDeadline.Token.ThrowIfCancellationRequested();
                }
            });
        }
        catch (Exception e) when (e is CryptographicException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
        finally
        {
            hs.Dispose();
            CryptographicOperations.ZeroMemory(psk);
        }
    }

    /// <summary>
    /// A directory node's identity: the X25519 private key in <paramref name="path"/> (base64, one line), made on first use.
    /// The file is created readable by this user only. Its public key goes into the network string's "dirKeys".
    /// </summary>
    public static X25519KeyPair LoadOrCreateIdentity(string path)
    {
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path).Trim();
            try { return X25519KeyPair.FromPrivateKey(Convert.FromBase64String(text)); }
            catch (FormatException) { throw new InvalidDataException(path); }
        }
        var key = X25519KeyPair.Generate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new StreamWriter(path, Encoding.ASCII, options)) file.Write(Convert.ToBase64String(key.ExportPrivateKey()) + "\n");
        return key;
    }

    private static bool IsPinned(byte[] key, IReadOnlyList<byte[]> pinned)
    {
        var match = false;
        foreach (var p in pinned) match |= p.Length == key.Length && CryptographicOperations.FixedTimeEquals(p, key);
        return match;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        if (message.Length > MaxMessage) throw new InvalidDataException(L.T("core.mp.public.too_large"));
        var frame = new byte[2 + message.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)message.Length);
        message.CopyTo(frame, 2);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct, byte? firstByte = null)
    {
        var header = new byte[2];
        if (firstByte is { } b)
        {
            header[0] = b;
            if (!await SecureStream.ReadExactAsync(stream, header.AsMemory(1), ct)) throw new EndOfStreamException();
        }
        else if (!await SecureStream.ReadExactAsync(stream, header, ct)) throw new EndOfStreamException();
        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length is 0 or > MaxMessage) throw new InvalidDataException(L.T("core.mp.public.bad_reply"));
        var message = new byte[length];
        if (!await SecureStream.ReadExactAsync(stream, message, ct)) throw new EndOfStreamException();
        return message;
    }
}

/// <summary>Sends the directory's reply (one frame).</summary>
public delegate Task DirectoryReply(JsonObject reply, CancellationToken ct);

/// <summary>
/// Sends a snapshot reply (<paramref name="json"/>, UTF-8) in chunks, padded with spaces to a multiple of
/// <paramref name="padTo"/>; gives up when one chunk takes longer than <paramref name="stall"/> to go out.
/// </summary>
public delegate Task DirectorySnapshotReply(byte[] json, int padTo, TimeSpan stall, CancellationToken ct);

/// <summary>
/// The machine at the directory address doesn't hold any of the network's directory keys. Nothing was sent to it.
/// An <see cref="IOException"/> so callers treat that address like an unreachable directory and try the next one.
/// </summary>
public sealed class DirectoryImpostorException() : IOException(L.T("core.mp.public.directory_impostor"));

/// <summary>
/// A network that pins nothing: the directory at this address now has a different key from the one Jay saw first.
/// Nothing was sent to it. An <see cref="IOException"/> like <see cref="DirectoryImpostorException"/>.
/// </summary>
public sealed class DirectoryKeyChangedException() : IOException(L.T("core.mp.public.directory_changed"));
