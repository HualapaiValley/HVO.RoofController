using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>What a line of the Alpaca section is, and so what Enter on it offers.</summary>
internal enum RoofUiAlpacaItemKind
{
    /// <summary>Something to read: the commands setting, the lease, a warning.</summary>
    Info,

    /// <summary>The human Stop hold, while it is set: Allow Alpaca again.</summary>
    Hold,

    /// <summary>An override grant in force: Revoke.</summary>
    Grant,

    /// <summary>An alert: Acknowledge, and Allow this Close once where a grant can lift its reasons.</summary>
    Alert
}

/// <summary>One line of the Alpaca section.</summary>
internal sealed record RoofUiAlpacaItem(RoofUiAlpacaItemKind Kind, string Text, RoofOverrideGrant? Grant = null, RoofAlpacaAlert? Alert = null);

/// <summary>
/// The Alpaca command state on the Roof page (#220), in place of the roof's state and readings: which commands are on,
/// the human Stop hold, the operator lease (with the idle warning for the lease this interface holds), the warnings, the
/// override grants and the alerts, newest first. Enter on a line offers what can be done about it, in a prompt over the
/// page: Allow Alpaca again on the hold, Revoke on a grant, Acknowledge on an alert, and Allow this Close once on a
/// refused Close a grant can lift, which asks again with what the grant lifts before it is sent. Each is a person's, never
/// an API key's. Stop withdraws a question it crossed: its action then sends nothing.
/// </summary>
internal sealed class RoofUiAlpacaSection
{
    internal const string FrameTitle = "ALPACA (Enter on a line: its actions)";

    internal const string NoAction = "Nothing to do on this line: Enter acts on the Stop hold, a grant or an alert.";

    internal const string HoldTitle = "Stop hold";

    internal const string GrantTitle = "Override grant";

    internal const string AlertTitle = "Alpaca alert";

    /// <summary>What clearing the hold does, said with the question.</summary>
    internal const string ClearHoldExplains =
        "Allow Alpaca again clears it: Alpaca commands may then run, as the controller's settings allow. The next person's Stop sets it again.";

    private readonly RoofTerminalUi _ui;
    private IReadOnlyList<RoofUiAlpacaItem> _items = [];

    // The question on screen, and the count of Stops it was asked under: a Stop since withdraws it.
    private RoofUiPrompt? _prompt;
    private int _promptStops;

