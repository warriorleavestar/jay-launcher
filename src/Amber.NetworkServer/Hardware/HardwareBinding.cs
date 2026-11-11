using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.NetworkServer.Hardware;

/// <summary>
/// Binds a directory node to the machine it runs on (docs/security/node-hardware-binding.md).
/// <list type="number">
/// <item>On the node, <see cref="CreateRequest"/> reads the hardware and writes salted HMACs of each identifier, plus the
/// node's directory key, to hardware.request.json. No raw identifier leaves the machine.</item>
/// <item>Offline, the network's root key signs that as a <see cref="HardwareCertificate"/> (<c>jay-server network hw-cert</c>).</item>
/// <item>Back on the node, <see cref="Install"/> checks the certificate against the hardware and encrypts the node's keys
/// (directory.key, login.key) with a key that only this hardware (and its TPM, when it has one) can rebuild.</item>
/// <item>Every start, <see cref="NodeHardwareGuard"/> checks again before the node listens. Different hardware: the
/// node's files are wiped. Tampered runtime: the node refuses to run.</item>
/// </list>
/// </summary>
public static class HardwareBinding
{
    public const string RequestFile = "hardware.request.json";
    public const string CertificateFile = "hardware.cert";
    public const string WrapFile = "hardware.wrap";
    public const string SealedSuffix = ".hw";

    /// <summary>The key files that are kept only in encrypted form once a node is bound.</summary>
    public static readonly string[] KeyFiles = ["directory.key", "login.key"];

    private static readonly byte[] IdLabel = "jay-hw-id-v1"u8.ToArray();
    private static readonly byte[] WrapLabel = "jay-hw-wrap-v1"u8.ToArray();

    /// <summary>Public, salted id of one identifier: what the certificate carries.</summary>
    internal static byte[] FactorId(byte[] salt, HardwareFactor f) => Mac(salt, IdLabel, f);

    /// <summary>Secret share of one identifier: never written anywhere, only used to rebuild the key.</summary>
    internal static byte[] FactorShare(byte[] salt, HardwareFactor f) => Mac(salt, WrapLabel, f);

    private static byte[] Mac(byte[] salt, byte[] label, HardwareFactor f) =>
        HMACSHA256.HashData(salt, (byte[])[.. label, 0, .. Encoding.UTF8.GetBytes(f.Kind), 0, .. Encoding.UTF8.GetBytes(f.Value.Trim())]);

    /// <summary>Whether this data dir is bound (or was, and lost its certificate: that counts as bound).</summary>
    public static bool IsBound(string dataDir) =>
        File.Exists(Path.Combine(dataDir, CertificateFile)) || File.Exists(Path.Combine(dataDir, WrapFile))
        || KeyFiles.Any(k => File.Exists(Path.Combine(dataDir, k + SealedSuffix)));

