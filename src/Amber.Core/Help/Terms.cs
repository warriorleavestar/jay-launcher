namespace Amber.Core.Help;

/// <summary>One plain-language term: its name, a one-sentence meaning and the help article that explains it more.</summary>
public sealed record Term(string Id, string NameKey, string HintKey, string Article);

/// <summary>
/// "What is this?" terms (official design §6.24, §9): words in the UI that a newcomer may not know get a small "?" next
/// to them. Hovering or focusing it shows one plain sentence; clicking it adds "Learn more" into the help center.
/// Keys are spelled out (not built from the id) so the localization tests can see every one of them.
/// </summary>
public static class Terms
{
    public static readonly IReadOnlyList<Term> All =
    [
        new("instance", "term.instance", "term.instance.hint", "instance"),
        new("loader", "term.loader", "term.loader.hint", "mods"),
        new("modpack", "term.modpack", "term.modpack.hint", "modpacks"),
        new("relay", "term.relay", "term.relay.hint", "connection"),
        new("safety_code", "term.safety_code", "term.safety_code.hint", "knock"),
        new("device_code", "term.device_code", "term.device_code.hint", "public-rooms"),
    ];

    public static Term? Get(string? id) => All.FirstOrDefault(t => t.Id == id);
}
