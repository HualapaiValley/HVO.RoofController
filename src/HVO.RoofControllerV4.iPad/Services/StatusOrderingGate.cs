using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Identifies one status-producing request: a local, never-reset sequence taken before the request is sent, plus
/// the connection generation it was sent on.
/// </summary>
public readonly record struct StatusTicket(long Sequence, long Generation);

public readonly record struct StatusAcceptance(bool Accepted, bool InstanceChanged, string? PreviousInstanceId, string? PreviousControllerName)
{
    public static StatusAcceptance Rejected { get; } = new(false, false, null, null);
}

/// <summary>
/// Decides whether a status snapshot or a failure may update the UI. Polls, commands and Stop complete out of order;
/// a result is applied only if it is newer than what is already shown, so a slow poll can never overwrite a newer
/// Stop result, and a late failure can never overwrite a newer success.
/// </summary>
/// <remarks>
/// Ordering uses the controller's <see cref="RoofStatusResponse.StatusVersion"/> when both snapshots carry one and
/// come from the same controller process, and the local request sequence otherwise. A restarted controller
/// (new <see cref="RoofStatusResponse.ControllerInstanceId"/>) resets its version counter, so the local sequence
/// decides across restarts.
/// </remarks>
public sealed class StatusOrderingGate
{
    private readonly object _sync = new();
    private long _nextSequence;
    private long _generation;
    private long _lastAppliedSequence;
    private long _lastAppliedVersion;
    private long _lastSuccessSequence;
    private string? _instanceId;
    private string? _controllerName;

    public string? ControllerInstanceId
    {
        get
        {
            lock (_sync)
            {
                return _instanceId;
            }
        }
    }

    public string? ControllerName
    {
        get
        {
            lock (_sync)
            {
                return _controllerName;
            }
        }
    }

    public long Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    /// <summary>Takes a ticket. Call this before sending the request whose result will be offered to the gate.</summary>
    public StatusTicket Begin()
    {
        lock (_sync)
        {
            return new StatusTicket(++_nextSequence, _generation);
        }
    }

    /// <summary>
    /// Forgets the applied state after the controller endpoint changes. Tickets from earlier generations are rejected.
    /// </summary>
    public void Reset(long generation)
    {
        lock (_sync)
        {
            _generation = generation;
            _lastAppliedSequence = 0;
            _lastAppliedVersion = 0;
            _lastSuccessSequence = 0;
            _instanceId = null;
            _controllerName = null;
        }
    }

    public StatusAcceptance TryAccept(StatusTicket ticket, RoofStatusResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_sync)
        {
            if (ticket.Generation != _generation)
            {
                return StatusAcceptance.Rejected;
            }

            var instanceId = string.IsNullOrWhiteSpace(snapshot.ControllerInstanceId) ? null : snapshot.ControllerInstanceId;
            var instanceChanged = instanceId is not null && _instanceId is not null && !string.Equals(instanceId, _instanceId, StringComparison.Ordinal);

            bool accept;
            if (instanceChanged || instanceId is null || _instanceId is null)
            {
                accept = ticket.Sequence > _lastAppliedSequence;
            }
            else if (snapshot.StatusVersion > 0 && _lastAppliedVersion > 0)
            {
                accept = snapshot.StatusVersion > _lastAppliedVersion
                    || (snapshot.StatusVersion == _lastAppliedVersion && ticket.Sequence > _lastAppliedSequence);
            }
            else
            {
                accept = ticket.Sequence > _lastAppliedSequence;
            }

            if (!accept)
            {
                return StatusAcceptance.Rejected;
            }

            var previousInstanceId = _instanceId;
            var previousName = _controllerName;

            _lastAppliedSequence = Math.Max(_lastAppliedSequence, ticket.Sequence);
            _lastSuccessSequence = Math.Max(_lastSuccessSequence, ticket.Sequence);
            _lastAppliedVersion = snapshot.StatusVersion;
            if (instanceId is not null)
            {
                _instanceId = instanceId;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.ControllerName))
            {
                _controllerName = snapshot.ControllerName;
            }

            return new StatusAcceptance(true, instanceChanged, instanceChanged ? previousInstanceId : null, instanceChanged ? previousName : null);
        }
    }

    /// <summary>
    /// True when a failure for <paramref name="ticket"/> may be shown, meaning no newer request has succeeded.
    /// </summary>
    public bool TryAcceptFailure(StatusTicket ticket)
    {
        lock (_sync)
        {
            return ticket.Generation == _generation && ticket.Sequence > _lastSuccessSequence;
        }
    }
}
