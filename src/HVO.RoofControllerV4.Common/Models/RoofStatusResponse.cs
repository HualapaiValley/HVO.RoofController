namespace HVO.RoofControllerV4.Common.Models
{
    /// <summary>
    /// Coherent snapshot of roof controller state. The positional members are the original v4 contract; the init-only
    /// members were added for verified relay state, fault latching, input freshness and controller identity. Older
    /// clients ignore the additions.
    /// </summary>
    public sealed record RoofStatusResponse(
        RoofControllerStatus Status,
        bool IsMoving,
        RoofControllerStopReason LastStopReason,
        DateTimeOffset? LastTransitionUtc,
        bool IsWatchdogActive,
        double? WatchdogSecondsRemaining,
        bool IsAtSpeed,
        bool IsUsingPhysicalHardware,
        bool IsIgnoringPhysicalLimitSwitches)
    {
        /// <summary>Monotonic per-process snapshot sequence. Clients discard snapshots older than one already applied.</summary>
        public long StatusVersion { get; init; }

        /// <summary>UTC time the snapshot was taken on the controller.</summary>
        public DateTimeOffset SnapshotUtc { get; init; }

        /// <summary>The motion the controller is commanding. <see cref="IsMoving"/> is true exactly when this is not None.</summary>
        public RoofMotionDirection CommandedMotion { get; init; }

        /// <summary>Outcome of the last relay transition as observed by register read-back (not contact state).</summary>
        public RoofRelayRegisterState RelayRegisterState { get; init; }

        /// <summary>Last relay register value read back (bits 0-3 = relays 1-4), or null when unknown.</summary>
        public int? RelayRegisterMask { get; init; }

        /// <summary>
        /// True when the most recent relay register read succeeded and is recent. False while reads fail or when
        /// supervision has not read the register recently; <see cref="RelayRegisterState"/> then reports the last result
        /// that was read, not the current register.
        /// </summary>
        public bool RelayRegisterReadsHealthy { get; init; }

        /// <summary>UTC time of the most recent successful relay register read.</summary>
        public DateTimeOffset? LastSuccessfulRelayReadUtc { get; init; }

        /// <summary>Number of consecutive failed relay register reads.</summary>
        public int ConsecutiveRelayReadFailures { get; init; }

        /// <summary>True when a safety fault is latched and must be reset (ClearFault) before motion is allowed.</summary>
        public bool IsFaultLatched { get; init; }

        /// <summary>The stop reason that latched the fault, when <see cref="IsFaultLatched"/> is true.</summary>
        public RoofControllerStopReason? LatchedFaultReason { get; init; }

        /// <summary>Logical drive-fault input (IN3) state from the last successful read; null when unknown.</summary>
        public bool? IsDriveFaultActive { get; init; }

        /// <summary>Logical open-limit state from the last successful read; null when unknown.</summary>
        public bool? IsOpenLimitActive { get; init; }

        /// <summary>Logical closed-limit state from the last successful read; null when unknown.</summary>
        public bool? IsClosedLimitActive { get; init; }

        /// <summary>True when the most recent safety-input read succeeded.</summary>
        public bool InputsHealthy { get; init; }

        /// <summary>UTC time of the most recent successful safety-input read.</summary>
        public DateTimeOffset? LastSuccessfulInputReadUtc { get; init; }

        /// <summary>Number of consecutive failed safety-input reads.</summary>
        public int ConsecutiveInputReadFailures { get; init; }

        /// <summary>Seconds left on the renewable operator lease while leased motion is active; null when no lease applies.</summary>
        public double? LeaseSecondsRemaining { get; init; }

        /// <summary>True while a clear-fault pulse is in progress.</summary>
        public bool IsClearFaultInProgress { get; init; }

        /// <summary>True when the controller has initialized successfully.</summary>
        public bool IsInitialized { get; init; }

        /// <summary>True once shutdown or disposal has begun; no new motion is admitted.</summary>
        public bool IsShuttingDown { get; init; }

        /// <summary>Operator-facing controller name (configuration), used by clients to show which controller they operate.</summary>
        public string? ControllerName { get; init; }

        /// <summary>Random identifier generated at process start; changes when the controller restarts.</summary>
        public string? ControllerInstanceId { get; init; }

        /// <summary>Most recent safety or hardware error message, if any.</summary>
        public string? LastError { get; init; }
    }
}
