using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>
/// The roof: its status from the hub, and Open, Close and Clear fault. A stale status is shown only as the last known
/// state, and motion is not offered on it: the roof could not be watched. Open and Close hold the operator lease while
/// the roof moves, as <c>hvo-roof open</c> does. History shows the roof command history (#206) in place of the
/// controller's readings: the caller's own commands, or everyone's for an admin.
/// </summary>
internal sealed class RoofUiRoofPage : RoofUiPage
{
    private const string NotConfigured = "no controller is configured";
    private const string ReadingsTitle = "CONTROLLER READINGS";
    private const string HistoryTitle = "COMMAND HISTORY";

    private readonly Label _status;
    private readonly Label _blocked;
    private readonly TerminalReadingView _state;
    private readonly TerminalReadingView _readings;
    private readonly FrameView _readingsFrame;
    private bool _commandInFlight;
    private string[]? _history;

    // The safety override question on screen, and the count of Stops it was asked under: a Stop since withdraws it.
    private RoofUiPrompt? _safetyPrompt;
    private int _safetyStops;

    public RoofUiRoofPage(RoofTerminalUi ui)
        : base(ui, "Roof")
    {
        _status = TextBlock(0, 1);
        var state = new FrameView { Title = "ROOF STATE", X = 0, Y = 1, Width = Dim.Percent(50), Height = Dim.Fill(3) };
        var readings = new FrameView { Title = ReadingsTitle, X = Pos.Right(state), Y = 1, Width = Dim.Fill(), Height = Dim.Fill(3) };
        _readingsFrame = readings;
        Ui.Theme.SetFrame(state);
        Ui.Theme.SetFrame(readings);
        _state = new TerminalReadingView { Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _readings = new TerminalReadingView { Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        state.Add(_state);
        readings.Add(_readings);
        _blocked = new Label { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 2 };
        Add(_status, state, readings, _blocked);
        OpenButton = AddButton("Open", null, () => Move(RoofMotionDirection.Opening));
        CloseButton = AddButton("Close", OpenButton, () => Move(RoofMotionDirection.Closing));
        OpenButton.SetScheme(Ui.Theme.Open);
        CloseButton.SetScheme(Ui.Theme.Close);
        ClearFaultButton = AddButton("Clear fault", CloseButton, ClearFault);
        var refresh = AddButton("Refresh", ClearFaultButton, Refresh);
        HistoryButton = AddButton("History", refresh, ToggleHistory);
        Ui.StopSent += WithdrawSafetyQuestion;
    }

    public Button OpenButton { get; }

    public Button CloseButton { get; }

    public Button ClearFaultButton { get; }

    /// <summary>Shows the command history in place of the readings, or the readings again.</summary>
    public Button HistoryButton { get; }

    /// <summary>True while the command history is shown in place of the controller's readings.</summary>
    public bool ShowsHistory { get; private set; }

    public override void Shown() => StatusChanged();

    public override void ConnectionChanged()
    {
        _history = null;
        if (ShowsHistory && ReferenceEquals(Ui.CurrentPage, this))
        {
            ReadHistory();
        }

        StatusChanged();
    }

    public override void StatusChanged()
    {
        var status = Ui.Status;
        if (status is null)
        {
            _state.Show([]);
            ShowReadings([]);
            _status.Text = Ui.Connection is null
                ? "No controller is configured. Use Setup (F5) to add its address and a credential."
                : "No status from the controller yet.";
        }
        else
        {
            var heading = Ui.StaleSince is { } since
                ? $"LAST KNOWN STATE, as of {RoofCliFormat.Time(status.SnapshotUtc)} (no status since {RoofCliFormat.Time(since)}). It may not be the roof's state now."
                : $"Status at {RoofCliFormat.Time(status.SnapshotUtc)}";
            _status.Text = $"{(Ui.IsStale ? "LAST KNOWN" : "LIVE")}: {RoofCliFormat.DescribeRoof(status)}";
            _status.SetScheme(status.IsFaultLatched ? Ui.Theme.Danger : Ui.IsStale ? Ui.Theme.Warning : Ui.Theme.Header);
            var rows = RoofCliFormat.DescribeStatus(status);
            _state.Show(rows.Where(row => IsStateRow(row.Label))
                .Select(row => $"{row.Label}:  {row.Value}").Prepend(heading).ToArray());
            ShowReadings(rows.Where(row => !IsStateRow(row.Label))
                .Select(row => $"{row.Label}:  {row.Value}").Concat(RoofStatusText.DescribeModeWarnings(status)).ToArray());
        }

        var open = BlockReason(RoofMotionDirection.Opening);
        var close = BlockReason(RoofMotionDirection.Closing);
        var clear = ClearFaultBlockReason();
        OpenButton.Enabled = open is null;
        CloseButton.Enabled = close is null;
        ClearFaultButton.Enabled = clear is null;
        // Green while offered, bright green while the roof moves that way, neutral while not offered (the reason is below).
        OpenButton.SetScheme(Ui.Theme.ForMotion(RoofActionStates.ForMotion(RoofMotionDirection.Opening, Ui.Status, Ui.IsStale, open), RoofMotionDirection.Opening));
        CloseButton.SetScheme(Ui.Theme.ForMotion(RoofActionStates.ForMotion(RoofMotionDirection.Closing, Ui.Status, Ui.IsStale, close), RoofMotionDirection.Closing));
        _blocked.Text = RoofCommandRules.DescribeMotionBlocks(open, close) ?? string.Empty;
    }

    // ROOF STATE: the roof, its faults, the safety conditions with each source, the lease and the watchdog.
    private static bool IsStateRow(string label)
        => label is "Roof" or "Last stop" or "Fault" or "Safety" or RoofStatusText.SafetySourceLabel or "Lease" or "Watchdog" or "Last error";

    public override string Describe() => string.Join('\n', (Ui.Status is null ? new[] { _status.Text } : _state.Lines).Concat(_readings.Lines).Append(_blocked.Text));

    /// <summary>
    /// Why Open or Close is not offered; null when it is. The rules every client shares (RoofCommandRules), and quitting,
    /// which waits on a motion started here. The controller still checks every request.
    /// </summary>
    internal string? BlockReason(RoofMotionDirection direction)
    {
        if (Ui.Connection is null)
        {
            return NotConfigured;
        }

        // Quitting comes after the role and the controller's availability, which say more.
        var state = CommandState;
        if (Ui.QuitRequested && state.CanOperate != false && RoofCommandRules.GetAvailabilityBlockReason(state.Status, state.IsStale) is null)
        {
            return "the interface is closing";
        }

        return RoofCommandRules.GetMotionBlockReason(direction, state);
    }

    internal string? ClearFaultBlockReason()
        => Ui.Connection is null ? NotConfigured : RoofCommandRules.GetClearFaultBlockReason(CommandState);

    // The role is known once the controller has said who the credential is.
    private RoofCommandState CommandState => new(Ui.Status, Ui.IsStale, Ui.Caller is null ? null : IsOperator, _commandInFlight, Ui.Caller is null ? null : IsAdmin);

    private void Move(RoofMotionDirection direction) => Move(direction, overrideSafety: false, Ui.StopsSent);

    // stopsSent: the count of Stops the command belongs to; for a confirmed override, the one its question was asked under.
    private void Move(RoofMotionDirection direction, bool overrideSafety, int stopsSent)
    {
        if (BlockReason(direction) is { } reason)
        {
            Ui.Say($"Not sent: {reason}.", error: true);
            return;
        }

        // #215: while the conditions are not safe and the interlock asks for an override, Open is sent only once the
        // person confirms, with the override.
        if (direction == RoofMotionDirection.Opening && !overrideSafety && RoofCommandRules.NeedsSafetyOverride(Ui.Status))
        {
            AskSafetyOverride(Ui.Status!.Safety);
            return;
        }

        var verb = direction == RoofMotionDirection.Opening ? "Open" : "Close";
        Command($"Sending {verb}…", async (client, cancellationToken) =>
        {
            RoofStatusResponse status;
            try
            {
                status = direction == RoofMotionDirection.Opening
                    ? await client.Roof.OpenAsync(overrideSafety, cancellationToken).ConfigureAwait(false)
                    : await client.Roof.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RoofApiException refusal) when (!overrideSafety && RoofCommandRules.IsSafetyOverrideRequest(refusal))
            {
                // The conditions were not safe when the controller checked, though this interface did not know: it asks now,
                // unless Stop withdrew this Open since, or the newest status blocks Open for another reason, which the
                // refusal and the Open button then say.
                return () =>
                {
                    if (refusal.RoofStatus is { } refused)
                    {
                        Ui.Apply(refused);
                    }

                    var detail = refusal.Detail is { Length: > 0 } text ? $" {text}" : string.Empty;
                    if (Ui.StopsSent != stopsSent || OpenBlockedForAnotherReason() is not null)
                    {
                        Ui.Say(RoofText.DescribeFailure(refusal) + detail, error: true);
                        return;
                    }

                    Ui.Say(RoofText.DescribeFailure(refusal), error: true);
                    AskSafetyOverride(refusal.RoofStatus?.Safety ?? Ui.Status?.Safety);
                };
            }
            catch (Exception error) when (RoofCli.CommandBuilder.MayHaveReachedController(error))
            {
                return () =>
                {
                    // The controller may have set the roof moving: it is treated as started here, so quitting while a
                    // status shows it moving sends Stop. With no answer, there is no lease to renew.
                    Ui.FollowUnansweredMotion();
                    Ui.Say(RoofCli.CommandBuilder.Unanswered(error, verb, "press F9"), error: true);
                    if (Ui.StopsSent != stopsSent)
                    {
                        Ui.Stop();
                    }
                };
            }

            return () =>
            {
                Ui.Apply(status);
                Ui.HoldLease(status);
                if (status.IsMoving && Ui.StopsSent != stopsSent)
                {
                    // Stop was sent while this command was on its way, and may have reached the controller first: Stop wins.
                    Ui.Say($"{verb} was accepted after Stop was sent from here, so Stop is sent again.", error: true);
                    Ui.Stop();
                    return;
                }

                Ui.Say(!status.IsMoving ? $"{verb} accepted. Roof: {RoofCliFormat.DescribeRoof(status)}."
                    : Ui.HoldsLease ? $"{verb} accepted. This interface renews the operator lease while the roof moves; F9 or quitting stops it."
                    : $"{verb} accepted. F9 or quitting stops it.");
            };
        }, motion: true);
    }

    /// <summary>
    /// Asks the safety override question over the page: "Open anyway" sends Open with the override (the controller logs
    /// it); Cancel or Esc sends nothing. F9 still sends Stop while it is open.
    /// </summary>
    internal void AskSafetyOverride(RoofSafetyStatus? safety)
    {
        var question = new RoofSafetyOverrideQuestion(safety);
        RoofUiPrompt? prompt = null;
        prompt = new RoofUiPrompt(
            question.Title,
            string.Join('\n', question.Lines),
            [],
            [new RoofUiAction(RoofSafetyText.OverrideConfirm, _ =>
            {
                // Only the question on screen, under the Stops it was asked under: one withdrawn by Stop sends nothing.
                if (!ReferenceEquals(_safetyPrompt, prompt) || Ui.StopsSent != _safetyStops)
                {
                    return null;
                }

                _safetyPrompt = null;
                Move(RoofMotionDirection.Opening, overrideSafety: true, _safetyStops);
                return null;
            })],
            CloseLabel: RoofSafetyText.OverrideCancel);
        _safetyPrompt = prompt;
        _safetyStops = Ui.StopsSent;
        Ui.Ask(prompt);
    }

    // Stop withdraws the safety override question: it is closed, and its Open anyway sends nothing.
    private void WithdrawSafetyQuestion()
    {
        if (_safetyPrompt is not { } prompt)
        {
            return;
        }

        _safetyPrompt = null;
        if (ReferenceEquals(Ui.Panel?.Prompt, prompt))
        {
            Ui.ClosePanel();
        }
    }

    // Why Open is not offered now, leaving out the command this interface has on its way (whose answer is being handled).
    private string? OpenBlockedForAnotherReason()
        => Ui.Connection is null ? NotConfigured : RoofCommandRules.GetMotionBlockReason(RoofMotionDirection.Opening, CommandState with { CommandInFlight = false });

    private void ClearFault()
    {
        if (ClearFaultBlockReason() is { } reason)
        {
            Ui.Say($"Not sent: {reason}.", error: true);
            return;
        }

        Command("Sending Clear fault…", async (client, cancellationToken) =>
        {
            var status = await client.Roof.ClearFaultAsync(null, cancellationToken).ConfigureAwait(false);
            return () =>
            {
                Ui.Apply(status);
                Ui.Say($"Clear fault accepted. Fault: {RoofCliFormat.DescribeFault(status)}.");
            };
        }, motion: false);
    }

    private void Refresh()
    {
        _ = Ui.Run("Reading the status…", async (client, cancellationToken) =>
        {
            var status = await client.Roof.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            Ui.Post(() =>
            {
                Ui.Apply(status);
                Ui.Say($"Read the status at {RoofCliFormat.Time(status.SnapshotUtc)}.");
            });
        });
        if (ShowsHistory)
        {
            ReadHistory();
        }
    }

    /// <summary>Shows the command history in place of the readings, reading it again; or the readings again.</summary>
    internal void ToggleHistory()
    {
        ShowsHistory = !ShowsHistory;
        _readingsFrame.Title = ShowsHistory ? HistoryTitle : ReadingsTitle;
        HistoryButton.Text = ShowsHistory ? "Readings" : "History";
        if (ShowsHistory)
        {
            _history = null;
            ReadHistory();
        }

        StatusChanged();
    }

    /// <summary>The readings, unless the history is shown in their place.</summary>
    private void ShowReadings(string[] lines)
    {
        if (!ShowsHistory)
        {
            _readings.Show(lines);
            return;
        }

        _readings.Show(_history ?? [Ui.Connection is null ? "No controller is configured." : "Reading the command history…"]);
    }

    private void ReadHistory()
    {
        if (Ui.Connection is null)
        {
            return;
        }

        _ = Ui.Run(null, async (client, cancellationToken) =>
        {
            string[] lines;
            try
            {
                var page = await client.Roof.GetHistoryAsync(new RoofHistoryQuery(), cancellationToken).ConfigureAwait(false);
                lines = DescribeHistory(page);
            }
            catch (Exception error) when (error is RoofApiException or RoofProtocolException or HttpRequestException or TimeoutException)
            {
                lines = [$"The command history could not be read: {RoofText.DescribeFailure(error)}"];
            }

            Ui.Post(() =>
            {
                _history = lines;
                StatusChanged();
            });
        });
    }

    /// <summary>Two lines an entry, newest first, to fit beside the roof's state; then the range and how long they are kept.</summary>
    internal static string[] DescribeHistory(RoofCommandHistoryPage page)
    {
        var lines = new List<string> { RoofHistoryText.DescribeScope(page) };
        if (page.Entries.Count == 0)
        {
            lines.Add(RoofHistoryText.Empty);
        }

        foreach (var entry in page.Entries)
        {
            lines.Add(RoofHistoryText.DescribeLine(entry));
            lines.Add($"  {RoofHistoryText.DescribeWho(entry)}; roof {RoofHistoryText.DescribeOutcome(entry)}; from {RoofHistoryText.DescribeSource(entry)}");
        }

        if (page.Entries.Count > 0)
        {
            lines.Add(RoofHistoryText.DescribeRange(page) + $" '{RoofCli.CommandName} history' reads more.");
        }

        lines.Add(RoofHistoryText.DescribeRetention(page));
        return [.. lines];
    }

    /// <summary>
    /// Sends one command at a time; a refusal's status (the controller attaches it) is shown too. <paramref name="motion"/>
    /// marks Open and Close, whose answer quitting waits for (up to a bound) before it sends Stop.
    /// </summary>
    private void Command(string busy, Func<RoofControllerClient, CancellationToken, Task<Action>> send, bool motion)
    {
        _commandInFlight = true;
        StatusChanged();
        var started = Ui.Run(busy, async (client, cancellationToken) =>
        {
            try
            {
                var done = await send(client, cancellationToken).ConfigureAwait(false);
                Ui.Post(done);
            }
            catch (RoofApiException refusal) when (refusal.RoofStatus is { } status)
            {
                Ui.Post(() => Ui.Apply(status));
                throw;
            }
            finally
            {
                Ui.Post(() =>
                {
                    _commandInFlight = false;
                    StatusChanged();
                });
            }
        }, motion);
        if (!started)
        {
            _commandInFlight = false;
            StatusChanged();
        }
    }
}
