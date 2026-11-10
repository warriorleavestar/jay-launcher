using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.NetworkServer;

/// <summary>管理口令：32 字节随机数（十六进制），存在数据目录的 admin.token，只有本机这个用户能读。</summary>
public static class AdminToken
{
    public static string LoadOrCreate(string path)
    {
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: 64 } existing && existing.All(char.IsAsciiHexDigit)) return existing;
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, token + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return token;
    }

    public static bool Matches(string expected, string? given) =>
        given is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(given.Trim()));
}

/// <summary>
/// 本机管理接口：一个只听 127.0.0.1 的小 HTTP 服务（一次一个请求，用完就关连接）。
/// <list type="bullet">
/// <item><c>GET /health</c>：不用口令，给 systemd / Docker / 监控用。</item>
/// <item>其他都要 <c>Authorization: Bearer &lt;admin.token&gt;</c>：
/// <c>GET /stats</c>、<c>GET /rooms</c>、<c>GET /bans</c>、<c>POST /ban</c> {kind, value, reason}、<c>POST /unban</c> {kind, value}、<c>POST /remove</c> {id}。</item>
/// </list>
/// 只听本机，又要口令：同一台机器上别的用户读不到口令文件，也就管不了。
/// </summary>
public sealed class AdminApi : IAsyncDisposable
{
    public const int MaxBody = 16 * 1024;
    private const int MaxHeader = 8 * 1024;

    private readonly JayNetworkServer _server;
    private readonly string _token;
    private readonly Action<string> _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(8, 8);
    private readonly Task _loop;

    public int Port { get; }

    private AdminApi(JayNetworkServer server, int port, string token, Action<string> log)
    {
        _server = server;
        _token = token;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = AcceptAsync();
    }

