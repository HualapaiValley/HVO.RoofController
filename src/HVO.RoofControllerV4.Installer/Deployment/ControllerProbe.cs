using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;

namespace HVO.RoofControllerV4.Installer.Deployment;

/// <summary>How the running controller answered an authenticated Status.</summary>
public enum ProbeOutcome
{
    /// <summary>HTTP 200: it knows the key.</summary>
    Accepted,

    /// <summary>HTTP 401: it does not know the key.</summary>
    Rejected,

    /// <summary>No answer, or another one: it is not ready, or not there.</summary>
    NoAnswer
}

/// <summary>
/// What the running controller said to an authenticated Status: whether it knows the key and, when it does, whether the
/// roof is moving. <see cref="Reason"/> says why there was no answer.
/// </summary>
public sealed record ProbeResult(ProbeOutcome Outcome, bool IsMoving = false, string? Motion = null, string? Reason = null)
{
    public bool IsAccepted => Outcome == ProbeOutcome.Accepted;
}

/// <summary>A key file the deploy script can be given (OPERATOR_KEY_FILE), and what it is.</summary>
public sealed record DeployKeyCandidate(string File, string Description);

/// <summary>
/// Asks the running controller for its Status from inside its container, over its loopback, as the deploy script does
/// (<see cref="ControllerApi"/>): the key never goes on a command line, and the log shows neither it nor the answer.
/// </summary>
public static class ControllerProbe
{
    /// <summary>The Status endpoint, which every role may call.</summary>
    public const string StatusPath = "api/v4.0/RoofControl/Status";

    /// <summary>Where the deploy script looks for its key by default, under a person's home folder.</summary>
    public const string OperatorKeyFile = ".config/hvo-roof/operator.key";

    public static async Task<ProbeResult> StatusAsync(InstallContext context, string container, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var response = await ControllerApi.SendAsync(context, container, key, StatusPath, null, cancellationToken).ConfigureAwait(false);
        switch (response.Status)
        {
            case null:
                return new ProbeResult(ProbeOutcome.NoAnswer, Reason: response.Reason);
            case 401:
                return new ProbeResult(ProbeOutcome.Rejected);
            case not 200:
                return new ProbeResult(ProbeOutcome.NoAnswer, Reason: $"its Status answered HTTP {response.Status}");
        }

        try
        {
            using var status = JsonDocument.Parse(response.Body);
            var root = status.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NotTheControllers;
            }

            var moving = root.TryGetProperty("isMoving", out var isMoving) && isMoving.ValueKind == JsonValueKind.True;
            var motion = root.TryGetProperty("commandedMotion", out var commanded) && commanded.ValueKind == JsonValueKind.String ? commanded.GetString() : null;
            return new ProbeResult(ProbeOutcome.Accepted, moving || (motion is not null && !motion.Equals("None", StringComparison.OrdinalIgnoreCase)), motion);
        }
        catch (JsonException)
        {
            return NotTheControllers;
        }
    }

    private static readonly ProbeResult NotTheControllers = new(ProbeOutcome.NoAnswer, Reason: "its Status answered with something that is not the controller's");

    /// <summary>
    /// The keys the deploy script could be given for the running controller, best first: the operator and admin keys the
    /// installer keeps, then any other operator or admin key in the secrets folder, then an operator.key file (root's,
    /// or that of the person who ran sudo) whose key the secrets folder configures by its hash. Each is one the new
    /// controller also reads, as the deploy script's pre-flight check needs. Kiosk keys are never used.
    /// </summary>
    public static IReadOnlyList<DeployKeyCandidate> Candidates(InstallerMachine machine, ControllerLayout layout, IReadOnlyList<ApiKeyAllocation> keys)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(keys);
        var configured = ApiKeyFiles.Read(machine, layout.Secrets);
        var deploys = configured.Where(key => key.IsUsable && !key.Kiosk && (key.Is(RoofControllerApiContract.OperatorRole) || key.Is(RoofControllerApiContract.AdminRole))).ToArray();
        var candidates = new List<DeployKeyCandidate>();
        foreach (var use in new[] { ApiKeyUse.Operator, ApiKeyUse.Admin })
        {
            if (keys.FirstOrDefault(key => key.Use == use && key.Reused) is { } kept && deploys.Any(key => key.Index == kept.Index))
            {
                candidates.Add(new DeployKeyCandidate(ApiKeyFiles.KeyFile(layout.Secrets, kept.Index), $"{kept.Name} ({kept.Role})"));
            }
        }

        foreach (var key in deploys)
        {
            var file = ApiKeyFiles.KeyFile(layout.Secrets, key.Index);
            if (candidates.All(candidate => candidate.File != file))
            {
                candidates.Add(new DeployKeyCandidate(file, $"{key.Name!.Trim()} ({key.Role!.Trim()})"));
            }
        }

        var hashes = configured
            .Where(key => !key.Kiosk && (key.Is(RoofControllerApiContract.OperatorRole) || key.Is(RoofControllerApiContract.AdminRole)))
            .Select(key => machine.ReadText(Path.Join(layout.Secrets, ApiKeyFiles.FileName(key.Index, "KeySha256")))?.Trim())
            .Where(hash => !string.IsNullOrEmpty(hash))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var home in Homes(machine))
        {
            var file = Path.Join(home, OperatorKeyFile);

            // The deploy script refuses a key file others can read.
            if (machine.GetMode(file) is not { } mode || (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            {
                continue;
            }

            if (ReadKey(machine, file) is { } key && hashes.Contains(Sha256(key)) && candidates.All(candidate => candidate.File != file))
            {
                candidates.Add(new DeployKeyCandidate(file, $"the key in {file}"));
            }
        }

        return candidates;
    }

    /// <summary>A key file's key as the deploy script reads it (without its line ends), or null when it is empty or missing.</summary>
    public static string? ReadKey(InstallerMachine machine, string file)
    {
        ArgumentNullException.ThrowIfNull(machine);
        try
        {
            var key = machine.ReadText(file)?.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
            return string.IsNullOrEmpty(key) ? null : key;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Homes(InstallerMachine machine)
    {
        yield return machine.Home;
        if (machine.IsRoot && machine.Environment("SUDO_USER") is { Length: > 0 } user && user != "root" && !user.Contains('/', StringComparison.Ordinal))
        {
            yield return machine.Os == InstallerOs.MacOS ? $"/Users/{user}" : $"/home/{user}";
        }
    }

    private static string Sha256(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLower(CultureInfo.InvariantCulture);
}
