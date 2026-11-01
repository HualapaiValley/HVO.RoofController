using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// What the Alpaca page does (#220): a person clears the human Stop hold ("Allow Alpaca again"), grants an override for
/// one Alpaca Close (or Open) from a refused Close's (or Open's) alert, after a question that says what it lifts, unless
/// the controller says no grant of that kind can be made now (an Open's in Block mode), revokes a grant, and acknowledges
/// an alert. Each goes with the person's own session; the controller refuses the kiosk's device key there.
/// What the page shows (the commands setting, the hold, the operator lease, grants, alerts and warnings) is the status's.
/// </summary>
/// <remarks>Used on the UI thread only; <see cref="Changed"/> is raised there.</remarks>
public sealed class KioskAlpacaPanel
{
    /// <summary>The grant question's title for a Close (an Open's is <see cref="RoofAlpacaText.GrantQuestionTitleFor"/>).</summary>
    public const string GrantQuestionTitle = "Allow one Alpaca Close?";

    /// <summary>Why allowing Alpaca again, granting and revoking are not offered to someone without the Operator role.</summary>
    public const string NeedsOperator = "allowing Alpaca again, granting and revoking need the Operator role";

    private readonly KioskConsole _console;
    private CancellationTokenSource _reset = new();

    public KioskAlpacaPanel(KioskConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    /// <summary>What is on its way to the controller, or null.</summary>
    public string? Busy { get; private set; }

    /// <summary>What the last request came to, or why it was not sent; null when there is nothing to say.</summary>
    public KioskNotice? Message { get; private set; }

    /// <summary>The question asked before a grant is sent, or null.</summary>
    public KioskQuestion? Question { get; private set; }

    /// <summary>Raised after anything here changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Forgets the question and what was said: the kiosk was locked, or unlocked by someone. A question asked of one
    /// person is never answered by the next, and an answer still on its way is not shown to them.
    /// </summary>
    public void Reset()
    {
        _reset.Cancel();
        _reset = new CancellationTokenSource();
        Busy = null;
        Message = null;
        Question = null;
        Raise();
    }

    /// <summary>Drops the question without sending anything.</summary>
    public void Dismiss()
    {
        if (Question is null)
        {
            return;
        }

        Question = null;
        Message = new KioskNotice("Nothing was granted.", KioskNoticeLevel.Info, Now);
        Raise();
    }

    /// <summary>"Allow Alpaca again": clears the human Stop hold (an operator's session).</summary>
    public Task ClearHoldAsync()
    {
        if (Refuse(operatorOnly: true))
        {
            return Task.CompletedTask;
        }

        return RunAsync("Allowing Alpaca again…", async () =>
        {
            await _console.Client.Alpaca.ClearHoldAsync();
            return () => Message = new KioskNotice("The Stop hold is cleared: Alpaca commands may run again when the controller allows them.", KioskNoticeLevel.Info, Now);
        });
    }

    /// <summary>
    /// Asks before granting an override for one Alpaca Close (or Open) from a refused Close's (or Open's) alert
    /// (<see cref="RoofAlpacaText.CanGrantFrom"/>): what it lifts, for which client, for how long, and that nothing else is
    /// bypassed. Nothing is sent until the person answers <see cref="RoofAlpacaText.GrantLabel"/> for its kind. One the
    /// controller says cannot be made now (<see cref="RoofAlpacaText.GrantUnavailable"/>) is not asked: why is said.
    /// </summary>
    public void AskGrant(RoofAlpacaAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (Busy is not null || Refuse(operatorOnly: true))
        {
            return;
        }

        if (!RoofAlpacaText.CanGrantFrom(alert))
        {
            Message = new KioskNotice("No grant can be made from this alert: only a refused Close or Open whose every reason a grant can lift can be allowed once.", KioskNoticeLevel.Warning, Now);
            Raise();
            return;
        }

        if (Unavailable(alert))
        {
            return;
        }

        var kind = alert.Command ?? RoofAlpacaCommandKind.Close;
        var window = _console.View.Status?.Alpaca?.OverrideWindowMinutes ?? 0;
        // The reasons the person is shown are the ones sent: the alert's, as it was when asked.
        IReadOnlyList<string> reasons = [.. alert.ReasonKeys];
        Message = null;
        Question = new KioskQuestion(
            RoofAlpacaText.GrantQuestionTitleFor(kind),
            RoofAlpacaText.DescribeGrantQuestion(alert with { ReasonKeys = reasons }, window),
            [new KioskAnswer(RoofAlpacaText.GrantLabel(kind), () => GrantAsync(alert, reasons))]);
        Raise();
    }

    /// <summary>Revokes the grant of <paramref name="kind"/> (an operator's session).</summary>
    public Task RevokeAsync(RoofAlpacaCommandKind kind)
    {
        if (Refuse(operatorOnly: true))
        {
            return Task.CompletedTask;
        }

        return RunAsync("Revoking the grant…", async () =>
        {
            try
            {
                await _console.Client.Alpaca.RevokeAsync(kind);
            }
            catch (RoofApiException gone) when (gone.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Used, lapsed, or cancelled already: the touch on Revoke took the person's lease back, which drops it.
                return () => Message = new KioskNotice($"The {kind} grant is no longer in force.", KioskNoticeLevel.Info, Now);
            }

            return () => Message = new KioskNotice($"The {kind} grant is revoked.", KioskNoticeLevel.Info, Now);
        });
    }

    /// <summary>Acknowledges an alert (a person's session).</summary>
    public Task AcknowledgeAsync(RoofAlpacaAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (Refuse(operatorOnly: false))
        {
            return Task.CompletedTask;
        }

        return RunAsync("Acknowledging the alert…", async () =>
        {
            await _console.Client.Alpaca.AcknowledgeAlertAsync(alert.Id);
            return () => Message = new KioskNotice("Alert acknowledged.", KioskNoticeLevel.Info, Now);
        });
    }

    private DateTimeOffset Now => _console.Client.Options.TimeProvider.GetUtcNow();

    private Task GrantAsync(RoofAlpacaAlert alert, IReadOnlyList<string> reasons)
    {
        Question = null;
        if (Refuse(operatorOnly: true) || Unavailable(alert))
        {
            return Task.CompletedTask;
        }

        // Through the console: the person steps away from the operator lease with the grant (KioskConsole.GrantAsync).
        var kind = alert.Command ?? RoofAlpacaCommandKind.Close;
        return RunAsync($"Granting one Alpaca {kind}…", async () =>
        {
            var grant = await _console.GrantAsync(kind, reasons);
            return () => Message = new KioskNotice($"Granted. {RoofAlpacaText.DescribeGrant(grant)}", KioskNoticeLevel.Warning, Now);
        });
    }

    // True, with why said, when the newest status says no grant of the alert's kind can be made now (an Open's in Block
    // mode): asked from a stale page, or answered after the status changed.
    private bool Unavailable(RoofAlpacaAlert alert)
    {
        if (RoofAlpacaText.GrantUnavailable(alert, _console.View.Status?.Alpaca) is not { } unavailable)
        {
            return false;
        }

        Question = null;
        Message = new KioskNotice($"Not sent: {unavailable}", KioskNoticeLevel.Warning, Now);
        Raise();
        return true;
    }

    // True, with the reason said, when nobody (or nobody who may) is signed in to send it.
    private bool Refuse(bool operatorOnly)
    {
        var view = _console.View;
        var reason = !view.IsUnlocked ? _console.Wording.AlpacaNeedsPerson
            : operatorOnly && !view.IsOperator ? NeedsOperator
            : null;
        if (reason is null)
        {
            return false;
        }

        Question = null;
        Message = new KioskNotice($"Not sent: {reason}.", KioskNoticeLevel.Warning, Now);
        Raise();
        return true;
    }

    /// <summary>
    /// Sends what <paramref name="send"/> sends, saying <paramref name="busy"/> meanwhile, then shows what it came to. A
    /// request sent is not cancelled by a lock: only its answer is dropped, if <see cref="Reset"/> came first.
    /// </summary>
    private async Task RunAsync(string busy, Func<Task<Action>> send)
    {
        if (Busy is not null)
        {
            return;
        }

        var reset = _reset.Token;
        Busy = busy;
        Message = null;
        Raise();
        Action show;
        try
        {
            show = await send();
        }
        catch (Exception error)
        {
            var message = RoofText.DescribeFailure(error);
            var detail = error is RoofApiException { Detail: { Length: > 0 } text } && text != message ? $" {text}" : string.Empty;
            show = () => Message = new KioskNotice(message + detail, KioskNoticeLevel.Danger, Now);
        }

        if (reset.IsCancellationRequested)
        {
            return;
        }

        show();
        Busy = null;
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