    internal static AdminApi Start(JayNetworkServer server, int port, string token, Action<string> log) => new(server, port, token, log);

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            if (!_slots.Wait(0)) { client.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(client); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException) { }
                finally { client.Dispose(); _slots.Release(); }
            });
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();
        var (method, path, headers, body) = await ReadRequestAsync(stream, deadline.Token);
        int status;
        string contentType;
        byte[] bytes;
        if (!IsLocalHost(headers.GetValueOrDefault("host")) || !IsLocalOrigin(headers.GetValueOrDefault("origin")))
        {
            // DNS rebinding or a cross-site page: refuse before anything else.
            (status, contentType, bytes) = (403, "application/json; charset=utf-8", Json(Fail("host")));
        }
        else if (method == "GET" && AdminPanel.Find(path.Split('?')[0]) is { } file)
        {
            (status, contentType, bytes) = (200, file.ContentType, file.Bytes);
        }
        else
        {
            var (code, reply) = Route(method, path, headers.GetValueOrDefault("authorization"), body);
            (status, contentType, bytes) = (code, "application/json; charset=utf-8", Json(reply));
        }
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n" +
            "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\n" +
            $"Content-Security-Policy: {AdminPanel.ContentSecurityPolicy}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), deadline.Token);
        await stream.WriteAsync(bytes, deadline.Token);
    }

    private static byte[] Json(JsonObject reply) => Encoding.UTF8.GetBytes(reply.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

    /// <summary>The Host header must name this machine (127.0.0.1, localhost or [::1], any port so SSH tunnels work).</summary>
    internal static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().ToLowerInvariant();
        var name = h.StartsWith('[') ? h[..(h.IndexOf(']') + 1)] : h.Split(':')[0];
        var port = h[name.Length..];
        return name is "127.0.0.1" or "localhost" or "[::1]" && (port.Length == 0 || (port[0] == ':' && port.Length <= 6 && port[1..].All(char.IsAsciiDigit) && port.Length > 1));
    }

    /// <summary>Browsers send Origin on cross-site requests; only this machine's own pages may call the API.</summary>
    internal static bool IsLocalOrigin(string? origin) =>
        origin is null || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == "http" && IsLocalHost(uri.Authority));

    /// <summary>读一个 HTTP/1.1 请求：请求行、头（最多 8 KB）、按 Content-Length 读正文（最多 16 KB）。</summary>
    internal static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeader + MaxBody];
        var length = 0;
        int end;
        while ((end = IndexOf(buffer, length, "\r\n\r\n"u8)) < 0)
        {
            if (length >= MaxHeader) throw new InvalidDataException("header");
            var read = await stream.ReadAsync(buffer.AsMemory(length, MaxHeader - length), ct);
            if (read == 0) throw new EndOfStreamException();
            length += read;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3) throw new InvalidDataException("line");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var size = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
        if (size is < 0 or > MaxBody) throw new InvalidDataException("body");
        var start = end + 4;
        while (length - start < size)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, start + size - length), ct);
            if (read == 0) throw new EndOfStreamException();
            length += read;
        }
        return (first[0], first[1], headers, Encoding.UTF8.GetString(buffer, start, size));
    }

    private static int IndexOf(byte[] buffer, int length, ReadOnlySpan<byte> pattern) => buffer.AsSpan(0, length).IndexOf(pattern);

    private static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };

    internal (int Status, JsonObject Reply) Route(string method, string path, string? authorization, string body)
    {
        path = path.Split('?')[0];
        if (method == "GET" && path == "/health") return (200, _server.Health());
        var bearer = authorization is { } a && a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? a[7..] : null;
        if (!AdminToken.Matches(_token, bearer)) return (401, Fail("token"));
        try
        {
            JsonObject Body() => JsonNode.Parse(body.Length == 0 ? "{}" : body) as JsonObject ?? throw new FormatException();
            switch (method, path)
            {
                case ("GET", "/stats"):
                    return (200, new JsonObject { ["ok"] = true, ["health"] = _server.Health(), ["stats"] = _server.Stats.ToJson() });
                case ("GET", "/rooms"):
                {
                    var view = _server.Directory?.View();
                    var rooms = _server.Directory?.ListedRooms() ?? [];
                    return (200, new JsonObject
                    {
                        ["ok"] = true,
                        ["total"] = view?.Rooms ?? 0,
                        ["hidden"] = (view?.Rooms ?? 0) - (view?.Listed ?? 0),
                        ["rooms"] = new JsonArray([.. rooms.Select(r => (JsonNode)new JsonObject
                        {
                            ["id"] = r.Id, ["name"] = r.Name, ["host"] = r.Host, ["hostFingerprint"] = r.HostFingerprint,
                            ["mode"] = r.Mode.ToString().ToLowerInvariant(), ["players"] = r.Players, ["game"] = r.Game,
                            ["account"] = r.Account, ["updated"] = r.Updated.ToUnixTimeSeconds(),
                        })]),
                    });
                }
                case ("GET", "/nodes"):
                    return (200, new JsonObject
                    {
                        ["ok"] = true,
                        ["enabled"] = _server.Volunteers is not null,
                        ["nodes"] = new JsonArray([.. (_server.Volunteers?.Listed() ?? []).Select(n => (JsonNode)n)]),
                    });
                case ("GET", "/bans"):
                    return (200, new JsonObject
                    {
                        ["ok"] = true,
                        ["bans"] = new JsonArray([.. (_server.Bans?.List() ?? []).Select(b => (JsonNode)new JsonObject
                        {
                            ["kind"] = b.Kind, ["value"] = b.Value, ["reason"] = b.Reason, ["at"] = b.At.ToUnixTimeSeconds(),
                            ["until"] = b.Until?.ToUnixTimeSeconds(),
                        })]),
                    });
                case ("POST", "/ban"):
                {
                    var o = Body();
                    TimeSpan? duration = o["for"]?.GetValue<long>() is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
                    var kind = o["kind"]?.GetValue<string>() ?? "";
                    var value = o["value"]?.GetValue<string>() ?? "";
                    var reason = o["reason"]?.GetValue<string>();
                    if (kind == "room-host")
                    {
                        // Ban whoever hosts this room: host fingerprint plus the account they published with.
                        if (_server.BanRoomHost(value, reason, duration) is not { } hit) return (404, Fail("not_found"));
                        _log($"admin: banned the host of room {value}, removed {hit.Removed} room(s)");
                        return (200, new JsonObject { ["ok"] = true, ["removed"] = hit.Removed, ["host"] = hit.Host, ["account"] = hit.Account });
                    }
                    var removed = _server.Ban(kind, value, reason, duration);
                    _log($"admin: banned {kind} {value}, removed {removed} room(s)");
                    return (200, new JsonObject { ["ok"] = true, ["removed"] = removed });
                }
                case ("POST", "/unban"):
                {
                    var o = Body();
                    if (_server.Bans is not { } bans) return (400, Fail("no_bans"));
                    var ok = bans.Remove(o["kind"]?.GetValue<string>() ?? "", o["value"]?.GetValue<string>() ?? "");
                    return (ok ? 200 : 404, ok ? new JsonObject { ["ok"] = true } : Fail("not_found"));
                }
                case ("POST", "/remove"):
                {
                    var id = Body()["id"]?.GetValue<string>() ?? "";
                    var ok = _server.Directory?.Remove(id) ?? false;
                    if (ok) _log($"管理：删掉房间 {id}");
                    return (ok ? 200 : 404, ok ? new JsonObject { ["ok"] = true } : Fail("not_found"));
                }
                default:
                    return (404, Fail("path"));
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or JsonException or InvalidOperationException)
        {
            return (400, Fail("bad"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        _cts.Dispose();
    }
}

/// <summary>管理接口的客户端（jay-server node 命令用，也可以给你自己的面板用）。</summary>
public sealed class AdminClient(HttpClient http, int port, string token)
{
    public static AdminClient ForDataDir(HttpClient http, string dataDir, int port = JayNetworkServer.DefaultAdminPort)
    {
        var path = Path.Combine(dataDir, "admin.token");
        if (!File.Exists(path)) throw new InvalidOperationException($"没找到管理口令 {path}：节点还没运行过，或者 --dir 不对");
        return new AdminClient(http, port, File.ReadAllText(path).Trim());
    }

    public async Task<JsonObject> CallAsync(string method, string path, JsonObject? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{port}{path}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("管理接口回了看不懂的东西");
    }
}
