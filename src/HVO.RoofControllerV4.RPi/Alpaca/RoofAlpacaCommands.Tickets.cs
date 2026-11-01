using System.Security.Claims;
using System.Security.Cryptography;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Security;

namespace HVO.RoofControllerV4.RPi.Alpaca;

/// <summary>What an Alpaca command came to: the controller's answer, and what the history records of it.</summary>
/// <param name="Result">The controller's answer (its status, or a refusal).</param>
/// <param name="ReasonKeys">The reasons that were not safe when it was judged.</param>
/// <param name="GrantId">The override grant it used, if any.</param>
public sealed record RoofAlpacaCommandOutcome(Result<RoofControllerStatus> Result, IReadOnlyList<string> ReasonKeys, string? GrantId);

/// <summary>What a ticket request came to: the ticket, or why not.</summary>
public sealed record RoofAlpacaTicketOutcome(RoofAlpacaTicket? Ticket, int StatusCode, RoofControllerException? Refusal);

// Tickets and the Alpaca Close (#220, §2, §10, §16 Y2/Y3, §19 L1, §20 N4): a single-use ticket per command, issued with the
// controller's instance, generation and authorization epoch on its monotonic clock; the command carries it, is checked
// against it, and is admitted under the controller's lock (IRoofCommandAdmission). The scope re-read starts at the
// ticket's issue.
public sealed partial class RoofAlpacaCommands
{
    private readonly Dictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
    private readonly LinkedList<Ticket> _ticketOrder = new();

    /// <summary>
    /// Issues a ticket for one command of <paramref name="kind"/> by the External Alpaca account's key behind
    /// <paramref name="user"/>, and starts the last-moment re-read of the blocking telescopes. An Open ticket is refused in
    /// this version (403).
    /// </summary>
    public RoofAlpacaTicketOutcome IssueTicket(RoofAlpacaCommandKind kind, ClaimsPrincipal user, RoofAlpacaClient client)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (kind != RoofAlpacaCommandKind.Close)
        {
            return new RoofAlpacaTicketOutcome(null, StatusCodes.Status403Forbidden,
                new RoofControllerException(RoofControllerErrorCode.AlpacaCommandRefused, "Alpaca Open is refused in this version: no Open ticket is issued.") { AlpacaRefusal = "OpenNotAvailable" });
        }

        var options = _options.CurrentValue;
        var budget = TimeSpan.FromMilliseconds(options.ScopeRereadBudgetMs);
        // Only the delay this Close would really wait counts (§20 N1/N4): what is left of it, or none.
        var delay = _controller.GetStartDelay(RoofMotionDirection.Closing);
        var deadline = budget + delay + RoofAlpacaContract.AnswerMargin;
        var arrival = _safety.Timestamp();
        var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Ticket ticket;
        lock (_gate)
        {
            RefreshEpoch_NoLock();
            ticket = new Ticket(
                id,
                kind,
                _controller.InstanceId,
                _controller.CommandGeneration,
                _epoch,
                RoofPrincipalFactory.GetCredentialId(user) ?? string.Empty,
                user.Identity?.Name ?? "the External Alpaca account",
                arrival,
                arrival + Ticks(RoofAlpacaContract.TicketLifetime),
                arrival + Ticks(budget),
                arrival + Ticks(deadline),
                client);
            _tickets[id] = ticket;
            _ticketOrder.AddLast(ticket);
            while (_ticketOrder.Count > RoofAlpacaContract.MaximumTickets)
            {
                // Oldest first: a dropped ticket is unknown, so its command is refused.
                _tickets.Remove(_ticketOrder.First!.Value.Id);
                _ticketOrder.RemoveFirst();
            }
        }

        // The re-read starts at the ticket's issue; the command joins it (it was issued after the command arrived).
        if (ConfiguredBlockingScopes().Count > 0)
        {
            _ = _safety.RereadBlockingScopesAsync(arrival, ticket.BudgetEnd);
        }

