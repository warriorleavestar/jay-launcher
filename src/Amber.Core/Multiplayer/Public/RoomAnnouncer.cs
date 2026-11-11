using System.Net.Sockets;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// 房主：每 30 秒把房间发布 / 续期到目录服务（地址可能被 EasyTier 改掉，每次都带最新的）；关房间时撤下。
/// 隐藏房间不进列表，只登记邀请码的查找键，拿着邀请码的人才找得到。
/// </summary>
public sealed class RoomAnnouncer : IAsyncDisposable
{
    private readonly IPublicNetwork _network;
    private readonly RoomDirectoryClient _client;
    private readonly Func<RoomListing> _listing;
    private readonly Func<IReadOnlyList<string>> _lookupKeys;
    private readonly Func<byte[]?>? _searchKey;
    private readonly Func<string?>? _account;
    private readonly Action<string>? _mail;
    private readonly Action<string>? _log;
    private readonly TimeSpan _every;
    private readonly CancellationTokenSource _cts = new();
    private readonly RoomSigner _signer = new();
    private readonly Task _loop;
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>上一次发布成功了没有（界面显示“已发布” / 失败原因）。</summary>
    public bool Published { get; private set; }
    public string? LastError { get; private set; }
    public event Action? StateChanged;

    public string RoomId => _signer.RoomId;

