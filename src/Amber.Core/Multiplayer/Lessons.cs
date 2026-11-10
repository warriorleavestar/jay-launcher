namespace Amber.Core.Multiplayer;

/// <summary>The two short lessons at the bottom of the multiplayer lobby (official §9.5).</summary>
public enum LessonKind
{
    /// <summary>联机小课堂: how playing together works.</summary>
    Multiplayer,
    /// <summary>安全小课: staying safe.</summary>
    Safety,
}

/// <summary>What the last card offers besides "Done".</summary>
public enum LessonAction
{
    None,
    /// <summary>Open "Connection problems?".</summary>
    Diagnostics,
    /// <summary>Start the drill (<see cref="SafetyDrill"/>).</summary>
    Drill,
}

/// <summary>One card: title and body by localization key, a line drawing by id (no text in it), and an optional action.</summary>
public sealed record LessonCard(string Title, string Body, string Picture, LessonAction Action = LessonAction.None);

/// <summary>
/// The cards of each lesson, five each, about two minutes. Text is by key; the pictures are Jay's own line drawings
/// (two theme colors, no text, no faces). Each lesson ends with a link to its full help article.
/// </summary>
public static class Lessons
{
    public static readonly IReadOnlyList<LessonCard> MultiplayerCards =
    [
        new("lesson.mp.room.title", "lesson.mp.room.body", "room"),
        new("lesson.mp.invite.title", "lesson.mp.invite.body", "invite"),
        new("lesson.mp.knock.title", "lesson.mp.knock.body", "knock"),
        new("lesson.mp.lan.title", "lesson.mp.lan.body", "lan"),
        new("lesson.mp.trouble.title", "lesson.mp.trouble.body", "trouble", LessonAction.Diagnostics),
    ];

    public static readonly IReadOnlyList<LessonCard> SafetyCards =
    [
        new("lesson.safe.invite.title", "lesson.safe.invite.body", "invite_friends"),
        new("lesson.safe.codes.title", "lesson.safe.codes.body", "codes"),
        new("lesson.safe.remote.title", "lesson.safe.remote.body", "remote"),
        new("lesson.safe.program.title", "lesson.safe.program.body", "program"),
        new("lesson.safe.public.title", "lesson.safe.public.body", "public", LessonAction.Drill),
    ];

    public static IReadOnlyList<LessonCard> Cards(LessonKind kind) => kind == LessonKind.Safety ? SafetyCards : MultiplayerCards;

    /// <summary>The help article with the long version.</summary>
    public static string Article(LessonKind kind) => kind == LessonKind.Safety ? "child-mode" : "multiplayer";

    /// <summary>Every picture id the lessons use (the app has a drawing for each).</summary>
    public static IEnumerable<string> Pictures => MultiplayerCards.Concat(SafetyCards).Select(c => c.Picture);
}
