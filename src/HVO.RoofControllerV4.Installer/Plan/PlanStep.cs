using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>What a step makes or changes; <c>--plan</c> groups the steps by it.</summary>
public enum StepKind
{
    Folder,
    File,
    User,
    Package,
    Container,
    Service,
    Port
}

/// <summary>What a step would do on this machine.</summary>
public enum StepChange
{
    /// <summary>It is not there: the step makes it.</summary>
    Create,

    /// <summary>It is there but differs: the step changes it.</summary>
    Change,

    /// <summary>It is there as the step would leave it: nothing to do.</summary>
    Unchanged,

    /// <summary>Nothing to make or change, but worth saying (a port the controller will listen on).</summary>
    Info,

    /// <summary>The step cannot go ahead (something the installer will not replace): nothing is installed.</summary>
    Blocked
}

/// <summary>What <see cref="PlanStep.CheckAsync"/> found: the change the step would make, and its detail (a mode, a reason).</summary>
public sealed record StepCheck(StepChange Change, string? Detail = null)
{
    public static StepCheck Unchanged(string? detail = null) => new(StepChange.Unchanged, detail);

    public bool MakesChange => Change is StepChange.Create or StepChange.Change;
}

/// <summary>What a step runs with: the machine (its commands logged), the log, what the survey found and the answers.</summary>
public sealed class InstallContext
{
    public required InstallerMachine Machine { get; init; }

    public required InstallLog Log { get; init; }

    public required MachineSurvey Survey { get; init; }

    public required InstallAnswers Answers { get; init; }

    /// <summary>The installer's version (without its commit): the release it installs.</summary>
    public required string Version { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// One thing the installer makes or changes. Each step first checks the machine (<see cref="CheckAsync"/>, which only
/// reads), so <c>--plan</c> and the review page say exactly what will change, and a second run finds nothing to do;
/// <see cref="ApplyAsync"/> then makes only that change.
/// </summary>
public abstract class PlanStep
{
    public abstract StepKind Kind { get; }

    /// <summary>What it makes or changes: a path, a container, a unit, a port.</summary>
    public abstract string Target { get; }

    /// <summary>What it is for, in a few words.</summary>
    public abstract string Purpose { get; }

    public abstract Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken);

    /// <summary>False for a step this installer can plan but not carry out yet: an install that needs it is refused first.</summary>
    public virtual bool CanApply => true;

    /// <summary>Makes the change <paramref name="check"/> found. Called only when it makes one.</summary>
    public abstract Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken);

    public override string ToString() => $"{Kind} {Target}";
}
