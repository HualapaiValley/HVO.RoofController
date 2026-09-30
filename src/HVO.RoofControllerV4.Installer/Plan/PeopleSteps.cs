using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The controller's first person, an admin, added once the controller runs: through its API with the installer's admin
/// key, as <c>hvo-roof setup --create-admin</c> adds one. Only while the controller has no admin: a person of that name,
/// or another admin, leaves it as it is. Their password, and PIN, are typed or given in files, and never saved, logged
/// or shown.
/// </summary>
public sealed class FirstAdminStep(ControllerLayout layout, FirstAdminSettings admin, ApiKeyAllocation adminKey) : PlanStep
{
    /// <summary>Where people are added.</summary>
    public const string UsersPath = "api/v4.0/Identity/Users";

    public override StepKind Kind => StepKind.User;

    public override string Target => admin.Name;

    public override string Purpose => admin.Pin
        ? "the first admin: signs in to the web UI, and at the kiosk with a PIN, and adds everyone else"
        : "the first admin: signs in to the web UI and adds everyone else";

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ControllerIdentity identity;
        try
        {
            identity = ControllerIdentity.Read(context.Machine, layout.IdentityFile);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new StepCheck(StepChange.Info, "only root can read the controller's people: run with sudo to check them"));
        }
        catch (InstallerException error)
        {
            return Task.FromResult(new StepCheck(StepChange.Blocked, $"{error.Message} Restore it from a backup, then run the installer again."));
        }

        if (identity.Users.FirstOrDefault(user => string.Equals(user.Name, admin.Name, StringComparison.OrdinalIgnoreCase)) is { } person)
        {
            return Task.FromResult(StepCheck.Unchanged($"there already, as {person.Role}"));
        }

        var admins = identity.Users.Where(user => user.Role.Equals(RoofControllerApiContract.AdminRole, StringComparison.OrdinalIgnoreCase)).Select(user => user.Name).ToArray();
        return Task.FromResult(admins.Length > 0
            ? new StepCheck(StepChange.Info, $"not added: the controller has an admin already ({string.Join(", ", admins)})")
            : new StepCheck(StepChange.Create, $"added through the controller's API as {RoofControllerApiContract.AdminRole}, with a password{(admin.Pin ? " and a PIN" : string.Empty)}"));
    }

    public override IReadOnlyList<InstallSecret> SecretsNeeded(StepCheck check)
        => check.Change != StepChange.Create ? []
            : admin.Pin ? [InstallSecret.AdminPassword, InstallSecret.AdminPin]
            : [InstallSecret.AdminPassword];

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        var password = context.Secrets[InstallSecret.AdminPassword] ?? throw NotGiven(InstallSecret.AdminPassword);
        var pin = admin.Pin ? context.Secrets[InstallSecret.AdminPin] ?? throw NotGiven(InstallSecret.AdminPin) : null;
        var keyFile = ApiKeyFiles.KeyFile(layout.Secrets, adminKey.Index);
        var key = ControllerProbe.ReadKey(context.Machine, keyFile)
            ?? throw new InstallerException($"The installer's admin key ({keyFile}) is gone: run the installer again.");
        var request = new RoofUserCreateRequest { Name = admin.Name, Role = RoofControllerApiContract.AdminRole, Password = password, Pin = pin };
        var response = await ControllerApi.SendAsync(
            context, MachineSurveyor.ControllerContainer, key, UsersPath, JsonSerializer.Serialize(request, JsonSerializerOptions.Web), cancellationToken).ConfigureAwait(false);
        switch (response.Status)
        {
            case 200 or 201:
                context.Log.Write($"Added {admin.Name}, the controller's first admin ({RoofControllerApiContract.AdminRole}{(pin is null ? string.Empty : ", with a PIN")}), with {adminKey.Name}.");
                return;
            case 409:
                // Added since the check, by someone else: their password is theirs.
                var there = $"{admin.Name} was added by someone else while the installer ran, so the password typed here was not used.";
                context.Log.Write(there);
                context.Progress?.Invoke(there);
                return;
            case null:
                throw new InstallerException($"The controller did not answer ({response.Reason}), so {admin.Name} was not added. Run the installer again once it runs.");
            case 401 or 403:
                throw new InstallerException($"The controller refused the installer's admin key ({keyFile}, HTTP {response.Status}), so {admin.Name} was not added. Run the installer again: it redeploys a controller that does not know its keys.");
            default:
                throw new InstallerException($"The controller did not add {admin.Name}: HTTP {response.Status}{(response.Problem is { } problem ? $", {context.Log.Redact(problem)}" : string.Empty)}");
        }
    }

    private static InstallerException NotGiven(InstallSecret secret)
        => new($"The install needs {InstallSecrets.Describe(secret)}, which was not given.");
}
