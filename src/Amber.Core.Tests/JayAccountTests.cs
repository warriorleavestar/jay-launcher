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

    private const string PinnedAbandonAddress = "jay1p7q30y99dgwtvgzxvxq8fg6k26ugsmv6xeg6x32pjgp9pcpgtqc8sxwq6ju";

    [Fact]
    public void AccountFromPhraseIsDeterministicAndUsesJaysOwnRootKey()
    {
        using var account = JayAccount.FromMnemonic(Bip39.Split(Abandon), null);
        // Pinned vector, cross-checked with an independent Python implementation (hashlib + hmac + cryptography + reference bech32m).
        Assert.Equal("93d93e2ba5180567d5ce978c28c4b46cb13cd3da72ea0a3b6b3fb9519946bf92", Convert.ToHexStringLower(account.PublicKey));
        // The address is the hash of both account keys; the ML-DSA key comes from m/9'/0' (pinned below, both implementations agree).
        Assert.Equal(PinnedAbandonAddress, account.Address);
        Assert.Equal(JayAddress.Encode(JayAddress.AccountId(account.PublicKey, account.MlDsaPublicKey)), account.Address);

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
        Assert.StartsWith("jay1p", account.Address);
        Assert.True(JayAddress.TryDecode(account.Address, out var key));
        Assert.Equal(account.Id, key);
        Assert.True(JayAddress.TryDecode("  " + account.Address.ToUpperInvariant() + " ", out _));
        var typo = account.Address[..20] + (account.Address[20] == 'q' ? 'p' : 'q') + account.Address[21..];
        Assert.False(JayAddress.IsValid(typo));
        Assert.False(JayAddress.IsValid(account.Address[..^1]));
        Assert.False(JayAddress.IsValid(Bech32m.Encode("abc", Bech32m.ConvertBits(account.PublicKey, 8, 5, true))));
        Assert.Equal($"{account.Address[..8]}…{account.Address[^4..]}", JayAddress.Short(account.Address));
        Assert.Equal(4, JayAddress.CheckWords(account.Id, MnemonicLanguage.ChineseSimplified).Length);
        Assert.Equal(JayAddress.CheckWords(account.Id, MnemonicLanguage.English), JayAddress.CheckWords(key, MnemonicLanguage.English));
        // The single-key addresses from before (version 0, the Ed25519 key itself) are not addresses any more.
        Assert.False(JayAddress.IsValid(Bech32m.Encode(JayAddress.Hrp, [0, .. Bech32m.ConvertBits(account.PublicKey, 8, 5, true)!])));
    }

    [Fact]
    public void DeviceCertificatesBindTheDeviceKey()
    {
        using var account = JayAccount.FromMnemonic(Bip39.Split(Abandon), null);
        using var keys = DeviceKeys.Generate();
        var device = keys.Dh.PublicKey.ToArray();
        var cert = account.CertifyDevice(keys, "Desk‮ PC\n", 0, DateTimeOffset.UtcNow);
        Assert.Equal("Desk PC", cert.DeviceName);
        Assert.True(cert.Verify(device));
        Assert.False(cert.Verify(RandomNumberGenerator.GetBytes(32)));

        var parsed = DeviceCertificate.FromJson(System.Text.Json.Nodes.JsonNode.Parse(cert.ToJson().ToJsonString()));
        Assert.NotNull(parsed);
        Assert.True(parsed.Verify(device));
        Assert.False((parsed with { DeviceName = "Other" }).Verify(device));
        Assert.False((parsed with { Epoch = 1 }).Verify(device));
        using var other = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        Assert.False((parsed with { AccountEd = other.PublicKey, AccountMlDsa = other.MlDsaPublicKey }).Verify(device));
        Assert.False((parsed with { DeviceEd = other.PublicKey }).Verify(device));
        Assert.Equal(account.Address, parsed.Account);
        Assert.Null(DeviceCertificate.FromJson(System.Text.Json.Nodes.JsonNode.Parse("{\"v\":2,\"account\":\"x\"}")));
    }

    [Fact]
    public void StoreKeepsSeedsEncryptedAndReopensAccounts()
    {
        using var keys = DeviceKeys.Generate();
        var device = keys.Dh.PublicKey.ToArray();
        var words = Bip39.Generate(MnemonicLanguage.English);
        string address;
        byte[] seed;
        using (var account = JayAccount.FromMnemonic(words, null))
        {
            address = account.Address;
            seed = Bip39.ToSeed(words, null);
            var store = new JayAccountStore(_dir);
            var entry = store.Add(account, "Main", backedUp: false, keys, "PC", DateTimeOffset.UtcNow);
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
        using var newKeys = DeviceKeys.Generate();
        var renewed = again.EnsureDeviceCertificate(address, newKeys, "PC", DateTimeOffset.UtcNow);
        Assert.True(renewed!.Device!.Verify(newKeys.Dh.PublicKey) && renewed.Device.Names(newKeys));
        Assert.Same(renewed, again.EnsureDeviceCertificate(address, newKeys, "PC", DateTimeOffset.UtcNow));
        Assert.True(renewed.BackedUp);

        again.Remove(address);
        var empty = new JayAccountStore(_dir);
        empty.Load();
        Assert.Empty(empty.Accounts);
    }

    /// <summary>An account saved with a version 0 address moves to its version 1 address, and says so for the bindings.</summary>
    [Fact]
    public void SavedVersion0AddressMovesToTheNewAddress()
    {
        using var keys = DeviceKeys.Generate();
        using var account = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        var store = new JayAccountStore(_dir);
        store.Add(account, "Main", backedUp: true, keys, "PC", DateTimeOffset.UtcNow);
        var path = Path.Combine(_dir, JayAccountStore.FileName);
        var legacyPath = Path.Combine(_dir, JayAccountStore.LegacyFileName);
        var old = JayAddress.EncodeVersion0(account.PublicKey);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["accounts"]![0]!["address"] = old;
        json["accounts"]![0]!["device"] = null;
        File.Delete(path);
        File.WriteAllText(legacyPath, json.ToJsonString());
        var legacyText = File.ReadAllText(legacyPath);

        var again = new JayAccountStore(_dir);
        again.Load();
        var moved = Assert.Single(again.Accounts);
        Assert.Equal((account.Address, "Main", true), (moved.Address, moved.Label, moved.BackedUp));
        Assert.Equal(account.Address, again.Readdressed[old]);
        var written = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["accounts"]![0]!;
        Assert.Equal((account.Address, old), ((string?)written["address"], (string?)written["was"]));
        // The old file stays exactly as it was, for older versions using this folder.
        Assert.Equal(legacyText, File.ReadAllText(legacyPath));
        // Later starts still know where the old address went, so bindings missed the first time can still follow.
        var later = new JayAccountStore(_dir);
        later.Load();
        Assert.Equal(account.Address, later.Readdressed[old]);
        using (var opened = again.Open(account.Address)) Assert.NotNull(opened);
        // The certificate is made once and then reused, not re-signed every time it is asked for.
        var first = again.EnsureDeviceCertificate(account.Address, keys, "PC", DateTimeOffset.UtcNow);
        Assert.Same(first, again.EnsureDeviceCertificate(account.Address, keys, "PC", DateTimeOffset.UtcNow));
        Assert.Single(again.Accounts);

        // Once the new file exists, the old one isn't read again: an account removed here doesn't come back from it.
        again.Remove(account.Address);
        var removed = new JayAccountStore(_dir);
        removed.Load();
        Assert.Empty(removed.Accounts);

        // An old address that the saved seed doesn't match isn't moved. The entry is kept as it is, through later saves,
        // so its seed is never lost. An address that is neither version, and a "was" that isn't a version 0 address,
        // are dropped.
        using var other = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        var otherOld = JayAddress.EncodeVersion0(other.PublicKey);
        var seed = (string?)json["accounts"]![0]!["seed"];
        File.WriteAllText(path, new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = 1,
            ["accounts"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject { ["address"] = otherOld, ["seed"] = seed },
                new System.Text.Json.Nodes.JsonObject { ["address"] = "jay1nonsense", ["seed"] = seed },
            },
        }.ToJsonString());
        var wrong = new JayAccountStore(_dir);
        wrong.Load();
        Assert.Empty(wrong.Accounts);
        Assert.Empty(wrong.Readdressed);
        using var third = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        wrong.Add(third, "Third", backedUp: false, keys, "PC", DateTimeOffset.UtcNow);
        var kept = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["accounts"]!.AsArray();
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, n => (string?)n!["address"] == otherOld && !string.IsNullOrEmpty((string?)n["seed"]));
    }

    /// <summary>
    /// Removing an account takes its encrypted seed out of every file here: the new file, the copy that couldn't be moved,
    /// and the older versions' file. Saving it again from the phrase drops an old copy that couldn't be moved.
    /// </summary>
    [Fact]
    public void RemovingAnAccountLeavesNoSeedBehind()
    {
        using var keys = DeviceKeys.Generate();
        using var account = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        using var keep = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        using var stuck = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        var store = new JayAccountStore(_dir);
        store.Add(account, "A", backedUp: true, keys, "PC", DateTimeOffset.UtcNow);
        store.Add(keep, "K", backedUp: true, keys, "PC", DateTimeOffset.UtcNow);
        var path = Path.Combine(_dir, JayAccountStore.FileName);
        var legacyPath = Path.Combine(_dir, JayAccountStore.LegacyFileName);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var seedOfA = (string?)json["accounts"]![0]!["seed"];
        json["accounts"]![0]!["address"] = JayAddress.EncodeVersion0(account.PublicKey);
        json["accounts"]![1]!["address"] = JayAddress.EncodeVersion0(keep.PublicKey);
        // An entry whose seed doesn't match its old address: it can't be moved, so it is kept as it is.
        json["accounts"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["address"] = JayAddress.EncodeVersion0(stuck.PublicKey), ["seed"] = seedOfA });
        File.Delete(path);
        File.WriteAllText(legacyPath, json.ToJsonString());

        var moved = new JayAccountStore(_dir);
        moved.Load();
        Assert.Equal(2, moved.Accounts.Count);
        moved.Remove(account.Address);
        Assert.DoesNotContain(JayAddress.EncodeVersion0(account.PublicKey), File.ReadAllText(legacyPath));
        Assert.Contains(JayAddress.EncodeVersion0(keep.PublicKey), File.ReadAllText(legacyPath));
        Assert.DoesNotContain(account.Address, File.ReadAllText(path));
        Assert.Contains(JayAddress.EncodeVersion0(stuck.PublicKey), File.ReadAllText(path));

        moved.Add(stuck, "S", backedUp: true, keys, "PC", DateTimeOffset.UtcNow);
        var entries = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["accounts"]!.AsArray();
        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(JayAddress.EncodeVersion0(stuck.PublicKey), File.ReadAllText(path));
    }

    /// <summary>
    /// Windows only: when the key protection can't be opened this logon (an SSH logon without a password), a version 0
    /// entry isn't moved and nothing throws; it is kept as it is for a later start.
    /// </summary>
    [Fact]
    public void LockedKeyProtectionLeavesVersion0EntriesForLater()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var account = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        Directory.CreateDirectory(_dir);
        var legacyPath = Path.Combine(_dir, JayAccountStore.LegacyFileName);
        var old = JayAddress.EncodeVersion0(account.PublicKey);
        var seed = Convert.ToBase64String([.. "AMBID1"u8.ToArray(), .. new byte[200]]);
        File.WriteAllText(legacyPath, new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = 1,
            ["accounts"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["address"] = old, ["seed"] = seed } },
        }.ToJsonString());
        Amber.Core.Multiplayer.MemberIdentity.DpapiProbe = () => false;
        try
        {
            var store = new JayAccountStore(_dir);
            store.Load();
            Assert.Empty(store.Accounts);
            Assert.Contains(old, File.ReadAllText(Path.Combine(_dir, JayAccountStore.FileName)));
        }
        finally
        {
            Amber.Core.Multiplayer.MemberIdentity.DpapiProbe = null;
        }
    }

    /// <summary>
    /// The "Me" page and the "is this their account?" request both show <see cref="JayAddress.CheckWordsText"/>: the words
    /// come from the account id in the address, so the owner and the friend see the same four.
    /// </summary>
    [Fact]
    public void CheckWordsTextMatchesTheAccountsCheckWords()
    {
        using var account = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        Assert.True(JayAddress.TryDecode(account.Address, out var id));
        foreach (var (ui, list) in new[] { ("en", MnemonicLanguage.English), ("ja", MnemonicLanguage.English), ("zh-Hans", MnemonicLanguage.ChineseSimplified), ("zh-Hant", MnemonicLanguage.ChineseSimplified), ("lzh", MnemonicLanguage.ChineseSimplified) })
            Assert.Equal(string.Join(" · ", JayAddress.CheckWords(id, list)), JayAddress.CheckWordsText(account.Address, ui));
        Assert.Equal(4, JayAddress.CheckWordsText(account.Address, "en").Split(" · ").Length);
        Assert.Equal("", JayAddress.CheckWordsText(JayAddress.EncodeVersion0(account.PublicKey), "en"));
        Assert.Equal("", JayAddress.CheckWordsText(null, "en"));
    }

    /// <summary>An entry left in the new file because it couldn't be moved before is moved, and saved, once it can be.</summary>
    [Fact]
    public void EntryThatCouldNotMoveBeforeIsSavedOnceMoved()
    {
        using var keys = DeviceKeys.Generate();
        using var account = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        var store = new JayAccountStore(_dir);
        store.Add(account, "A", backedUp: true, keys, "PC", DateTimeOffset.UtcNow);
        var path = Path.Combine(_dir, JayAccountStore.FileName);
        var old = JayAddress.EncodeVersion0(account.PublicKey);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["accounts"]![0]!["address"] = old;
        File.WriteAllText(path, json.ToJsonString());

        var moved = new JayAccountStore(_dir);
        moved.Load();
        Assert.Equal(account.Address, Assert.Single(moved.Accounts).Address);
        var written = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["accounts"]![0]!;
        Assert.Equal((account.Address, old), ((string?)written["address"], (string?)written["was"]));
    }

    /// <summary>A "was" that isn't a version 0 address is ignored, so two entries with the same junk can't break startup.</summary>
    [Fact]
    public void JunkFormerAddressesAreIgnored()
    {
        using var keys = DeviceKeys.Generate();
        using var a = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        using var b = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        var store = new JayAccountStore(_dir);
        store.Add(a, "A", backedUp: true, keys, "PC", DateTimeOffset.UtcNow);
        store.Add(b, "B", backedUp: true, keys, "PC", DateTimeOffset.UtcNow);
        var path = Path.Combine(_dir, JayAccountStore.FileName);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var entry in json["accounts"]!.AsArray()) entry!["was"] = "";
        File.WriteAllText(path, json.ToJsonString());
        var again = new JayAccountStore(_dir);
        again.Load();
        Assert.Equal(2, again.Accounts.Count);
        Assert.Empty(again.Readdressed);
        Assert.All(again.Accounts, e => Assert.Null(e.FormerAddress));
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
