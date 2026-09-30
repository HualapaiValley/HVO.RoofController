using HVO.RoofControllerV4.Installer.Answers;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>The steps of an install, in the order they are carried out.</summary>
public sealed class InstallPlan(IReadOnlyList<PlanStep> steps)
{
    public IReadOnlyList<PlanStep> Steps { get; } = steps;

    /// <summary>Checks every step against the machine. It only reads: this is what <c>--plan</c> and the review page show.</summary>
    public async Task<CheckedPlan> CheckAsync(InstallContext context, CancellationToken cancellationToken = default)
    {
        var checkedSteps = new List<CheckedStep>();
        foreach (var step in Steps)
        {
            checkedSteps.Add(new CheckedStep(step, await step.CheckAsync(context, cancellationToken).ConfigureAwait(false)));
        }

        return new CheckedPlan(checkedSteps);
    }
}

/// <summary>A step, and what it would do on this machine.</summary>
public sealed record CheckedStep(PlanStep Step, StepCheck Check);

/// <summary>A plan checked against the machine: what each step would do.</summary>
public sealed class CheckedPlan(IReadOnlyList<CheckedStep> steps)
{
    public IReadOnlyList<CheckedStep> Steps { get; } = steps;

    public bool IsBlocked => Steps.Any(step => step.Check.Change == StepChange.Blocked);

    public bool HasChanges => Steps.Any(step => step.Check.MakesChange);

    public int Count(StepChange change) => Steps.Count(step => step.Check.Change == change);

    /// <summary>The secrets the changes need, each once, in the order the steps need them.</summary>
    public IReadOnlyList<InstallSecret> NeededSecrets()
        => [.. Steps.Where(step => step.Check.MakesChange).SelectMany(step => step.Step.SecretsNeeded(step.Check)).Distinct()];

    /// <summary>
    /// Makes the changes, step by step in order. Each step is checked again just before it runs, so one that an earlier
    /// step already took care of is skipped. A failure stops the install there; running it again carries on.
    /// </summary>
    public async Task ApplyAsync(InstallContext context, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsBlocked)
        {
            throw new InstallerRefusedException("The plan has a step that cannot go ahead; nothing was installed.");
        }

        var notYet = Steps.Where(step => step.Check.MakesChange && !step.Step.CanApply).Select(step => step.Step.Target).ToArray();
        if (notYet.Length > 0)
        {
            throw new InstallerRefusedException(
                $"This installer cannot install {string.Join(", ", notYet)} yet, so nothing was installed. The plan (--plan) shows what an install will do.");
        }

        var missing = NeededSecrets().Where(secret => !context.Secrets.Has(secret)).ToArray();
        if (missing.Length > 0)
        {
            throw new InstallerRefusedException(
                $"The install needs {string.Join(" and ", missing.Select(InstallSecrets.Describe))}, which {(missing.Length == 1 ? "was" : "were")} not given, so nothing was installed.");
        }

        foreach (var (step, planned) in Steps)
        {
            if (!planned.MakesChange)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var doing = $"{PlanText.Verb(planned.Change)} {PlanText.Noun(step.Kind)} {step.Target}";
            try
            {
                var check = await step.CheckAsync(context, cancellationToken).ConfigureAwait(false);
                if (check.Change == StepChange.Blocked)
                {
                    // The machine changed since the plan was checked: stop, not skip (earlier steps may have run).
                    throw new StepBlockedException($"{doing}: {check.Detail ?? "it can no longer go ahead"}");
                }

                if (!check.MakesChange)
                {
                    continue;
                }

                doing = $"{PlanText.Verb(check.Change)} {PlanText.Noun(step.Kind)} {step.Target}";
                progress?.Invoke($"{doing}…");
                await step.ApplyAsync(context, check, cancellationToken).ConfigureAwait(false);
                context.MarkApplied(step);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                context.Log.Write($"Failed: {doing}: {error.Message}");
                throw error is InstallerException installerError
                    ? installerError
                    : new InstallerException($"{doing} failed: {error.Message}", error);
            }

            progress?.Invoke($"{doing}: done.");
        }
    }
}

/// <summary>How a checked plan reads, in <c>--plan</c>'s output and on the review page.</summary>
public static class PlanText
{
    private static readonly StepKind[] Order = Enum.GetValues<StepKind>();

    /// <summary>The plan's lines: the steps grouped by what they make (folders, files, …), then a summary line.</summary>
    public static IReadOnlyList<string> Lines(CheckedPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new List<string>();
        var width = Math.Min(44, plan.Steps.Select(step => step.Step.Target.Length).DefaultIfEmpty(0).Max());
        foreach (var kind in Order)
        {
            var steps = plan.Steps.Where(step => step.Step.Kind == kind).ToArray();
            if (steps.Length == 0)
            {
                continue;
            }

            lines.Add(Heading(kind));
            foreach (var (step, check) in steps)
            {
                var detail = check.Detail is null || check.Change == StepChange.Blocked ? string.Empty : $" ({check.Detail})";
                lines.Add($"  {Word(check.Change),-9}  {step.Target.PadRight(width)}  {step.Purpose}{detail}");
                if (check.Change == StepChange.Blocked && check.Detail is not null)
                {
                    lines.Add($"  {string.Empty,-9}  {check.Detail}");
                }
            }
        }

        lines.Add(string.Empty);
        lines.Add(Summary(plan));
        return lines;
    }

    public static string Summary(CheckedPlan plan)
    {
        if (plan.IsBlocked)
        {
            var blocked = plan.Count(StepChange.Blocked);
            return $"Blocked: {blocked} {(blocked == 1 ? "step cannot" : "steps cannot")} go ahead, so nothing will be installed.";
        }

        return plan.HasChanges
            ? $"{plan.Count(StepChange.Create)} to create, {plan.Count(StepChange.Change)} to change, {plan.Count(StepChange.Unchanged)} unchanged."
            : "Nothing to change: this machine is already as the answers describe.";
    }

    public static string Word(StepChange change) => change switch
    {
        StepChange.Create => "create",
        StepChange.Change => "change",
        StepChange.Unchanged => "unchanged",
        StepChange.Info => "info",
        StepChange.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, null)
    };

    internal static string Verb(StepChange change) => change == StepChange.Create ? "Creating" : "Changing";

    internal static string Noun(StepKind kind) => kind switch
    {
        StepKind.Folder => "folder",
        StepKind.File => "file",
        StepKind.User => "user",
        StepKind.Package => "packages",
        StepKind.Container => "container",
        StepKind.Service => "service",
        StepKind.Port => "port",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static string Heading(StepKind kind) => kind switch
    {
        StepKind.Folder => "Folders",
        StepKind.File => "Files",
        StepKind.User => "Users",
        StepKind.Package => "Packages",
        StepKind.Container => "Containers",
        StepKind.Service => "Services",
        StepKind.Port => "Ports",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
