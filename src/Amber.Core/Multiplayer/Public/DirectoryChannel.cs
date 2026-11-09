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
/// One request per connection, Noise_XXpsk0_25519_ChaChaPoly_SHA256 (the same implementation the knock and the gate use):
/// <c>-> psk, e</c>, <c>&lt;- e, ee, s, es</c>, then Jay compares the directory's static key with the pinned keys and only then
/// sends <c>-> s, se</c> with the JSON request as payload. The reply is one ChaCha20-Poly1305 record under the split keys.
/// Jay's static key in the handshake is a fresh throwaway per connection, so the directory learns nothing new about who asks.
/// The psk comes from the network's EasyTier secret, which is public for public networks: it only keeps handshakes of
/// different networks apart. The authentication comes from the pinned static key.
/// </para>
/// <para>
/// On the wire every message is a 2-byte big-endian length and the bytes. A legacy request is a JSON line starting with
/// '{' (0x7B); a Noise first message is 48 bytes, so its first byte is 0x00. The directory tells them apart by that byte.
/// </para>
/// </summary>
public static class DirectoryChannel
{
    private static readonly byte[] Prologue = "jay-directory-v1"u8.ToArray();
    private const int MaxMessage = RoomDirectoryProtocol.MaxLine + 256;

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
    public static async Task<JsonObject> CallAsync(Stream stream, IReadOnlyList<byte[]> pinned, string networkSecret, JsonObject request, CancellationToken ct)
    {
        if (pinned.Count == 0) throw new ArgumentException("No directory keys to check against.", nameof(pinned));
        var payload = Encoding.UTF8.GetBytes(request.ToJsonString());
        if (payload.Length > RoomDirectoryProtocol.MaxLine) throw new InvalidDataException(L.T("core.mp.public.too_large"));
        var psk = Psk(networkSecret);
        using var throwaway = X25519KeyPair.Generate();
        using var hs = new NoiseHandshake(NoisePattern.XXpsk0, initiator: true, Prologue, psk, throwaway);
        try
        {
            await WriteFrameAsync(stream, hs.WriteMessage([]), ct);
            hs.ReadMessage(await ReadFrameAsync(stream, ct));
            var key = hs.RemoteStaticPublicKey ?? throw new CryptographicException(L.T("core.mp.public.bad_reply"));
            if (!IsPinned(key, pinned)) throw new DirectoryImpostorException();
            await WriteFrameAsync(stream, hs.WriteMessage(payload), ct);
            var (send, receive) = hs.Split();
            using (send)
            using (receive)
            {
                var frame = await ReadFrameAsync(stream, ct);
                var reply = receive.DecryptWithAd(Prologue, frame);
                try
                {
                    return JsonNode.Parse(reply) as JsonObject ?? throw new InvalidDataException(L.T("core.mp.public.bad_reply"));
                }
                catch (JsonException) { throw new InvalidDataException(L.T("core.mp.public.bad_reply")); }
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

    /// <summary>
    /// The directory's side, after it read <paramref name="firstByte"/> and found it isn't a legacy request.
    /// Returns the request and a function that sends the reply, or null when the handshake fails (the caller just closes).
    /// </summary>
    public static async Task<(JsonObject Request, Func<JsonObject, CancellationToken, Task> Reply)?> AcceptAsync(
        Stream stream, byte firstByte, X25519KeyPair identity, string networkSecret, CancellationToken ct)
    {
        var psk = Psk(networkSecret);
        var hs = new NoiseHandshake(NoisePattern.XXpsk0, initiator: false, Prologue, psk, identity);
        try
        {
            hs.ReadMessage(await ReadFrameAsync(stream, ct, firstByte));
            await WriteFrameAsync(stream, hs.WriteMessage([]), ct);
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
                    var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
                    if (bytes.Length > RoomDirectoryProtocol.MaxLine) throw new InvalidDataException(L.T("core.mp.public.too_large"));
                    await WriteFrameAsync(stream, send.EncryptWithAd(Prologue, bytes), token);
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

/// <summary>
/// The machine at the directory address doesn't hold any of the network's directory keys. Nothing was sent to it.
/// An <see cref="IOException"/> so callers treat that address like an unreachable directory and try the next one.
/// </summary>
public sealed class DirectoryImpostorException() : IOException(L.T("core.mp.public.directory_impostor"));
