namespace HVO.RoofControllerV4.Screens;

/// <summary>How a person gets the screens' operator and admin view.</summary>
public enum KioskSignIn
{
    /// <summary>At the kiosk: the person picks their name and types their PIN; the kiosk locks again.</summary>
    Pin,

    /// <summary>In the Mac app: the person types their name and password; the app signs them out again.</summary>
    Password
}

/// <summary>
/// The words, and the ways of typing, that differ between the kiosk, which a person unlocks with a PIN, and the Mac app,
/// which a person signs in to with their name and password. The roof, Stop and the settings use the wording every client
/// shares.
/// </summary>
public sealed record KioskWording
{
    /// <summary>The touchscreen kiosk's words.</summary>
    public static KioskWording Kiosk { get; } = new();

    /// <summary>The Mac app's words.</summary>
    public static KioskWording Desktop { get; } = new()
    {
        SignIn = KioskSignIn.Password,
        KeyboardTyping = true,
        Device = "app",
        Starting = "the app is starting",
        SignedOutBlock = "no one is signed in: sign in with your name and password",
        SignedOutHint = "Sign in with your name and password to open or close the roof.",
        SignedOutBadge = "Signed out",
        SignInAction = "Sign in",
        SignOutAction = "Sign out",
        SignedOutNotice = "Signed out.",
        SignedInAs = "Signed in as",
        IdleSignedOut = "Signed out after",
        Session = "session",
        AlreadySignedIn = "Someone is already signed in.",
        LeaseDroppedOnSignOut =
            "Signed out, so the app stopped renewing the operator lease. If the roof is still moving, it stops when the lease runs out.",
        SessionEnded = "The controller ended the session, so the app signed out.",
        SessionEndedWithLease =
            "The controller ended the session, so the app signed out and stopped renewing the operator lease. If the roof is still moving, it stops when the lease runs out.",
        AcceptedAfterSignOut = "accepted after the app signed out, so it does not renew the operator lease. If the roof is still moving, it stops when the lease runs out.",
        RenewsLease = "The app renews the operator lease while the roof moves; if it signs out or cannot reach the controller, the roof stops when the lease runs out.",
        SecretsElsewhere = "Secrets are not typed in this app: set them in the web UI or with hvo-roof.",
        WithoutUse = "without use"
    };

    public KioskSignIn SignIn { get; init; } = KioskSignIn.Pin;

    /// <summary>
    /// True where the person has a keyboard (the Mac app): a setting's value is typed in a text box. False on the kiosk,
    /// where it is typed on keys drawn on the screen.
    /// </summary>
    public bool KeyboardTyping { get; init; }

    /// <summary>What the screens are called in a sentence: "kiosk" or "app".</summary>
    public string Device { get; init; } = "kiosk";

    /// <summary>Why nothing but Stop is offered before the console started.</summary>
    public string Starting { get; init; } = KioskText.Starting;

    /// <summary>Why Open, Close and Clear fault are not offered while no one is signed in.</summary>
    public string SignedOutBlock { get; init; } = KioskText.Locked;

    /// <summary>The roof page's line while no one is signed in.</summary>
    public string SignedOutHint { get; init; } = "Locked: unlock the kiosk with a PIN to open or close the roof.";

    /// <summary>The header's badge while no one is signed in.</summary>
    public string SignedOutBadge { get; init; } = "Locked";

    /// <summary>The rail's button that signs a person in.</summary>
    public string SignInAction { get; init; } = "Unlock";

    /// <summary>The rail's button that signs the person out.</summary>
    public string SignOutAction { get; init; } = "Lock";

    /// <summary>The notice after the person signed out.</summary>
    public string SignedOutNotice { get; init; } = KioskText.LockedNotice;

    /// <summary>The start of the notice after a person signed in, before their name.</summary>
    public string SignedInAs { get; init; } = "Unlocked by";

    /// <summary>The start of the notice after the idle sign-out, before how long.</summary>
    public string IdleSignedOut { get; init; } = "Locked after";

    /// <summary>What the idle sign-out waits for: "without a touch".</summary>
    public string WithoutUse { get; init; } = "without a touch";

    /// <summary>The controller's session a person gets: "PIN session" or "session".</summary>
    public string Session { get; init; } = "PIN session";

    public string AlreadySignedIn { get; init; } = "The kiosk is already unlocked.";

    public string LeaseDroppedOnSignOut { get; init; } = KioskText.LeaseDroppedOnLock;

    public string SessionEnded { get; init; } = KioskText.SessionEnded;

    public string SessionEndedWithLease { get; init; } = KioskText.SessionEndedWithLease;

    /// <summary>After the verb ("Open"): a motion accepted after the person signed out.</summary>
    public string AcceptedAfterSignOut { get; init; } =
        "accepted after the kiosk was locked, so it does not renew the operator lease. If the roof is still moving, it stops when the lease runs out.";

    /// <summary>After "Open accepted.": the lease the screens renew while the roof moves.</summary>
    public string RenewsLease { get; init; } =
        "The kiosk renews the operator lease while the roof moves; if it is locked or cannot reach the controller, the roof stops when the lease runs out.";

    /// <summary>Why a secret setting cannot be changed here.</summary>
    public string SecretsElsewhere { get; init; } = "Secrets are not typed on the kiosk: set them in the web UI or with hvo-roof.";

    /// <summary>The answer to a sign-in while the screens close.</summary>
    public string Closing => $"The {Device} is closing.";

    /// <summary>The banner when the controller refused the device key.</summary>
    public string KeyRefused => $"The controller refused this {Device}'s device key, so there is no live status.";

    /// <summary>"Unlocked by olga (Operator)."</summary>
    public string SignedIn(string name, string role) => $"{SignedInAs} {name} ({KioskText.DescribeRole(role)}).";

    /// <summary>"Locked after 2 min without a touch."</summary>
    public string IdleSignedOutAfter(TimeSpan idle) => $"{IdleSignedOut} {KioskText.Duration(idle)} {WithoutUse}.";

    /// <summary>The sign-out before the controller would end an idle session.</summary>
    public string SessionIdleSignedOut(TimeSpan idle)
        => $"{SignedOutNotice.TrimEnd('.')}: the controller ends a {Session} after {KioskText.Duration(idle)} without a request.";

    /// <summary>The banner when the controller refused the device key, like <see cref="KioskText.DescribeUnreachable"/>.</summary>
    public string DescribeKeyRefused(DateTimeOffset? staleSince)
        => staleSince is { } since
            ? $"{KeyRefused} No status since {Client.RoofStatusText.Time(since)}: showing the last known state. {KioskText.StopStillTried}"
            : $"{KeyRefused} {KioskText.StopStillTried}";
}