    /// <summary>
    /// Step 1, on the node: hardware.request.json for the offline signer. Makes directory.key first if needed.
    /// </summary>
    public static JsonObject CreateRequest(string dataDir, IHardwareSource hardware, ITpmSealer tpm)
    {
        if (IsBound(dataDir)) throw new InvalidOperationException("This node is already bound to its hardware.");
        var factors = hardware.Read();
        if (factors.Count < 2) throw new InvalidOperationException("Fewer than two hardware identifiers are readable here; binding would not be reliable.");
        NodeWiper.Mark(dataDir);
        using var key = DirectoryChannel.LoadOrCreateIdentity(Path.Combine(dataDir, "directory.key"));
        var salt = RandomNumberGenerator.GetBytes(32);
        var ids = new JsonObject();
        foreach (var f in factors) ids[f.Kind] = Convert.ToBase64String(FactorId(salt, f));
        var request = new JsonObject
        {
            ["v"] = 1,
            ["key"] = Convert.ToBase64String(key.PublicKey),
            ["salt"] = Convert.ToBase64String(salt),
            ["f"] = ids,
            ["tpm"] = tpm.Available,
            ["pcr7"] = tpm.BindsBootState,
            ["secureBoot"] = RuntimeIntegrity.SecureBootEnabled(),
        };
        File.WriteAllText(Path.Combine(dataDir, RequestFile), request.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return request;
    }

    /// <summary>
    /// Step 3, on the node: checks the certificate against this hardware and this node's key, then encrypts the key files.
    /// Returns the files now kept only in sealed form.
    /// </summary>
    public static IReadOnlyList<string> Install(string dataDir, byte[] certificate, IHardwareSource hardware, ITpmSealer tpm, TrustQuorum trust)
    {
        if (File.Exists(Path.Combine(dataDir, WrapFile))) throw new InvalidOperationException("This node is already bound to its hardware.");
        var cert = HardwareCertificate.Parse(certificate) ?? throw new InvalidDataException("hardware.cert isn't a valid hardware certificate.");
        if (!trust.IsMet(cert.Signers))
            throw new InvalidDataException($"hardware.cert has {trust.Count(cert.Signers)} of the {trust.Need} root signatures this network needs ({trust.Keys.Count} roots listed).");
        var directoryKeyPath = Path.Combine(dataDir, "directory.key");
        using (var key = DirectoryChannel.LoadOrCreateIdentity(directoryKeyPath))
            if (!CryptographicOperations.FixedTimeEquals(key.PublicKey, cert.DirectoryKey)) throw new InvalidDataException("hardware.cert was issued for another directory key.");
        var match = cert.Match(hardware.Read());
        if (match.Matched != cert.Factors.Count) throw new InvalidDataException("hardware.cert doesn't match this machine: " + match.Reason);
        if (cert.RequireTpm && !tpm.Available) throw new InvalidDataException("hardware.cert requires a TPM and none is usable here.");

        // The data key: random, wrapped once per acceptable subset of identifiers (so one changed part doesn't lock the
        // node out), and additionally sealed to the TPM when the certificate asks for it.
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var wrap = new JsonObject { ["v"] = 1, ["blobs"] = WrapForSubsets(dataKey, cert, match.Shares) };
        if (cert.RequireTpm)
        {
            var tpmSecret = RandomNumberGenerator.GetBytes(32);
            wrap["tpm"] = Convert.ToBase64String(tpm.Seal(tpmSecret));
            dataKey = Combine(dataKey, tpmSecret);
        }

        var sealedFiles = new List<string>();
        foreach (var name in KeyFiles)
        {
            var plain = Path.Combine(dataDir, name);
            if (!File.Exists(plain)) continue;
            WritePrivate(plain + SealedSuffix, Encrypt(dataKey, File.ReadAllBytes(plain), Encoding.UTF8.GetBytes(name)));
            sealedFiles.Add(name);
        }
        WritePrivate(Path.Combine(dataDir, CertificateFile), certificate);
        WritePrivate(Path.Combine(dataDir, WrapFile), Encoding.UTF8.GetBytes(wrap.ToJsonString()));
        // Only now that the sealed copies exist: destroy the plaintext keys.
        foreach (var name in sealedFiles) Overwrite(Path.Combine(dataDir, name));
        File.Delete(Path.Combine(dataDir, RequestFile));
        return sealedFiles;
    }

    /// <summary>Opens the sealed key files with this hardware. Throws <see cref="HardwareMismatchException"/> or <see cref="TpmUnsealException"/>.</summary>
    internal static Dictionary<string, byte[]> Open(string dataDir, HardwareCertificate cert, HardwareMatch match, ITpmSealer tpm)
    {
        var wrapPath = Path.Combine(dataDir, WrapFile);
        if (!File.Exists(wrapPath)) throw new HardwareMismatchException("hardware.wrap is missing");
        JsonObject wrap;
        try { wrap = JsonNode.Parse(File.ReadAllBytes(wrapPath)) as JsonObject ?? throw new HardwareMismatchException("hardware.wrap is damaged"); }
        catch (System.Text.Json.JsonException) { throw new HardwareMismatchException("hardware.wrap is damaged"); }

        byte[]? dataKey = null;
        foreach (var blob in wrap["blobs"] as JsonArray ?? [])
        {
            if (blob is not JsonObject b || b["k"] is not JsonArray kinds) continue;
            var subset = kinds.Select(k => k?.GetValue<string>() ?? "").ToArray();
            if (!subset.All(match.Shares.ContainsKey)) continue;
            try
            {
                dataKey = Decrypt(SubsetKey(subset, match.Shares), Convert.FromBase64String(b["c"]?.GetValue<string>() ?? ""), Encoding.UTF8.GetBytes(string.Join(',', subset)));
                break;
            }
            catch (Exception e) when (e is CryptographicException or FormatException) { }
        }
        if (dataKey is null) throw new HardwareMismatchException("the hardware identifiers don't rebuild the node key");
        if (cert.RequireTpm)
        {
            var sealedSecret = wrap["tpm"]?.GetValue<string>() ?? throw new HardwareMismatchException("the TPM part of hardware.wrap is missing");
            dataKey = Combine(dataKey, tpm.Unseal(Convert.FromBase64String(sealedSecret)));
        }

        var keys = new Dictionary<string, byte[]>();
        foreach (var name in KeyFiles)
        {
            var path = Path.Combine(dataDir, name + SealedSuffix);
            if (!File.Exists(path)) continue;
            try { keys[name] = Decrypt(dataKey, File.ReadAllBytes(path), Encoding.UTF8.GetBytes(name)); }
            catch (CryptographicException) { throw new HardwareMismatchException(name + SealedSuffix + " doesn't open with this machine's key"); }
        }
        return keys;
    }

    /// <summary>One wrapped copy of the data key per subset of exactly <c>need</c> identifiers that keeps every mandatory one.</summary>
    private static JsonArray WrapForSubsets(byte[] dataKey, HardwareCertificate cert, IReadOnlyDictionary<string, byte[]> shares)
    {
        var blobs = new JsonArray();
        var kinds = cert.Factors.Keys.Order(StringComparer.Ordinal).ToArray();
        foreach (var subset in Subsets(kinds, cert.Need))
        {
            if (!cert.Must.All(subset.Contains)) continue;
            blobs.Add(new JsonObject
            {
                ["k"] = new JsonArray([.. subset.Select(k => (JsonNode)k)]),
                ["c"] = Convert.ToBase64String(Encrypt(SubsetKey(subset, shares), dataKey, Encoding.UTF8.GetBytes(string.Join(',', subset)))),
            });
        }
        return blobs;
    }

    internal static IEnumerable<string[]> Subsets(string[] items, int size)
    {
        if (size <= 0 || size > items.Length) yield break;
        var idx = Enumerable.Range(0, size).ToArray();
        while (true)
        {
            yield return [.. idx.Select(i => items[i])];
            var p = size - 1;
            while (p >= 0 && idx[p] == items.Length - size + p) p--;
            if (p < 0) yield break;
            idx[p]++;
            for (var q = p + 1; q < size; q++) idx[q] = idx[q - 1] + 1;
        }
    }

    private static byte[] SubsetKey(string[] subset, IReadOnlyDictionary<string, byte[]> shares)
    {
        var ikm = subset.Order(StringComparer.Ordinal).SelectMany(k => shares[k]).ToArray();
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, "jay-hw-subset-v1"u8.ToArray(), Encoding.UTF8.GetBytes(string.Join(',', subset.Order(StringComparer.Ordinal))));
    }

