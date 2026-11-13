using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Amber.Core.Multiplayer;

/// <summary>本机“对局域网开放”的一局游戏。</summary>
public sealed record LanGame(string Motd, int Port);

/// <summary>
/// 找到本机开放到局域网的游戏，以及在加入方电脑上把远端房间“广播”成局域网游戏。
/// 游戏每 1.5 秒往 224.0.2.60:4445 组播一条 [MOTD]世界名[/MOTD][AD]端口[/AD]，多人游戏界面靠它列出局域网游戏。
/// </summary>
public static partial class LanGames
{
    public const string MulticastAddress = "224.0.2.60";
    public const int MulticastPort = 4445;

    /// <summary>
    /// Finds the "open to LAN" port in a game log line: the integrated server's "Started serving on 12345", or the
    /// system chat notice ("Local game hosted on port 12345" and its translations).
    /// The text has to start the message. Player chat ("[CHAT] &lt;Steve&gt; Started serving on 22") and server
    /// broadcasts ("[CHAT] [Server] …") don't count, so nobody in the room can point the room at another local port by chatting.
    /// </summary>
    public static int? PortFromLog(string line)
    {
        var message = LogPrefix().Replace(line, "", 1);
        var m = PortPattern().Match(message);
        return m.Success && int.TryParse(m.Groups[1].Value, out var port) && port is > 0 and < 65536 ? port : null;
    }

    /// <summary>"[12:00:01] [Server thread/INFO]: " (each part optional), then an optional "[System] [CHAT] ".</summary>
    [GeneratedRegex(@"^\s*(?:\[[^\]/]*\]\s*)?(?:\[[^\]]*/[A-Z]+\]:?\s*)?(?:\[System\]\s*)?(?:\[CHAT\]\s*)?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LogPrefix();

    [GeneratedRegex(@"^(?:Started serving on|Local game hosted on port|(?:本地游戏已|本地遊戲已)?在端口)\s*(\d{2,5})\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PortPattern();

    public static LanGame? Parse(string message)
    {
        var m = AnnouncePattern().Match(message);
        return m.Success && int.TryParse(m.Groups[2].Value, out var port) && port is > 0 and < 65536 ? new LanGame(m.Groups[1].Value, port) : null;
    }

    [GeneratedRegex(@"\[MOTD\](.*?)\[/MOTD\]\[AD\](?:[^\]:]*:)?(\d{1,5})\[/AD\]", RegexOptions.Singleline)]
    private static partial Regex AnnouncePattern();

    public static string Format(string motd, int port) => $"[MOTD]{motd}[/MOTD][AD]{port}[/AD]";

    /// <summary>
    /// 听一会儿本机的局域网广播，返回听到的游戏（按端口去重）。只收来自本机地址的广播，
    /// 别人电脑上开的局域网游戏不能拿来开房间。
    /// </summary>
    public static async Task<List<LanGame>> ListenAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var found = new Dictionary<int, LanGame>();
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        client.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
        var local = LocalAddresses();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(duration);
        try
        {
            while (true)
            {
                var result = await client.ReceiveAsync(timeout.Token);
                if (!local.Contains(result.RemoteEndPoint.Address)) continue;
                if (Parse(Encoding.UTF8.GetString(result.Buffer)) is { } game) found[game.Port] = game;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 时间到。
        }
        return found.Values.OrderBy(g => g.Port).ToList();
    }

    /// <summary>
    /// Listens for this computer's LAN broadcasts until <paramref name="ct"/> is cancelled, reporting each one heard.
    /// Like <see cref="ListenAsync"/>, broadcasts from other computers are ignored. The local addresses are read again
    /// every minute, since a room's virtual adapter can come up after listening starts.
    /// </summary>
    public static async Task WatchAsync(Action<LanGame> heard, CancellationToken ct)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        client.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
        var local = LocalAddresses();
        var readAt = DateTime.UtcNow;
        try
        {
            while (true)
            {
                var result = await client.ReceiveAsync(ct);
                if (DateTime.UtcNow - readAt > TimeSpan.FromMinutes(1))
                {
                    local = LocalAddresses();
                    readAt = DateTime.UtcNow;
                }
                if (!local.Contains(result.RemoteEndPoint.Address)) continue;
                if (Parse(Encoding.UTF8.GetString(result.Buffer)) is { } game) heard(game);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The room stopped listening.
        }
    }

    private static HashSet<IPAddress> LocalAddresses()
    {
        var set = new HashSet<IPAddress> { IPAddress.Loopback };
        try
        {
            foreach (var a in Dns.GetHostAddresses(Dns.GetHostName())) set.Add(a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a);
        }
        catch (SocketException) { }
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            foreach (var u in nic.GetIPProperties().UnicastAddresses) set.Add(u.Address);
        return set;
    }

    /// <summary>
    /// 在加入方电脑上持续广播，让游戏的“多人游戏”列表里出现这个房间（地址是本机，端口是转发端口）。
    /// 只发到本机回环，不会发到真实局域网。
    /// </summary>
    public static async Task AnnounceAsync(string motd, int port, CancellationToken ct)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 0);
        var bytes = Encoding.UTF8.GetBytes(Format(motd, port));
        var target = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await client.SendAsync(bytes, target, ct);
            }
            catch (SocketException)
            {
                // 网卡变化时偶尔失败，下一轮再发。
            }
            try
            {
                await Task.Delay(1500, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
