using System.Buffers.Binary;
using System.Security.Cryptography;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>LengthMask (the record length cover in SecureStream): AES-256-CTR keystream from counter 0, applied across calls.</summary>
public class LengthMaskTests
{
    private static byte[] Key(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + seed)).ToArray();

    /// <summary>Reference AES-256-CTR keystream: AES-ECB of a 128-bit big-endian counter starting at 0.</summary>
    private static byte[] Keystream(byte[] key, int length)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var output = new byte[(length + 15) / 16 * 16];
        var counter = new byte[16];
        for (var block = 0; block * 16 < length; block++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(counter.AsSpan(8), (ulong)block);
            aes.EncryptEcb(counter, output.AsSpan(block * 16, 16), PaddingMode.None);
        }
        return output[..length];
    }

    [Fact]
    public void MatchesAesCtrFromCounterZero()
    {
        using var mask = new LengthMask(Key(1));
        var data = new byte[40];
        mask.Apply(data);
        Assert.Equal(Keystream(Key(1), 40), data);
    }

    [Fact]
    public void TwoByteHeadersContinueTheSameKeystream()
    {
        // SecureStream masks one 2-byte length per record; the keystream carries on across records and block boundaries.
        using var whole = new LengthMask(Key(2));
        using var pieces = new LengthMask(Key(2));
        var expected = new byte[50];
        whole.Apply(expected);
        var actual = new byte[50];
        for (var i = 0; i < actual.Length; i += 2) pieces.Apply(actual.AsSpan(i, 2));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SenderAndReceiverWithTheSameKeyRoundTrip()
    {
        using var send = new LengthMask(Key(3));
        using var receive = new LengthMask(Key(3));
        var lengths = new ushort[] { 0, 1, 1400, 16384, ushort.MaxValue };
        foreach (var length in lengths)
        {
            var header = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(header, length);
            send.Apply(header);
            receive.Apply(header);
            Assert.Equal(length, BinaryPrimitives.ReadUInt16BigEndian(header));
        }
    }

    [Fact]
    public void DifferentKeysGiveDifferentMasks()
    {
        using var a = new LengthMask(Key(4));
        using var b = new LengthMask(Key(5));
        var x = new byte[16];
        var y = new byte[16];
        a.Apply(x);
        b.Apply(y);
        Assert.NotEqual(x, y);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(31)]
    public void RejectsKeysThatAreNotAesSized(int length) =>
        Assert.ThrowsAny<CryptographicException>(() => new LengthMask(new byte[length]));
}
