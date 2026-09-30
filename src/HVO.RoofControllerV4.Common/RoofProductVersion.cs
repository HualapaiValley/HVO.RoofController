using System.Reflection;

namespace HVO.RoofControllerV4.Common;

/// <summary>
/// The product version every program and image carries (Directory.Build.props, docs/releasing.md): 4.0.0 for a release,
/// 4.0.0-rc.1 for a prerelease, 4.0.0-ci.123 from CI and 4.0.0-dev from a workstation, with the commit after a + when the
/// build knew it (4.0.0-ci.123+0123abcd…).
/// </summary>
public static class RoofProductVersion
{
    /// <summary>How many characters of the commit <see cref="Describe"/> shows.</summary>
    public const int ShortCommitLength = 12;

    /// <summary>The informational version of <paramref name="assembly"/>: the version, and the commit after a + when known.</summary>
    public static string Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "unknown"
            : informational.Trim();
    }

    /// <summary>The version without its commit: 4.0.0-ci.123 for 4.0.0-ci.123+0123abcd….</summary>
    public static string WithoutCommit(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }

    /// <summary>The commit after the +, or null.</summary>
    public static string? Commit(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 || plus == informationalVersion.Length - 1 ? null : informationalVersion[(plus + 1)..];
    }

    /// <summary>The version as people read it: 4.0.0, or 4.0.0-ci.123 (commit 0123abcd4567).</summary>
    public static string Describe(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        var version = WithoutCommit(informationalVersion);
        var commit = Commit(informationalVersion);
        return commit is null
            ? version
            : $"{version} (commit {(commit.Length > ShortCommitLength ? commit[..ShortCommitLength] : commit)})";
    }

    /// <summary><see cref="Describe"/> for <paramref name="assembly"/>.</summary>
    public static string Describe(Assembly assembly) => Describe(Of(assembly));
}
