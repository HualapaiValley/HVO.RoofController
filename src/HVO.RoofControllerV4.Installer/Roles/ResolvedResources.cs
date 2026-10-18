using System.Globalization;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Roles;

/// <summary>
/// The controller's CPU and memory limits, resolved as the deploy script resolves them (resolve_limits; #149, review
/// ROOF-151-R2 F5), with one precedence everywhere: a limit given (the answers', the install record's, or --cpus and
/// --memory) first, then the newest desired-limits record's (<see cref="DesiredLimits"/>: its value when it keeps it as
/// set, RoofController's default when it keeps the default), then the running controller's, then the default. The
/// installer gives the script only the limits given, so its plan, its guards and the script agree. A record that is not
/// valid is never applied: one a limit is needed from refuses the run (<see cref="Problems"/>), as the script does.
/// </summary>
public sealed record ResolvedResources
{
    /// <summary>A limit given: in the answers, the install record, or on the command line.</summary>
    public const string Given = "given";

    /// <summary>A limit the newest desired-limits record keeps as set.</summary>
    public const string Kept = "kept from the last deploy";

    /// <summary>RoofController's default.</summary>
    public const string Default = "the default";

    /// <summary>A limit the running controller has, with no record.</summary>
    public static string KeptFromController => $"kept from {MachineSurveyor.ControllerContainer}";

    /// <summary>The limits the deploy gives; a CPU or memory limit is none for the default, so it says the default.</summary>
    public required ContainerResources Resources { get; init; }

    public required string CpusSource { get; init; }

    public required string MemorySource { get; init; }