    public RoofUiAlpacaSection(RoofTerminalUi ui)
    {
        _ui = ui;
        Frame = new FrameView { Title = FrameTitle, X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(3), Visible = false };
        ui.Theme.SetFrame(Frame);
        List = new RoofUiItemList { Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        List.Accepting += (_, e) =>
        {
            e.Handled = true;
            Act();
        };
        Frame.Add(List);
        ui.StopSent += Withdraw;
    }

    public FrameView Frame { get; }

    public RoofUiItemList List { get; }

    /// <summary>The lines shown.</summary>
    public IReadOnlyList<RoofUiAlpacaItem> Items => _items;

    /// <summary>Shows the newest status's Alpaca command state.</summary>
    public void Show()
    {
        _items = DescribeItems(_ui.Connection is not null, _ui.Status, _ui.LeaseHolder);
        List.Show([.. _items.Select(item => item.Text)]);
    }

    /// <summary>Selects the first line that <paramref name="match"/> picks (tests, and reading without a screen).</summary>
    public void Select(Func<RoofUiAlpacaItem, bool> match)
    {
        var index = _items.ToList().FindIndex(item => match(item));
        if (index < 0)
        {
            throw new InvalidOperationException("No Alpaca line matches.");
        }

        List.Select(index);
    }

    /// <summary>
    /// The section's lines: the commands setting, the hold, the lease, the idle warning for <paramref name="person"/> (whose
    /// lease this interface holds), the warnings, the grants and the alerts (newest first, as the controller orders them).
    /// </summary>
    internal static IReadOnlyList<RoofUiAlpacaItem> DescribeItems(bool connected, RoofStatusResponse? status, string? person)
    {
        if (!connected)
        {
            return [new(RoofUiAlpacaItemKind.Info, "No controller is configured. Use Setup (F5).")];
        }

        if (status is null)
        {
            return [new(RoofUiAlpacaItemKind.Info, "No status from the controller yet.")];
        }

        if (status.Alpaca is not { } alpaca)
        {
            return [new(RoofUiAlpacaItemKind.Info, RoofCliAlpaca.NoState)];
        }

        var items = new List<RoofUiAlpacaItem>
        {
            new(RoofUiAlpacaItemKind.Info, RoofAlpacaText.DescribeMode(alpaca.Mode)),
            RoofAlpacaText.DescribeHold(alpaca.Hold) is { } hold
                ? new(RoofUiAlpacaItemKind.Hold, hold)
                : new(RoofUiAlpacaItemKind.Info, RoofCliAlpaca.HoldClear),
            new(RoofUiAlpacaItemKind.Info, RoofCliAlpaca.DescribeLease(alpaca.OperatorLease))
        };
        if (RoofAlpacaText.DescribeIdleWarning(alpaca.OperatorLease, person) is { } idle)
        {
            items.Add(new(RoofUiAlpacaItemKind.Info, idle));
        }

        items.AddRange(alpaca.Warnings.Select(warning => new RoofUiAlpacaItem(RoofUiAlpacaItemKind.Info, $"Warning: {warning}")));
        items.AddRange(alpaca.Grants.Count == 0
            ? [new(RoofUiAlpacaItemKind.Info, RoofCliAlpaca.NoGrants)]
            : alpaca.Grants.Select(grant => new RoofUiAlpacaItem(RoofUiAlpacaItemKind.Grant, RoofAlpacaText.DescribeGrant(grant), Grant: grant)));
        items.AddRange(alpaca.Alerts.Count == 0
            ? [new(RoofUiAlpacaItemKind.Info, RoofCliAlpaca.NoAlerts)]
            : alpaca.Alerts.Select(alert => new RoofUiAlpacaItem(RoofUiAlpacaItemKind.Alert, RoofCliAlpaca.DescribeAlert(alert), Alert: alert)));
        return items;
    }

    /// <summary>A short summary for the controller's readings: the commands setting, the hold while commands are on, new alerts.</summary>
    internal static IEnumerable<string> DescribeSummary(RoofStatusResponse status)
    {
        if (status.Alpaca is not { } alpaca)
        {
            yield break;
        }

        yield return $"Alpaca:  {RoofAlpacaText.DescribeMode(alpaca.Mode)}";
        if (alpaca.Mode != RoofAlpacaCommandMode.Off && RoofAlpacaText.DescribeHold(alpaca.Hold) is { } hold)
        {
            yield return hold;
        }

        var fresh = alpaca.Alerts.Count(alert => !alert.Acknowledged);
        if (fresh > 0)
        {
            yield return $"Alpaca alerts:  {fresh} not acknowledged; Alpaca shows them.";
        }
    }

    /// <summary>Enter on the selected line: the prompt of what can be done about it.</summary>
    public void Act()
    {
        if (List.SelectedIndex is not { } index || index >= _items.Count)
        {
            return;
        }

        var item = _items[index];
        switch (item.Kind)
        {
            case RoofUiAlpacaItemKind.Hold:
                Ask(new RoofUiPrompt(HoldTitle, $"{item.Text}\n{ClearHoldExplains}", [], [Action(RoofAlpacaText.ClearHoldLabel, ClearHold)]));
                break;
            case RoofUiAlpacaItemKind.Grant when item.Grant is { } grant:
                Ask(new RoofUiPrompt(GrantTitle, item.Text, [], [Action(RoofAlpacaText.RevokeLabel, () => Revoke(grant.Kind))]));
                break;
            case RoofUiAlpacaItemKind.Alert when item.Alert is { } alert:
                var actions = new List<RoofUiAction>();
                if (!alert.Acknowledged)
                {
                    actions.Add(Action(RoofAlpacaText.AcknowledgeLabel, () => Acknowledge(alert.Id)));
                }

                if (RoofAlpacaText.CanGrantFrom(alert))
                {
                    actions.Add(Action(RoofAlpacaText.GrantCloseLabel, () => AskGrant(alert)));
                }

                Ask(new RoofUiPrompt(AlertTitle, item.Text, [], actions, CloseLabel: actions.Count == 0 ? "Done" : "Cancel"));
                break;
            default:
                _ui.Say(NoAction);
                break;
        }
    }

    /// <summary>
    /// Asks whether to grant an override for one Close despite <paramref name="alert"/>'s reasons, with what it lifts, for
    /// whom and for how long (the shared question). Only its confirmation sends the grant, lifting exactly the alert's
    /// reasons; Cancel or Esc sends nothing.
    /// </summary>
    internal void AskGrant(RoofAlpacaAlert alert)
    {
        var keys = alert.ReasonKeys.ToArray();
        var lines = RoofAlpacaText.DescribeGrantQuestion(alert, _ui.Status?.Alpaca?.OverrideWindowMinutes ?? 0);
        Ask(new RoofUiPrompt($"{RoofAlpacaText.GrantCloseLabel}?", string.Join('\n', lines), [], [Action(RoofAlpacaText.GrantCloseLabel, () => Grant(keys))]));
    }

    private void Ask(RoofUiPrompt prompt)
    {
        _prompt = prompt;
        _promptStops = _ui.StopsSent;
        _ui.Ask(prompt);
    }

    /// <summary>
    /// An action of the question on screen: one withdrawn by Stop sends nothing, and an API key is refused here, before
    /// anything is sent (the controller refuses every API key for these).
    /// </summary>
    private RoofUiAction Action(string label, Action run)
    {
        RoofUiAction? action = null;
        action = new RoofUiAction(label, _ =>
        {
            if (_prompt is not { } prompt || !prompt.Actions.Any(candidate => ReferenceEquals(candidate, action)) || _ui.StopsSent != _promptStops)
            {
                return null;
            }

            if (_ui.Caller is { Kind: RoofCredentialKind.ApiKey })
            {
                return RoofCliAlpaca.PersonOnly;
            }

            _prompt = null;
            run();
            return null;
        });
        return action;
    }

    // Stop withdraws the question: it is closed, and its action sends nothing.
    private void Withdraw()
    {
        if (_prompt is not { } prompt)
        {
            return;
        }

        _prompt = null;
        if (ReferenceEquals(_ui.Panel?.Prompt, prompt))
        {
            _ui.ClosePanel();
        }
    }

    private void ClearHold() => Send("Allowing Alpaca again…", async (client, cancellationToken) =>
    {
        await client.Alpaca.ClearHoldAsync(cancellationToken).ConfigureAwait(false);
        return RoofCliAlpaca.HoldCleared;
    });

    private void Revoke(RoofAlpacaCommandKind kind) => Send($"Revoking the {kind} grant…", async (client, cancellationToken) =>
    {
        await client.Alpaca.RevokeAsync(kind, cancellationToken).ConfigureAwait(false);
        return $"Revoked the {kind} grant.";
    });

    private void Acknowledge(string id) => Send($"Acknowledging alert {id}…", async (client, cancellationToken) =>
    {
        var alert = await client.Alpaca.AcknowledgeAlertAsync(id, cancellationToken).ConfigureAwait(false);
        return $"Acknowledged alert {alert.Id}.";
    });

    private void Grant(IReadOnlyList<string> keys) => Send("Granting one Alpaca Close…", async (client, cancellationToken) =>
    {
        var grant = await client.Alpaca.GrantAsync(RoofAlpacaCommandKind.Close, keys, cancellationToken).ConfigureAwait(false);
        return $"Granted. {RoofAlpacaText.DescribeGrant(grant)}";
    });

    /// <summary>Sends one request; its result is said on the message line, and the status hub brings the new state.</summary>
    private void Send(string busy, Func<RoofControllerClient, CancellationToken, Task<string>> send)
        => _ = _ui.Run(busy, async (client, cancellationToken) =>
        {
            var done = await send(client, cancellationToken).ConfigureAwait(false);
            _ui.Post(() => _ui.Say(done));
        });
}
