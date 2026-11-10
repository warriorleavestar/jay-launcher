using System.Security.Cryptography;
using Amber.Core.Identity;

namespace Amber.Core.Tests;

/// <summary>
/// Jay accounts: BIP39 phrases and seeds (official vectors), SLIP-0010 Ed25519 (official vectors), Bech32m (BIP350 vectors),
/// jay1 addresses, device certificates, and the encrypted account store.
/// </summary>
public sealed class JayAccountTests : IDisposable
{
    private const string Abandon = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-account-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("00000000000000000000000000000000", Abandon,
        "c55257c360c07c72029aebc1b53c05ed0362ada38ead3e3e9efa3708e53495531f09a6987599d18264c1e1c92f2cf141630c7a3c4ab7c81b2f001698e7463b04")]
    [InlineData("7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f", "legal winner thank year wave sausage worth useful legal winner thank yellow",
        "2e8905819b8723fe2c1d161860e5ee1830318dbf49a83bd451cfb8440c28bd6fa457fe1296106559a3c80937a1c1069be3a3a5bd381ee6260e8d9739fce1f607")]
    public void Bip39OfficialVectors(string entropyHex, string phrase, string seedHex)
    {
        var entropy = Convert.FromHexString(entropyHex);
        Assert.Equal(phrase, string.Join(' ', Bip39.FromEntropy(entropy, MnemonicLanguage.English)));
        Assert.Equal(entropy, Bip39.ToEntropy(Bip39.Split(phrase), out var language));
        Assert.Equal(MnemonicLanguage.English, language);
        Assert.Equal(seedHex, Convert.ToHexStringLower(Bip39.ToSeed(Bip39.Split(phrase), "TREZOR")));
    }

    [Fact]
    public void WordlistsAreTheOfficialOnes()
    {
        Assert.Equal("abandon", Bip39.Wordlist(MnemonicLanguage.English)[0]);
        Assert.Equal("zoo", Bip39.Wordlist(MnemonicLanguage.English)[2047]);
        Assert.Equal("的", Bip39.Wordlist(MnemonicLanguage.ChineseSimplified)[0]);
        Assert.Equal(2048, Bip39.Wordlist(MnemonicLanguage.ChineseSimplified).Distinct().Count());
    }

    [Theory]
    [InlineData(MnemonicLanguage.English, 12)]
    [InlineData(MnemonicLanguage.English, 24)]
    [InlineData(MnemonicLanguage.ChineseSimplified, 12)]
    public void GeneratedPhrasesRoundTrip(MnemonicLanguage language, int count)
    {
        var words = Bip39.Generate(language, count);
        Assert.Equal(count, words.Length);
        Bip39.ToEntropy(words, out var detected);
        Assert.Equal(language, detected);
        Assert.NotEqual(words, Bip39.Generate(language, count));
    }

    [Fact]
    public void ChineseWithoutSpacesAndMessyInputIsSplit()
    {
        var words = Bip39.Generate(MnemonicLanguage.ChineseSimplified);
        Assert.Equal(words, Bip39.Split(string.Concat(words)));
        Assert.Equal(words, Bip39.Split(string.Join("　", words)));
        Assert.Equal(Bip39.Split(Abandon), Bip39.Split("  ABANDON, abandon\nabandon abandon abandon abandon abandon abandon abandon abandon abandon about "));
    }

    [Fact]
    public void BadPhrasesSayWhy()
    {
        Assert.Equal(MnemonicProblem.Empty, Assert.Throws<MnemonicException>(() => Bip39.ToEntropy([], out _)).Problem);
        var wrongEnd = Bip39.Split(Abandon.Replace("about", "abandon"));
        Assert.Equal(MnemonicProblem.Checksum, Assert.Throws<MnemonicException>(() => Bip39.ToEntropy(wrongEnd, out _)).Problem);
        var typo = Bip39.Split(Abandon.Replace("about", "abuot"));
        var e = Assert.Throws<MnemonicException>(() => Bip39.ToEntropy(typo, out _));
        Assert.Equal((MnemonicProblem.UnknownWord, 11, "abuot"), (e.Problem, e.WordIndex, e.Word));
        Assert.Equal(MnemonicProblem.WrongLength, Assert.Throws<MnemonicException>(() => Bip39.ToEntropy(Bip39.Split(Abandon)[..11], out _)).Problem);
        var mixed = Bip39.Split(Abandon).ToArray();
        mixed[3] = "的";
        Assert.Equal(MnemonicProblem.MixedLanguages, Assert.Throws<MnemonicException>(() => Bip39.ToEntropy(mixed, out _)).Problem);
    }

    [Fact]
    public void BackupQuizAsksDistinctWordsAndChecksAnswers()
    {
        var words = Bip39.Split(Abandon);
        var positions = MnemonicQuiz.Pick(words.Length);
        Assert.Equal(3, positions.Distinct().Count());
        Assert.All(positions, p => Assert.InRange(p, 0, 11));
        Assert.True(MnemonicQuiz.Check(words, [11, 0], [" About ", "ABANDON"]));
        Assert.False(MnemonicQuiz.Check(words, [11, 0], ["abandon", "abandon"]));
        Assert.False(MnemonicQuiz.Check(words, [11], [null]));
        Assert.False(MnemonicQuiz.Check(words, [11], ["about about"]));
        Assert.False(MnemonicQuiz.Check(words, [], []));
    }

    [Fact]
    public void Slip10OfficialEd25519Vector()
    {
        var seed = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        var (key, chain) = Slip10.Derive(seed, Slip10.Ed25519RootKey);
        Assert.Equal("2b4be7f19ee27bbf30c667b642d5f4aa69fd169872f8fc3059c08ebae2eb19e7", Convert.ToHexStringLower(key));
        Assert.Equal("90046a93de5380a72b5e45010748567d5ea02bbf6522f979e05c0d8d8ca9fffb", Convert.ToHexStringLower(chain));
        (key, chain) = Slip10.Derive(seed, Slip10.Ed25519RootKey, 0);
        Assert.Equal("68e0fe46dfb67e368c75379acec591dad19df3cde26e63b93a8e704f1dade7a3", Convert.ToHexStringLower(key));
        Assert.Equal("8b59aa11380b624e81507a27fedda59fea6d0b779a778918a2fd3590e16e9c69", Convert.ToHexStringLower(chain));
        var pub = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(key).GeneratePublicKey().GetEncoded();
        Assert.Equal("8c8a13df77a28f3445213a0f432fde644acaa215fc72dcdf300d5efaa85d350c", Convert.ToHexStringLower(pub));
    }

    [Theory]
    [InlineData("A1LQFN3A")]
    [InlineData("abcdef1l7aum6echk45nj3s0wdvt2fg8x9yrzpqzd3ryx")]
    [InlineData("?1v759aa")]
    public void Bech32mValidVectors(string text) => Assert.True(Bech32m.TryDecode(text, out _, out _));

    [Theory]
    [InlineData("A1G7SGD8")] // bech32 (not m) checksum
    [InlineData("an84characterslonghumanreadablepartthatcontainsthetheexcludedcharactersbioandnumber11d6pts4")]
    [InlineData("qyrz8wqd2c9m")]
    [InlineData("1qyrz8wqd2c9m")]
    public void Bech32mInvalidVectors(string text) => Assert.False(Bech32m.TryDecode(text, out _, out _));

    [Fact]
    public void AccountFromPhraseIsDeterministicAndUsesJaysOwnRootKey()
    {
        using var account = JayAccount.FromMnemonic(Bip39.Split(Abandon), null);
        // Pinned vector, cross-checked with an independent Python implementation (hashlib + hmac + cryptography + reference bech32m).
        Assert.Equal("93d93e2ba5180567d5ce978c28c4b46cb13cd3da72ea0a3b6b3fb9519946bf92", Convert.ToHexStringLower(account.PublicKey));
        Assert.Equal("jay1qj0vnu2a9rqzk04wwj7xz3395djcne576wt4q5wmt87u4rx2xh7fqdxvdag", account.Address);

        // Same words with the standard SLIP-0010 root key (what a wallet would use) give a different key.
        var seed = Bip39.ToSeed(Bip39.Split(Abandon), null);
        var (walletKey, _) = Slip10.Derive(seed, Slip10.Ed25519RootKey, 0, 0);
        var walletPub = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(walletKey).GeneratePublicKey().GetEncoded();
        Assert.NotEqual(account.PublicKey, walletPub);

        using var withPassword = JayAccount.FromMnemonic(Bip39.Split(Abandon), "extra");
        Assert.NotEqual(account.Address, withPassword.Address);
    }

    [Fact]
    public void AddressesRoundTripAndCatchTypos()
    {
        using var account = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        Assert.Equal(63, account.Address.Length);
        Assert.StartsWith("jay1q", account.Address);
        Assert.True(JayAddress.TryDecode(account.Address, out var key));
        Assert.Equal(account.PublicKey, key);
        Assert.True(JayAddress.TryDecode("  " + account.Address.ToUpperInvariant() + " ", out _));
        var typo = account.Address[..20] + (account.Address[20] == 'q' ? 'p' : 'q') + account.Address[21..];
        Assert.False(JayAddress.IsValid(typo));
        Assert.False(JayAddress.IsValid(account.Address[..^1]));
        Assert.False(JayAddress.IsValid(Bech32m.Encode("abc", Bech32m.ConvertBits(account.PublicKey, 8, 5, true))));
        Assert.Equal($"{account.Address[..8]}…{account.Address[^4..]}", JayAddress.Short(account.Address));
        Assert.Equal(4, JayAddress.CheckWords(account.PublicKey, MnemonicLanguage.ChineseSimplified).Length);
        Assert.Equal(JayAddress.CheckWords(account.PublicKey, MnemonicLanguage.English), JayAddress.CheckWords(key, MnemonicLanguage.English));
    }

    [Fact]
    public void DeviceCertificatesBindTheDeviceKey()
    {
        using var account = JayAccount.FromMnemonic(Bip39.Split(Abandon), null);
        var device = RandomNumberGenerator.GetBytes(32);
        var cert = account.CertifyDevice(device, "Desk‮ PC\n", 0, DateTimeOffset.UtcNow);
        Assert.Equal("Desk PC", cert.DeviceName);
        Assert.True(cert.Verify(device));
        Assert.False(cert.Verify(RandomNumberGenerator.GetBytes(32)));

        var parsed = DeviceCertificate.FromJson(System.Text.Json.Nodes.JsonNode.Parse(cert.ToJson().ToJsonString()));
        Assert.NotNull(parsed);
        Assert.True(parsed.Verify(device));
        Assert.False((parsed with { DeviceName = "Other" }).Verify(device));
        Assert.False((parsed with { Epoch = 1 }).Verify(device));
        using var other = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        Assert.False((parsed with { Account = other.Address }).Verify(device));
        Assert.Null(DeviceCertificate.FromJson(System.Text.Json.Nodes.JsonNode.Parse("{\"account\":\"x\"}")));
    }

    [Fact]
    public void StoreKeepsSeedsEncryptedAndReopensAccounts()
    {
        var device = RandomNumberGenerator.GetBytes(32);
        var words = Bip39.Generate(MnemonicLanguage.English);
        string address;
        byte[] seed;
        using (var account = JayAccount.FromMnemonic(words, null))
        {
            address = account.Address;
            seed = Bip39.ToSeed(words, null);
            var store = new JayAccountStore(_dir);
            var entry = store.Add(account, "Main", backedUp: false, device, "PC", DateTimeOffset.UtcNow);
            Assert.True(entry.Device!.Verify(device));
        }

        var text = File.ReadAllText(Path.Combine(_dir, JayAccountStore.FileName));
        Assert.DoesNotContain(words[0] + " " + words[1], text);
        Assert.DoesNotContain(Convert.ToBase64String(seed), text);
        Assert.DoesNotContain(Convert.ToHexString(seed), text, StringComparison.OrdinalIgnoreCase);

        var again = new JayAccountStore(_dir);
        again.Load();
        var saved = Assert.Single(again.Accounts);
        Assert.Equal((address, "Main", false), (saved.Address, saved.Label, saved.BackedUp));
        using (var opened = again.Open(address))
        {
            Assert.NotNull(opened);
            Assert.Equal(address, opened.Address);
        }

        again.MarkBackedUp(address);
        var newDevice = RandomNumberGenerator.GetBytes(32);
        var renewed = again.EnsureDeviceCertificate(address, newDevice, "PC", DateTimeOffset.UtcNow);
        Assert.True(renewed!.Device!.Verify(newDevice));
        Assert.True(renewed.BackedUp);

        again.Remove(address);
        var empty = new JayAccountStore(_dir);
        empty.Load();
        Assert.Empty(empty.Accounts);
    }

    [Fact]
    public void DamagedStoreIsKeptAndTreatedAsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, JayAccountStore.FileName), "{not json");
        var store = new JayAccountStore(_dir);
        store.Load();
        Assert.Empty(store.Accounts);
        Assert.True(File.Exists(Path.Combine(_dir, JayAccountStore.FileName + ".broken")));
        Assert.Null(store.Open("jay1qj0vnu2a9rqzk04wwj7xz3395djcne576wt4q5wmt87u4rx2xh7fqdxvdag"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (DirectoryNotFoundException) { }
    }
}