    /// <param name="listing">当前的房间信息（每次续期时取，地址由这里填）。</param>
    /// <param name="lookupKeys">现在还能用的邀请码对应的查找键（<see cref="RoomDirectoryProtocol.LookupKey"/>）。</param>
    /// <param name="mail">目录捎来的话（直连房间的回复码）。</param>
    public RoomAnnouncer(IPublicNetwork network, Func<RoomListing> listing, Func<IReadOnlyList<string>> lookupKeys, Action<string>? mail = null,
        Action<string>? log = null, TimeSpan? every = null, RoomDirectoryClient? client = null, Func<byte[]?>? searchKey = null, Func<string?>? account = null)
    {
        _account = account;
        _searchKey = searchKey;
        _network = network;
        _client = client ?? new RoomDirectoryClient(network);
        _listing = listing;
        _lookupKeys = lookupKeys;
        _mail = mail;
        _log = log;
        _every = every ?? RoomDirectoryProtocol.RenewEvery;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>房间信息变了（人数、模式）：马上续一次，不等下一轮。</summary>
    public void Refresh() => _wake.Release();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await AnnounceOnceAsync(_cts.Token);
            try { await _wake.WaitAsync(_every, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private readonly SemaphoreSlim _one = new(1, 1);

    internal async Task AnnounceOnceAsync(CancellationToken ct)
    {
        // 一次只发一个：序号在签名时定，先签的必须先到，否则目录会把后到的旧序号当重放拒掉。
        try { await _one.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try { await AnnounceCoreAsync(ct); }
        finally { _one.Release(); }
    }

    private async Task AnnounceCoreAsync(CancellationToken ct)
    {
        string? error = null;
        try
        {
            var ip = await _network.MyIpAsync(ct);
            if (ip is null) error = L.T("core.mp.public.no_address");
            else
            {
                var listing = _signer.Sign(_listing() with { Ip = ip }, DateTimeOffset.UtcNow);
                var keys = _lookupKeys();
                var search = _searchKey?.Invoke();
                // 隐藏房间又没有能用的邀请、也没开指纹查找：目录里没什么可登记的（之前登记的 90 秒后自己过期）。
                if (listing.Mode == RoomVisibility.Hidden && keys.Count == 0 && search is null)
                {
                    // 之前在列表里（刚撤下）：马上从目录里撤掉，不等 90 秒过期。
                    if (Published) await _client.WithdrawAsync(_signer, ct);
                    if (Published || LastError is not null)
                    {
                        Published = false;
                        LastError = null;
                        StateChanged?.Invoke();
                    }
                    return;
                }
                var mails = await _client.AnnounceAsync(listing, listing.Mode != RoomVisibility.Hidden, keys, ct, search, _account?.Invoke());
                foreach (var m in mails) _mail?.Invoke(m);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or OperationCanceledException or ArgumentException)
        {
            error = e.Message;
        }
        var published = error is null;
        if (published != Published || error != LastError)
        {
            Published = published;
            LastError = error;
            if (error is not null) _log?.Invoke(L.F("core.mp.public.announce_failed", error));
            StateChanged?.Invoke();
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
        if (Published)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _client.WithdrawAsync(_signer, timeout.Token); }
            catch (Exception e) when (e is IOException or SocketException or InvalidDataException or OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}

/// <summary>加入方：经公共网络找房主、敲门。</summary>
public static class PublicJoin
{
    /// <summary>
    /// 经公共网络连到房主的敲门端口敲门。<paramref name="expectedHostKey"/> 给了时核对握手里房主的公钥（房间列表 / 目录里登记的那把）：
    /// 对不上就当没成功，免得有人在目录里冒用别人的房间。
    /// </summary>
    public static async Task<KnockOutcome> KnockAsync(IPublicNetwork network, string hostIp, JayInvite invite, X25519KeyPair me, string name, string version,
        byte[]? expectedHostKey, CancellationToken ct, DeviceFingerprint? device = null, string? account = null, Identity.DeviceCertificate? jay = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var connected = false;
            try
            {
                await using var stream = await network.ConnectAsync(hostIp, KnockProtocol.Port, ct);
                connected = true;
                var outcome = await KnockClient.KnockAsync(stream, invite, me, name, version, ct, device: device, account: account, expectedHost: expectedHostKey, jay: jay);
                if (expectedHostKey is not null && !outcome.HostPublicKey.AsSpan().SequenceEqual(expectedHostKey))
                    return new KnockOutcome(null, "mismatch", outcome.HostPublicKey);
                return outcome;
            }
            catch (WrongHostException)
            {
                // Someone else answered at this address: stop before saying anything about myself.
                return new KnockOutcome(null, "mismatch", []);
            }
            catch (Exception e) when (connected && IsRejected(e) && !ct.IsCancellationRequested)
            {
                // 握手前就被断开：房主不认这张邀请（密码不对、邀请已撤销），或者这个地址上已经不是这个房主。
                return new KnockOutcome(null, "invalid", []);
            }
            catch (IOException) when (attempt < 2 && !ct.IsCancellationRequested)
            {
                await Task.Delay(1500, ct);
            }
        }
    }

    // 房主不认就直接断开：Linux/macOS 上读到的是 EOF，Windows 上常是连接被重置/中止。
    private static bool IsRejected(Exception e) => e switch
    {
        EndOfStreamException or System.Security.Cryptography.CryptographicException => true,
        IOException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted } } => true,
        _ => false,
    };

    /// <summary>拿着邀请码：先在目录里找房主；找到就经公共网络敲门，找不到返回 null（调用方改走邀请码自己的大厅）。</summary>
    /// <param name="expectedHost">A mailed invite names its host: a directory entry with another key is ignored (null).</param>
    public static async Task<KnockOutcome?> TryKnockByInviteAsync(IPublicNetwork network, JayInvite invite, X25519KeyPair me, string name, string version,
        CancellationToken ct, RoomDirectoryClient? client = null, DeviceFingerprint? device = null, string? account = null, Identity.DeviceCertificate? jay = null,
        byte[]? expectedHost = null)
    {
        var hit = await (client ?? new RoomDirectoryClient(network)).LookupAsync(invite.LobbyKey, ct);
        if (hit is null) return null;
        if (expectedHost is not null && !hit.HostKey.AsSpan().SequenceEqual(expectedHost)) return null;
        return await KnockAsync(network, hit.Ip, invite, me, name, version, hit.HostKey, ct, device, account, jay);
    }
}
