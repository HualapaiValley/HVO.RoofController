using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>
/// <c>hvo-roof ui</c>: a full-screen terminal interface over the same REST API and status hub as the commands. Stop is on
/// every page (the button and F9), goes straight to REST, and is never disabled. The roof's status comes from the hub;
/// when it goes stale, the interface says so and shows the values only as the last known state.
/// </summary>
internal sealed class RoofTerminalUi : IDisposable
{
    /// <summary>The keys of the key bar, and what each does.</summary>
    internal static readonly (string Key, string Name)[] Keys =
    [
        ("F1", "Roof"), ("F2", "Settings"), ("F3", "People"), ("F4", "System"), ("F5", "Setup"), ("F9", RoofStopText.ButtonLabel), ("F10", "Quit")
    ];

    internal static readonly string KeyBar = string.Join("  ", Keys.Select(key => $"{key.Key} {key.Name}"));

    private readonly RoofCliContext _context;
    private readonly IApplication _app;
    private readonly RoofUiPage[] _pages;
    private readonly Label _header;
    private readonly Label _stopResult;
    private readonly Label _banner;
    private readonly FrameView _content;
    private readonly Label _message;
    private readonly CancellationTokenSource _closing = new();
    private RoofUiPanel? _panel;
    private RoofUiPage? _page;
    private RoofControllerClient? _client;
    private RoofStatusFeed? _feed;
    private object? _timer;
    private int _pending;

    // Ticks of the one-second timer until the lease of a motion started here is renewed: -1 when this interface holds
    // no motion, 0 while a renewal is on its way.
    private int _leaseTicks = -1;

    // True from Open or Close accepted here until a status says the roof is not moving, with or without a lease. A Stop
    // that nothing confirmed leaves it set: quitting then sends Stop again.
    private bool _startedMotion;

    // Open or Close sent from here and not yet ended. The controller may act on one before its answer arrives, so
    // quitting waits for its answer and sends Stop after it: sent sooner, Stop could reach the controller first. The
    // wait is bounded (_motionAnswerTimer): then the command is cancelled (_motionCancel) and Stop is sent. With no
    // answer (none in time, or the connection ended), the command may still reach the controller after it (_motionAnswerLost).
    private int _motionsInFlight;
    private CancellationTokenSource? _motionCancel;
    private object? _motionAnswerTimer;
    private bool _motionAnswerLost;

    // Set when quitting stayed open because a command's answer was lost: F10 then closes, sending Stop only if the
    // roof moves.
    private bool _answerLostShown;

    // Every Stop sent from here that may still be on its way; the client is not disposed before they are answered.
    private Task _stops = Task.CompletedTask;
    private int _stopsInFlight;

    // Numbers the Stops sent from here: only the answer to the newest decides the result shown and UnconfirmedStop.
    private int _stopsSent;
    private bool _quitAfterStop;
    private bool _quitInterrupted;

    // Set when quitting stayed open on a Stop that nothing confirmed: F10 then closes without sending Stop again.
    private bool _closeWithoutStop;

    // When the status shown was taken; with no live status, a status read over REST is stale from then.
    private DateTimeOffset? _statusTakenAt;
    private volatile bool _closed;

