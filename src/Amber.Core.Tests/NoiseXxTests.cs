using System.Security.Cryptography;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Conformance of the Noise layer. Production only speaks the hybrid patterns (NNhfspsk0, XXhfspsk0 with ML-KEM-768),
/// for which no published vectors exist, so the checks are layered:
/// <list type="number">
/// <item>A small test-local reference driver, written straight from the pattern tokens of the Noise and HFS specs on top of
/// the production <see cref="SymmetricState"/> and <see cref="X25519KeyPair"/>, reproduces the official cacophony XXpsk3
/// vector and a python-noiseprotocol XXpsk0 vector byte for byte. That pins the symmetric layer and the token semantics.</item>
/// <item>The same driver, fed the HFS token lists, interoperates with production in both directions and lands on the same
/// handshake hash. A token-order or mixing mistake on either side breaks that.</item>
/// <item>Negative cases: wrong psk, tampering anywhere, all-zero keys, the old classic handshakes, mismatched patterns.</item>
/// </list>
/// The class sits in the ML-KEM collection because some tests flip <c>MlKem768.ForceBouncyCastle</c>.
/// </summary>
[Collection(nameof(MlKemCollection))]
public sealed class NoiseXxTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    // cacophony vectors/cacophony.txt, protocol_name = Noise_XXpsk3_25519_ChaChaPoly_SHA256.
    private static readonly byte[] Prologue = H("4a6f686e2047616c74");
    private static readonly byte[] Psk = H("54686973206973206d7920417573747269616e20706572737065637469766521");
    private const string InitStatic = "e61ef9919cde45dd5f82166404bd08e38bceb5dfdfded0a34c8df7ed542214d1";
    private const string InitEphemeral = "893e28b9dc6ca8d611ab664754b8ceb7bac5117349a4439a6b0569da977c464a";
    private const string RespStatic = "4a3acbfdb163dec651dfa3194dece676d437029c62a408b4c5ea9114246e4893";
    private const string RespEphemeral = "bbdb4cdbd309f1a1f2e1456967fe288cadd6f712d65dc7b7793d5e63da6b375b";
    private const string HandshakeHash = "a477edf6a131bbdb54707f6ea30eab6cd935d9b560f0e5fd1f053a95a99669fb";

    private static readonly (string Payload, string Ciphertext)[] Messages =
    [
        ("4c756477696720766f6e204d69736573", "ca35def5ae56cec33dc2036731ab14896bc4c75dbb07a61f879f8e3afa4c7944c9f5ff0e8079630cb7e270c20bbf480821b77a384a645c71a2fd9b3db1c16a5f"),
        ("4d757272617920526f746862617264", "95ebc60d2b1fa672c1f46a8aa265ef51bfe38e7ccb39ec5be34069f144808843b123def17f71e6ae8e57e0e1dec5949c5f7415c6f33517398747d821a06dc23ad430aa1fd7381d46195c378a819fd574425462cbb2d4ca339e738a0b7001dc91423fbf55a99af0c6f1df21012ceb2f"),
        ("462e20412e20486179656b", "52187316111b118d4c060364f7b975dc0809b2590779aff2d63113c564f11744493384db7bf32d5ae6686df6ab06d508d2e07caaf1d6afc010b978735fc78900e71ae1d314130d042e729a"),
        ("4361726c204d656e676572", "eaedc672d4c21e0e2955758756fb98f194c4e90d5deb5b6cf30b27"),
        ("4a65616e2d426170746973746520536179", "522d543c5fe799d09a3d9da7ff54d0dc03c8af1dc7751d2ff708339d2290943e98"),
        ("457567656e2042f6686d20766f6e2042617765726b", "7d4e2c3873eef6a213b04e72f9df60a91666072d3544c5d96c34a09e2329b5030bee796741"),
    ];

    // Noise_XXpsk0 has no official vector in cacophony or snow. This one was generated with python noiseprotocol (after
    // checking it reproduces the official XXpsk3 vector above byte for byte), with the same keys, prologue, psk and payloads.
    private const string HandshakeHashPsk0 = "0793e6b2d2fed61d803e0d9de8a4b961436e896194246a784d30d1bb1fd42e54";
    private static readonly string[] CiphertextsPsk0 =
    [
        "ca35def5ae56cec33dc2036731ab14896bc4c75dbb07a61f879f8e3afa4c794444e557af4bb1c735a4948bdd9aedbb4b3db9a8aed79bc6517b05eca117a755c3",
        "95ebc60d2b1fa672c1f46a8aa265ef51bfe38e7ccb39ec5be34069f14480884309813c929a2ee793a6909502f23492a90b088e4acc71ae6ca49464e67d97fdf404b94bc46aad47491994c66e9a8832f1701cdc1f47fc2ba565695638f005ca2e7b9a569a4c241a57879a0779d8bdbd",
        "591674f2df5cc5e0a9b3ddea86bafb1219b36345c12aa4bd0526a9f727fe2e2a14efe7e0a7b7710d042006afd97e7cf58a3efe9041dba7cf07099b7c2345135e8d51254f6f742730eb7755",
        "65bc9bce988c8c9becae660cd4fdd43f1c0dfaa40a2ac2322a09ae",
        "4cc7db3b57b01e80a8f684fa6eb8d043ae32f38aa598cf8bbe474aa9ffc4dfce9b",
        "2ebc31fbd26efafc391423fd69aa09159c5c15d9327ebae0e4afaffac55baa42fd9b742f91",
    ];

    [Fact]
    public void ReferenceDriverMatchesCacophonyXxPsk3Vector() => RunVector(Ref.XXpsk3, HandshakeHash, [.. Messages.Select(m => m.Ciphertext)]);

    [Fact]
    public void ReferenceDriverMatchesXxPsk0Vector() => RunVector(Ref.XXpsk0, HandshakeHashPsk0, CiphertextsPsk0);

    private static void RunVector(Ref.Spec spec, string handshakeHash, string[] ciphertexts)
    {
        using var initStatic = X25519KeyPair.FromPrivateKey(H(InitStatic));
        using var respStatic = X25519KeyPair.FromPrivateKey(H(RespStatic));
        using var init = new Ref(spec, true, Prologue, Psk, initStatic, X25519KeyPair.FromPrivateKey(H(InitEphemeral)));
        using var resp = new Ref(spec, false, Prologue, Psk, respStatic, X25519KeyPair.FromPrivateKey(H(RespEphemeral)));

        for (var i = 0; i < 3; i++)
        {
            var (writer, reader) = i % 2 == 0 ? (init, resp) : (resp, init);
            var message = writer.Write(H(Messages[i].Payload));
            Assert.Equal(ciphertexts[i], Convert.ToHexStringLower(message));
            Assert.Equal(Messages[i].Payload, Convert.ToHexStringLower(reader.Read(message)));
        }
        Assert.Equal(handshakeHash, Convert.ToHexStringLower(init.HandshakeHash));
        Assert.Equal(handshakeHash, Convert.ToHexStringLower(resp.HandshakeHash));
        Assert.Equal(respStatic.PublicKey, init.RemoteStatic);
        Assert.Equal(initStatic.PublicKey, resp.RemoteStatic);

        // Transport messages: the responder sends first (message 4 of the vector), then they alternate.
        // Split gives (initiator → responder, responder → initiator) on both sides.
        var (i1, i2) = init.Split();
        var (r1, r2) = resp.Split();
        using (i1) using (i2) using (r1) using (r2)
        {
            for (var i = 3; i < Messages.Length; i++)
            {
                var (send, recv) = i % 2 == 0 ? (i1, r1) : (r2, i2);
                var c = send.EncryptWithAd([], H(Messages[i].Payload));
                Assert.Equal(ciphertexts[i], Convert.ToHexStringLower(c));
                Assert.Equal(Messages[i].Payload, Convert.ToHexStringLower(recv.DecryptWithAd([], c)));
            }
        }
    }

    /// <summary>Production and the reference driver agree on every hybrid pattern, whichever side each one plays.</summary>
    [Theory]
    [InlineData(NoisePattern.NNhfspsk0, true)]
    [InlineData(NoisePattern.NNhfspsk0, false)]
    [InlineData(NoisePattern.XXhfspsk0, true)]
    [InlineData(NoisePattern.XXhfspsk0, false)]
    public void ProductionInteroperatesWithReference(NoisePattern pattern, bool productionInitiates)
    {
        var xx = pattern == NoisePattern.XXhfspsk0;
        var spec = xx ? Ref.XXhfspsk0 : Ref.NNhfspsk0;
        using var a = xx ? X25519KeyPair.Generate() : null;
        using var b = xx ? X25519KeyPair.Generate() : null;
        using var prod = new NoiseHandshake(pattern, productionInitiates, Prologue, Psk, productionInitiates ? a : b);
        using var reference = new Ref(spec, !productionInitiates, Prologue, Psk, productionInitiates ? b : a);

        for (var i = 0; i < spec.Messages.Length; i++)
        {
            var payload = new byte[i + 3];
            RandomNumberGenerator.Fill(payload);
            var productionWrites = (i % 2 == 0) == productionInitiates;
            var read = productionWrites ? reference.Read(prod.WriteMessage(payload)) : prod.ReadMessage(reference.Write(payload));
            Assert.Equal(payload, read);
        }
        Assert.True(prod.IsComplete);
        Assert.Equal(reference.HandshakeHash, prod.HandshakeHash);
        if (xx) Assert.Equal(productionInitiates ? b!.PublicKey : a!.PublicKey, prod.RemoteStaticPublicKey);

        var (send, receive) = prod.Split();
        var (r1, r2) = reference.Split();
        var (refSend, refReceive) = productionInitiates ? (r2, r1) : (r1, r2);
        using (send) using (receive) using (refSend) using (refReceive)
        {
            Assert.Equal("ping"u8.ToArray(), refReceive.DecryptWithAd([], send.EncryptWithAd([], "ping"u8)));
            Assert.Equal("pong"u8.ToArray(), receive.DecryptWithAd([], refSend.EncryptWithAd([], "pong"u8)));
        }
    }

    /// <summary>
    /// Both ML-KEM implementations, in every combination of who generates the key and who encapsulates
    /// (on a machine without system ML-KEM every combination is BouncyCastle, which still exercises the path).
    /// </summary>
    [Theory]
    [InlineData(NoisePattern.NNhfspsk0, false, false)]
    [InlineData(NoisePattern.NNhfspsk0, false, true)]
    [InlineData(NoisePattern.NNhfspsk0, true, false)]
    [InlineData(NoisePattern.NNhfspsk0, true, true)]
    [InlineData(NoisePattern.XXhfspsk0, false, false)]
    [InlineData(NoisePattern.XXhfspsk0, false, true)]
    [InlineData(NoisePattern.XXhfspsk0, true, false)]
    [InlineData(NoisePattern.XXhfspsk0, true, true)]
    public void HandshakeMatrixAcrossMlKemImplementations(NoisePattern pattern, bool initiatorBouncyCastle, bool responderBouncyCastle)
    {
        try
        {
            var (init, resp) = Pair(pattern, out var keys);
            using (keys) using (init) using (resp)
            {
                MlKem768.ForceBouncyCastle = initiatorBouncyCastle;
                var m1 = init.WriteMessage("one"u8);
                MlKem768.ForceBouncyCastle = responderBouncyCastle;
                Assert.Equal("one"u8.ToArray(), resp.ReadMessage(m1));
                var m2 = resp.WriteMessage("two"u8);
                MlKem768.ForceBouncyCastle = initiatorBouncyCastle;
                Assert.Equal("two"u8.ToArray(), init.ReadMessage(m2));
                if (pattern == NoisePattern.XXhfspsk0) resp.ReadMessage(init.WriteMessage("three"u8));
                Assert.True(init.IsComplete && resp.IsComplete);
                Assert.Equal(init.HandshakeHash, resp.HandshakeHash);
            }
        }
        finally
        {
            MlKem768.ForceBouncyCastle = false;
        }
    }

    /// <summary>A wrong psk fails on the very first message: the responder learns nothing and sends nothing.</summary>
    [Theory]
    [InlineData(NoisePattern.NNhfspsk0)]
    [InlineData(NoisePattern.XXhfspsk0)]
    public void WrongPskFailsAtFirstMessage(NoisePattern pattern)
    {
        var other = (byte[])Psk.Clone();
        other[^1] ^= 0x80;
        var (init, resp) = Pair(pattern, out var keys, respPsk: other);
        using (keys) using (init) using (resp)
        {
            Assert.ThrowsAny<CryptographicException>(() => resp.ReadMessage(init.WriteMessage([])));
            Assert.Null(resp.RemoteStaticPublicKey);
            Assert.False(resp.IsComplete);
        }
    }

    /// <summary>A different prologue (an old v1 channel label against a v2 one, say) fails the same way.</summary>
    [Fact]
    public void DifferentPrologueFailsAtFirstMessage()
    {
        using var init = new NoiseHandshake(NoisePattern.NNhfspsk0, true, "jay-member-v1|chat"u8, Psk);
        using var resp = new NoiseHandshake(NoisePattern.NNhfspsk0, false, "jay-member-v2|chat"u8, Psk);
        Assert.ThrowsAny<CryptographicException>(() => resp.ReadMessage(init.WriteMessage([])));
    }

    /// <summary>One flipped bit anywhere in any handshake message (X25519 key, ML-KEM key or ciphertext, s, payload) fails.</summary>
    [Theory]
    [InlineData(NoisePattern.NNhfspsk0, 0, 0)]
    [InlineData(NoisePattern.NNhfspsk0, 0, 40)]
    [InlineData(NoisePattern.NNhfspsk0, 0, 1240)]
    [InlineData(NoisePattern.NNhfspsk0, 1, 0)]
    [InlineData(NoisePattern.NNhfspsk0, 1, 600)]
    [InlineData(NoisePattern.NNhfspsk0, 1, 1130)]
    [InlineData(NoisePattern.XXhfspsk0, 0, 5)]
    [InlineData(NoisePattern.XXhfspsk0, 0, 700)]
    [InlineData(NoisePattern.XXhfspsk0, 1, 10)]
    [InlineData(NoisePattern.XXhfspsk0, 1, 900)]
    [InlineData(NoisePattern.XXhfspsk0, 1, 1150)]
    [InlineData(NoisePattern.XXhfspsk0, 1, 1190)]
    [InlineData(NoisePattern.XXhfspsk0, 2, 5)]
    [InlineData(NoisePattern.XXhfspsk0, 2, 60)]
    public void TamperedMessageFails(NoisePattern pattern, int index, int position)
    {
        var (init, resp) = Pair(pattern, out var keys);
        using (keys) using (init) using (resp)
        {
            for (var i = 0; i <= index; i++)
            {
                var (writer, reader) = i % 2 == 0 ? (init, resp) : (resp, init);
                var message = writer.WriteMessage("payload-payload"u8);
                if (i == index)
                {
                    message[position % message.Length] ^= 0x01;
                    Assert.ThrowsAny<CryptographicException>(() => reader.ReadMessage(message));
                }
                else reader.ReadMessage(message);
            }
        }
    }

    /// <summary>Truncated handshake messages fail cleanly, never with an out-of-range exception.</summary>
    [Theory]
    [InlineData(NoisePattern.NNhfspsk0)]
    [InlineData(NoisePattern.XXhfspsk0)]
    public void TruncatedMessagesFail(NoisePattern pattern)
    {
        foreach (var keep in new[] { 0, 31, 32, 500, 1231, 1247 })
        {
            var (init, resp) = Pair(pattern, out var keys);
            using (keys) using (init) using (resp)
            {
                var m1 = init.WriteMessage([]);
                Assert.ThrowsAny<CryptographicException>(() => resp.ReadMessage(m1.AsSpan(0, keep)));
            }
        }
    }

    /// <summary>An all-zero ephemeral or long-term public key from the peer is rejected.</summary>
    [Fact]
    public void AllZeroRemoteKeysRejected()
    {
        var (init, resp) = Pair(NoisePattern.XXhfspsk0, out var keys);
        using (keys) using (init) using (resp)
        {
            var first = init.WriteMessage([]);
            Array.Clear(first, 0, X25519KeyPair.Length);
            Assert.ThrowsAny<CryptographicException>(() =>
            {
                resp.ReadMessage(first);
                resp.WriteMessage([]);
            });
        }

        // An honest responder can't send an all-zero s, so the reference driver plays a forged one: everything else in
        // its second message is valid, and the initiator must refuse when it decrypts s.
        using var a = X25519KeyPair.Generate();
        using var b = X25519KeyPair.Generate();
        using var init2 = new NoiseHandshake(NoisePattern.XXhfspsk0, true, Prologue, Psk, a);
        using var forged = new Ref(Ref.XXhfspsk0, false, Prologue, Psk, b) { SendZeroStatic = true };
        forged.Read(init2.WriteMessage([]));
        var m2 = forged.Write([]);
        Assert.ThrowsAny<CryptographicException>(() => init2.ReadMessage(m2));
        Assert.Null(init2.RemoteStaticPublicKey);
    }

    /// <summary>The classic (pre-quantum) handshakes the old protocol used are not accepted any more.</summary>
    [Theory]
    [InlineData(NoisePattern.NNhfspsk0)]
    [InlineData(NoisePattern.XXhfspsk0)]
    public void OldClassicHandshakeIsRejected(NoisePattern pattern)
    {
        var xx = pattern == NoisePattern.XXhfspsk0;
        using var a = xx ? X25519KeyPair.Generate() : null;
        using var b = xx ? X25519KeyPair.Generate() : null;
        using var old = new Ref(xx ? Ref.XXpsk0 : Ref.NNpsk0, true, Prologue, Psk, a);
        using var resp = new NoiseHandshake(pattern, false, Prologue, Psk, b);
        Assert.ThrowsAny<CryptographicException>(() => resp.ReadMessage(old.Write("hello"u8)));
        Assert.False(resp.IsComplete);
    }

    /// <summary>NN on one side and XX on the other never completes (the protocol names differ, so the keys do).</summary>
    [Fact]
    public void MismatchedPatternsFail()
    {
        using var b = X25519KeyPair.Generate();
        using var init = new NoiseHandshake(NoisePattern.NNhfspsk0, true, Prologue, Psk);
        using var resp = new NoiseHandshake(NoisePattern.XXhfspsk0, false, Prologue, Psk, b);
        Assert.ThrowsAny<CryptographicException>(() => resp.ReadMessage(init.WriteMessage([])));
    }

    /// <summary>Every pattern production can be asked for is a hybrid (X25519 + ML-KEM-768) one.</summary>
    [Fact]
    public void EveryPatternIsHybrid()
    {
        var patterns = Enum.GetValues<NoisePattern>();
        Assert.NotEmpty(patterns);
        foreach (var pattern in patterns)
        {
            Assert.Contains("hfs", pattern.ToString(), StringComparison.Ordinal);
            Assert.Contains("hfspsk0_25519+MLKEM768_ChaChaPoly_SHA256", NoiseHandshake.ProtocolName(pattern), StringComparison.Ordinal);
        }
    }

    /// <summary>XX needs a long-term key; NN must not get one.</summary>
    [Fact]
    public void StaticKeyRequiredOnlyForXx()
    {
        using var a = X25519KeyPair.Generate();
        Assert.Throws<ArgumentException>(() => new NoiseHandshake(NoisePattern.XXhfspsk0, true, Prologue, Psk));
        Assert.Throws<ArgumentException>(() => new NoiseHandshake(NoisePattern.NNhfspsk0, true, Prologue, Psk, a));
    }

    [Fact]
    public void PrivateKeyRoundTrips()
    {
        using var a = X25519KeyPair.Generate();
        var raw = a.ExportPrivateKey();
        using var b = X25519KeyPair.FromPrivateKey(raw);
        CryptographicOperations.ZeroMemory(raw);
        Assert.Equal(a.PublicKey, b.PublicKey);
    }

    private sealed class Keys(X25519KeyPair? a, X25519KeyPair? b) : IDisposable
    {
        public void Dispose()
        {
            a?.Dispose();
            b?.Dispose();
        }
    }

    private static (NoiseHandshake Init, NoiseHandshake Resp) Pair(NoisePattern pattern, out Keys keys, byte[]? respPsk = null)
    {
        var xx = pattern == NoisePattern.XXhfspsk0;
        var a = xx ? X25519KeyPair.Generate() : null;
        var b = xx ? X25519KeyPair.Generate() : null;
        keys = new Keys(a, b);
        return (new NoiseHandshake(pattern, true, Prologue, Psk, a), new NoiseHandshake(pattern, false, Prologue, respPsk ?? Psk, b));
    }

    /// <summary>
    /// Reference handshake driver: walks a pattern's token list exactly as the Noise spec (§5.3, §9) and the HFS extension
    /// define each token, with no shortcuts. Independent of <see cref="NoiseHandshake"/> except for the shared primitives.
    /// </summary>
    private sealed class Ref : IDisposable
    {
        public sealed record Spec(string Name, string[][] Messages);

        public static readonly Spec XXpsk3 = new("Noise_XXpsk3_25519_ChaChaPoly_SHA256", [["e"], ["e", "ee", "s", "es"], ["s", "se", "psk"]]);
        public static readonly Spec XXpsk0 = new("Noise_XXpsk0_25519_ChaChaPoly_SHA256", [["psk", "e"], ["e", "ee", "s", "es"], ["s", "se"]]);
        public static readonly Spec NNpsk0 = new("Noise_NNpsk0_25519_ChaChaPoly_SHA256", [["psk", "e"], ["e", "ee"]]);
        public static readonly Spec NNhfspsk0 = new("Noise_NNhfspsk0_25519+MLKEM768_ChaChaPoly_SHA256", [["psk", "e", "e1"], ["e", "ee", "ekem1"]]);
        public static readonly Spec XXhfspsk0 = new("Noise_XXhfspsk0_25519+MLKEM768_ChaChaPoly_SHA256", [["psk", "e", "e1"], ["e", "ee", "ekem1", "s", "es"], ["s", "se"]]);

        private readonly Spec _spec;
        private readonly bool _initiator;
        private readonly byte[] _psk;
        private readonly X25519KeyPair? _s;
        private readonly SymmetricState _ss;
        private X25519KeyPair? _e;
        private MlKem768.IDecapsulationKey? _e1;
        private byte[]? _re, _re1;
        private int _index;

        public Ref(Spec spec, bool initiator, ReadOnlySpan<byte> prologue, byte[] psk, X25519KeyPair? s, X25519KeyPair? fixedE = null)
        {
            _spec = spec;
            _initiator = initiator;
            _psk = psk;
            _s = s;
            _e = fixedE;
            _ss = new SymmetricState(spec.Name);
            _ss.MixHash(prologue);
        }

        /// <summary>Forge an all-zero long-term key in place of s (an honest implementation can't).</summary>
        public bool SendZeroStatic { get; init; }
        public byte[]? RemoteStatic { get; private set; }
        public byte[] HandshakeHash => _ss.HandshakeHash;
        public (CipherState, CipherState) Split() => _ss.Split();

        private byte[] Dh(string token) => token switch
        {
            "ee" => _e!.Dh(_re!),
            "es" => _initiator ? _e!.Dh(RemoteStatic!) : _s!.Dh(_re!),
            "se" => _initiator ? _s!.Dh(_re!) : _e!.Dh(RemoteStatic!),
            _ => throw new InvalidOperationException(token),
        };

        public byte[] Write(ReadOnlySpan<byte> payload)
        {
            Assert.Equal(_initiator, _index % 2 == 0);
            var output = new List<byte>();
            foreach (var token in _spec.Messages[_index])
            {
                switch (token)
                {
                    case "psk": _ss.MixKeyAndHash(_psk); break;
                    case "e":
                        _e ??= X25519KeyPair.Generate();
                        output.AddRange(_e.PublicKey);
                        _ss.MixHash(_e.PublicKey);
                        _ss.MixKey(_e.PublicKey); // every pattern here is a psk pattern (spec §9.2)
                        break;
                    case "e1":
                        _e1 = MlKem768.Generate();
                        output.AddRange(_ss.EncryptAndHash(_e1.ExportEncapsulationKey()));
                        break;
                    case "ekem1":
                        var (ct, shared) = MlKem768.Encapsulate(_re1!);
                        output.AddRange(_ss.EncryptAndHash(ct));
                        _ss.MixKey(shared);
                        break;
                    case "s": output.AddRange(_ss.EncryptAndHash(SendZeroStatic ? new byte[32] : _s!.PublicKey)); break;
                    // A forged responder has no matching private key for es; any value will do, the peer refuses earlier.
                    default: _ss.MixKey(token == "es" && SendZeroStatic ? new byte[32] : Dh(token)); break;
                }
            }
            output.AddRange(_ss.EncryptAndHash(payload));
            _index++;
            return [.. output];
        }

        public byte[] Read(byte[] message)
        {
            Assert.NotEqual(_initiator, _index % 2 == 0);
            var at = 0;
            byte[] Take(int n)
            {
                var part = message[at..(at + n)];
                at += n;
                return part;
            }
            foreach (var token in _spec.Messages[_index])
            {
                switch (token)
                {
                    case "psk": _ss.MixKeyAndHash(_psk); break;
                    case "e":
                        _re = Take(32);
                        _ss.MixHash(_re);
                        _ss.MixKey(_re);
                        break;
                    case "e1": _re1 = _ss.DecryptAndHash(Take(MlKem768.EncapsulationKeySize + CipherState.TagLength)); break;
                    case "ekem1": _ss.MixKey(_e1!.Decapsulate(_ss.DecryptAndHash(Take(MlKem768.CiphertextSize + CipherState.TagLength)))); break;
                    case "s": RemoteStatic = _ss.DecryptAndHash(Take(_ss.HasKey ? 48 : 32)); break;
                    default: _ss.MixKey(Dh(token)); break;
                }
            }
            var payload = _ss.DecryptAndHash(message[at..]);
            _index++;
            return payload;
        }

        public void Dispose()
        {
            _ss.Dispose();
            _e?.Dispose();
            _e1?.Dispose();
        }
    }
}