    private static byte[] Combine(byte[] hardwareKey, byte[] tpmSecret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, [.. hardwareKey, .. tpmSecret], 32, "jay-hw-tpm-v1"u8.ToArray(), []);

    private static byte[] Encrypt(byte[] key, byte[] plain, byte[] ad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var output = new byte[12 + plain.Length + 16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, output.AsSpan(12, plain.Length), output.AsSpan(12 + plain.Length), ad);
        nonce.CopyTo(output, 0);
        return output;
    }

    private static byte[] Decrypt(byte[] key, byte[] data, byte[] ad)
    {
        if (data.Length < 28) throw new CryptographicException("too short");
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, plain.Length), data.AsSpan(data.Length - 16), plain, ad);
        return plain;
    }

    private static void WritePrivate(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var f = new FileStream(path, options);
        f.Write(bytes);
        f.Flush(true);
    }

    private static void Overwrite(string path)
    {
        var length = new FileInfo(path).Length;
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Write)) { f.Write(RandomNumberGenerator.GetBytes((int)Math.Min(length, 1 << 20))); f.Flush(true); }
        File.Delete(path);
    }
}

/// <summary>The outcome of comparing the hardware with a certificate.</summary>
/// <param name="Shares">Secret shares of the identifiers that matched, by kind.</param>
public sealed record HardwareMatch(bool Ok, int Matched, int Need, string Reason, IReadOnlyDictionary<string, byte[]> Shares);

