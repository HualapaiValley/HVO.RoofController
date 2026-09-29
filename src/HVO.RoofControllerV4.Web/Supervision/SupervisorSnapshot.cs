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

    [JsonPropertyName("controller")]
    public SupervisedProcess Controller { get; init; } = new();

    [JsonPropertyName("ui")]
    public SupervisedProcess Ui { get; init; } = new();
}

/// <summary>One supervised process: the controller or the web UI.</summary>
public sealed record SupervisedProcess
{
    /// <summary>The states the supervisor writes.</summary>
    public static class States
    {
        public const string Starting = "starting";
        public const string Running = "running";
        /// <summary>Exited, and waiting to be started again (at once after a requested restart, or after a backoff).</summary>
        public const string Restarting = "restarting";
        /// <summary>The controller crashed too often and is left stopped.</summary>
        public const string CrashLoop = "crash-loop";
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
