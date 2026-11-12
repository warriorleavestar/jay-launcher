using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amber.Core.Localization;

namespace Amber.Core.Launch;

/// <summary>A newer release found by <see cref="UpdateCheck"/>, and whether its signatures held up.</summary>
/// <param name="Info">Version, release page and notes from the feed.</param>
/// <param name="Check">The release signature check. Only <see cref="ReleaseVerdict.Valid"/> may be offered to the user.</param>
/// <param name="Sums">The verified SHA256SUMS.txt (file name → lowercase hex); empty unless the check is valid.</param>
/// <param name="Assets">Download addresses of the release's files, by name.</param>
public sealed record VerifiedUpdate(UpdateInfo Info, ReleaseCheck Check, IReadOnlyDictionary<string, string> Sums, IReadOnlyDictionary<string, Uri> Assets)
{
    public bool IsVerified => Check.IsValid;
}

/// <summary>
/// The update check with release signatures (privacy plan, item 2). It reads the same feed as <see cref="UpdateChecker"/>
/// (GitHub Releases API: <c>tag_name</c>, <c>html_url</c>, <c>body</c>, <c>assets[].name/browser_download_url</c>). For a
/// newer release it downloads SHA256SUMS.txt, SHA256SUMS.txt.sigs and trust-chain.json from the release's assets and
/// checks them with <see cref="ReleaseSignature.Verify"/> before anything is shown as an update. The highest release seq
/// accepted so far is kept in <c>release-seq.txt</c> in the data directory, so an older release can't be passed off later.
/// <see cref="DownloadAsync"/> fetches a package of a verified release and keeps it only when its SHA-256 matches the list.
/// </summary>
public sealed class UpdateCheck(HttpClient http, string dataDir, TrustList? trust = null)
{
    public const string StateFileName = "release-seq.txt";
    private const int MaxSums = 1024 * 1024;
    private const int MaxSigs = 128 * 1024;
    private const int MaxChain = 1024 * 1024;

    private string StatePath => Path.Combine(dataDir, StateFileName);

