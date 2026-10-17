using System.Text.Json.Serialization;

namespace HVO.RoofControllerV4.Web.Supervision;

/// <summary>
/// The container supervisor's state file (supervisor.json, written by container/roof-supervisor.sh at each change).
/// </summary>
public sealed record SupervisorSnapshot
{
    /// <summary>running, stopping or stopped.</summary>
    [JsonPropertyName("supervisor")]
    public string Supervisor { get; init; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>How many controller crashes within <see cref="CrashWindowSeconds"/> leave it stopped (a crash loop).</summary>
    [JsonPropertyName("crashLimit")]
    public int CrashLimit { get; init; }

    [JsonPropertyName("crashWindowSeconds")]
    public int CrashWindowSeconds { get; init; }

    /// <summary>A forced restart requested within this many seconds of the controller's start is ignored.</summary>
    [JsonPropertyName("forceRestartMinSeconds")]
    public int ForceRestartMinSeconds { get; init; }

    /// <summary>What the supervisor did with the last forced restart request, or null when there has been none.</summary>
    [JsonPropertyName("lastForcedRestart")]
    public ForcedRestartRecord? LastForcedRestart { get; init; }

    /// <summary>The relays as the supervisor knows them, or null from a supervisor that does not report them.</summary>
    [JsonPropertyName("relays")]
    public SupervisorRelays? Relays { get; init; }

    [JsonPropertyName("controller")]
    public SupervisedProcess Controller { get; init; } = new();

    [JsonPropertyName("ui")]
    public SupervisedProcess Ui { get; init; } = new();
}

/// <summary>The last forced restart request the supervisor read, and what it did with it.</summary>
public sealed record ForcedRestartRecord
{
    /// <summary>The outcomes the supervisor writes.</summary>
    public static class Outcomes
    {
        /// <summary>It killed the controller and started it again.</summary>
        public const string Restarted = "restarted";
        /// <summary>The controller had started too recently (<see cref="SupervisorSnapshot.ForceRestartMinSeconds"/>).</summary>
        public const string Ignored = "ignored";
        /// <summary>
        /// It killed the controller, if it ran, but did not start it again: the relays could not be verified off, so the
        /// controller is held stopped (<see cref="SupervisedProcess.States.RelaysUnverified"/>) until they are.
        /// </summary>
        public const string Held = "held";
    }

    /// <summary>When the supervisor read the request.</summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; init; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;
}

/// <summary>One supervised process: the controller or the web UI.</summary>
public sealed record SupervisedProcess
{
    /// <summary>The states the supervisor writes.</summary>
    public static class States
    {
        /// <summary>Not started yet; for the web UI, also while the supervisor prepares it (its copies, its keys directory).</summary>
        public const string Starting = "starting";
        public const string Running = "running";
        /// <summary>Exited, and waiting to be started again (at once after a requested restart, or after a backoff).</summary>
        public const string Restarting = "restarting";
        /// <summary>The controller crashed too often and is left stopped.</summary>
        public const string CrashLoop = "crash-loop";
        /// <summary>
        /// The controller is held stopped: the supervisor could not verify every relay off, and starts it only once a
        /// read-back shows them off (the owner's decision, #138). It keeps trying.
        /// </summary>
        public const string RelaysUnverified = "relays-unverified";
        public const string Stopping = "stopping";
        public const string Stopped = "stopped";
    }

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("pid")]
    public int? Pid { get; init; }

    /// <summary>How many times the supervisor has started it.</summary>
    [JsonPropertyName("starts")]
    public int Starts { get; init; }

    /// <summary>Its crashes within the crash window.</summary>
    [JsonPropertyName("recentCrashes")]
    public int RecentCrashes { get; init; }

    [JsonPropertyName("lastExitCode")]
    public int? LastExitCode { get; init; }

    /// <summary>Why it last exited: "restart requested", "crashed (exit code 1)", "forced restart" and so on.</summary>
    [JsonPropertyName("lastExitReason")]
    public string? LastExitReason { get; init; }

    [JsonPropertyName("lastExitAt")]
    public DateTimeOffset? LastExitAt { get; init; }
}

/// <summary>What the supervisor knows of the relays, and when and why it last turned them off.</summary>
public sealed record SupervisorRelays
{
    /// <summary>The states the supervisor writes.</summary>
    public static class States
    {
        /// <summary>Not tried yet.</summary>
        public const string Unknown = "unknown";
        /// <summary>The controller runs and drives them.</summary>
        public const string Controller = "controller";
        /// <summary>Turned off and verified by read-back since the controller last ran.</summary>
        public const string Off = "off";
        /// <summary>The last try could not verify them off; the supervisor tries again.</summary>
        public const string Unverified = "unverified";
    }

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    /// <summary>When they were last turned off (or tried).</summary>
    [JsonPropertyName("at")]
    public DateTimeOffset? At { get; init; }

    /// <summary>Why: "the controller exited (killed by signal 9)", "before the controller starts", ...</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
