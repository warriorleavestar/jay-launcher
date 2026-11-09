using Amber.Core.Game;
using Amber.Core.Launch;

namespace Amber.Core.GameLibrary;

/// <summary>What the instance page's start button is showing (official design §6.3).</summary>
public enum StartKind
{
    Ready,
    NeedAccount,
    ResumeInstall,
    Repair,
    RetryAfterCrash,
    Preparing,
    Entering,
    Playing,
    Exiting,
}

/// <summary>The separate button next to the start button. It never shares the start button's position.</summary>
public enum StartSide { None, Cancel, QuitGame, ForceKill, InstallOnly, OpenFolder }

/// <summary>
/// One state of the start button. <see cref="LabelKey"/> is a translation key. <see cref="Amber"/> marks the one amber
/// button on the screen. <see cref="Busy"/> shows a progress ring; the button stays focusable and is never disabled.
/// </summary>
public sealed record StartPlan(StartKind Kind, string LabelKey, bool Amber, bool Busy, StartSide Side, string? NoteKey = null)
{
    public string SideKey => Side switch
    {
        StartSide.Cancel => "common.cancel",
        StartSide.QuitGame => "instance.hero.quit_game",
        StartSide.ForceKill => "home.button.force_kill",
        StartSide.InstallOnly => "instance.hero.install_only",
        StartSide.OpenFolder => "common.open_folder",
        _ => "",
    };
}

/// <summary>Maps the instance's state to its start button. Pure, so the whole table is unit-tested.</summary>
public static class StartButton
{
    /// <param name="session">The latest session for this instance, or null if it has not been started in this run of Jay.</param>
    /// <param name="hasAccount">An account is selected.</param>
    /// <param name="state">The instance's install state on disk.</param>
    /// <param name="forceKillOffered">The game has been asked to exit for long enough that force kill is offered.</param>
    public static StartPlan For(LaunchSession? session, bool hasAccount, InstanceState state, bool forceKillOffered = false)
    {
        switch (session?.State)
        {
            case LaunchState.Preparing:
                return new(StartKind.Preparing, "instance.hero.preparing", true, true, StartSide.Cancel, "instance.hero.note.preparing");
            case LaunchState.Entering:
                return new(StartKind.Entering, "instance.hero.entering", true, true, StartSide.QuitGame, "instance.hero.note.entering");
            case LaunchState.Playing:
                // PlayTint, not solid amber: the game is the thing on screen now.
                return new(StartKind.Playing, "instance.hero.playing", false, false, StartSide.QuitGame);
            case LaunchState.Exiting:
                return new(StartKind.Exiting, "instance.hero.exiting", false, true, forceKillOffered ? StartSide.ForceKill : StartSide.None, "instance.hero.note.exiting");
        }
        if (!hasAccount) return new(StartKind.NeedAccount, "instance.hero.need_account", false, false, StartSide.None, "instance.hero.note.no_account");
        return state switch
        {
            InstanceState.Incomplete => new(StartKind.ResumeInstall, "home.button.resume_install", true, false, StartSide.InstallOnly, "instance.hero.note.incomplete"),
            InstanceState.Broken => new(StartKind.Repair, "home.button.repair", true, false, StartSide.OpenFolder, "instance.hero.note.broken"),
            _ when session?.State == LaunchState.Crashed => new(StartKind.RetryAfterCrash, "instance.hero.retry", true, false, StartSide.None),
            _ => new(StartKind.Ready, "home.launch", true, false, StartSide.None),
        };
    }
}
