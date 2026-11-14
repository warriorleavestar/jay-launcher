using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// Where a Jay server with no domain can be found without putting its IP in the invite: a <see cref="GateInvite"/> carries
/// this locator instead of a host, and the address lives in a signed record on that public network's directories.
/// </summary>
/// <param name="NetworkId">The public network whose directories keep the record (the network's id, e.g. "jay").</param>
/// <param name="Id">16 random bytes per server: the record's lookup and seal key come from it, so a directory sees neither.</param>
/// <param name="SignerHash">Pins the server's record key (<see cref="GateRecord.SignerHash"/>): a directory can withhold the record but not replace it.</param>
public sealed record GateLocator(string NetworkId, byte[] Id, byte[] SignerHash)
{
    public const int IdLength = 16;
    public const int SignerHashLength = 16;
}

/// <summary>One way into the gate, as the record lists it.</summary>
public sealed record GateRecordEntry(string Host, int Port, GateEntryRotation? Rotation);

/// <summary>
/// The gate's signed record (<see cref="SignedRecord"/>, ops <c>rec.put</c> / <c>rec.get</c>): the entries players connect
/// to (the gate itself, or its relays in privacy-plus mode), sealed under the locator id. The gate puts it again well
/// before it expires (<see cref="Renew"/>); a player resolves it once, at enrollment.
/// <para>Blob plaintext: [1 version][1 count] then per entry [1 host length][host][2 port][1 rotation flag][rule + seed].</para>
/// </summary>
public static class GateRecord
{
    /// <summary>How long one put lasts. The gate renews every <see cref="Renew"/>, so a few missed renewals are fine.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(3);
    public static readonly TimeSpan Renew = TimeSpan.FromHours(12);
    public const int MaxEntries = 16;
    private const byte Version = 1;

    public static byte[] SignerHash(ReadOnlySpan<byte> signerPublicKey) =>
        SHA256.HashData([.. "jay-gate-record-signer-v1"u8, .. signerPublicKey])[..GateLocator.SignerHashLength];

    public static byte[] Encode(IReadOnlyList<GateRecordEntry> entries)
    {
        if (entries.Count is 0 or > MaxEntries) throw new ArgumentException(L.T("core.gate.record.no_entries"), nameof(entries));
        var body = new List<byte> { Version, (byte)entries.Count };
        foreach (var e in entries)
        {
            if (!GateInvite.IsValidHost(e.Host) || e.Port is < 1 or > 65535) throw new ArgumentException(L.T("core.gate.invite.badhost"), nameof(entries));
            var host = Encoding.ASCII.GetBytes(e.Host);
            body.Add((byte)host.Length);
            body.AddRange(host);
            body.Add((byte)(e.Port >> 8));
            body.Add((byte)e.Port);
            body.Add(e.Rotation is null ? (byte)0 : (byte)1);
            if (e.Rotation is { } r)
            {
                r.Rotation.Write(body);
                body.AddRange(r.Seed);
            }
        }
        return [.. body];
    }

    /// <summary>The entries in a record's plaintext; null when it is malformed.</summary>
    public static IReadOnlyList<GateRecordEntry>? Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length < 2 || bytes[0] != Version || bytes[1] is 0 or > MaxEntries) return null;
            var entries = new List<GateRecordEntry>();
            var p = 2;
            for (var i = 0; i < bytes[1]; i++)
            {
                int hostLength = bytes[p++];
                var host = Encoding.ASCII.GetString(bytes.Slice(p, hostLength)); p += hostLength;
                int port = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(p, 2)); p += 2;
                GateEntryRotation? rotation = null;
                if (bytes[p++] == 1)
                {
                    var rule = GateRotation.Read(bytes[p..]) ?? throw new FormatException();
                    p += GateRotation.EncodedLength;
                    rotation = new GateEntryRotation(rule, bytes.Slice(p, GateRotation.SeedLength).ToArray());
                    p += GateRotation.SeedLength;
                }
                if (!GateInvite.IsValidHost(host) || port is < 1 or > 65535) return null;
                entries.Add(new GateRecordEntry(host, port, rotation));
            }
            return p == bytes.Length ? entries : null;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>The record for these entries, sealed under the locator id and signed with the gate's record key.</summary>
    public static SignedRecord Make(RecordSigner signer, byte[] id, IReadOnlyList<GateRecordEntry> entries, DateTimeOffset now)
    {
        var plain = Encode(entries);
        return signer.Sign(SignedRecord.Lookup(id), SignedRecord.Seal(id, plain), Lifetime, now);
    }

    /// <summary>
    /// Player side: looks the locator up on the directories and returns the invite with a real entry to connect to (a random
    /// one when the record lists several, as a plain invite would). Throws <see cref="IOException"/> with a reason.
    /// </summary>
    public static async Task<GateInvite> ResolveAsync(RoomDirectoryClient directories, GateInvite invite, CancellationToken ct)
    {
        if (invite.Locator is not { } locator) return invite;
        var record = await directories.GetRecordAsync(SignedRecord.Lookup(locator.Id),
            key => CryptographicOperations.FixedTimeEquals(SignerHash(key), locator.SignerHash), ct)
            ?? throw new IOException(L.T("core.gate.record.not_found"));
        IReadOnlyList<GateRecordEntry>? entries;
        try
        {
            var plain = SignedRecord.Open(locator.Id, record.Blob);
            entries = Decode(plain);
        }
        catch (CryptographicException) { entries = null; }
        if (entries is null) throw new IOException(L.T("core.gate.record.damaged"));
        var entry = entries[RandomNumberGenerator.GetInt32(entries.Count)];
        return invite with { Host = entry.Host, Port = entry.Port, Rotation = entry.Rotation, Locator = null };
    }

    /// <summary>
    /// Joins <paramref name="profile"/> just long enough to look the invite up (its own EasyTier, closed afterwards).
    /// </summary>
    /// <param name="keyMemory">
    /// Where directory keys of networks that pin none are remembered (trust on first use): the app's
    /// <see cref="DirectoryKeyMemory"/>, so the lookup checks the same keys as every other directory call.
    /// </param>
    public static async Task<GateInvite> ResolveOnNetworkAsync(GateInvite invite, PublicNetworkProfile profile, EasyTierInstaller installer, string workDir,
        CancellationToken ct, DirectoryKeyMemory? keyMemory = null)
    {
        if (invite.Locator is null) return invite;
        await installer.EnsureAsync(null, ct);
        using var session = PublicNetworkSession.Start(installer, new PublicNetworkOptions(profile, EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort()), workDir);
        await session.WaitForDirectoryAsync(TimeSpan.FromSeconds(60), ct);
        return await ResolveAsync(new RoomDirectoryClient(session) { KeyMemory = keyMemory }, invite, ct);
    }
}