    public RoofTerminalUi(RoofCliContext context, IApplication app)
    {
        _context = context;
        _app = app;
        Theme = RoofUiTheme.For(context.Host.GetEnvironmentVariable);

        Window = new Window { Title = WindowTitle(null), X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        Window.SetScheme(Theme.Base);
        Theme.SetFrame(Window, Theme.WindowFrame);

        // Stop comes first, so it is also the first control that takes focus.
        StopButton = Theme.Styled(new Button { Text = $"{RoofStopText.ButtonLabel} (F9)", X = 0, Y = 0 }, Theme.Stop);
        StopButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            Stop();
        };
        // The Stop result, the banner and the message wrap, so a narrow terminal still shows all of each: the page gets
        // the rows they leave.
        _stopResult = Wrapping(new Label { Text = RoofStopText.AlwaysAvailable, X = Pos.Right(StopButton) + 2, Y = 0 });
        _header = new Label { X = 0, Y = Pos.Bottom(_stopResult), Width = Dim.Fill() };
        _header.SetScheme(Theme.Header);
        _banner = Wrapping(new Label { X = 0, Y = Pos.Bottom(_header) });
        _message = Wrapping(new Label { X = 0, Y = Pos.AnchorEnd() - 1 });
        _content = new FrameView { X = 0, Y = Pos.Bottom(_banner), Width = Dim.Fill(), Height = Dim.Fill(_message) };
        Theme.SetFrame(_content);
        Window.Add(StopButton, _stopResult, _header, _banner, _content, _message, CreateKeyBar());

        _pages =
        [
            new RoofUiRoofPage(this),
            new RoofUiSettingsPage(this),
            new RoofUiPeoplePage(this),
            new RoofUiSystemPage(this),
            new RoofUiSetupPage(this)
        ];

        _app.Keyboard.KeyDown += OnKeyDown;
    }

    public Window Window { get; }

    /// <summary>HVO Dark, or with <c>NO_COLOR</c> set, the same interface without colour.</summary>
    public RoofUiTheme Theme { get; }

    // A line of text as wide as the window, which takes as many rows as its text needs (at least one).
    private static Label Wrapping(Label label)
    {
        label.Width = Dim.Fill();
        label.Height = Dim.Auto(DimAutoStyle.Text, minimumContentDim: 1);
        label.TextFormatter.WordWrap = true;
        return label;
    }

