using System.Collections.Concurrent;

namespace Amber.Core.Net;

/// <summary>
/// The five kinds of network use Jay has (official design §8.8). "About and privacy" lists exactly these; every HTTP
/// request Jay sends belongs to one of them.
/// </summary>
public enum NetworkCategory
{
    /// <summary>Downloads you start: game, loaders, Java, mods, modpacks, multiplayer components, any file.</summary>
    Download = 1,
    /// <summary>Microsoft sign-in and refresh, your own skin; skin-site sign-in.</summary>
    Login = 2,
    /// <summary>Modrinth and CurseForge: search, details, checking mods for updates when you ask.</summary>
    ContentApi = 3,
    /// <summary>Checking for a new Jay.</summary>
    UpdateCheck = 4,
    /// <summary>Multiplayer: rooms, public networks, Jay servers, port mapping, server status.</summary>
    Multiplayer = 5,
}

/// <summary>A request that fits none of the five categories. Thrown only in strict mode (tests, the network sandbox).</summary>
public sealed class UnclassifiedRequestException(Uri? uri) : InvalidOperationException($"Network request outside the network list: {uri}")
{
    public Uri? Uri { get; } = uri;
}

/// <summary>
/// The network list as code: every HTTP request is put in a <see cref="NetworkCategory"/> by, in order, an explicit
/// <see cref="Scope"/> around the call, the HttpClient's own category, or the host (well-known services). In strict mode
/// an unclassified request throws instead of going out; otherwise it is counted so a test can find it.
/// Nothing is sent anywhere: this only labels and counts requests on this computer.
/// </summary>
public static class NetworkPolicy
{
    private static readonly AsyncLocal<NetworkCategory?> Current = new();
    private static readonly ConcurrentDictionary<string, NetworkCategory> Hosts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<NetworkCategory, DateTimeOffset> LastUse = new();
    private static int _unclassified;

    /// <summary>When true, an unclassified request throws <see cref="UnclassifiedRequestException"/> before it is sent.</summary>
    public static bool Strict { get; set; }

    private static readonly AsyncLocal<bool> StrictHere = new();

    /// <summary>Strict mode only for calls made inside the returned scope (tests, without touching other threads).</summary>
    public static IDisposable StrictScope()
    {
        var previous = StrictHere.Value;
        StrictHere.Value = true;
        return new RestoreStrict(previous);
    }

    /// <summary>Unclassified requests seen since start (non-strict mode).</summary>
    public static int UnclassifiedCount => _unclassified;

    /// <summary>Raised for every request with its category (null = unclassified).</summary>
    public static event Action<NetworkCategory?, Uri?>? Requested;

    // Well-known services. Mirrors and user-entered addresses (update feed, skin sites, CurseForge root, nodes) are
    // registered at run time with Register or covered by the client's category / a scope.
    private static readonly (string Host, NetworkCategory Category)[] Known =
    [
        ("api.modrinth.com", NetworkCategory.ContentApi),
        ("api.curseforge.com", NetworkCategory.ContentApi),
        ("login.microsoftonline.com", NetworkCategory.Login),
        ("login.live.com", NetworkCategory.Login),
        ("user.auth.xboxlive.com", NetworkCategory.Login),
        ("xsts.auth.xboxlive.com", NetworkCategory.Login),
        ("api.minecraftservices.com", NetworkCategory.Login),
        ("textures.minecraft.net", NetworkCategory.Login),
        ("piston-meta.mojang.com", NetworkCategory.Download),
        ("piston-data.mojang.com", NetworkCategory.Download),
        ("launchermeta.mojang.com", NetworkCategory.Download),
        ("launcher.mojang.com", NetworkCategory.Download),
        ("libraries.minecraft.net", NetworkCategory.Download),
        ("resources.download.minecraft.net", NetworkCategory.Download),
        ("bmclapi2.bangbang93.com", NetworkCategory.Download),
        ("meta.fabricmc.net", NetworkCategory.Download),
        ("maven.fabricmc.net", NetworkCategory.Download),
        ("meta.quiltmc.org", NetworkCategory.Download),
        ("maven.quiltmc.org", NetworkCategory.Download),
        ("maven.minecraftforge.net", NetworkCategory.Download),
        ("files.minecraftforge.net", NetworkCategory.Download),
        ("maven.neoforged.net", NetworkCategory.Download),
        ("repo1.maven.org", NetworkCategory.Download),
        ("api.adoptium.net", NetworkCategory.Download),
        ("cdn.modrinth.com", NetworkCategory.Download),
        ("edge.forgecdn.net", NetworkCategory.Download),
        ("mediafilez.forgecdn.net", NetworkCategory.Download),
        ("authlib-injector.yushi.moe", NetworkCategory.Download),
    ];

    static NetworkPolicy()
    {
        foreach (var (host, category) in Known) Hosts[host] = category;
    }

    /// <summary>Puts a run-time address (update feed, skin site, mirror, node) into a category.</summary>
    public static void Register(string? url, NetworkCategory category)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0) Hosts[uri.Host] = category;
    }

    /// <summary>Everything sent inside the returned scope belongs to <paramref name="category"/>.</summary>
    public static IDisposable Scope(NetworkCategory category)
    {
        var previous = Current.Value;
        Current.Value = category;
        return new Restore(previous);
    }

    /// <summary>The category by host alone (well-known services and registered addresses), or null.</summary>
    public static NetworkCategory? ByHost(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return null;
        var host = uri.Host;
        while (host.Length > 0)
        {
            if (Hosts.TryGetValue(host, out var c)) return c;
            var dot = host.IndexOf('.');
            if (dot < 0) break;
            host = host[(dot + 1)..];
        }
        return null;
    }

    /// <summary>Classifies a request: scope, then the client's category, then the host.</summary>
    public static NetworkCategory? Classify(Uri? uri, NetworkCategory? clientCategory = null) =>
        Current.Value ?? clientCategory ?? ByHost(uri);

    /// <summary>When a category was last used in this session (for "About and privacy"), or null.</summary>
    public static DateTimeOffset? LastUsed(NetworkCategory category) => LastUse.TryGetValue(category, out var t) ? t : null;

    internal static void Record(NetworkCategory? category, Uri? uri)
    {
        if (category is { } c) LastUse[c] = DateTimeOffset.Now;
        else
        {
            if (Strict || StrictHere.Value) throw new UnclassifiedRequestException(uri);
            Interlocked.Increment(ref _unclassified);
        }
        Requested?.Invoke(category, uri);
    }

    private sealed class RestoreStrict(bool previous) : IDisposable
    {
        public void Dispose() => StrictHere.Value = previous;
    }

    private sealed class Restore(NetworkCategory? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// Put into every HttpClient Jay builds: classifies each request (<see cref="NetworkPolicy.Classify"/>) before it is sent.
/// </summary>
public sealed class NetworkPolicyHandler : DelegatingHandler
{
    public NetworkPolicyHandler(HttpMessageHandler inner, NetworkCategory? category = null) : base(inner) => Category = category;

    /// <summary>The category of every request through this client, unless a scope says otherwise.</summary>
    public NetworkCategory? Category { get; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        NetworkPolicy.Record(NetworkPolicy.Classify(request.RequestUri, Category), request.RequestUri);
        return base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        NetworkPolicy.Record(NetworkPolicy.Classify(request.RequestUri, Category), request.RequestUri);
        return base.Send(request, cancellationToken);
    }
}
