using Amber.Core.Localization;
using Amber.Core.Net;

namespace Amber.Core.Auth;

/// <summary>启动时要用的身份：会话信息 + 外置登录需要的额外 JVM 参数。</summary>
public sealed record ResolvedSession(GameSession Session, IReadOnlyList<string> ExtraJvmArgs);

/// <summary>把账号变成可启动的会话，必要时刷新令牌并保存。</summary>
public sealed class AccountService(HttpClient http, AccountStore store, string dataDir, Func<string?> clientIdProvider)
{
    public AccountStore Store { get; } = store;

    /// <summary>用户配置里的 Client ID 优先，其次环境变量，都没有时用内置的默认值。</summary>
    public string? MicrosoftClientId
    {
        get
        {
            var configured = clientIdProvider();
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            var env = Environment.GetEnvironmentVariable(MicrosoftAuth.ClientIdEnvironmentVariable);
            return string.IsNullOrWhiteSpace(env) ? MicrosoftAuth.DefaultClientId : env.Trim();
        }
    }

    public MicrosoftAuth CreateMicrosoftAuth() =>
        new(http, MicrosoftClientId ?? throw new InvalidOperationException(L.T("core.auth.microsoft.missingclientid")));

    // 外置登录用自己的 HttpClient：不自动跟随跳转（见 ExternalAuth）。
    public ExternalAuth External { get; } = new(ExternalAuth.CreateHttpClient());

    public Account AddOffline(string name)
    {
        var account = new Account { Kind = AccountKind.Offline, Name = name.Trim(), Uuid = OfflineAccount.OfflineUuid(name.Trim()) };
        Store.AddOrUpdate(account);
        return account;
    }

    public async Task<ResolvedSession> ResolveAsync(Account account, CancellationToken ct = default)
    {
        switch (account.Kind)
        {
            case AccountKind.Offline:
                return new ResolvedSession(OfflineAccount.Create(account.Name), []);

            case AccountKind.Microsoft:
                await CreateMicrosoftAuth().RefreshAsync(account, ct);
                Store.Save();
                return new ResolvedSession(
                    new GameSession(account.Name, account.Uuid, account.AccessToken!, "msa", account.Xuid, MicrosoftClientId ?? ""), []);

            case AccountKind.External:
                // 旧版本存下的 http 服务器：启动时说清楚原因和怎么办（不只在账号页提示）。
                if (ExternalAuth.HasInsecureServer(account)) throw new InvalidOperationException(L.F("core.auth.external.insecure_saved", account.Name));
                await External.EnsureValidAsync(account, ct);
                Store.Save();
                var metadata = await External.GetMetadataAsync(account.ServerUrl!, ct);
                var jar = await new AuthlibInjector(http, Path.Combine(dataDir, "authlib-injector")).EnsureJarAsync(ct);
                return new ResolvedSession(
                    new GameSession(account.Name, account.Uuid, account.AccessToken!, "mojang"),
                    AuthlibInjector.JvmArgs(jar, account.ServerUrl!, metadata));

            default:
                throw new ArgumentOutOfRangeException(nameof(account));
        }
    }

    /// <summary>下载并缓存皮肤，返回 PNG 路径。没有皮肤或下载失败时返回 null（预览退回默认头像）。</summary>
    private const int MaxSkinBytes = 4 * 1024 * 1024;
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public async Task<string?> GetSkinFileAsync(Account account, CancellationToken ct = default)
    {
        try
        {
            if (account is { Kind: AccountKind.External, SkinUrl: null }) await External.LoadSkinAsync(account, ct);
            if (account.SkinUrl is not { } url) return null;
            // A third-party login server names the skin address: only plain web addresses, a capped size, and a real PNG.
            if (!WebLinks.TryGetHttpUrl(url, out var uri)) return null;
            var folder = Path.Combine(dataDir, "skins");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".png");
            if (File.Exists(path)) return path;
            byte[] bytes;
            using (NetworkPolicy.Scope(NetworkCategory.Login))
            {
                // A slow or silent skin server must not hold the preview (or anything awaiting it) for long.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(SkinTotalTimeout);
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                bytes = await ReadCappedAsync(stream, MaxSkinBytes, SkinStallTimeout, deadline.Token);
            }
            if (!bytes.AsSpan().StartsWith(PngSignature)) return null;
            // A temp name per attempt, so two fetches of the same skin at once do not write over each other.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, ct);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or FormatException or InvalidDataException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly TimeSpan SkinTotalTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SkinStallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads <paramref name="stream"/> to the end without blocking a thread. Throws <see cref="InvalidDataException"/> past
    /// <paramref name="max"/> bytes and <see cref="OperationCanceledException"/> when no byte arrives for
    /// <paramref name="stall"/> or <paramref name="ct"/> is cancelled.
    /// </summary>
    internal static async Task<byte[]> ReadCappedAsync(Stream stream, int max, TimeSpan stall, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quiet.CancelAfter(stall);
            var n = await stream.ReadAsync(chunk, quiet.Token);
            if (n == 0) return buffer.ToArray();
            if (buffer.Length + n > max) throw new InvalidDataException("too large");
            buffer.Write(chunk, 0, n);
        }
    }
}