    /// <summary>The key bar: each key in the accent colour, and what it does, on the navigation bar's grey.</summary>
    private View CreateKeyBar()
    {
        var bar = new View { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        bar.SetScheme(Theme.KeyName);
        View? previous = null;
        foreach (var (key, name) in Keys)
        {
            var keyLabel = new Label { Text = key, X = previous is null ? 0 : Pos.Right(previous) + 2, Y = 0 };
            keyLabel.SetScheme(Theme.Key);
            var nameLabel = new Label { Text = name, X = Pos.Right(keyLabel) + 1, Y = 0 };
            bar.Add(keyLabel, nameLabel);
            previous = nameLabel;
        }

        return bar;
    }

    public Button StopButton { get; }

    public RoofCliContext Context => _context;

    /// <summary>The connection in use; null when there is no controller address (the Setup page says how to add one).</summary>
    public RoofCliConnection? Connection { get; private set; }

    /// <summary>Why there is no connection, when there is none.</summary>
    public string? ConnectionProblem { get; private set; }

    /// <summary>Who the controller says the credential is; null until it has answered.</summary>
    public RoofCallerResponse? Caller { get; private set; }

    /// <summary>The newest status: from the hub, or from the answer to a command when that is newer.</summary>
    public RoofStatusResponse? Status { get; private set; }

    /// <summary>When the status stopped being current; null while the hub is delivering it.</summary>
    public DateTimeOffset? StaleSince { get; private set; }

    public bool IsStale => StaleSince is not null || Status is null;

    /// <summary>True while this interface renews the lease of a motion it started.</summary>
    public bool HoldsLease => _leaseTicks >= 0;

    /// <summary>
    /// True while a motion that may have been started here is under way: quitting sends Stop first. That includes the
    /// roof moving after an Open or Close whose answer was lost, which may have reached the controller late.
    /// </summary>
    public bool FollowsMotion => (_startedMotion || HoldsLease || _motionAnswerLost) && Status is { IsMoving: true };

    /// <summary>True while Open or Close sent from here has not ended: quitting waits for it, then sends Stop.</summary>
    public bool MotionInFlight => _motionsInFlight > 0;

    /// <summary>
    /// True when quitting stopped waiting for the answer to an Open or Close sent from here: the command may still reach
    /// the controller after the Stop sent then.
    /// </summary>
    public bool MotionAnswerLost => _motionAnswerLost;

    /// <summary>How many Stops were sent from here: a command compares it to see whether Stop was sent meanwhile.</summary>
    public int StopsSent => _stopsSent;

    /// <summary>True while a Stop sent from here has not been answered.</summary>
    public bool StopInFlight => _stopsInFlight > 0;

    /// <summary>Requests still on their way (tests wait for none).</summary>
    public int PendingOperations => Volatile.Read(ref _pending);

    public RoofUiPage CurrentPage => _page!;

    /// <summary>The prompt on screen, if any.</summary>
    public RoofUiPanel? Panel => _panel;

    public string Message => _message.Text;

    public string StopResult => _stopResult.Text;

    /// <summary>True once F10 asked the interface to close.</summary>
    public bool QuitRequested { get; private set; }

    /// <summary>
    /// The result of the last Stop sent from here when nothing confirmed it (it failed, or the relays could not be
    /// verified); null when none was sent, or the last one was acknowledged.
    /// </summary>
    public RoofStopResult? UnconfirmedStop { get; private set; }

    /// <summary>Connects and shows the first page: the roof, or Setup when there is no controller address.</summary>
    public void Start()
    {
        Connect();
        ShowPage(Connection is null ? _pages.Length - 1 : 0);
        _timer = _app.AddTimeout(TimeSpan.FromSeconds(1), OnTick);
    }

    /// <summary>(Re)reads the connection from the environment and the credentials file, and restarts the status feed.</summary>
    public void Connect()
    {
        Disconnect();
        Status = null;
        StaleSince = null;
        Caller = null;
        ConnectionProblem = null;
        try
        {
            Connection = _context.ResolveConnection();
        }
        catch (Exception error) when (error is RoofCliNotConfiguredException or RoofCredentialFileException)
        {
            Connection = null;
            ConnectionProblem = RoofCliContext.Classify(error).Message;
        }

        if (Connection is { } connection)
        {
            var client = _context.CreateClient(connection);
            var feed = client.CreateStatusFeed();
            feed.StatusReceived += (_, e) => Post(() =>
            {
                if (ReferenceEquals(feed, _feed))
                {
                    OnStatusReceived(e);
                }
            });
            feed.StateChanged += (_, _) => Post(() =>
            {
                if (ReferenceEquals(feed, _feed))
                {
                    UpdateFeedState();
                }
            });
            _client = client;
            _feed = feed;
            feed.Start();
            _ = Run(null, async (current, cancellationToken) =>
            {
                var caller = await current.Auth.GetCallerAsync(cancellationToken).ConfigureAwait(false);
                Post(() =>
                {
                    if (ReferenceEquals(current, _client))
                    {
                        Caller = caller;
                        UpdateHeader();
                        _page?.ConnectionChanged();
                    }
                });
            });
        }

        UpdateHeader();
        UpdateFeedState();
        _page?.ConnectionChanged();
    }

    public void ShowPage(int index)
    {
        ClosePanel();
        var page = _pages[index];
        if (_page is not null)
        {
            _content.Remove(_page);
        }

        _page = page;
        _content.Title = $"F{index + 1} {page.Title}";
        _content.Add(page);
        page.Shown();
        page.SetFocus();
    }

    /// <summary>
    /// Sends Stop at once, on its own request: it never waits for another command, and nothing disables it.
    /// </summary>
    public void Stop()
    {
        // Stop ends this interface's hold on a motion: the lease is no longer renewed. The motion is still followed until
        // a status says the roof stopped, so quitting after a Stop that nothing confirmed sends Stop again.
        _leaseTicks = -1;
        var sequence = ++_stopsSent;
        ShowStopResult(RoofStopOutcome.Sent, RoofStopText.Sending);
        if (_client is not { } client)
        {
            var message = RoofStopText.Failed("No controller address is configured.");
            ShowStopResult(RoofStopOutcome.Failed, message);
            UnconfirmedStop = new RoofStopResult(RoofStopOutcome.Failed, message, null, null);
            QuitIfAsked();
            return;
        }

        // Until it is answered, a termination signal does not end the process (RoofCliTermination), closing the interface
        // waits for it, and the client it uses is not disposed.
        var hold = _context.Host.Termination?.Hold();
        _stopsInFlight++;
        Interlocked.Increment(ref _pending);
        var stop = Task.Run(async () =>
        {
            RoofStopResult result;
            try
            {
                result = await client.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                result = new RoofStopResult(RoofStopOutcome.Failed, RoofStopText.Failed(RoofText.DescribeFailure(error)), null, error);
            }

            try
            {
                Post(() =>
                {
                    _stopsInFlight--;

                    // An older Stop's answer says nothing about a newer one, which may have failed or been confirmed.
                    if (sequence == _stopsSent)
                    {
                        ShowStopResult(result.Outcome, result.Message);
                        UnconfirmedStop = result.IsAcknowledged ? null : result;
                    }

                    if (result.Status is { } status)
                    {
                        Apply(status);
                    }

                    QuitIfAsked();
                });
            }
            finally
            {
                hold?.Dispose();
                Interlocked.Decrement(ref _pending);
            }
        });
        _stops = Task.WhenAll(_stops, stop);
    }

    /// <summary>
    /// Closes the interface. A motion started here is stopped first, as Ctrl+C does for 'open': for an Open or Close
    /// still on its way, Stop is sent once it is answered, since the controller may have acted on it, or after
    /// <see cref="MotionAnswerWait"/> without an answer. A Stop still on its way is answered before the interface closes.
    /// When quitting waited for a Stop that nothing then confirmed, or for an answer that did not come, F10 leaves the
    /// interface open with that on screen, and the next F10 closes it, sending Stop again first while the roof moves.
    /// After a Stop that nothing confirmed it sends none, unless the Open or Close's answer was also lost: that command
    /// may have set the roof moving after the Stop. A termination signal (<paramref name="interrupted"/>) sends Stop
    /// again while the roof moves and closes it anyway, and 'hvo-roof ui' says so on the restored terminal.
    /// </summary>
    public void Quit(bool interrupted = false)
    {
        QuitRequested = true;
        _quitInterrupted |= interrupted;
        if (MotionInFlight)
        {
            Say(WaitingForMotionAnswer);
            _quitAfterStop = true;
            _motionAnswerTimer ??= _app.AddTimeout(MotionAnswerWait, MotionAnswerTimedOut);
            return;
        }

        // After a Stop that nothing confirmed, F10 closes without another; a signal sends one while the roof moves, and
        // so does F10 when an Open or Close's answer was lost, since that command may have set the roof moving after it.
        if (FollowsMotion && (!_closeWithoutStop || interrupted || _motionAnswerLost))
        {
            Say("Stopping the roof, which moves on a command from this interface, before closing.");
            _quitAfterStop = true;
            Stop();
            return;
        }

        if (StopInFlight)
        {
            Say("Waiting for Stop to be answered before closing.");
            _quitAfterStop = true;
            return;
        }

        _app.RequestStop();
    }

    private const string StoppingMotionInFlight = "Stopping the roof, which may move on a command from this interface, before closing.";

    internal static readonly string WaitingForMotionAnswer =
        $"Waiting up to {MotionAnswerWait.TotalSeconds:0} s for the answer to the Open or Close on its way, then stopping the roof before closing.";

    /// <summary>How long quitting waits for the answer to an Open or Close on its way before it sends Stop anyway.</summary>
    private static TimeSpan MotionAnswerWait => RoofCli.CommandBuilder.MotionAnswerWait;

    internal static readonly string MotionAnswerLostText =
        "The Open or Close sent from here was not answered, so it may still reach the controller after the Stop. "
        + "The interface stays open to show the roof: F9 stops it, F10 closes the interface.";

    internal const string UnconfirmedStopText = "Nothing confirmed the Stop, so the interface stays open. F10 closes it.";

    internal const string UnconfirmedStopAndAnswerLostText =
        "Nothing confirmed the Stop, and the Open or Close sent from here was not answered, so it may still reach the "
        + "controller after the Stop. The interface stays open to show the roof: F9 stops it, F10 closes the interface, "
        + "sending Stop again if the roof moves.";

    /// <summary>Closes the interface when F10 (or a termination signal) waited for the Stops sent from here.</summary>
    private void QuitIfAsked()
    {
        if (!_quitAfterStop || _stopsInFlight > 0 || _motionsInFlight > 0)
        {
            return;
        }

        _quitAfterStop = false;
        if (UnconfirmedStop is not null && !_quitInterrupted && !_closeWithoutStop)
        {
            // Quitting said it stops the roof first; it does not close on a Stop that nothing confirmed. A lost answer is
            // said with it: that command may still set the roof moving after this Stop.
            QuitRequested = false;
            _closeWithoutStop = true;
            _answerLostShown |= _motionAnswerLost;
            Say(_motionAnswerLost ? UnconfirmedStopAndAnswerLostText : UnconfirmedStopText, error: true);
            return;
        }

        if (_motionAnswerLost && !_quitInterrupted && !_answerLostShown)
        {
            // Nor on a command that may still set the roof moving after the Stop: the status shows whether it did.
            QuitRequested = false;
            _answerLostShown = true;
            Say(MotionAnswerLostText, error: true);
            return;
        }

        _app.RequestStop();
    }

    /// <summary>How long closing waits for a Stop still on its way: the Stop timeout and a margin.</summary>
    private TimeSpan StopWait => (_client?.Options.StopTimeout ?? DefaultStopTimeout) + TimeSpan.FromSeconds(2);

    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long closing waits for the live status to close.</summary>
    internal static readonly TimeSpan FeedCloseWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Runs <paramref name="work"/> off the interface's thread with the current client. A failure is shown on the
    /// message line in the shared wording. <paramref name="busy"/>, when given, is shown until the work says more.
    /// Returns false, having said why, when there is no controller to send to. <paramref name="motion"/> marks Open or
    /// Close: quitting while it is on its way waits for it (or cancels it after <see cref="MotionAnswerWait"/>) and then
    /// sends Stop.
    /// </summary>
    public bool Run(string? busy, Func<RoofControllerClient, CancellationToken, Task> work, bool motion = false)
    {
        if (_client is not { } client)
        {
            Say(ConnectionProblem ?? "No controller address is configured. Use Setup (F5).", error: true);
            return false;
        }

        if (busy is not null)
        {
            Say(busy);
        }

        var cancellationToken = _closing.Token;
        if (motion)
        {
            _motionsInFlight++;
            _motionCancel ??= CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            cancellationToken = _motionCancel.Token;

            // A new motion is followed again: quitting sends Stop for it, whatever an earlier Stop's result, and says
            // again if its answer is lost.
            _closeWithoutStop = false;
            _motionAnswerLost = false;
            _answerLostShown = false;
        }

        Interlocked.Increment(ref _pending);
        _ = Task.Run(async () =>
        {
            try
            {
                await work(client, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                Post(() => ShowError(error));
            }
            finally
            {
                if (motion)
                {
                    Post(MotionEnded);
                }

                Interlocked.Decrement(ref _pending);
            }
        });
        return true;
    }

    /// <summary>
    /// After Open or Close ended, answered or not: when quitting waited for it, Stop is sent now. It was not sent at once,
    /// since it could then reach the controller before the command it is to stop.
    /// </summary>
    private void MotionEnded()
    {
        if (--_motionsInFlight > 0)
        {
            return;
        }

        if (_motionAnswerTimer is not null)
        {
            _app.RemoveTimeout(_motionAnswerTimer);
            _motionAnswerTimer = null;
        }

        _motionCancel?.Dispose();
        _motionCancel = null;
        if (_quitAfterStop)
        {
            Say(StoppingMotionInFlight);
            Stop();
        }
    }

    /// <summary>Runs <paramref name="action"/> on the interface's thread.</summary>
    public void Post(Action action)
    {
        if (_closed)
        {
            return;
        }

        try
        {
            _app.Invoke(() =>
            {
                if (!_closed)
                {
                    action();
                }
            });
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
        {
            // The interface closed while the answer was on its way.
        }
    }

    public void Say(string text, bool error = false)
    {
        _message.Text = text;
        _message.SetScheme(error ? Theme.Danger : Theme.Base);
    }

    private void ShowStopResult(RoofStopOutcome outcome, string text)
    {
        _stopResult.Text = text;
        _stopResult.SetScheme(Theme.ForStop(outcome));
    }

    public void ShowError(Exception error)
    {
        var (_, message) = RoofCliContext.Classify(error);
        var detail = error is RoofApiException { Detail: { Length: > 0 } text } && text != message ? $" {text}" : string.Empty;
        Say(message + detail, error: true);
    }

    /// <summary>Takes <paramref name="status"/> when it is newer than the one shown, and tells the page.</summary>
    public void Apply(RoofStatusResponse status)
    {
        if (!RoofStatusRules.ShouldApply(Status, status))
        {
            return;
        }

        Status = status;
        _statusTakenAt = _context.Host.Time.GetUtcNow();
        if (!status.IsMoving)
        {
            _leaseTicks = -1;
            _startedMotion = false;
        }

        // A status read over REST while the hub is not delivering is stale at once.
        UpdateFeedState();
        _page?.StatusChanged();
    }

    /// <summary>
    /// After Open or Close was accepted here: follows the motion, so quitting sends Stop first, and renews the operator
    /// lease while the roof moves, as 'hvo-roof open' does.
    /// </summary>
    public void HoldLease(RoofStatusResponse status)
    {
        _startedMotion |= status.IsMoving;
        _leaseTicks = NextRenewal(status);
    }

    /// <summary>
    /// After Open or Close got no answer: the controller may have set the roof moving, so quitting sends Stop while a
    /// status shows it moving, as after an accepted one. When quitting waited for that answer, the Stop it sends next may
    /// still be overtaken by the command, as after no answer within the wait.
    /// </summary>
    public void FollowUnansweredMotion()
    {
        _startedMotion = true;
        _motionAnswerLost |= _quitAfterStop;
    }

    /// <summary>Shows a prompt over the page. Esc (or Cancel) closes it; F9 still sends Stop.</summary>
    public void Ask(RoofUiPrompt prompt)
    {
        ClosePanel();
        var panel = new RoofUiPanel(prompt, Theme, closing =>
        {
            // An action that opened another prompt has already replaced this one.
            if (ReferenceEquals(_panel, closing))
            {
                ClosePanel();
            }
        });
        _panel = panel;
        _content.Enabled = false;
        Window.Add(panel);
        panel.FocusFirst();
    }

    public void ClosePanel()
    {
        if (_panel is not { } panel)
        {
            return;
        }

        _panel = null;
        Window.Remove(panel);

        // Disposed on the next loop iteration: this may run inside one of the panel's own button handlers.
        _app.AddTimeout(TimeSpan.Zero, () =>
        {
            panel.Dispose();
            return false;
        });
        _content.Enabled = true;
        _page?.SetFocus();
    }

    /// <summary>Reads the connection again after Setup saved or signed in, and tells every page.</summary>
    public void Reconnect()
    {
        Connect();
        foreach (var page in _pages)
        {
            if (!ReferenceEquals(page, _page))
            {
                page.ConnectionChanged();
            }
        }
    }

    private void OnKeyDown(object? sender, Key key)
    {
        if (key == Key.F9)
        {
            key.Handled = true;
            Stop();
            return;
        }

        if (key == Key.Esc)
        {
            // Esc closes a prompt; it never closes the interface.
            key.Handled = true;
            ClosePanel();
            return;
        }

        if (key == Key.F10)
        {
            key.Handled = true;
            Quit();
            return;
        }

        var page = key == Key.F1 ? 0 : key == Key.F2 ? 1 : key == Key.F3 ? 2 : key == Key.F4 ? 3 : key == Key.F5 ? 4 : -1;
        if (page >= 0)
        {
            key.Handled = true;
            ShowPage(page);
        }
    }

    private void OnStatusReceived(RoofStatusReceivedEventArgs e)
    {
        if (e.IsNewInstance && e.Previous is not null)
        {
            Say("The controller restarted.");
        }

        if (e.SafetyAlert is { } alert)
        {
            Say($"SAFETY: {alert.Title}. {alert.Message}", error: true);
        }

        Apply(e.Status);
        UpdateFeedState();
    }

    private bool OnTick()
    {
        if (_leaseTicks > 0 && --_leaseTicks == 0)
        {
            RenewLease();
        }

        UpdateFeedState();
        return true;
    }

    /// <summary>
    /// Quitting waited <see cref="MotionAnswerWait"/> for an answer that did not come: the command is cancelled, and Stop
    /// is sent once it has ended. The controller may still act on the command after that Stop, so a motion is followed.
    /// </summary>
    private bool MotionAnswerTimedOut()
    {
        _motionAnswerTimer = null;
        _motionAnswerLost = true;
        _motionCancel?.Cancel();
        return false;
    }

    private void RenewLease()
    {
        _ = Run(null, async (client, cancellationToken) =>
        {
            try
            {
                var status = await client.Roof.RenewLeaseAsync(cancellationToken).ConfigureAwait(false);
                Post(() =>
                {
                    Apply(status);
                    if (_leaseTicks == 0)
                    {
                        _leaseTicks = NextRenewal(status);
                    }
                });
            }
            catch (RoofApiException refusal) when (refusal.Code == RoofControllerErrorCode.LeaseNotActive)
            {
                Post(() =>
                {
                    _leaseTicks = -1;
                    if (refusal.RoofStatus is { } status)
                    {
                        Apply(status);
                    }
                });
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Post(() =>
                {
                    Say($"The lease could not be renewed: {RoofCliContext.Classify(error).Message} If the controller is running, it stops the roof when the lease runs out.", error: true);
                    if (_leaseTicks == 0)
                    {
                        // Tried again each second while the roof moves; Stop or the end of the motion ends it.
                        _leaseTicks = 1;
                    }
                });
            }
        });
    }

    private int NextRenewal(RoofStatusResponse status)
        => status.IsMoving && RoofStatusRules.GetLeaseRenewalDelay(status.LeaseSecondsRemaining) is { } delay
            ? Math.Max(1, (int)Math.Floor(delay.TotalSeconds))
            : -1;

    // Spaced from the frame: Terminal.Gui makes a link of an address up to the next space, so the frame line after an
    // address with no space would be part of its link.
    internal static string WindowTitle(Uri? controller) => controller is null ? " HVO roof " : $" HVO roof: {controller} ";

    private void UpdateHeader()
    {
        if (Connection is not { } connection)
        {
            _header.Text = ConnectionProblem ?? "Not configured.";
            return;
        }

        var who = Caller is { } caller
            ? $"{caller.Name} ({RoofCliFormat.Role(caller.Role)}{(caller.Kind == RoofCredentialKind.ApiKey ? ", API key" : string.Empty)})"
            : connection.Credential is null ? "no credential: sign in on Setup (F5)" : "checking the credential…";
        Window.Title = WindowTitle(connection.Controller);
        _header.Text = $"{who} · status {DescribeFeed()}";
    }

    private void UpdateFeedState()
    {
        if (_feed is not { } feed)
        {
            StaleSince = null;
            _banner.Text = Connection is null ? "No controller is configured: use Setup (F5)." : string.Empty;
            _banner.SetScheme(Connection is null ? Theme.Warning : Theme.Base);
            return;
        }

        var wasStale = IsStale;
        if (Status is null)
        {
            StaleSince = null;
            _banner.Text = feed.State == RoofStatusFeedState.Unauthorized
                ? "No status: the controller refused the credential. Sign in on Setup (F5)."
                : "No status from the controller yet.";
        }
        else if (feed.StaleSince is { } since)
        {
            StaleSince = since;
            _banner.Text = $"STALE: no status since {RoofCliFormat.Time(since)}. Showing the last known state; Stop still works.";
        }
        else if (!IsLive(feed))
        {
            // The hub has not delivered a status: the one shown is a single read over REST (Refresh, or the answer to a
            // command), and nothing says it is still current.
            var read = _statusTakenAt ?? _context.Host.Time.GetUtcNow();
            StaleSince = read;
            _banner.Text = $"STALE: status from a single read at {RoofCliFormat.Time(read)}; live status is not connected. Stop still works.";
        }
        else
        {
            StaleSince = null;
            _banner.Text = string.Empty;
        }

        _banner.SetScheme(_banner.Text.Length == 0 ? Theme.Base
            : feed.State == RoofStatusFeedState.Unauthorized ? Theme.Danger
            : Theme.Warning);
        UpdateHeader();
        if (wasStale != IsStale)
        {
            _page?.StatusChanged();
        }
    }

    /// <summary>True while the hub delivers the status: connected, with a status that is not stale.</summary>
    private static bool IsLive(RoofStatusFeed feed)
        => feed.State == RoofStatusFeedState.Connected && feed.Status is not null && !feed.IsStale;

    private string DescribeFeed() => _feed?.State switch
    {
        null => "not connected",
        RoofStatusFeedState.Unauthorized => "refused",
        _ when _feed.IsStale || Status is null => "STALE",
        RoofStatusFeedState.Connected => "live",
        RoofStatusFeedState.Connecting => "connecting",
        RoofStatusFeedState.Reconnecting => "reconnecting",
        _ => "stopped"
    };

    private void Disconnect()
    {
        _leaseTicks = -1;
        _startedMotion = false;
        _motionAnswerLost = false;
        var feed = _feed;
        var client = _client;
        var stops = _stops;
        var wait = StopWait;
        _feed = null;
        _client = null;
        if (feed is not null || client is not null)
        {
            _ = Task.Run(async () =>
            {
                if (feed is not null)
                {
                    await feed.DisposeAsync().ConfigureAwait(false);
                }

                // Disposing the client would cancel a Stop still on its way.
                await Task.WhenAny(stops, Task.Delay(wait)).ConfigureAwait(false);
                client?.Dispose();
            });
        }
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _app.Keyboard.KeyDown -= OnKeyDown;
        if (_timer is not null)
        {
            _app.RemoveTimeout(_timer);
        }

        if (_motionAnswerTimer is not null)
        {
            _app.RemoveTimeout(_motionAnswerTimer);
        }

        _closing.Cancel();

        // A Stop still on its way is answered first: disposing the client would cancel it.
        Task.WaitAny([_stops], StopWait);
        var feed = _feed;
        var client = _client;
        _feed = null;
        _client = null;
        if (feed is not null)
        {
            // Off the interface's thread: the feed's handlers only post to it, and posting stops once closed. Not waited
            // for long: after a signal, the process has StopGrace for the answer wait, the Stop, this, and the restored
            // terminal's messages, and a feed that does not close in time is dropped with the process.
            Task.Run(async () => await feed.DisposeAsync().ConfigureAwait(false)).Wait(FeedCloseWait);
        }

        client?.Dispose();
        _motionCancel?.Dispose();
        _closing.Dispose();

        // The window disposes the page and prompt it shows; the other pages are not in it.
        foreach (var page in _pages.Where(page => !ReferenceEquals(page, _page)))
        {
            page.Dispose();
        }

        Window.Dispose();
    }
}

/// <summary>One field of a prompt.</summary>
internal sealed record RoofUiField(string Label, bool Secret = false, string Initial = "", bool ReadOnly = false);

/// <summary>One action of a prompt: returns an error to show (the prompt stays open), or null to close it.</summary>
internal sealed record RoofUiAction(string Label, Func<IReadOnlyList<string>, string?> Run);

/// <summary>A prompt: a message, fields to fill in, and actions. Closing it (Cancel, or Esc) is always offered.</summary>
internal sealed record RoofUiPrompt(
    string Title,
    string Message,
    IReadOnlyList<RoofUiField> Fields,
    IReadOnlyList<RoofUiAction> Actions,
    string CloseLabel = "Cancel");
