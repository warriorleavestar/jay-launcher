using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>Whether one service (game or chat) of an invite room runs split over several paths.</summary>
public enum SplitPolicy : byte
{
    /// <summary>Never split: one path, as before.</summary>
    Off = 0,
    /// <summary>Split when the paths are ready; otherwise one path, with the reason shown.</summary>
    Auto = 1,
    /// <summary>Only split connections: a member whose paths aren't ready can't use this service.</summary>
    Strict = 2,
}

/// <summary>
/// One extra EasyTier network that carries one more leg (leg i + 1 uses Nets[i]; leg 0 is the room's own network).
/// Random like the room's network, rotated together with it after a kick. ToString holds no values.
/// </summary>
public sealed record SplitNet(string Network, string Secret)
{
    public override string ToString() => "SplitNet";

    internal static SplitNet Create() =>
        new("jay-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    internal JsonObject ToJson() => new() { ["net"] = Network, ["secret"] = Secret };

    internal static SplitNet? FromJson(JsonNode? json)
    {
        if (json is not JsonObject o) return null;
        var net = o["net"] is JsonValue n && n.TryGetValue<string>(out var a) ? a : null;
        var secret = o["secret"] is JsonValue s && s.TryGetValue<string>(out var b) ? b : null;
        if (net is null || secret is null || !KnockProtocol.NetworkPattern().IsMatch(net) || !KnockProtocol.SecretPattern().IsMatch(secret)) return null;
        return new SplitNet(net, secret);
    }
}

/// <summary>
/// Multi-path split settings of an invite room (design mp-split-design v3 §5). The host decides them; they travel in the
/// room credentials and the rekey message, both only over the member's own authenticated channel. They hold network names
/// and secrets only, never an address: which nodes a path uses is up to each member's own node list.
/// </summary>
public sealed record SplitSettings(SplitPolicy Game, SplitPolicy Chat, int Paths, IReadOnlyList<SplitNet> Nets)
{
    public static readonly SplitSettings None = new(SplitPolicy.Off, SplitPolicy.Off, SplitStream.MinLegs, []);

    /// <summary>
    /// Host ports for split legs: leg i listens on <c>FirstLegPort + i</c> (47657, 47658, 47659). The leg index comes from the
    /// port, and the service (game or chat) from the handshake payload, like on the single-path port 47656.
    /// </summary>
    public const int FirstLegPort = 47657;

    public static int LegPort(int index) => index is >= 0 and < SplitStream.MaxLegs ? FirstLegPort + index : throw new ArgumentOutOfRangeException(nameof(index));

    public bool Enabled => (Game != SplitPolicy.Off || Chat != SplitPolicy.Off) && Nets.Count == Paths - 1;

    public SplitPolicy For(TunnelService service) => !Enabled ? SplitPolicy.Off : service switch
    {
        TunnelService.Game => Game,
        TunnelService.Chat => Chat,
        _ => SplitPolicy.Off,
    };

    /// <summary>Developer switch for hosts until the setting is in the app: <c>AMBER_SPLIT=auto,strict,3</c> (game, chat, paths).</summary>
    public const string DeveloperVariable = "AMBER_SPLIT";

    /// <summary>What a host puts in new wrapped invite rooms. Off unless the developer switch asks for more.</summary>
    public static SplitSettings Requested => FromSwitch(System.Environment.GetEnvironmentVariable(DeveloperVariable));

    /// <summary>
    /// The split settings a new room carries: only wrapped rooms split, and a host in full-LAN (TUN) mode doesn't split in
    /// this version, so both get none.
    /// </summary>
    public static SplitSettings ForNewRoom(bool wraps, bool fullLan, SplitSettings requested) => wraps && !fullLan ? requested : None;