    /// <summary>What refuses the deploy before anything changes, as the script refuses it.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>
    /// What the installer warns of, as the script does: a limit given that differs from the record's (the one given comes
    /// first), a record written for another container than the running controller, and a record not valid that is not needed.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The limits <paramref name="given"/> resolved against the records and the controller in <paramref name="survey"/>.</summary>
    public static ResolvedResources Resolve(ContainerResources given, MachineSurvey survey)
    {
        ArgumentNullException.ThrowIfNull(given);
        ArgumentNullException.ThrowIfNull(survey);
        given = given.Normalised();
        var problems = new List<string>();
        var warnings = new List<string>();
        var (cpus, cpusSource) = given.Cpus is { } givenCpus ? (givenCpus, Given) : ((decimal?)null, (string?)null);
        var (memory, memorySource) = given.Memory is { } givenMemory ? (givenMemory, Given) : ((string?)null, (string?)null);
        var docker = survey.Docker;
        var container = MachineSurveyor.ControllerContainer;
        if (docker.DesiredLimitsProblem is { } unreadable)
        {
            problems.Add($"Could not read the desired limits' records from Docker (volumes {DesiredLimits.Prefix(container)}<generation>; a limit not given keeps them): {unreadable}. Nothing was changed; check Docker and run it again.");
        }
        else if (docker.DesiredLimits is { IsValid: false } invalid)
        {
            if (cpus is null || memory is null)
            {
                problems.Add($"The newest desired-limits record, {invalid.Volume}, is not valid: {invalid.Problem}. It is not applied, and nothing was changed: give controller.resources.cpus and controller.resources.memory (or --cpus and --memory; the deploy then records them in its place), or remove it (docker volume rm {invalid.Volume}).");
            }
            else
            {
                warnings.Add($"The newest desired-limits record, {invalid.Volume}, is not valid ({invalid.Problem}): it is not applied, and the deploy records the limits given in its place.");
            }
        }
        else if (docker.DesiredLimits is { } record)
        {
            var recordCpus = decimal.Parse(record.Cpus!, NumberStyles.Number, CultureInfo.InvariantCulture);
            var recordMemory = new ContainerResources { Memory = record.Memory }.Normalised();
            if (cpus is { } c && record.CpusSource == DesiredLimits.Set && c != recordCpus)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"The controller's CPU limit is given as {c:0.##} (controller.resources.cpus, or --cpus), and the deploy script's last record, {record.Volume} ({record.Written}), keeps {record.Cpus}: the one given comes first, and the deploy records it in its place."));
            }

            if (memory is not null && record.MemorySource == DesiredLimits.Set
                && new ContainerResources { Memory = memory }.MemoryBytes != recordMemory.MemoryBytes)
            {
                warnings.Add($"The controller's memory limit is given as {memory} (controller.resources.memory, or --memory), and the deploy script's last record, {record.Volume} ({record.Written}), keeps {record.Memory}: the one given comes first, and the deploy records it in its place.");
            }

            if (cpus is null || memory is null)
            {
                // One written for another container than the running controller is older than it: it still comes first.
                if (survey.Controller is not { } running)
                {
                    warnings.Add($"The desired limits' record, {record.Volume}, was written for container {record.Controller}, and there is no {container} now: a limit not given still takes its value.");
                }
                else if (!running.Id.StartsWith(record.Controller!, StringComparison.Ordinal))
                {
                    warnings.Add($"The desired limits' record, {record.Volume}, was written for container {record.Controller}, not for the running {container} ({Short(running.Id)}): a limit not given still takes the record's value, not the running controller's.");
                }
            }

            if (cpus is null)
            {
                (cpus, cpusSource) = record.CpusSource == DesiredLimits.Set ? ((decimal?)recordCpus, Kept) : ((decimal?)null, Default);
            }

            if (memory is null)
            {
                (memory, memorySource) = record.MemorySource == DesiredLimits.Set ? (recordMemory.Memory, Kept) : (null, Default);
            }
        }

        if (survey.Controller is { } controller && (cpusSource is null || memorySource is null))
        {
            if (cpusSource is null && controller.NanoCpus > 0)
            {
                if (controller.NanoCpus % 10_000_000 != 0)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{container} has a CPU limit of {controller.NanoCpus} billionths of a CPU (NanoCpus), not a number of CPUs with at most two decimals, so it cannot be kept: give controller.resources.cpus (or --cpus). Nothing was changed."));
                }
                else
                {
                    (cpus, cpusSource) = (controller.NanoCpus / 1_000_000_000m, KeptFromController);
                }
            }

            if (memorySource is null && controller.MemoryBytes > 0)
            {
                const long MiB = 1024 * 1024;
                if (controller.MemoryBytes % MiB != 0)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{container} has a memory limit of {controller.MemoryBytes} bytes, not a whole number of MiB, so it cannot be kept: give controller.resources.memory (or --memory). Nothing was changed."));
                }
                else
                {
                    var mib = controller.MemoryBytes / MiB;
                    (memory, memorySource) = (mib % 1024 == 0 ? string.Create(CultureInfo.InvariantCulture, $"{mib / 1024}g") : string.Create(CultureInfo.InvariantCulture, $"{mib}m"), KeptFromController);
                }
            }
        }

        var resources = (given with { Cpus = cpusSource == Default ? null : cpus, Memory = memorySource == Default ? null : memory }).Normalised();

        // A limit kept from the running controller is held to the floor too; one given is checked with the answers.
        if (cpusSource == KeptFromController && resources.CpusOrDefault < ContainerResources.MinimumCpus)
        {
            problems.Add($"The controller's CPU limit of {resources.CpusText} ({KeptFromController}, as none is given) is less than 1 vCPU, the minimum (RoofController's): give controller.resources.cpus (or --cpus) of at least 1.");
        }

        if (memorySource == KeptFromController && resources.MemoryBytes < ContainerResources.MinimumMemoryBytes)
        {
            problems.Add($"The controller's memory limit of {resources.MemoryOrDefault} ({KeptFromController}, as none is given) is less than 1g, the minimum (RoofController's): give controller.resources.memory (or --memory) of at least 1g.");
        }

        return new ResolvedResources
        {
            Resources = resources,
            CpusSource = cpusSource ?? Default,
            MemorySource = memorySource ?? Default,
            Problems = problems,
            Warnings = warnings
        };
    }

    /// <summary>The limits as the plan says them, with where a limit not given comes from (the record, the running controller).</summary>
    public string Describe()
    {
        var kept = new List<string>();
        if (CpusSource is not (Given or Default))
        {
            kept.Add($"the CPUs {CpusSource}");
        }

        if (MemorySource is not (Given or Default))
        {
            kept.Add($"the memory {MemorySource}");
        }

        return kept.Count == 0 ? Resources.Describe() : $"{Resources.Describe()} ({string.Join(", ", kept)})";
    }

    private static string Short(string id) => id.Length > 12 ? id[..12] : id;
}
