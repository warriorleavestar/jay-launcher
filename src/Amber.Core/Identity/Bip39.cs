using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Amber.Core.Identity;

public enum MnemonicLanguage { English, ChineseSimplified }

/// <summary>Why a recovery phrase was refused. The UI turns these into localized text.</summary>
public enum MnemonicProblem { Empty, WrongLength, UnknownWord, MixedLanguages, Checksum }

public sealed class MnemonicException(MnemonicProblem problem, int wordIndex = -1, string? word = null)
    : FormatException($"{problem}{(wordIndex >= 0 ? $" at word {wordIndex + 1}" : "")}")
{
    public MnemonicProblem Problem { get; } = problem;
    /// <summary>0-based position of the offending word, or -1.</summary>
    public int WordIndex { get; } = wordIndex;
    public string? Word { get; } = word;
}

/// <summary>
/// BIP39 recovery phrases (https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki):
/// entropy + SHA-256 checksum bits → 11-bit word indices; seed = PBKDF2-HMAC-SHA512(NFKD(phrase), "mnemonic" + NFKD(passphrase), 2048).
/// Only the encoding and seed step follow BIP39. Keys are derived from the seed with Jay's own root key
/// (<see cref="Slip10.JayRootKey"/>), so a cryptocurrency wallet's phrase never yields wallet keys inside Jay and vice versa.
/// </summary>
public static class Bip39
{
    public static readonly int[] AllowedWordCounts = [12, 15, 18, 21, 24];
    private const int Iterations = 2048;

    private static readonly Lazy<string[]> English = new(() => Load("english"));
    private static readonly Lazy<string[]> Chinese = new(() => Load("chinese_simplified"));
    private static readonly Lazy<Dictionary<string, int>> EnglishIndex = new(() => Index(English.Value));
    private static readonly Lazy<Dictionary<string, int>> ChineseIndex = new(() => Index(Chinese.Value));

    public static IReadOnlyList<string> Wordlist(MnemonicLanguage language) =>
        language == MnemonicLanguage.ChineseSimplified ? Chinese.Value : English.Value;

    /// <summary>A fresh phrase from the OS random generator. 12 words = 128 bits, 24 words = 256 bits.</summary>
    public static string[] Generate(MnemonicLanguage language, int words = 12)
    {
        if (!AllowedWordCounts.Contains(words)) throw new ArgumentOutOfRangeException(nameof(words));
        var entropy = RandomNumberGenerator.GetBytes(words * 4 / 3);
        try { return FromEntropy(entropy, language); }
        finally { CryptographicOperations.ZeroMemory(entropy); }
    }

    public static string[] FromEntropy(ReadOnlySpan<byte> entropy, MnemonicLanguage language)
    {
        if (entropy.Length is < 16 or > 32 || entropy.Length % 4 != 0) throw new ArgumentException("entropy must be 16..32 bytes, a multiple of 4", nameof(entropy));
        var list = Wordlist(language);
        var checksumBits = entropy.Length / 4;
        var hash = SHA256.HashData(entropy);
        var totalBits = entropy.Length * 8 + checksumBits;
        var words = new string[totalBits / 11];
        for (var w = 0; w < words.Length; w++)
        {
            var index = 0;
            for (var b = 0; b < 11; b++)
            {
                var bit = w * 11 + b;
                index = (index << 1) | Bit(entropy, hash, bit);
            }
            words[w] = list[index];
        }
        return words;

        static int Bit(ReadOnlySpan<byte> entropy, byte[] hash, int bit)
        {
            var source = bit < entropy.Length * 8 ? entropy : hash;
            var at = bit < entropy.Length * 8 ? bit : bit - entropy.Length * 8;
            return (source[at / 8] >> (7 - at % 8)) & 1;
        }
    }

    /// <summary>
    /// Splits what the user typed or pasted into words: any whitespace (incl. the ideographic space) or commas separate words;
    /// Chinese typed without spaces is split per character (every Chinese BIP39 word is one character).
    /// </summary>
    public static string[] Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var parts = normalized.Split([' ', '\t', '\r', '\n', '\u3000', ',', '\uff0c', '\u3001'], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var part in parts)
        {
            if (part.Length > 1 && part.All(IsHan)) result.AddRange(part.Select(c => c.ToString()));
            else result.Add(part);
        }
        return [.. result];