/// <summary>
/// The offline signature over a node's hardware: "directory key K belongs on the machine with these identifier ids;
/// at least <see cref="Need"/> of them (always including <see cref="Must"/>) must match". No expiry: a node must not
/// wipe itself because a date passed.
/// <para>
/// Signed bytes: <c>"jay-hardware-cert-v1\n"</c> + the JSON content. On disk, like <see cref="DirectoryCertificate"/>:
/// {"c": base64(content), "sigs": [{"r": root public key, "s": signature}, …]}. The network's roots sign one after another,
/// offline (<c>network hw-cert</c>, then <c>hw-cert --cosign</c>); a node accepts it only when the network's root quorum
/// (<see cref="JayTrust"/> for the built-in network, "dirRoots" + "dirRootsNeed" otherwise) is met. One bad signature
/// rejects the whole certificate.
/// </para>
/// </summary>
public sealed record HardwareCertificate(
    byte[] DirectoryKey, string? Network, byte[] Salt, IReadOnlyDictionary<string, byte[]> Factors, int Need, IReadOnlyList<string> Must,
    bool RequireTpm, bool RequireSecureBoot, IReadOnlyList<TrustSignature> Signatures, byte[] Content)
{
    private static readonly byte[] Domain = "jay-hardware-cert-v1\n"u8.ToArray();
    public const int MaxSize = 4096;
    public const int MaxSignatures = 8;

    /// <summary>The roots that signed (all verified).</summary>
    public IEnumerable<byte[]> Signers => Signatures.Select(sig => sig.Key);

    /// <summary>SHA-256 of the signed bytes: what two signers compare out loud before signing.</summary>
    public byte[] Digest => SHA256.HashData([.. Domain, .. Content]);

    /// <summary>Adds another root's signature. Refuses a certificate that doesn't verify, or a root that already signed.</summary>
    public static byte[] Cosign(ReadOnlySpan<byte> certificate, NetworkSigner root)
    {
        var cert = Parse(certificate) ?? throw new InvalidDataException("not a valid hardware certificate");
        if (cert.Signers.Any(k => k.AsSpan().SequenceEqual(root.PublicKey))) throw new InvalidDataException("this root already signed");
        if (cert.Signatures.Count >= MaxSignatures) throw new InvalidDataException("too many signatures");
        return Write(cert.Content, [.. cert.Signatures, new TrustSignature(root.PublicKey, root.Sign([.. Domain, .. cert.Content]))]);
    }

    private static byte[] Write(byte[] content, IEnumerable<TrustSignature> signatures) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["c"] = Convert.ToBase64String(content),
        ["sigs"] = new JsonArray([.. signatures.Select(sig => (JsonNode)new JsonObject
        {
            ["r"] = Convert.ToBase64String(sig.Key),
            ["s"] = Convert.ToBase64String(sig.Signature),
        })]),
    }.ToJsonString());

    /// <summary>The unsigned content a first signer would sign for this request (to show it before signing).</summary>
    public static HardwareCertificate Preview(JsonObject request, string? network, int? need = null, bool? requireTpm = null, bool requireSecureBoot = false) =>
        FromContent(BuildContent(request, network, need, requireTpm, requireSecureBoot), [])
        ?? throw new InvalidDataException("Not a hardware request from jay-server node bind.");

    /// <summary>
    /// Signs a node's hardware.request.json (run offline). <paramref name="need"/> defaults to all identifiers but one
    /// (at least two); a TPM identifier, when present, is always mandatory.
    /// </summary>
    public static byte[] Issue(NetworkSigner root, JsonObject request, string? network, int? need = null, bool? requireTpm = null, bool requireSecureBoot = false)
    {
        var bytes = BuildContent(request, network, need, requireTpm, requireSecureBoot);
        return Write(bytes, [new TrustSignature(root.PublicKey, root.Sign([.. Domain, .. bytes]))]);
    }

    private static byte[] BuildContent(JsonObject request, string? network, int? need, bool? requireTpm, bool requireSecureBoot)
    {
        var key = Convert.FromBase64String(request["key"]?.GetValue<string>() ?? "");
        var salt = Convert.FromBase64String(request["salt"]?.GetValue<string>() ?? "");
        if (key.Length != DirectoryChannel.KeyLength || salt.Length != 32 || request["f"] is not JsonObject ids || ids.Count < 2)
            throw new InvalidDataException("Not a hardware request from jay-server node bind.");
        var count = ids.Count;
        var n = need ?? Math.Max(2, count - 1);
        if (n < 2 || n > count) throw new ArgumentException($"--need must be between 2 and {count}");
        var tpm = requireTpm ?? request["tpm"]?.GetValue<bool>() == true;
        var must = ids.ContainsKey("tpm") ? new JsonArray("tpm") : [];
        var content = new JsonObject
        {
            ["v"] = 1,
            ["key"] = Convert.ToBase64String(key),
            ["salt"] = Convert.ToBase64String(salt),
            ["f"] = ids.DeepClone(),
            ["need"] = n,
            ["must"] = must,
            ["tpm"] = tpm,
            ["secureBoot"] = requireSecureBoot,
        };
        if (network is not null) content["net"] = network;
        return Encoding.UTF8.GetBytes(content.ToJsonString());
    }

    /// <summary>
    /// Reads the certificate and checks every signature: one bad or repeated signature rejects it. Doesn't check whether
    /// the signers meet a quorum (the caller does, with the network's roots). Null when invalid.
    /// </summary>
    public static HardwareCertificate? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxSize) return null;
        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject wire || wire["sigs"] is not JsonArray sigs || sigs.Count is 0 or > MaxSignatures) return null;
            var content = Convert.FromBase64String(wire["c"]?.GetValue<string>() ?? "");
            var signatures = new List<TrustSignature>();
            foreach (var entry in sigs)
            {
                if (entry is not JsonObject e) return null;
                var sig = new TrustSignature(Convert.FromBase64String(e["r"]?.GetValue<string>() ?? ""), Convert.FromBase64String(e["s"]?.GetValue<string>() ?? ""));
                if (signatures.Any(x => x.Key.AsSpan().SequenceEqual(sig.Key)) || !sig.Verify([.. Domain, .. content])) return null;
                signatures.Add(sig);
            }
            return FromContent(content, signatures);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException) { return null; }
    }

    private static HardwareCertificate? FromContent(byte[] content, IReadOnlyList<TrustSignature> signatures)
    {
        try
        {
            if (JsonNode.Parse(content) is not JsonObject o || o["v"]?.GetValue<int>() != 1) return null;
            var key = Convert.FromBase64String(o["key"]?.GetValue<string>() ?? "");
            var salt = Convert.FromBase64String(o["salt"]?.GetValue<string>() ?? "");
            if (key.Length != DirectoryChannel.KeyLength || salt.Length != 32 || o["f"] is not JsonObject ids) return null;
            // Only the fields this version knows: a signer must never vouch for something Jay would silently ignore.
            if (o.Select(p => p.Key).Any(k => k is not ("v" or "key" or "salt" or "f" or "need" or "must" or "tpm" or "secureBoot" or "net"))) return null;
            var factors = ids.ToDictionary(p => p.Key, p => Convert.FromBase64String(p.Value!.GetValue<string>()));
            var need = o["need"]?.GetValue<int>() ?? 0;
            var must = (o["must"] as JsonArray ?? []).Select(m => m!.GetValue<string>()).ToArray();
            if (need < 1 || need > factors.Count || must.Any(m => !factors.ContainsKey(m))) return null;
            return new HardwareCertificate(key, o["net"]?.GetValue<string>(), salt, factors, need, must,
                o["tpm"]?.GetValue<bool>() == true, o["secureBoot"]?.GetValue<bool>() == true, signatures, content);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Compares this machine's identifiers with the certificate.</summary>
    public HardwareMatch Match(IReadOnlyList<HardwareFactor> hardware)
    {
        var shares = new Dictionary<string, byte[]>();
        foreach (var f in hardware)
            if (Factors.TryGetValue(f.Kind, out var id) && CryptographicOperations.FixedTimeEquals(id, HardwareBinding.FactorId(Salt, f)))
                shares[f.Kind] = HardwareBinding.FactorShare(Salt, f);
        var missingMust = Must.Where(m => !shares.ContainsKey(m)).ToArray();
        var changed = Factors.Keys.Where(k => !shares.ContainsKey(k)).Order(StringComparer.Ordinal).ToArray();
        var ok = shares.Count >= Need && missingMust.Length == 0;
        var reason = ok ? (changed.Length == 0 ? "all identifiers match" : "tolerated change: " + string.Join(", ", changed))
            : missingMust.Length > 0 ? "mandatory identifier changed: " + string.Join(", ", missingMust)
            : $"{shares.Count} of {Factors.Count} identifiers match, {Need} needed (changed: {string.Join(", ", changed)})";
        return new HardwareMatch(ok, shares.Count, Need, reason, shares);
    }
}

/// <summary>The node runs on different hardware than its certificate says (or its bound files were tampered with).</summary>
public sealed class HardwareMismatchException(string reason) : IOException("Hardware check failed: " + reason)
{
    public string Reason { get; } = reason;
    /// <summary>What the wipe removed (empty in a dry run or when nothing was there).</summary>
    public IReadOnlyList<string> Wiped { get; init; } = [];
    public bool DryRun { get; init; }
}

/// <summary>The node refuses to run: something watches or modifies it, or the TPM won't unseal because the boot state changed.</summary>
public sealed class NodeIntegrityException(IReadOnlyList<string> problems) : IOException("Refusing to run: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// The start-up check of a bound node. Runs before the node opens any port; also re-checks the runtime while it runs.
/// </summary>
public sealed class NodeHardwareGuard(string dataDir)
{
    public IHardwareSource Hardware { get; init; } = SystemHardwareSource.Instance;
    public ITpmSealer Tpm { get; init; } = TpmSealers.ForThisMachine();
    public IRuntimeIntegrity Integrity { get; init; } = RuntimeIntegrity.Instance;
    /// <summary>
    /// Who must have signed hardware.cert: the network's root quorum (<see cref="JayTrust"/> for the built-in network,
    /// "dirRoots" + "dirRootsNeed" otherwise). A certificate that verifies but falls short only makes the node refuse to
    /// run (a wrong --network flag must not wipe a node); a missing or badly signed one is treated as tampering.
    /// </summary>
    public TrustQuorum Trust { get; init; } = TrustQuorum.None;
    /// <summary>Only list what a failed check would wipe; delete nothing.</summary>
    public bool DryRun { get; init; }
    public Action<string> Log { get; init; } = _ => { };

    public bool RequireSecureBoot { get; private set; }

    /// <summary>
    /// Checks the node. Returns null when it isn't bound (nothing to do), otherwise the opened key files by name.
    /// Throws <see cref="HardwareMismatchException"/> after wiping, or <see cref="NodeIntegrityException"/> (nothing wiped).
    /// </summary>
    public Dictionary<string, byte[]>? Enforce()
    {
        // A wiped node stays down: starting it again would quietly make a new, unbound identity.
        if (File.Exists(Path.Combine(dataDir, NodeWiper.LogFile)))
            throw new HardwareMismatchException($"this node wiped itself earlier ({NodeWiper.LogFile}); restore its keys into a fresh data dir and bind it again");
        if (!HardwareBinding.IsBound(dataDir)) return null;

        var certPath = Path.Combine(dataDir, HardwareBinding.CertificateFile);
        var cert = File.Exists(certPath) ? HardwareCertificate.Parse(File.ReadAllBytes(certPath)) : null;
        RequireSecureBoot = cert?.RequireSecureBoot == true;

        // Runtime first: a watched process must not even try the keys.
        var problems = Integrity.Problems(dataDir, RequireSecureBoot);
        if (problems.Count > 0) throw new NodeIntegrityException(problems);

        if (cert is null) Fail("hardware.cert is missing or its signature is invalid");
        if (!Trust.IsMet(cert!.Signers))
            throw new NodeIntegrityException([$"hardware.cert has {Trust.Count(cert.Signers)} of the {Trust.Need} root signatures this network needs; check --network"]);
        var match = cert!.Match(Hardware.Read());
        if (!match.Ok) Fail(match.Reason);
        if (cert.RequireTpm && !Tpm.Available) Fail("the certificate requires a TPM and this machine has none");

        Dictionary<string, byte[]> keys = [];
        try { keys = HardwareBinding.Open(dataDir, cert, match, Tpm); }
        catch (TpmUnsealException e) { throw new NodeIntegrityException([e.Message]); }
        catch (HardwareMismatchException e) { Fail(e.Reason); }

        if (keys.TryGetValue("directory.key", out var dirKey))
        {
            using var pair = X25519KeyPair.FromPrivateKey(Convert.FromBase64String(Encoding.ASCII.GetString(dirKey).Trim()));
            if (!CryptographicOperations.FixedTimeEquals(pair.PublicKey, cert.DirectoryKey)) Fail("the sealed directory key isn't the certified one");
        }
        else Fail("directory.key" + HardwareBinding.SealedSuffix + " is missing");
        Log($"Hardware check passed ({match.Reason}{(cert.RequireTpm ? ", TPM" : "")}{(Tpm.BindsBootState && cert.RequireTpm ? " + Secure Boot state" : "")})");
        return keys;
    }

    /// <summary>The periodic check while running. Empty when fine.</summary>
    public IReadOnlyList<string> Recheck() => Integrity.Problems(dataDir, RequireSecureBoot);

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(string reason)
    {
        Log((DryRun ? "[dry run] " : "") + "Hardware check FAILED: " + reason + (DryRun ? "; would wipe:" : "; wiping this node's Jay files"));
        var wiped = NodeWiper.Wipe(dataDir, reason, DryRun);
        foreach (var path in wiped) Log("  " + path);
        throw new HardwareMismatchException(reason) { Wiped = wiped, DryRun = DryRun };
    }
}
