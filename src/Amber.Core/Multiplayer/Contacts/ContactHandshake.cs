using System.Security.Cryptography;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Contacts;

/// <summary>
/// The short check two people do when they add each other: both screens show four emoji, and they compare them by voice,
/// in person or over a video call. Same emoji: nobody swapped the contact codes on the way.
/// <para>
/// Four emoji are 24 bits, far too few to compare against fixed keys (a platform that swaps codes could search for a key
/// that gives the same emoji). So the emoji come from both keys and two fresh nonces, exchanged with a hash commitment
/// as in ZRTP and Bluetooth numeric comparison: the one who adds sends H(nA) first, the other answers with nB, and only
/// then is nA revealed. Someone in the middle has to fix its own nonces before it can see the other side's, so a swap
/// gets through with probability 2^-24 per attempt, and every attempt costs the two people a comparison.
/// </para>
/// <para>The three messages travel as sealed mail (<see cref="SealedMail"/>): hello {c}, hello-r {n}, hello-n {n}.</para>
/// </summary>
public static class ContactHandshake
{
    public const int NonceLength = 32;
    public const int EmojiCount = 4;

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceLength);

    public static byte[] Commit(ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != NonceLength) throw new ArgumentException(null, nameof(nonce));
        return SHA256.HashData([.. "jay-sas-commit-v1"u8, .. nonce]);
    }

    public static bool Opens(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> nonce) =>
        nonce.Length == NonceLength && commitment.Length == 32 && CryptographicOperations.FixedTimeEquals(Commit(nonce), commitment);

    /// <summary>
    /// The four emoji (as indexes into <see cref="Emoji"/>). <paramref name="initiator"/> is the key of whoever added the
    /// other (sent hello), <paramref name="initiatorNonce"/> the nonce it committed to.
    /// </summary>
    public static int[] Sas(ReadOnlySpan<byte> initiator, ReadOnlySpan<byte> responder, ReadOnlySpan<byte> initiatorNonce, ReadOnlySpan<byte> responderNonce)
    {
        if (initiator.Length != X25519KeyPair.Length || responder.Length != X25519KeyPair.Length) throw new ArgumentException("key length");
        if (initiatorNonce.Length != NonceLength || responderNonce.Length != NonceLength) throw new ArgumentException("nonce length");
        var digest = SHA256.HashData([.. "jay-sas-v1"u8, .. initiator, .. responder, .. initiatorNonce, .. responderNonce]);
        var bits = (digest[0] << 16) | (digest[1] << 8) | digest[2];
        var result = new int[EmojiCount];
        for (var i = 0; i < EmojiCount; i++) result[i] = (bits >> (18 - 6 * i)) & 63;
        return result;
    }

    /// <summary>
    /// 64 emoji that look different from each other and render on Windows 10, macOS and common Linux fonts
    /// (no variation selectors, no skin tones, no flags).
    /// </summary>
    public static readonly string[] Emoji =
    [
        "🐶", "🐱", "🐭", "🐰", "🦊", "🐻", "🐼", "🐨", "🐯", "🦁", "🐮", "🐷", "🐸", "🐵", "🐔", "🐧",
        "🐦", "🐢", "🐍", "🐙", "🦀", "🐟", "🐬", "🐳", "🦋", "🐌", "🐝", "🐞", "🌵", "🌲", "🌴", "🍀",
        "🌻", "🌹", "🍄", "🌙", "⭐", "🌈", "⚡", "🔥", "💧", "🍎", "🍌", "🍇", "🍉", "🍒", "🍓", "🍋",
        "🥕", "🌽", "🍞", "🧀", "🍕", "🍩", "🍪", "🚀", "🚗", "⛵", "🎈", "🎁", "🔑", "🔔", "🎸", "🎲",
    ];

    /// <summary>English names of <see cref="Emoji"/>, in the same order, for screen readers and for reading them out.</summary>
    public static readonly string[] EmojiNames =
    [
        "dog", "cat", "mouse", "rabbit", "fox", "bear", "panda", "koala", "tiger", "lion", "cow", "pig", "frog", "monkey", "chicken", "penguin",
        "bird", "turtle", "snake", "octopus", "crab", "fish", "dolphin", "whale", "butterfly", "snail", "bee", "ladybug", "cactus", "pine tree", "palm tree", "clover",
        "sunflower", "rose", "mushroom", "moon", "star", "rainbow", "lightning", "fire", "droplet", "apple", "banana", "grapes", "watermelon", "cherries", "strawberry", "lemon",
        "carrot", "corn", "bread", "cheese", "pizza", "doughnut", "cookie", "rocket", "car", "sailboat", "balloon", "gift", "key", "bell", "guitar", "dice",
    ];
}
