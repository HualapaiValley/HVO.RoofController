using System.Text.RegularExpressions;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Installer.Answers;

/// <summary>Host authentication is installed only with this explicit opt-in; no Linux account is created.</summary>
public sealed record LinuxAuthSettings
{
    public bool Enabled { get; init; }
    /// <summary>Explicitly enroll only the verified sudo caller as a controller administrator. Resolved to Users before planning.</summary>
    public bool EnrollSetupUser { get; init; }
    public IReadOnlyList<LinuxAuthUserSettings> Users { get; init; } = [];

    public IEnumerable<string> Problems()
    {
        if (!Enabled && (Users.Count > 0 || EnrollSetupUser))
            yield return "controller.linuxAuth.users requires enabled: true.";
        if (Users.Select(user => user.Name).Distinct(StringComparer.Ordinal).Count() != Users.Count)
            yield return "Each Linux authentication account may be listed only once.";
        foreach (var user in Users)
        {
            if (!Regex.IsMatch(user.Name, "^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant) || user.Name == "root")
                yield return "Linux authentication names must be existing non-root local account names.";
            if (user.Role is not (RoofControllerApiContract.ViewerRole or RoofControllerApiContract.OperatorRole or RoofControllerApiContract.AdminRole))
                yield return "Linux authentication roles must be RoofViewer, RoofOperator or RoofAdmin.";
        }
    }

    public bool Equals(LinuxAuthSettings? other)
        => other is not null && Enabled == other.Enabled && EnrollSetupUser == other.EnrollSetupUser && Users.SequenceEqual(other.Users);
    public override int GetHashCode() => HashCode.Combine(Enabled, Users.Count, EnrollSetupUser);
}

/// <summary>Explicit enrollment of an existing account. LocalAdmin permits only the fixed helper, independently of role.</summary>
public sealed record LinuxAuthUserSettings
{
    public string Name { get; init; } = string.Empty;
    public string Role { get; init; } = RoofControllerApiContract.ViewerRole;
    public bool LocalAdmin { get; init; }
}