    /// <summary>Parses the developer switch: "game,chat[,paths]" with off, auto or strict. Anything else means no split.</summary>
    internal static SplitSettings FromSwitch(string? value)
    {
        var parts = (value ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3) return None;
        SplitPolicy? Policy(string s) => s.ToLowerInvariant() switch { "off" => SplitPolicy.Off, "auto" => SplitPolicy.Auto, "strict" => SplitPolicy.Strict, _ => null };
        var paths = parts.Length == 3 && int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : parts.Length == 2 ? SplitStream.MinLegs : 0;
        if (Policy(parts[0]) is not { } game || Policy(parts[1]) is not { } chat || paths is < SplitStream.MinLegs or > SplitStream.MaxLegs) return None;
        return Create(game, chat, paths);
    }

    /// <summary>New settings with fresh random networks for the extra paths.</summary>
    public static SplitSettings Create(SplitPolicy game, SplitPolicy chat, int paths = SplitStream.MinLegs)
    {
        if (paths is < SplitStream.MinLegs or > SplitStream.MaxLegs) throw new ArgumentOutOfRangeException(nameof(paths));
        if (game == SplitPolicy.Off && chat == SplitPolicy.Off) return None;
        return new SplitSettings(game, chat, paths, [.. Enumerable.Range(0, paths - 1).Select(_ => SplitNet.Create())]);
    }

    /// <summary>After a kick: every extra network is replaced, like the room's own network.</summary>
    public SplitSettings Rotate() => Enabled ? this with { Nets = [.. Nets.Select(_ => SplitNet.Create())] } : None;

    /// <summary>Takes the extra networks from a rekey. A rekey without the right number of them turns splitting off:
    /// the old networks are known to whoever was just kicked, so they must not stay in use.</summary>
    public SplitSettings WithNets(IReadOnlyList<SplitNet> nets) => Enabled && nets.Count == Nets.Count ? this with { Nets = [.. nets] } : None;

    public override string ToString() => "SplitSettings";

    internal JsonObject ToJson() => new()
    {
        ["game"] = Name(Game),
        ["chat"] = Name(Chat),
        ["paths"] = Paths,
        ["nets"] = NetsToJson(Nets),
    };

    internal static JsonArray NetsToJson(IReadOnlyList<SplitNet> nets) => [.. nets.Select(n => (JsonNode)n.ToJson())];

    /// <summary>
    /// Reads the host's split settings. Anything this version doesn't understand (an unknown policy, more paths than it
    /// supports, a malformed network) means no split: the member still joins the room and connects on one path. A Strict
    /// host then closes that connection; the member sees it drop, and the host's log records the refusal.
    /// </summary>
    internal static SplitSettings FromJson(JsonNode? json)
    {
        if (json is not JsonObject o) return None;
        var game = Parse(o["game"]);
        var chat = Parse(o["chat"]);
        var paths = o["paths"] is JsonValue p && p.TryGetValue<int>(out var n) ? n : 0;
        var nets = NetsFromJson(o["nets"]);
        if (game is null || chat is null || paths is < SplitStream.MinLegs or > SplitStream.MaxLegs || nets is null || nets.Count != paths - 1) return None;
        var settings = new SplitSettings(game.Value, chat.Value, paths, nets);
        return settings.Enabled ? settings : None;
    }

    /// <summary>Null when the list is missing or any entry is malformed or repeated.</summary>
    internal static IReadOnlyList<SplitNet>? NetsFromJson(JsonNode? json)
    {
        if (json is not JsonArray array || array.Count > SplitStream.MaxLegs - 1) return null;
        var nets = new List<SplitNet>();
        foreach (var item in array)
        {
            if (SplitNet.FromJson(item) is not { } net || nets.Any(x => x.Network == net.Network || x.Secret == net.Secret)) return null;
            nets.Add(net);
        }
        return nets;
    }

    private static string Name(SplitPolicy policy) => policy switch
    {
        SplitPolicy.Auto => "auto",
        SplitPolicy.Strict => "strict",
        _ => "off",
    };

    private static SplitPolicy? Parse(JsonNode? json) => json is JsonValue v && v.TryGetValue<string>(out var s) ? s switch
    {
        "off" => SplitPolicy.Off,
        "auto" => SplitPolicy.Auto,
        "strict" => SplitPolicy.Strict,
        _ => null,
    } : null;
}
