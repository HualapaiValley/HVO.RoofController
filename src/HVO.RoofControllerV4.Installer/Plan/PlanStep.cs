using System.Security.Cryptography.X509Certificates;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
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
    Port,

    /// <summary>Something run once the rest is in place, to show it works (the Mac app opened with <c>--check</c>).</summary>
    Check
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

    /// <summary>Where the release's release.json comes from: read once a run, the first time a step needs it.</summary>
    public ReleaseSource Release { get; init; } = ReleaseSource.GitHub();

    /// <summary>Where a long step (a deploy) says how it is getting on, a line at a time; null when nobody is watching.</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>The passwords and PINs given for this run: never saved, logged or shown.</summary>
    public InstallSecrets Secrets { get; init; } = new();

    private readonly HashSet<PlanStep> applied = [];
    private readonly Dictionary<Uri, Task<X509Certificate2>> authorities = [];

    /// <summary>The release this installer installs: its release.json, read once a run.</summary>
    public Task<ReleaseManifest> ReleaseAsync(CancellationToken cancellationToken)
        => Release.GetAsync(Machine, Log, Version, cancellationToken);

    /// <summary>
    /// The CA the controller at <paramref name="controller"/> serves, checked against the certificate it presents: fetched
    /// once a run, the first time a step needs it (see <see cref="InstallerMachine.FetchCaAsync"/>). A controller that does
    /// not answer in time fails with a <see cref="TimeoutException"/>, which is kept for the run too, so each step does not
    /// wait for it again; a fetch the run itself cancelled is tried again.
    /// </summary>
    public Task<X509Certificate2> ControllerCaAsync(Uri controller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (!authorities.TryGetValue(controller, out var fetch) || fetch.IsCanceled)
        {
            fetch = FetchCaAsync(controller, cancellationToken);
            authorities[controller] = fetch;
        }

        return fetch;
    }

    private async Task<X509Certificate2> FetchCaAsync(Uri controller, CancellationToken cancellationToken)
    {
        try
        {
            return await Machine.FetchCaAsync(controller, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{controller} did not answer in time.", error);
        }
    }

    /// <summary>
    /// Whether <paramref name="step"/> made a change in this run: a step that depends on another (the controller on its
    /// keys and certificate) makes its own change when that one did.
    /// </summary>
    public bool HasApplied(PlanStep step) => applied.Contains(step);

    internal void MarkApplied(PlanStep step) => applied.Add(step);
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

    /// <summary>
    /// The secrets the change <paramref name="check"/> found needs (the first admin's password): the installer asks for
    /// them, or reads them from files, before it changes anything.
    /// </summary>
    public virtual IReadOnlyList<InstallSecret> SecretsNeeded(StepCheck check) => [];

    /// <summary>Makes the change <paramref name="check"/> found. Called only when it makes one.</summary>
    public abstract Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken);

    public override string ToString() => $"{Kind} {Target}";
}
