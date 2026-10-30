using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>Which page of the roof command history to read (<see cref="RoofControlApi.GetHistoryAsync"/>).</summary>
public sealed record RoofHistoryQuery
{
    /// <summary>Only commands sent by, or on behalf of, this name. Admins only, unless it is the caller's own name.</summary>
    public string? User { get; init; }

    /// <summary>Only commands at or after this time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only commands before this time.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Entries to skip, newest first.</summary>
    public int Skip { get; init; }

    /// <summary>Entries in the page: 1 to <see cref="RoofCommandHistoryContract.MaximumTake"/>.</summary>
    public int Take { get; init; } = RoofCommandHistoryContract.DefaultTake;
}