    /// <summary>The highest release seq this install has accepted (0 when none).</summary>
    public long HighestSeq
    {
        get
        {
            try
            {
                return long.TryParse(File.ReadAllText(StatePath).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seq) ? seq : 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        }
    }

    /// <summary>Null when no feed is set or there is nothing newer; otherwise the newer release with its signature verdict.</summary>
    public async Task<VerifiedUpdate?> CheckAsync(string? feedUrl, string currentVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl)) return null;
        using var scope = Net.NetworkPolicy.Scope(Net.NetworkCategory.UpdateCheck);
        // The feed address is typed by the user and the reply comes from the network: neither may throw anything but
        // the exceptions callers already report (a bad address or a reply of the wrong shape is InvalidDataException).
        if (!Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var feed) || (feed.Scheme != Uri.UriSchemeHttps && feed.Scheme != Uri.UriSchemeHttp))
            throw new InvalidDataException(L.T("tools.download.invalid_url"));
        using var doc = JsonDocument.Parse(await http.GetStringAsync(feed, ct));
        var root = doc.RootElement;
        var tag = Text(root, "tag_name") ?? throw new InvalidDataException(L.T("core.launch.update.missingversion"));
        if (!UpdateChecker.IsNewer(tag, currentVersion)) return null;
        string? page = null;
        if (Text(root, "html_url") is { } url)
        {
            if (!UpdateChecker.TryGetSafeUpdateUrl(url, out var validated)) throw new InvalidDataException(L.T("tools.download.invalid_url"));
            page = validated!.AbsoluteUri;
        }
        var info = new UpdateInfo(tag.TrimStart('v', 'V'), page, Text(root, "body"));

        var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var a in list.EnumerateArray())
                if (Text(a, "name") is { Length: > 0 } name && name == Path.GetFileName(name)
                    && UpdateChecker.TryGetSafeUpdateUrl(Text(a, "browser_download_url"), out var link)
                    && link!.Scheme == Uri.UriSchemeHttps)
                    assets.TryAdd(name, link);

        var empty = new Dictionary<string, string>();
        if (!assets.TryGetValue(ReleaseSignature.SumsFileName, out var sumsUrl) || !assets.TryGetValue(ReleaseSignature.SignatureFileName, out var sigsUrl))
            return new(info, new(ReleaseVerdict.Unsigned, null, 0), empty, assets);
        var sums = await GetAsync(sumsUrl, MaxSums, ct);
        var sigs = Encoding.UTF8.GetString(await GetAsync(sigsUrl, MaxSigs, ct));
        var chain = assets.TryGetValue(ReleaseSignature.ChainFileName, out var chainUrl) ? Encoding.UTF8.GetString(await GetAsync(chainUrl, MaxChain, ct)) : null;

        var check = ReleaseSignature.Verify(sigs, sums, chain, tag, trust, HighestSeq);
        if (!check.IsValid) return new(info, check, empty, assets);
        Remember(check.Seq);
        return new(info, check, ReleaseSignature.ParseSums(Encoding.UTF8.GetString(sums)), assets);
    }

    /// <summary>
    /// Downloads <paramref name="name"/> from a verified release into <paramref name="destinationDir"/>. The file appears
    /// there only when its SHA-256 equals the one in the signed list; otherwise nothing is kept and this throws.
    /// </summary>
    public async Task<string> DownloadAsync(VerifiedUpdate update, string name, string destinationDir, CancellationToken ct = default)
    {
        if (!update.IsVerified) throw new InvalidOperationException("release is not verified");
        if (!update.Sums.TryGetValue(name, out var expected) || !update.Assets.TryGetValue(name, out var url))
            throw new InvalidDataException($"{name} is not listed in the signed SHA256SUMS.txt");
        Directory.CreateDirectory(destinationDir);
        var target = Path.Combine(destinationDir, name);
        var partial = target + ".part";
        using (var scope = Net.NetworkPolicy.Scope(Net.NetworkCategory.UpdateCheck))
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(file, ct);
        }
        string actual;
        await using (var read = File.OpenRead(partial)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, ct));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected)))
        {
            File.Delete(partial);
            throw new InvalidDataException($"{name}: SHA-256 differs from the signed list");
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>Why a release was not offered, in the user's language.</summary>
    public static string Explain(ReleaseVerdict verdict) => L.T(verdict switch
    {
        ReleaseVerdict.Unsigned => "tools.update.verdict.unsigned",
        ReleaseVerdict.NoTrustedKey => "tools.update.verdict.notrustedkey",
        ReleaseVerdict.SumsMismatch => "tools.update.verdict.sumsmismatch",
        ReleaseVerdict.Rollback => "tools.update.verdict.rollback",
        ReleaseVerdict.BrokenChain => "tools.update.verdict.brokenchain",
        ReleaseVerdict.NotEnoughSignatures => "tools.update.verdict.notenoughsignatures",
        ReleaseVerdict.WrongTag => "tools.update.verdict.wrongtag",
        ReleaseVerdict.NotListed => "tools.update.verdict.notlisted",
        ReleaseVerdict.HashMismatch => "tools.update.verdict.hashmismatch",
        ReleaseVerdict.WrongComponent => "tools.update.verdict.wrongcomponent",
        _ => "tools.update.verdict.malformed",
    });

    /// <summary>The string value of <paramref name="name"/>, or null when the element is not an object or the value is not a string.</summary>
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void Remember(long seq)
    {
        if (seq <= HighestSeq) return;
        Directory.CreateDirectory(dataDir);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, seq.ToString(CultureInfo.InvariantCulture) + "\n");
        File.Move(temp, StatePath, overwrite: true);
    }

    private async Task<byte[]> GetAsync(Uri url, int max, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > max) throw new InvalidDataException($"{url.AbsolutePath} is too large");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) throw new InvalidDataException($"{url.AbsolutePath} is too large");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
