using System.Globalization;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Answers;

/// <summary>Resolves explicit setup-user consent against the account database, never against sudo-group membership.</summary>
public static class SetupUserEnrollment
{
    public static async Task<LinuxAuthSettings> ResolveAsync(InstallerMachine machine, LinuxAuthSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.EnrollSetupUser) return settings;
        var name = machine.Environment("SUDO_USER");
        if (!settings.Enabled || machine.Os != InstallerOs.Linux || !machine.IsRoot
            || !uint.TryParse(machine.Environment("SUDO_UID"), NumberStyles.None, CultureInfo.InvariantCulture, out var uid) || uid == 0
            || string.IsNullOrEmpty(name) || new LinuxAuthSettings { Enabled = true, Users = [new() { Name = name }] }.Problems().Any())
            throw new InstallerUsageException("Setup-user enrollment requires sudo from one named non-root Linux account, with matching SUDO_USER and SUDO_UID; otherwise list the intended account explicitly.");
        var result = await machine.Commands.RunAsync(new CommandLine("getent", "passwd", uid.ToString(CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(false);
        var rows = result.Output.Trim().Split('\n');
        var fields = rows[0].Split(':');
        if (!result.Succeeded || rows.Length != 1 || fields.Length != 7 || fields[0] != name
            || !uint.TryParse(fields[2], out var actualUid) || actualUid != uid)
            throw new InstallerUsageException("The sudo caller does not match the Linux account database; setup-user enrollment refused.");
        var existing = settings.Users.SingleOrDefault(user => user.Name == name);
        if (existing is not null && (existing.Role != RoofControllerApiContract.AdminRole || !existing.LocalAdmin))
            throw new InstallerUsageException("The setup user already has a different explicit role or local permission; review the enrollment choices.");
        return settings with
        {
            EnrollSetupUser = false,
            Users = existing is null ? [.. settings.Users, new() { Name = name, Role = RoofControllerApiContract.AdminRole, LocalAdmin = true }] : settings.Users
        };
    }
}
