using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>Which page of the roof command history to read (<see cref="RoofControlApi.GetHistoryAsync"/>).</summary>
public sealed record RoofHistoryQuery
{
    /// <summary>Only the commands of the person or API key named so now. Admins only, unless it is the caller's own name.</summary>
    public string? User { get; init; }

    /// <summary>
    /// Only the commands recorded under this identifier (an entry's <see cref="RoofCommandHistoryEntry.PrincipalId"/>), for
    /// someone since removed or a key since rotated. Admins only, unless it is the caller's own.
    /// </summary>
    public string? PrincipalId { get; init; }

    /// <summary>
    /// Only the caller's own commands, matched by their identifier, an admin's too (#208). Without it an admin reads
    /// everyone's, and <see cref="User"/> finds an API key named like them as well.
    /// </summary>
    public bool Mine { get; init; }

    /// <summary>Only commands at or after this time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only commands before this time.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Entries to skip, newest first.</summary>
    public int Skip { get; init; }

    /// <summary>Entries in the page: 1 to <see cref="RoofCommandHistoryContract.MaximumTake"/>.</summary>
    public int Take { get; init; } = RoofCommandHistoryContract.DefaultTake;
}