        static bool IsHan(char c) => c is >= '\u3400' and <= '\u9fff';
    }

    /// <summary>Checks the words and checksum; returns the entropy and the phrase's language. Throws <see cref="MnemonicException"/>.</summary>
    public static byte[] ToEntropy(IReadOnlyList<string> words, out MnemonicLanguage language)
    {
        if (words.Count == 0) throw new MnemonicException(MnemonicProblem.Empty);
        language = ChineseIndex.Value.ContainsKey(words[0]) ? MnemonicLanguage.ChineseSimplified : MnemonicLanguage.English;
        var index = language == MnemonicLanguage.ChineseSimplified ? ChineseIndex.Value : EnglishIndex.Value;
        var other = language == MnemonicLanguage.ChineseSimplified ? EnglishIndex.Value : ChineseIndex.Value;
        var indices = new int[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            if (index.TryGetValue(words[i], out var at)) indices[i] = at;
            else if (other.ContainsKey(words[i])) throw new MnemonicException(MnemonicProblem.MixedLanguages, i, words[i]);
            else throw new MnemonicException(MnemonicProblem.UnknownWord, i, words[i]);
        }
        if (!AllowedWordCounts.Contains(words.Count)) throw new MnemonicException(MnemonicProblem.WrongLength);

        var totalBits = words.Count * 11;
        var checksumBits = totalBits / 33;
        var entropyBytes = (totalBits - checksumBits) / 8;
        var all = new byte[(totalBits + 7) / 8];
        for (var bit = 0; bit < totalBits; bit++)
        {
            if (((indices[bit / 11] >> (10 - bit % 11)) & 1) != 0) all[bit / 8] |= (byte)(0x80 >> (bit % 8));
        }
        var entropy = all[..entropyBytes];
        var hash = SHA256.HashData(entropy);
        for (var i = 0; i < checksumBits; i++)
        {
            var bit = entropyBytes * 8 + i;
            var expected = (hash[i / 8] >> (7 - i % 8)) & 1;
            var actual = (all[bit / 8] >> (7 - bit % 8)) & 1;
            if (expected != actual)
            {
                CryptographicOperations.ZeroMemory(entropy);
                CryptographicOperations.ZeroMemory(all);
                throw new MnemonicException(MnemonicProblem.Checksum);
            }
        }
        CryptographicOperations.ZeroMemory(all);
        return entropy;
    }

    public static bool IsValid(IReadOnlyList<string> words)
    {
        try
        {
            CryptographicOperations.ZeroMemory(ToEntropy(words, out _));
            return true;
        }
        catch (MnemonicException) { return false; }
    }

    /// <summary>The 64-byte BIP39 seed. Validate the words first (<see cref="ToEntropy"/>); this step accepts any text.</summary>
    public static byte[] ToSeed(IReadOnlyList<string> words, string? passphrase)
    {
        var phrase = Encoding.UTF8.GetBytes(string.Join(' ', words).Normalize(NormalizationForm.FormKD));
        var salt = Encoding.UTF8.GetBytes(("mnemonic" + (passphrase ?? "")).Normalize(NormalizationForm.FormKD));
        try { return Rfc2898DeriveBytes.Pbkdf2(phrase, salt, Iterations, HashAlgorithmName.SHA512, 64); }
        finally
        {
            CryptographicOperations.ZeroMemory(phrase);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private static string[] Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Amber.Core.Identity.{name}.txt")
            ?? throw new InvalidOperationException($"missing wordlist {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var words = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length != 2048) throw new InvalidOperationException($"wordlist {name} has {words.Length} words");
        return words;
    }

    private static Dictionary<string, int> Index(string[] list)
    {
        var map = new Dictionary<string, int>(list.Length, StringComparer.Ordinal);
        for (var i = 0; i < list.Length; i++) map[list[i]] = i;
        return map;
    }
}

/// <summary>The "did you write it down?" check after a new phrase is shown: ask for a few random words by position.</summary>
public static class MnemonicQuiz
{
    /// <summary>Distinct 0-based positions in ascending order.</summary>
    public static int[] Pick(int wordCount, int questions = 3)
    {
        if (questions > wordCount) throw new ArgumentOutOfRangeException(nameof(questions));
        var picked = new SortedSet<int>();
        while (picked.Count < questions) picked.Add(RandomNumberGenerator.GetInt32(wordCount));
        return [.. picked];
    }

    /// <summary>True when every answer matches the word at its position (case, width and surrounding spaces ignored).</summary>
    public static bool Check(IReadOnlyList<string> words, IReadOnlyList<int> positions, IReadOnlyList<string?> answers)
    {
        if (positions.Count == 0 || positions.Count != answers.Count) return false;
        for (var i = 0; i < positions.Count; i++)
        {
            var answer = Bip39.Split(answers[i]);
            if (answer.Length != 1 || answer[0] != words[positions[i]]) return false;
        }
        return true;
    }
}
