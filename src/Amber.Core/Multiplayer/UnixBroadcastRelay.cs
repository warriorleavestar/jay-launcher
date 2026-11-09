using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Mac/Linux 完整局域网模式的 UDP 广播转发（Windows 上 EasyTier 自己用 --enable-udp-broadcast-relay 做）。
/// 很多游戏往 255.255.255.255（或本机网段的广播地址）发包找局域网房间，这些包走的是物理网卡，到不了虚拟网卡。
/// root 守护抓本机从物理网卡发出的 UDP 广播，原样（同样的源端口、目标端口和内容）从虚拟网卡再发一份到 255.255.255.255，
/// EasyTier 会把它交给房间里所有人；对方回包是发给本机虚拟 IP 的单播，本来就通。
/// Linux 用 AF_PACKET 抓包，macOS 用系统自带的 libpcap；发包用绑定到虚拟网卡的原始 UDP 套接字。
/// 只转发本机发出的包（不转发从局域网收到的），从虚拟网卡发出的包不再抓，不会绕圈；每秒最多转发 200 个包。
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixBroadcastRelay : IDisposable
{
    internal const int MaxPerSecond = 200;
    internal delegate void PacketHandler(ReadOnlySpan<byte> packet);
    private readonly CancellationTokenSource _stop = new();
    private readonly IPAddress _virtualIp;
    private readonly Action<string> _log;
    private readonly Thread _thread;

    private UnixBroadcastRelay(IPAddress virtualIp, Action<string> log)
    {
        _virtualIp = virtualIp;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Jay broadcast relay" };
        _thread.Start();
    }

    public static IDisposable Start(IPAddress virtualIp, Action<string> log) => new UnixBroadcastRelay(virtualIp, log);

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(3));
    }

    private void Run()
    {
        try
        {
            // 等 EasyTier 建好虚拟网卡（带上本机虚拟 IP）。
            NetworkInterface? tun = null;
            for (var i = 0; i < 120 && !_stop.IsCancellationRequested && tun is null; i++)
            {
                tun = FindByAddress(_virtualIp);
                if (tun is null) _stop.Token.WaitHandle.WaitOne(500);
            }
            if (tun is null || _stop.IsCancellationRequested) return;
            using var sender = OpenSender(tun);
            _log($"broadcast relay: started on {tun.Name}");
            var limiter = new RateLimiter(MaxPerSecond);
            // 第一次转发成功、第一次发送失败各记一行（排查用，不刷屏）。
            var loggedOk = false;
            var loggedError = false;
            void Forward(ReadOnlySpan<byte> ipPacket)
            {
                if (!TryParse(ipPacket, out var srcPort, out var dstPort, out var payload)) return;
                if (!limiter.Take()) return;
                var datagram = BuildUdp(srcPort, dstPort, payload);
                try
                {
                    sender.SendTo(datagram, new IPEndPoint(IPAddress.Broadcast, 0));
                    if (!loggedOk) { loggedOk = true; _log($"broadcast relay: forwarded first packet to port {dstPort}"); }
                }
                catch (SocketException e)
                {
                    if (!loggedError) { loggedError = true; _log($"broadcast relay: send failed: {e.SocketErrorCode} {e.Message}"); }
                }
            }
            if (OperatingSystem.IsLinux()) CaptureLinux(TunIndex(tun), Forward);
            else if (OperatingSystem.IsMacOS()) CaptureMac(tun.Name, Forward);
        }
        catch (Exception e) when (e is SocketException or IOException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or NetworkInformationException)
        {
            _log("broadcast relay stopped: " + e.Message);
        }
    }

    internal static NetworkInterface? FindByAddress(IPAddress address) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(address)));

    private static int TunIndex(NetworkInterface tun) => tun.GetIPProperties().GetIPv4Properties()?.Index ?? -1;

    private Socket OpenSender(NetworkInterface tun)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Udp) { EnableBroadcast = true, ReceiveBufferSize = 1024 };
        if (OperatingSystem.IsLinux())
        {
            var name = System.Text.Encoding.ASCII.GetBytes(tun.Name + "\0");
            socket.SetRawSocketOption(1, 25, name); // SOL_SOCKET, SO_BINDTODEVICE
        }
        else if (OperatingSystem.IsMacOS())
        {
            Span<byte> index = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(index, TunIndex(tun));
            socket.SetRawSocketOption(0, 25, index); // IPPROTO_IP, IP_BOUND_IF
        }
        socket.Bind(new IPEndPoint(_virtualIp, 0));
        return socket;
    }

    /// <summary>从 IPv4 包里取出发往广播地址的 UDP（不要分片的包和发自虚拟网段的包）。</summary>
    internal static bool TryParse(ReadOnlySpan<byte> ip, out ushort srcPort, out ushort dstPort, out ReadOnlySpan<byte> payload)
    {
        srcPort = dstPort = 0;
        payload = default;
        if (ip.Length < 28 || ip[0] >> 4 != 4 || ip[9] != 17) return false;
        var ihl = (ip[0] & 0x0f) * 4;
        if (ihl < 20 || ip.Length < ihl + 8) return false;
        // 分片的包（MF 位或偏移不为 0）不转发。
        if ((BinaryPrimitives.ReadUInt16BigEndian(ip[6..8]) & 0x3fff) != 0) return false;
        // 目标必须是广播地址：255.255.255.255 或某个网段的广播（最后一字节 255，具体网段由抓包时的过滤条件保证）。
        if (ip[19] != 255) return false;
        // 虚拟网段自己的包不转发（防止绕圈）。
        if (ip[12] == 10 && ip[13] == 233 && ip[14] == 144) return false;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(ip[2..4]);
        if (totalLength > ip.Length || totalLength < ihl + 8) return false;
        var udp = ip[ihl..totalLength];
        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(udp[4..6]);
        if (udpLength < 8 || udpLength > udp.Length) return false;
        srcPort = BinaryPrimitives.ReadUInt16BigEndian(udp[..2]);
        dstPort = BinaryPrimitives.ReadUInt16BigEndian(udp[2..4]);
        payload = udp[8..udpLength];
        return dstPort != 0;
    }

    /// <summary>UDP 头 + 内容；校验和填 0（IPv4 允许），IP 头由系统按绑定的虚拟 IP 生成。</summary>
    internal static byte[] BuildUdp(ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4, 2), (ushort)buffer.Length);
        payload.CopyTo(buffer.AsSpan(8));
        return buffer;
    }

    internal sealed class RateLimiter(int perSecond)
    {
        private long _windowStart = Environment.TickCount64;
        private int _count;

        public bool Take()
        {
            var now = Environment.TickCount64;
            if (now - _windowStart >= 1000) { _windowStart = now; _count = 0; }
            return ++_count <= perSecond;
        }
    }

    // ---------- Linux：AF_PACKET ----------

    private const int AF_PACKET = 17, SOCK_DGRAM = 2, SOL_SOCKET = 1, SO_ATTACH_FILTER = 26, SO_RCVTIMEO = 20, PACKET_OUTGOING = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SockFilter { public ushort Code; public byte Jt; public byte Jf; public uint K; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockFprog { public ushort Len; public IntPtr Filter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeVal { public long Sec; public long Usec; }

    [DllImport("libc", SetLastError = true)] private static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] private static extern unsafe int setsockopt(int fd, int level, int name, void* value, uint length);
    [DllImport("libc", SetLastError = true)] private static extern unsafe nint recvfrom(int fd, byte* buffer, nuint length, int flags, byte* address, uint* addressLength);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    /// <summary>
    /// 内核里先过滤（经典 BPF，偏移从 IP 头算起）：IPv4、本机发出（PACKET_OUTGOING）、UDP、目标地址最后一字节是 255。
    /// 发出的包只有 ETH_P_ALL 的套接字收得到，所以协议和方向用 BPF 的附加字段（SKF_AD_PROTOCOL / SKF_AD_PKTTYPE）判断。
    /// </summary>
    internal static SockFilter[] LinuxFilter() =>
    [
        new() { Code = 0x20, K = 0xfffff000 },              // ld #proto
        new() { Code = 0x15, Jt = 0, Jf = 7, K = 0x0800 },  // IPv4 ? 下一条 : 拒绝
        new() { Code = 0x20, K = 0xfffff004 },              // ld #pkttype
        new() { Code = 0x15, Jt = 0, Jf = 5, K = PACKET_OUTGOING },
        new() { Code = 0x30, K = 9 },                       // ldb [9] 协议
        new() { Code = 0x15, Jt = 0, Jf = 3, K = 17 },
        new() { Code = 0x30, K = 19 },                      // ldb [19] 目标地址最后一字节
        new() { Code = 0x15, Jt = 0, Jf = 1, K = 255 },
        new() { Code = 0x06, K = 0xffff },                  // 接受
        new() { Code = 0x06, K = 0 },                       // 拒绝
    ];

    private unsafe void CaptureLinux(int tunIndex, PacketHandler forward)
    {
        var fd = socket(AF_PACKET, SOCK_DGRAM, BinaryPrimitives.ReverseEndianness((ushort)0x0003)); // ETH_P_ALL
        if (fd < 0) throw new SocketException(Marshal.GetLastPInvokeError());
        try
        {
            var filter = LinuxFilter();
            fixed (SockFilter* f = filter)
            {
                var prog = new SockFprog { Len = (ushort)filter.Length, Filter = (IntPtr)f };
                if (setsockopt(fd, SOL_SOCKET, SO_ATTACH_FILTER, &prog, (uint)sizeof(SockFprog)) != 0) throw new SocketException(Marshal.GetLastPInvokeError());
            }
            var timeout = new TimeVal { Sec = 0, Usec = 500_000 };
            setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, (uint)sizeof(TimeVal));
            var loopback = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties().GetIPv4Properties()?.Index ?? -1).ToHashSet();
            var buffer = new byte[65536];
            var address = new byte[32]; // struct sockaddr_ll
            var broadcasts = LocalBroadcasts();
            var refreshed = Environment.TickCount64;
            while (!_stop.IsCancellationRequested)
            {
                uint addressLength = (uint)address.Length;
                nint n;
                fixed (byte* b = buffer) fixed (byte* a = address) n = recvfrom(fd, b, (nuint)buffer.Length, 0, a, &addressLength);
                if (n <= 0) continue;
                var ifIndex = BitConverter.ToInt32(address, 4);
                var packetType = address[10];
                var protocol = BinaryPrimitives.ReadUInt16BigEndian(address.AsSpan(2, 2));
                if (packetType != PACKET_OUTGOING || protocol != 0x0800 || ifIndex == tunIndex || loopback.Contains(ifIndex)) continue;
                if (Environment.TickCount64 - refreshed > 10_000) { broadcasts = LocalBroadcasts(); refreshed = Environment.TickCount64; }
                var packet = buffer.AsSpan(0, (int)n);
                if (packet.Length >= 20 && !IsBroadcastTarget(packet[16..20], broadcasts)) continue;
                forward(packet);
            }
        }
        finally { close(fd); }
    }

    /// <summary>本机物理网卡所在网段的广播地址（游戏有时发往 192.168.1.255 这类地址），加上 255.255.255.255。</summary>
    internal static HashSet<uint> LocalBroadcasts()
    {
        var set = new HashSet<uint> { 0xffffffff };
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var a in n.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.PrefixLength is <= 0 or >= 31) continue;
                var ip = BinaryPrimitives.ReadUInt32BigEndian(a.Address.GetAddressBytes());
                var mask = uint.MaxValue << (32 - a.PrefixLength);
                set.Add(ip | ~mask);
            }
        }
        return set;
    }

    internal static bool IsBroadcastTarget(ReadOnlySpan<byte> destination, HashSet<uint> broadcasts) =>
        broadcasts.Contains(BinaryPrimitives.ReadUInt32BigEndian(destination));

    // ---------- macOS：libpcap ----------

    private const string Pcap = "/usr/lib/libpcap.dylib";

    [StructLayout(LayoutKind.Sequential)]
    private struct BpfProgram { public uint Len; public IntPtr Insns; }

    [DllImport(Pcap)] private static extern unsafe IntPtr pcap_open_live(string device, int snaplen, int promisc, int timeoutMs, byte* errbuf);
    [DllImport(Pcap)] private static extern unsafe int pcap_compile(IntPtr p, BpfProgram* program, string filter, int optimize, uint netmask);
    [DllImport(Pcap)] private static extern unsafe int pcap_setfilter(IntPtr p, BpfProgram* program);
    [DllImport(Pcap)] private static extern unsafe void pcap_freecode(BpfProgram* program);
    [DllImport(Pcap)] private static extern unsafe int pcap_next_ex(IntPtr p, IntPtr* header, byte** data);
    [DllImport(Pcap)] private static extern int pcap_datalink(IntPtr p);
    [DllImport(Pcap)] private static extern void pcap_close(IntPtr p);

    /// <summary>每块有 IPv4 地址的物理网卡开一个抓包线程，只抓本机地址发往广播地址的 UDP。</summary>
    private void CaptureMac(string tunName, PacketHandler forward)
    {
        var gate = new object();
        var threads = new List<Thread>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.Name == tunName || n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.OperationalStatus != OperationalStatus.Up) continue;
            var addresses = n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.PrefixLength is > 0 and < 31).ToList();
            if (addresses.Count == 0 || n.Name.StartsWith("utun", StringComparison.Ordinal)) continue;
            var sources = string.Join(" or ", addresses.Select(a => $"src host {a.Address}"));
            var targets = string.Join(" or ", addresses.Select(a =>
            {
                var ip = BinaryPrimitives.ReadUInt32BigEndian(a.Address.GetAddressBytes());
                var b = ip | ~(uint.MaxValue << (32 - a.PrefixLength));
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, b);
                return $"dst host {new IPAddress(bytes)}";
            }).Prepend("dst host 255.255.255.255"));
            var expression = $"ip and udp and ({sources}) and ({targets})";
            var name = n.Name;
            _log($"broadcast relay: capturing on {name}");
            var thread = new Thread(() =>
            {
                try { CaptureMacInterface(name, expression, packet => { lock (gate) forward(packet); }); }
                catch (Exception e) when (e is IOException or DllNotFoundException or EntryPointNotFoundException) { _log($"broadcast relay ({name}): {e.Message}"); }
            }) { IsBackground = true };
            thread.Start();
            threads.Add(thread);
        }
        foreach (var t in threads) t.Join();
    }


    private unsafe void CaptureMacInterface(string device, string expression, PacketHandler forward)
    {
        var errbuf = stackalloc byte[256];
        var p = pcap_open_live(device, 65535, 0, 500, errbuf);
        if (p == IntPtr.Zero) throw new IOException(Marshal.PtrToStringUTF8((IntPtr)errbuf) ?? "pcap_open_live failed");
        try
        {
            var program = new BpfProgram();
            if (pcap_compile(p, &program, expression, 1, 0xffffffff) != 0) throw new IOException("pcap_compile failed");
            try { if (pcap_setfilter(p, &program) != 0) throw new IOException("pcap_setfilter failed"); }
            finally { pcap_freecode(&program); }
            // DLT_NULL（4 字节协议族）、DLT_EN10MB（14 字节以太网头）、DLT_RAW（直接是 IP 包）。
            var offset = pcap_datalink(p) switch { 0 => 4, 1 => 14, 12 => 0, _ => -1 };
            if (offset < 0) return;
            // struct pcap_pkthdr { struct timeval ts; bpf_u_int32 caplen; bpf_u_int32 len; }，64 位 macOS 上 timeval 是 16 字节。
            const int CaplenOffset = 16;
            while (!_stop.IsCancellationRequested)
            {
                IntPtr header;
                byte* data;
                var r = pcap_next_ex(p, &header, &data);
                if (r == 0) continue;
                if (r < 0) return;
                var caplen = *(uint*)((byte*)header + CaplenOffset);
                if (caplen <= offset) continue;
                forward(new ReadOnlySpan<byte>(data + offset, (int)caplen - offset));
            }
        }
        finally { pcap_close(p); }
    }
}