        _logger.LogInformation("Alpaca {Kind} ticket issued for {Account} (ClientID {ClientId} at {ClientAddress}, reported); generation {Generation}",
            kind, ticket.Account, client.ClientId, client.Address, ticket.Generation);
        return new RoofAlpacaTicketOutcome(
            new RoofAlpacaTicket(id, kind, ticket.InstanceId, ticket.Generation, deadline.TotalMilliseconds),
            StatusCodes.Status200OK,
            null);
    }

    /// <summary>
    /// An Alpaca Close: its ticket checked (fields, identity and kind, then a used ticket's stored answer, an in-progress
    /// one's answer, then expiry), the re-read waited for within the budget, then the controller's admission under its
    /// lock with the Alpaca rules. A duplicate of a ticket already used gets the first request's answer, never a second
    /// admission.
    /// </summary>
    public async Task<RoofAlpacaCommandOutcome> CloseAsync(RoofAlpacaCommandRequest request, ClaimsPrincipal user, RoofAlpacaClient client)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(request.TicketId) || request.TicketId.Length > 64 || string.IsNullOrWhiteSpace(request.InstanceId) || request.Generation is null)
        {
            return Refused("TicketInvalid", "The command carries no ticket, or its ticket, instance or generation is missing or malformed.");
        }

        Ticket? ticket;
        Task<RoofAlpacaCommandOutcome>? inProgress = null;
        TaskCompletionSource<RoofAlpacaCommandOutcome>? mine = null;
        (string Rule, string Message)? refusal = null;

        // Decided under the gate, answered after it: a refusal's answer carries the controller's status, and nothing may
        // hold the gate while it waits for the controller's lock (an admission holds that lock and then takes the gate).
        lock (_gate)
        {
            _tickets.TryGetValue(request.TicketId, out ticket);
            if (ticket is null)
            {
                refusal = ("TicketUnknown", "The ticket is unknown: never issued here, forgotten, or issued by a controller that has since restarted.");
            }

            // Identity, kind and field consistency first.
            else if (!string.Equals(ticket.InstanceId, _controller.InstanceId, StringComparison.Ordinal)
                || !string.Equals(request.InstanceId, ticket.InstanceId, StringComparison.Ordinal)
                || request.Generation != ticket.Generation
                || ticket.Kind != RoofAlpacaCommandKind.Close
                || !string.Equals(ticket.KeyId, RoofPrincipalFactory.GetCredentialId(user) ?? string.Empty, StringComparison.Ordinal))
            {
                refusal = ("TicketMismatch", "The ticket does not match the command: another kind, another controller instance or generation, or another key.");
            }
            else if (ticket.Outcome is { } done)
            {
                // Used: the stored answer of its first request, while the record is kept (also after the 5 s deadline).
                inProgress = done.Task;
            }
            else if (_time.GetTimestamp() > ticket.TicketDeadline)
            {
                refusal = ("TicketExpired", "The ticket expired: the command came more than 5 s after it was issued.");
            }
            else
            {
                mine = new TaskCompletionSource<RoofAlpacaCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                ticket.Outcome = mine;
            }
        }

        if (refusal is { } refused)
        {
            return Refused(refused.Rule, refused.Message);
        }

        if (inProgress is not null)
        {
            // In progress: waits for and gets that answer, never a second admission.
            return await inProgress.ConfigureAwait(false);
        }

        RoofAlpacaCommandOutcome outcome;
        try
        {
            outcome = await AdmitCloseAsync(ticket!, client).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The Alpaca Close on ticket {TicketId} failed unexpectedly", ticket!.Id);
            outcome = new RoofAlpacaCommandOutcome(Result<RoofControllerStatus>.Failure(
                new RoofControllerException(RoofControllerErrorCode.Unknown, "The Alpaca Close failed unexpectedly. See the controller log.", innerException: ex)), [], null);
        }

        mine!.TrySetResult(outcome);
        return outcome;
    }

    private async Task<RoofAlpacaCommandOutcome> AdmitCloseAsync(Ticket ticket, RoofAlpacaClient client)
    {
        // The re-read (started at the ticket's issue) is waited for outside the lock, within the budget.
        if (ConfiguredBlockingScopes().Count > 0)
        {
            await _safety.RereadBlockingScopesAsync(ticket.Arrival, ticket.BudgetEnd).ConfigureAwait(false);
        }

        var admission = new Admission(this, ticket, client);
        var result = await _controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing)
        {
            Owner = new RoofMotionOwner(RoofMotionOwnerKind.Alpaca, ticket.Account),
            Admission = admission
        }).ConfigureAwait(false);

        if (result.IsSuccessful)
        {
            _logger.LogInformation("Alpaca Close accepted on ticket {TicketId} (ClientID {ClientId} at {ClientAddress}, reported){Grant}",
                ticket.Id, client.ClientId, client.Address, admission.UsedGrant is { } used ? $" with override grant {used.Id}" : string.Empty);
        }
        else if (result.Error is RoofControllerException { Code: RoofControllerErrorCode.FaultLatched })
        {
            // A latch refuses every Close; the UIs say so beside the latch itself (§6).
            RaiseAlert("Refused", RoofAlpacaCommandKind.Close, "An Alpaca Close was refused: a safety fault is latched; a person clears it after checking the roof.", ["fault-latched"], client);
        }

        return new RoofAlpacaCommandOutcome(result, admission.Reasons, admission.UsedGrant?.Id);
    }

    /// <summary>A refusal before admission, with the controller's status. Never called holding the gate.</summary>
    private RoofAlpacaCommandOutcome Refused(string rule, string message)
    {
        _logger.LogInformation("Alpaca command refused ({Rule}): {Message}", rule, message);
        return new RoofAlpacaCommandOutcome(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.AlpacaCommandRefused, message, _controller.GetCurrentStatusSnapshot()) { AlpacaRefusal = rule }), [], null);
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    /// <summary>Tickets are kept until 10 minutes past their deadline, then forgotten (an old one is then unknown).</summary>
    private void PruneTickets_NoLock(long now)
    {
        var memory = Ticks(RoofAlpacaContract.TicketMemory);
        while (_ticketOrder.First is { } oldest && now > oldest.Value.TicketDeadline + memory)
        {
            _tickets.Remove(oldest.Value.Id);
            _ticketOrder.RemoveFirst();
        }
    }

    /// <summary>A ticket (its mutable part guarded by the gate).</summary>
    private sealed class Ticket(
        string id,
        RoofAlpacaCommandKind kind,
        string instanceId,
        long generation,
        long epoch,
        string keyId,
        string account,
        long arrival,
        long ticketDeadline,
        long budgetEnd,
        long answerDeadline,
        RoofAlpacaClient client)
    {
        public string Id { get; } = id;

        public RoofAlpacaCommandKind Kind { get; } = kind;

        public string InstanceId { get; } = instanceId;

        public long Generation { get; } = generation;

        public long Epoch { get; } = epoch;

        public string KeyId { get; } = keyId;

        public string Account { get; } = account;

        /// <summary>When the command arrived: the ticket's issue (monotonic).</summary>
        public long Arrival { get; } = arrival;

        /// <summary>The ticket's 5 s deadline (monotonic).</summary>
        public long TicketDeadline { get; } = ticketDeadline;

        /// <summary>When the re-read budget ends (monotonic).</summary>
        public long BudgetEnd { get; } = budgetEnd;

        /// <summary>When the command's answer is due at the latest (budget, delay and margin from the issue).</summary>
        public long AnswerDeadline { get; } = answerDeadline;

        public RoofAlpacaClient Client { get; } = client;

        /// <summary>Its command's answer: in progress, then done. Null until a command uses it.</summary>
        public TaskCompletionSource<RoofAlpacaCommandOutcome>? Outcome { get; set; }
    }

    /// <summary>
    /// One Alpaca Close's checks under the controller's lock (#220's rule order), with access to the command state through
    /// the gate (taken briefly, never calling back into the controller).
    /// </summary>
    private sealed class Admission(RoofAlpacaCommands owner, Ticket ticket, RoofAlpacaClient client) : IRoofCommandAdmission
    {
        // The margin the relay sequence needs within the answer's deadline.
        private static readonly TimeSpan RelayMargin = TimeSpan.FromMilliseconds(100);

        /// <summary>The grant used up at the use-up point, if one was in force.</summary>
        public RoofOverrideGrantRecord? UsedGrant { get; private set; }

        /// <summary>The reasons that were not safe at the last judgement.</summary>
        public IReadOnlyList<string> Reasons { get; private set; } = [];

        public RoofControllerException? CheckGate()
        {
            lock (owner._gate)
            {
                owner.RefreshEpoch_NoLock();
                if (owner._mode == RoofAlpacaCommandMode.Off)
                {
                    // Log-only: no alert while commands are off.
                    return Refusal("CommandsOff", "Alpaca commands are off on this controller (RoofController:Alpaca:AllowCommands is Off).");
                }

                if (owner._epoch != ticket.Epoch)
                {
                    return Refusal("AuthorizationChanged", "The External Alpaca account, its key, AllowCommands or ServerAddresses changed after the ticket was issued.");
                }
            }

            return string.Equals(ticket.InstanceId, owner._controller.InstanceId, StringComparison.Ordinal)
                ? null
                : Refusal("TicketMismatch", "The ticket was issued by another controller instance.");
        }

        public RoofControllerException? CheckHumanControl()
        {
            string? holder;
            RoofStopHoldState hold;
            lock (owner._gate)
            {
                holder = owner._leases.Values.OrderBy(lease => lease.TakenAt).FirstOrDefault()?.Name;
                hold = owner._hold;
            }

            if (holder is not null)
            {
                var refusal = Refusal("LeaseHeld", $"Operator lease held by {holder}: Alpaca commands wait until they release it or it lapses.");
                owner.RaiseAlert("Refused", RoofAlpacaCommandKind.Close, $"An Alpaca Close was refused: the operator lease is held by {holder}.", ["lease-held:" + holder], client);
                return refusal;
            }

            if (hold.Held)
            {
                var refusal = Refusal("StopHold", $"The human Stop hold is set (by {hold.SetBy}): a person clears it in the web UI, the terminal or the kiosk.");
                owner.RaiseAlert("Refused", RoofAlpacaCommandKind.Close, $"An Alpaca Close was refused: the human Stop hold is set (by {hold.SetBy}).", ["stop-hold"], client);
                return refusal;
            }

            return null;
        }

        public RoofControllerException? CheckDeadline(TimeSpan wait)
        {
            var now = owner._time.GetTimestamp();
            var finish = now + owner.Ticks(wait + RelayMargin);
            return finish <= ticket.AnswerDeadline
                ? null
                : Refusal("DeadlineExceeded", "The command came too late to finish within its answer's deadline; nothing moved. Send it again.");
        }

        public void UseGrant()
        {
            lock (owner._gate)
            {
                if (owner._grants.Remove(RoofAlpacaCommandKind.Close, out var grant))
                {
                    owner.EndStepAway_NoLock(grant.Id);
                }

                if (grant is not null && grant.ExpiresAt > owner._time.GetTimestamp())
                {
                    UsedGrant = new RoofOverrideGrantRecord(grant.Id, grant.ReasonKeys);
                    owner._logger.LogWarning("AUDIT override grant {GrantId} (by {GrantedBy}) used up by an Alpaca Close on ticket {TicketId}, whatever follows",
                        grant.Id, grant.GrantedBy, ticket.Id);
                }
            }

            if (UsedGrant is not null)
            {
                owner.NotifyChanged();
            }
        }

        public RoofControllerException? CheckGeneration(long generation)
            => generation == ticket.Generation
                ? null
                : new RoofControllerException(RoofControllerErrorCode.Superseded,
                    "Superseded: a Stop, a protective stop or another command came after this command's ticket; nothing moved for it.") { AlpacaRefusal = "Superseded" };

        public RoofControllerException? CheckPolicy() => Judge(continuation: false);

        public RoofControllerException? Recheck()
            => CheckGate() ?? CheckHumanControl() ?? CheckDeadline(TimeSpan.Zero) ?? Judge(continuation: true);

        public void ContinuationRefused(RoofControllerException refusal)
            => owner.RaiseAlert("ContinuationRefused", RoofAlpacaCommandKind.Close,
                $"An admitted Alpaca Close was refused before its relay, after the direction-change delay: {refusal.Message} The roof stays stopped.",
                refusal.ReasonKeys, client);

        /// <summary>
        /// The fresh-evidence guard, then the Close policy judged now: every configured blocking telescope clear, from a
        /// read issued after the command arrived. Otherwise the grant used must cover every reason. Weather never gates
        /// Close.
        /// </summary>
        private RoofControllerException? Judge(bool continuation)
        {
            var configured = owner.ConfiguredBlockingScopes();
            if (configured.Count == 0)
            {
                Reasons = [];
                return null;
            }

            var judgements = owner._safety.JudgeBlockingScopes(ticket.Arrival, configured);
            if (!continuation && judgements.Any(judgement => !judgement.IssuedAfterArrival))
            {
                return Refusal("NoFreshEvidence", "No read of every blocking telescope was made after the command arrived; nothing moved.");
            }

            var unsafeOnes = judgements.Where(judgement => judgement.Clearance != RoofScopeClearance.Clear).ToArray();
            Reasons = [.. unsafeOnes.Select(judgement => RoofAlpacaContract.ReasonKey(
                judgement.Clearance == RoofScopeClearance.NotClear ? RoofAlpacaContract.ScopeNotClearKind : RoofAlpacaContract.ScopeUnknownKind,
                judgement.Name))];
            if (unsafeOnes.Length == 0)
            {
                return null;
            }

            var text = string.Join("; ", unsafeOnes.Select(judgement => judgement.Text));
            if (UsedGrant is { } grant && Reasons.All(reason => grant.ReasonKeys.Contains(reason, StringComparer.Ordinal)))
            {
                owner._logger.LogWarning("AUDIT Alpaca Close on ticket {TicketId} goes ahead under override grant {GrantId}, which lifts: {Reasons}", ticket.Id, grant.Id, text);
                return null;
            }

            var why = UsedGrant is null
                ? $"Not safe to close: {text}. A person may grant an override in the web UI, the terminal or the kiosk."
                : $"Not safe to close: {text}. The override grant used covered other reasons; a new grant is needed.";
            owner.RaiseAlert("Refused", RoofAlpacaCommandKind.Close, $"An Alpaca Close was refused: {text}.", Reasons, client);
            return new RoofControllerException(RoofControllerErrorCode.AlpacaCommandRefused, why) { AlpacaRefusal = "NotSafe", ReasonKeys = Reasons };
        }

        private static RoofControllerException Refusal(string rule, string message)
            => new(RoofControllerErrorCode.AlpacaCommandRefused, message) { AlpacaRefusal = rule };
    }
}

/// <summary>An override grant as a command used it: its id and the reasons it lifts.</summary>
public sealed record RoofOverrideGrantRecord(string Id, IReadOnlyList<string> ReasonKeys);
