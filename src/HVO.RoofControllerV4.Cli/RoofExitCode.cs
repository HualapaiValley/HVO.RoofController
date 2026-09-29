namespace HVO.RoofControllerV4.Cli;

/// <summary>
/// The exit codes of <c>hvo-roof</c>. Scripts may rely on them; docs/cli.md lists them, and a test pins each value.
/// </summary>
public enum RoofExitCode
{
    /// <summary>The command did what it was asked.</summary>
    Success = 0,

    /// <summary>Something failed that no other code describes, for example an answer that could not be read.</summary>
    Failed = 1,

    /// <summary>The command line was not valid: an unknown command or option, or a missing or malformed value.</summary>
    Usage = 2,

    /// <summary>
    /// There is no controller address or credential, or the credentials file cannot be used (for example, other users
    /// can read it). <c>hvo-roof setup</c> fixes this.
    /// </summary>
    NotConfigured = 3,

    /// <summary>The controller could not be reached, or did not answer in time.</summary>
    Unreachable = 4,

    /// <summary>The controller did not accept the credential (HTTP 401): the key is wrong or the session has ended.</summary>
    SignedOut = 5,

    /// <summary>The credential is valid but its role may not do this (HTTP 403).</summary>
    Forbidden = 6,

    /// <summary>
    /// The controller refused the command, for example because of the roof's state, a latched fault, a settings
    /// version conflict or a value it does not accept.
    /// </summary>
    Refused = 7,

    /// <summary><c>health</c>: the controller answered, but it is degraded or unhealthy.</summary>
    Unhealthy = 8,

    /// <summary>
    /// <c>stop</c>: nothing confirms that the roof stopped. The controller could not verify that the relays are off, or
    /// the stop did not reach it, or its answer never came. Use the stop control at the roof.
    /// </summary>
    StopNotVerified = 9,

    /// <summary>
    /// The change is safety-critical and was not sent: review it, then run the command again with
    /// <c>--confirm-safety-critical</c> (or <c>--force</c> for <c>restart</c>).
    /// </summary>
    ConfirmationRequired = 10,

    /// <summary><c>status --watch</c>: it was ended while the status was stale (no recent message from the controller).</summary>
    Stale = 11,

    /// <summary>The command was interrupted (Ctrl+C) before it finished. What it had sent may still take effect.</summary>
    Interrupted = 130
}
