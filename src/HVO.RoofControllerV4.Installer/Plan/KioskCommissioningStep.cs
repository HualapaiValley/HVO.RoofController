using System.Text;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>One-use first-install eligibility. Kept outside removable controller data.</summary>
public sealed class KioskCommissioningClaimStep(ControllerLayout layout) : PlanStep
{
    public const string ClaimDirectory = "/var/lib/hvo-roof-commissioning";
    public const string ClaimFile = ClaimDirectory + "/first-install-claimed";
    internal bool Claimed { get; private set; }
    public override StepKind Kind => StepKind.File;
    public override string Target => ClaimFile;
    public override string Purpose => "claim one-time kiosk PIN setup for a genuine first installation";
    private bool Fresh(InstallerMachine machine) => !machine.FileExists(ClaimFile)
        && !machine.FileExists(InstallPaths.SystemRecord) && !machine.DirectoryExists(layout.Configuration)
        && !machine.DirectoryExists(layout.Data) && !machine.FileExists(LinuxAuthStep.StateFile);

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
        => Task.FromResult(Fresh(context.Machine) ? new StepCheck(StepChange.Create)
            : new StepCheck(StepChange.Blocked, "First-install evidence changed; commissioning cannot be recreated. Re-plan without temporary access."));

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        if (!Fresh(machine)) throw new InstallerException("First-install eligibility is no longer available.");
        machine.CreateDirectory(ClaimDirectory, Modes.PrivateFolder);
        using (var stream = new FileStream(machine.OnDisk(ClaimFile), new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = Modes.PrivateFile }))
        {
            stream.Write(Encoding.UTF8.GetBytes("One-time commissioning claimed. Never remove to recreate access.\n"));
            stream.Flush(true);
        }
        // sync -f flushes this filesystem, including the newly created directory entry.
        var flush = await machine.Commands.RunAsync(new CommandLine("sync", "-f", machine.OnDisk(ClaimDirectory)), cancellationToken).ConfigureAwait(false);
        if (!flush.Succeeded) throw new InstallerException("First-install claim could not be durably saved; no temporary access was created.");
        Claimed = true;
    }
}

/// <summary>Seed the fresh identity before controller launch; no startup or enrollment API can recreate this state.</summary>
public sealed class KioskCommissioningSeedStep(ControllerLayout layout, LinuxAuthSettings settings,
    KioskCommissioningClaimStep claim) : PlanStep
{
    public override StepKind Kind => StepKind.User;
    public override string Target => "installation owner's kiosk PIN";
    public override string Purpose => "temporary setup-only access; choose and confirm a personal PIN before operating";
    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
        => Task.FromResult(context.Machine.FileExists(layout.IdentityFile)
            ? new StepCheck(StepChange.Blocked, "An identity store already exists; temporary access is never recreated.")
            : settings.Users.Count != 1 || settings.Users[0].Role != RoofControllerApiContract.AdminRole || settings.Users[0].Name == KioskSteps.User
                ? new StepCheck(StepChange.Blocked, "Kiosk commissioning needs one explicitly selected human Linux administrator; multiple people retire temporary access.")
                : new StepCheck(StepChange.Create));

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        if (!claim.Claimed || context.Machine.FileExists(layout.IdentityFile))
            throw new InstallerException("No unused first-install claim is available.");
        var user = settings.Users.Single();
        if (user.Name == KioskSteps.User) throw new InstallerException("The kiosk service account cannot own a human PIN.");
        var native = LinuxAuthStep.Enrollments(context.Machine).Single(e => e.UserName == user.Name && e.Enabled);
        var lookup = await context.Machine.Commands.RunAsync(new CommandLine("getent", "passwd", user.Name), cancellationToken).ConfigureAwait(false);
        var fields = lookup.Output.Trim().Split(':');
        if (!lookup.Succeeded || fields.Length != 7 || fields[0] != user.Name || fields[2] != native.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || native.Uid < 1000 || fields[6].EndsWith("/nologin", StringComparison.Ordinal) || fields[6].EndsWith("/false", StringComparison.Ordinal))
            throw new InstallerException("Commissioning requires the selected existing human account; a service identity is not eligible.");
        var id = Guid.NewGuid().ToString("N"); var stamp = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var document = new
        {
            schemaVersion = 3, revision = 1, users = Array.Empty<object>(), apiKeys = Array.Empty<object>(), sessions = Array.Empty<object>(),
            linuxPrincipals = new[] { new { id, subject = native.Subject, name = user.Name, role = user.Role,
                enrollmentStamp = native.EnrollmentStamp, stamp, enabled = true } },
            ownedKeys = Array.Empty<object>(), assertionNonces = Array.Empty<object>(),
            kioskCredential = new { principalId = id, ownerStamp = stamp, stamp = Guid.NewGuid().ToString("N"), state = "pending",
                pinHash = (string?)null, createdUtc = now, expiresUtc = now.AddHours(24), failures = 0, lockedUntilUtc = (DateTimeOffset?)null }
        };
        // Exclusive creation refuses a concurrent controller or installer. A partial file is invalid, never repaired to pending.
        using (var stream = new FileStream(context.Machine.OnDisk(layout.IdentityFile), new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = Modes.PrivateFile }))
        {
            JsonSerializer.Serialize(stream, document, JsonSerializerOptions.Web);
            stream.Flush(true);
        }
        var flush = await context.Machine.Commands.RunAsync(new CommandLine("sync", "-f", context.Machine.OnDisk(layout.Identity)), cancellationToken).ConfigureAwait(false);
        if (!flush.Succeeded) throw new InstallerException("Commissioning state could not be durably saved; controller launch refused.");
    }
}
