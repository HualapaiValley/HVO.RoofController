using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>
/// The roof: its status from the hub, and Open, Close and Clear fault. A stale status is shown only as the last known
/// state, and motion is not offered on it: the roof could not be watched. Open and Close hold the operator lease while
/// the roof moves, as <c>hvo-roof open</c> does.
/// </summary>
internal sealed class RoofUiRoofPage : RoofUiPage
{
    private readonly Label _status;
    private readonly Label _blocked;
    private bool _commandInFlight;

    public RoofUiRoofPage(RoofTerminalUi ui)
        : base(ui, "Roof")
    {
        _status = TextBlock(0, Dim.Fill(2));
        _blocked = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill() };
        Add(_status, _blocked);
        OpenButton = AddButton("Open", null, () => Move(RoofMotionDirection.Opening));
        CloseButton = AddButton("Close", OpenButton, () => Move(RoofMotionDirection.Closing));
        ClearFaultButton = AddButton("Clear fault", CloseButton, ClearFault);
        AddButton("Refresh", ClearFaultButton, Refresh);
    }

    public Button OpenButton { get; }

    public Button CloseButton { get; }

    public Button ClearFaultButton { get; }

    public override void Shown() => StatusChanged();

    public override void ConnectionChanged() => StatusChanged();

    public override void StatusChanged()
    {
        var status = Ui.Status;
        if (status is null)
        {
            _status.Text = Ui.Connection is null
                ? "No controller is configured. Use Setup (F5) to add its address and a credential."
                : "No status from the controller yet.";
        }
        else
        {
            var heading = Ui.StaleSince is { } since
                ? $"LAST KNOWN STATE, as of {RoofCliFormat.Time(status.SnapshotUtc)} (no status since {RoofCliFormat.Time(since)}). It may not be the roof's state now."
                : $"Status at {RoofCliFormat.Time(status.SnapshotUtc)}";
            _status.Text = heading + "\n\n" + Rows(RoofCliFormat.DescribeStatus(status));
        }

        var open = BlockReason(RoofMotionDirection.Opening);
        var close = BlockReason(RoofMotionDirection.Closing);
        var clear = ClearFaultBlockReason();
        OpenButton.Enabled = open is null;
        CloseButton.Enabled = close is null;
        ClearFaultButton.Enabled = clear is null;
        _blocked.Text = open is not null && close is not null
            ? $"Open and Close: {(open == close ? open : $"{open}; {close}")}."
            : open is not null ? $"Open: {open}." : close is not null ? $"Close: {close}." : string.Empty;
    }

    public override string Describe() => $"{_status.Text}\n{_blocked.Text}";

    /// <summary>Why Open or Close is not offered; null when it is. The controller still checks every request.</summary>
    internal string? BlockReason(RoofMotionDirection direction)
    {
        if (Ui.Caller is not null && !IsOperator)
        {
            return "the Operator role is needed to open or close the roof";
        }

        if (Availability() is { } unavailable)
        {
            return unavailable;
        }

        var status = Ui.Status!;
        if (_commandInFlight)
        {
            return "a command is on its way";
        }

        if (status.IsClearFaultInProgress)
        {
            return "a clear-fault pulse is in progress";
        }

        if (status.IsFaultLatched)
        {
            return "a fault is latched: clear it first";
        }

        if (status.IsMoving)
        {
            return "the roof is moving: stop it first";
        }

        return direction switch
        {
            RoofMotionDirection.Opening when status.Status is RoofControllerStatus.Open or RoofControllerStatus.Opening => "the roof is already open",
            RoofMotionDirection.Closing when status.Status is RoofControllerStatus.Closed or RoofControllerStatus.Closing => "the roof is already closed",
            _ => null
        };
    }

    internal string? ClearFaultBlockReason()
    {
        if (Ui.Caller is not null && !IsOperator)
        {
            return "the Operator role is needed to clear a fault";
        }

        if (Availability() is { } unavailable)
        {
            return unavailable;
        }

        var status = Ui.Status!;
        return _commandInFlight ? "a command is on its way"
            : status.IsClearFaultInProgress ? "a clear-fault pulse is in progress"
            : status.IsMoving ? "the roof is moving: stop it first"
            : status.IsFaultLatched ? null
            : "no fault is latched";
    }

    private string? Availability()
    {
        if (Ui.Connection is null)
        {
            return "no controller is configured";
        }

        if (Ui.Status is not { } status)
        {
            return "there is no status yet";
        }

        if (Ui.IsStale)
        {
            return "the status is stale, so the roof cannot be watched";
        }

        return !status.IsInitialized ? "the controller is initializing"
            : status.IsShuttingDown ? "the controller is shutting down"
            : null;
    }

    private void Move(RoofMotionDirection direction)
    {
        if (BlockReason(direction) is { } reason)
        {
            Ui.Say($"Not sent: {reason}.", error: true);
            return;
        }

        var verb = direction == RoofMotionDirection.Opening ? "Open" : "Close";
        Command($"Sending {verb}…", async (client, cancellationToken) =>
        {
            var status = direction == RoofMotionDirection.Opening
                ? await client.Roof.OpenAsync(cancellationToken).ConfigureAwait(false)
                : await client.Roof.CloseAsync(cancellationToken).ConfigureAwait(false);
            return () =>
            {
                Ui.Apply(status);
                Ui.HoldLease(status);
                Ui.Say(status.IsMoving
                    ? $"{verb} accepted. This interface renews the operator lease while the roof moves; F9 or quitting stops it."
                    : $"{verb} accepted. Roof: {RoofCliFormat.DescribeRoof(status)}.");
            };
        });
    }

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
        });
    }

    private void Refresh() => _ = Ui.Run("Reading the status…", async (client, cancellationToken) =>
    {
        var status = await client.Roof.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        Ui.Post(() =>
        {
            Ui.Apply(status);
            Ui.Say($"Read the status at {RoofCliFormat.Time(status.SnapshotUtc)}.");
        });
    });

    /// <summary>Sends one command at a time; a refusal's status (the controller attaches it) is shown too.</summary>
    private void Command(string busy, Func<RoofControllerClient, CancellationToken, Task<Action>> send)
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
        });
        if (!started)
        {
            _commandInFlight = false;
            StatusChanged();
        }
    }
}
