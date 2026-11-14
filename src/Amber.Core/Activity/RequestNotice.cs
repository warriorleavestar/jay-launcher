using Amber.Core.Localization;

namespace Amber.Core.Activity;

/// <summary>
/// What a notice shown outside Jay's own window (the topmost popup while Jay is in the background, or a system
/// notification) says about a request. Normally that is the request itself. With "for a child" and its "no names or
/// content in notifications" item on, it only says what kind of thing happened, without names or content, and offers
/// only the safe answer: the full request stays in Jay's request bar and room, where it is answered with the details in view.
/// </summary>
public sealed record RequestNotice(string Title, string? Body, IReadOnlyList<RequestChoice> Choices, bool Quiet)
{
    public static RequestNotice For(RequestSpec spec, bool quiet)
    {
        if (!quiet) return new(spec.Title, spec.Body, spec.Choices, false);
        var title = spec.Kind switch
        {
            "knock" => L.T("activity.quiet.knock"),
            "file" => L.T("activity.quiet.file"),
            "call" => L.T("activity.quiet.call"),
            _ => L.T("activity.quiet.other"),
        };
        var safe = spec.Choices.FirstOrDefault(c => c.IsDefault) ?? spec.Choices[0];
        return new(title, L.T("activity.quiet.body"), [safe], true);
    }
}
