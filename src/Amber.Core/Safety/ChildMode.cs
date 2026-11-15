namespace Amber.Core.Safety;

/// <summary>
/// "For a child" (official design §9.6): one main switch and six items under it. Turning the main switch on turns every
/// item on; each item can then be turned off on its own. Items only take effect while the main switch is on. There is
/// deliberately no parent password (it would not stop a child who edits the settings file); instead the time of the
/// last change is kept so a parent can see whether it was changed.
/// </summary>
public sealed class ChildModeSettings
{
    public bool Enabled { get; set; }
    /// <summary>Hide remote control and decline every request.</summary>
    public bool NoRemoteControl { get; set; } = true;
    /// <summary>No public rooms or public networks; rooms are invite-only.</summary>
    public bool NoPublicNetworks { get; set; } = true;
    /// <summary>No "through my computer / through theirs" network exit.</summary>
    public bool NoNetworkExit { get; set; } = true;
    /// <summary>Accept files only from friends; anything else is declined.</summary>
    public bool FriendsFilesOnly { get; set; } = true;
    /// <summary>Every knock needs a yes, even from friends or an auto-approve invite.</summary>
    public bool AlwaysAskKnock { get; set; } = true;
    /// <summary>
    /// Notices outside Jay's window (the topmost popup while Jay is in the background) say only what kind of thing
    /// happened, without names or content, and offer only the safe answer or opening Jay (see <see cref="Activity.RequestNotice"/>).
    /// </summary>
    public bool QuietNotifications { get; set; } = true;
    /// <summary>When any of the above was last changed.</summary>
    public DateTimeOffset? ChangedAt { get; set; }

    /// <summary>The main switch: on turns every item on.</summary>
    public void SetEnabled(bool on, DateTimeOffset now)
    {
        if (on == Enabled) return;
        Enabled = on;
        if (on) NoRemoteControl = NoPublicNetworks = NoNetworkExit = FriendsFilesOnly = AlwaysAskKnock = QuietNotifications = true;
        ChangedAt = now;
    }

    /// <summary>One item; records the change time when the value really changes.</summary>
    public void Set(ChildModeItem item, bool on, DateTimeOffset now)
    {
        if (Get(item) == on) return;
        switch (item)
        {
            case ChildModeItem.NoRemoteControl: NoRemoteControl = on; break;
            case ChildModeItem.NoPublicNetworks: NoPublicNetworks = on; break;
            case ChildModeItem.NoNetworkExit: NoNetworkExit = on; break;
            case ChildModeItem.FriendsFilesOnly: FriendsFilesOnly = on; break;
            case ChildModeItem.AlwaysAskKnock: AlwaysAskKnock = on; break;
            case ChildModeItem.QuietNotifications: QuietNotifications = on; break;
        }
        ChangedAt = now;
    }

    public bool Get(ChildModeItem item) => item switch
    {
        ChildModeItem.NoRemoteControl => NoRemoteControl,
        ChildModeItem.NoPublicNetworks => NoPublicNetworks,
        ChildModeItem.NoNetworkExit => NoNetworkExit,
        ChildModeItem.FriendsFilesOnly => FriendsFilesOnly,
        ChildModeItem.AlwaysAskKnock => AlwaysAskKnock,
        ChildModeItem.QuietNotifications => QuietNotifications,
        _ => false,
    };

    /// <summary>What is in effect now (everything off while the main switch is off).</summary>
    public ChildModeEffects Effects => Enabled
        ? new(NoRemoteControl, NoPublicNetworks, NoNetworkExit, FriendsFilesOnly, AlwaysAskKnock, QuietNotifications)
        : ChildModeEffects.None;
}

public enum ChildModeItem { NoRemoteControl, NoPublicNetworks, NoNetworkExit, FriendsFilesOnly, AlwaysAskKnock, QuietNotifications }

/// <summary>The rules in effect, read by multiplayer, file transfer and notifications.</summary>
public readonly record struct ChildModeEffects(
    bool HideRemoteControl, bool InviteOnly, bool HideNetworkExit, bool FriendsFilesOnly, bool AlwaysAskKnock, bool QuietNotifications)
{
    public static ChildModeEffects None => default;

    /// <summary>Whether an incoming file offer may be shown at all.</summary>
    public bool AcceptsFileFrom(bool isFriend) => !FriendsFilesOnly || isFriend;

    /// <summary>Whether a knock may be let in without asking (an auto-approve invite).</summary>
    public bool MayAutoAdmit(bool autoApprove) => autoApprove && !AlwaysAskKnock;
}
