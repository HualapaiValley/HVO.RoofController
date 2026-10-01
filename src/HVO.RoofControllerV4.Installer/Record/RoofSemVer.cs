using System.Text.RegularExpressions;

namespace HVO.RoofControllerV4.Installer.Record;

/// <summary>
/// Release versions in SemVer 2.0's order: by major, minor and patch; a prerelease (<c>4.0.0-rc.1</c>) before its
/// release; a prerelease's dot-separated identifiers compared as numbers when both are, a number before a word, words
/// ordinally, and more identifiers after fewer that match. Build metadata (after <c>+</c>) does not count.
/// </summary>
public static partial class RoofSemVer
{
    /// <summary>True when <paramref name="version"/> is a SemVer version (<c>4.0.0</c>, <c>4.0.1-rc.1</c>).</summary>
    public static bool IsValid(string? version) => version is not null && Pattern().IsMatch(version);

    /// <summary>Below zero when <paramref name="left"/> comes before <paramref name="right"/>, zero when they are the same release, above zero after.</summary>
    /// <exception cref="FormatException">Either is not a SemVer version.</exception>
    public static int Compare(string left, string right)
    {
        var (a, b) = (Parts(left), Parts(right));
        for (var index = 0; index < 3; index++)
        {
            if (CompareNumbers(a.Core[index], b.Core[index]) is not 0 and var core)
            {
                return core;
            }
        }

        // A release comes after its prereleases.
        if (a.Prerelease.Length == 0 || b.Prerelease.Length == 0)
        {
            return Math.Sign(b.Prerelease.Length.CompareTo(a.Prerelease.Length));
        }

        for (var index = 0; index < Math.Min(a.Prerelease.Length, b.Prerelease.Length); index++)
        {
            if (CompareIdentifiers(a.Prerelease[index], b.Prerelease[index]) is not 0 and var identifier)
            {
                return identifier;
            }
        }

        return Math.Sign(a.Prerelease.Length.CompareTo(b.Prerelease.Length));
    }

    /// <summary>True when <paramref name="left"/> is an older release than <paramref name="right"/>.</summary>
    public static bool IsOlder(string left, string right) => Compare(left, right) < 0;

    private static (string[] Core, string[] Prerelease) Parts(string version)
    {
        var match = Pattern().Match(version ?? throw new ArgumentNullException(nameof(version)));
        if (!match.Success)
        {
            throw new FormatException($"'{version}' is not a release version (major.minor.patch, as 4.0.0 or 4.0.1-rc.1).");
        }

        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
        return ([match.Groups["major"].Value, match.Groups["minor"].Value, match.Groups["patch"].Value], prerelease);
    }

    private static int CompareIdentifiers(string left, string right)
    {
        var (leftNumber, rightNumber) = (IsNumber(left), IsNumber(right));
        return leftNumber && rightNumber ? CompareNumbers(left, right)
            : leftNumber ? -1
            : rightNumber ? 1
            : Math.Sign(string.CompareOrdinal(left, right));
    }

    // Numbers of any length, without leading zeros (the pattern refuses them): the longer is larger, else by digits.
    private static int CompareNumbers(string left, string right)
        => Math.Sign(left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right));

    private static bool IsNumber(string identifier) => identifier.All(char.IsAsciiDigit);

    [GeneratedRegex(@"^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
