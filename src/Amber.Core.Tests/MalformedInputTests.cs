using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Gate;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// A hostile peer or a pasted string can be anything. Every code parser must answer "not a code" instead of
/// throwing, and every wire reader must fail with one of the expected errors (bad data, cut stream) instead of
/// crashing or hanging. (Readers cap their length fields; see KnockProtocol.MaxFrame, McDisguise.MaxPacket, GateProtocol.MaxRecord.)
/// </summary>
public class MalformedInputTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static IEnumerable<string> ValidCodes()
    {
        yield return JayInvite.Create(RandomNumberGenerator.GetBytes(16), TunnelSettings.Default).ToString();
        yield return JayInvite.Create(RandomNumberGenerator.GetBytes(16), new TunnelSettings(TransportMode.High, true)).ToString();
        yield return RoomCode.Create(25565).ToString();
        var host = new DirectAddresses("0123456789abcdef", NatKind.Cone, [new(PathKind.Punch, IPEndPoint.Parse("203.0.113.5:51234"))]);
        using var offer = SealedDirect.Seal(host, DirectTestKeys.Invite, Now);
        yield return offer.Code;
        using var key = X25519KeyPair.Generate();
        yield return ContactCode.Format(key.PublicKey);
    }

    /// <summary>Random edits of real codes, plus odd strings that often break parsers.</summary>
    private static IEnumerable<string> Mutants(int seed, int perCode)
    {
        var rnd = new Random(seed);
        string[] odd = ["", " ", "-", "JAY2-", "JAYDS1-", "JAYC1-", "JAYF1-", "JAYNET", "AMB-", "JAYS2-", "\0", "‮", "😀😀",
            new string('A', 100_000), "JAY2-" + new string('=', 5000), "JAY2-" + new string('9', 70_000)];
        foreach (var s in odd) yield return s;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_=+/:.@[] \t\né中\U0001F600\0";
        foreach (var code in ValidCodes())
        {
            for (var i = 0; i < perCode; i++)
            {
                var chars = new StringBuilder(code);
                switch (rnd.Next(5))
                {
                    case 0: chars[rnd.Next(chars.Length)] = alphabet[rnd.Next(alphabet.Length)]; break;
                    case 1: chars.Length = rnd.Next(chars.Length); break;
                    case 2: chars.Insert(rnd.Next(chars.Length), alphabet[rnd.Next(alphabet.Length)]); break;
                    case 3: chars.Remove(rnd.Next(chars.Length), 1); break;
                    default: chars.Append(chars.ToString()); break;
                }
                yield return chars.ToString();
            }
        }
    }

    [Fact]
    public void CodeParsersNeverThrowOnGarbage()
    {
        var parsers = new (string Name, Action<string> Parse)[]
        {
            ("PastedCode.Detect", s => PastedCode.Detect(s)),
            ("JayInvite.TryParse", s => JayInvite.TryParse(s, out _)),
            ("RoomCode.TryParse", s => RoomCode.TryParse(s, out _)),
            ("GateInvite.TryParse", s => GateInvite.TryParse(s, out _)),
            ("GateInvite.TryOpen", s => GateInvite.TryOpen(s, "1234 5678", out _)),
            ("GateRelayCode.TryParse", s => GateRelayCode.TryParse(s)),
            ("GateEndpoint.TryParse", s => GateEndpoint.TryParse(s)),
            ("RelayRecord.TryParse", s => RelayRecord.TryParse(s)),
            ("FingerprintCode.TryParse", s => FingerprintCode.TryParse(s, out _)),
            ("ContactCode.TryParse", s => ContactCode.TryParse(s, out _)),
            ("JoinTarget.TryParse", s => JoinTarget.TryParse(s, out _, out _, out _)),
            ("NetworkString.TryParse", s => NetworkString.TryParse(s)),
        };
        var failures = new List<string>();
        foreach (var text in Mutants(seed: 42, perCode: 60))
        {
            foreach (var (name, parse) in parsers)
            {
                var watch = Stopwatch.StartNew();
                try { parse(text); }
                catch (Exception e) { failures.Add($"{name} threw {e.GetType().Name} on {Show(text)}"); }
                if (watch.Elapsed > TimeSpan.FromSeconds(2)) failures.Add($"{name} took {watch.Elapsed.TotalSeconds:F1}s on {Show(text)}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct().Take(20)));
    }

    [Fact]
    public void SealedOfferRejectsGarbageWithAClearError()
    {
        foreach (var text in Mutants(seed: 7, perCode: 40))
        {
            try
            {
                using var opened = SealedDirect.Open(text, "1111 2222 3333 4444", Now);
            }
            catch (Exception e) when (e is InvalidDataException or FormatException or CryptographicException) { }
        }
    }

    // ---- Wire readers ----

    private static bool Expected(Exception e) => e is CryptographicException or EndOfStreamException or InvalidDataException
        or IOException or FormatException or OperationCanceledException or AuthenticationTagMismatchException;

    private static IEnumerable<byte[]> HostileStreams(int seed)
    {
        var rnd = new Random(seed);
        yield return [];
        yield return [0xFF, 0xFF];                                   // largest 16-bit length, then nothing
        yield return [0xFF, 0xFF, 0xFF, 0xFF, 0x0F];                 // largest VarInt length
        yield return [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];           // VarInt that never ends
        yield return [0x7F, 0xFF, 0xFF, 0xFF];                       // 2 GB big-endian length
        yield return [0x00, 0x00];                                   // zero length
        for (var i = 0; i < 40; i++)
        {
            var bytes = new byte[rnd.Next(1, 3000)];
            rnd.NextBytes(bytes);
            yield return bytes;
        }
    }

    private static async Task<string?> Feed(string name, byte[] input, Func<Stream, CancellationToken, Task> read)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await read(new Peer(input), cts.Token);
        }
        catch (Exception e) when (Expected(e)) { }
        catch (Exception e) { return $"{name} threw {e.GetType().Name} on {input.Length} bytes starting {Convert.ToHexString(input.AsSpan(0, Math.Min(8, input.Length)))}: {e.Message}"; }
        return cts.IsCancellationRequested ? $"{name} hung on {input.Length} bytes" : null;
    }

    [Fact]
    public async Task WireReadersRejectHostileBytes()
    {
        var room = RoomCode.Create(25565);
        using var serverKey = X25519KeyPair.Generate();
        var readers = new (string Name, Func<Stream, CancellationToken, Task> Read)[]
        {
            ("KnockProtocol.ReadFrameAsync", (s, ct) => KnockProtocol.ReadFrameAsync(s, ct)),
            ("McDisguise.ReadLoginUuidAsync", (s, ct) => McDisguise.ReadLoginUuidAsync(s, ct)),
            ("McDisguise.ReadEncryptionRequestAsync", (s, ct) => McDisguise.ReadEncryptionRequestAsync(s, ct)),
            ("McDisguise.ReadEncryptionResponseAsync", (s, ct) => McDisguise.ReadEncryptionResponseAsync(s, ct)),
            ("Tunnel.Accept(High)", (s, ct) => TunnelHandshake.AcceptAsync(s, room, TransportMode.High, false, ct)),
            ("Tunnel.Accept(High, disguise)", (s, ct) => TunnelHandshake.AcceptAsync(s, room, TransportMode.High, true, ct)),
            ("Tunnel.Accept(Extreme)", (s, ct) => TunnelHandshake.AcceptAsync(s, room, TransportMode.Extreme, false, ct)),
            ("Tunnel.Connect(Minecraft)", (s, ct) => TunnelHandshake.ConnectAsync(s, room, TransportMode.Minecraft, false, TunnelService.Game, ct)),
            ("Gate.Accept", (s, ct) => GateProtocol.AcceptAsync(s, serverKey, RandomNumberGenerator.GetBytes(32), [("i1", RandomNumberGenerator.GetBytes(32))], ct)),
        };
        var failures = new List<string>();
        foreach (var input in HostileStreams(seed: 3))
            foreach (var (name, read) in readers)
                if (await Feed(name, input, read) is { } problem) failures.Add(problem);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>A peer that sends <paramref name="input"/> and then hangs up; whatever we send it is dropped.</summary>
    private sealed class Peer(byte[] input) : Stream
    {
        private readonly MemoryStream _in = new(input, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private static string Show(string s) => s.Length > 60 ? $"\"{s[..60]}…\" ({s.Length} chars)" : $"\"{s}\"";
}
