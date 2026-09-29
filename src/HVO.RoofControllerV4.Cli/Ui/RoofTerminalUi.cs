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
    internal const string KeyBar = "F1 Roof  F2 Settings  F3 People  F4 System  F5 Setup  F9 " + RoofStopText.ButtonLabel + "  F10 Quit";

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
    private volatile bool _closed;

    public RoofTerminalUi(RoofCliContext context, IApplication app)
    {
        _context = context;
        _app = app;

        Window = new Window { Title = "HVO roof", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

        // Stop comes first, so it is also the first control that takes focus.
        StopButton = new Button { Text = $"{RoofStopText.ButtonLabel} (F9)", X = 0, Y = 0 };
        StopButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            Stop();
        };
        _stopResult = new Label { Text = RoofStopText.AlwaysAvailable, X = Pos.Right(StopButton) + 2, Y = 0, Width = Dim.Fill() };
        _header = new Label { X = 0, Y = 1, Width = Dim.Fill() };
        _banner = new Label { X = 0, Y = 2, Width = Dim.Fill() };
        _content = new FrameView { X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(2) };
        _message = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill() };
        var keys = new Label { Text = KeyBar, X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
        Window.Add(StopButton, _stopResult, _header, _banner, _content, _message, keys);

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

    /// <summary>Requests still on their way (tests wait for none).</summary>
    public int PendingOperations => Volatile.Read(ref _pending);

    public RoofUiPage CurrentPage => _page!;

    /// <summary>The prompt on screen, if any.</summary>
    public RoofUiPanel? Panel => _panel;

    public string Message => _message.Text;

    public string StopResult => _stopResult.Text;

    /// <summary>True once F10 asked the interface to close.</summary>
    public bool QuitRequested { get; private set; }

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
    public void Stop() => Stop(then: null);

    private void Stop(Action? then)
    {
        // Stop ends this interface's hold on a motion: the lease is no longer renewed.
        _leaseTicks = -1;
        _stopResult.Text = RoofStopText.Sending;
        if (_client is not { } client)
        {
            _stopResult.Text = RoofStopText.Failed("No controller address is configured.");
            then?.Invoke();
            return;
        }

        Interlocked.Increment(ref _pending);
        _ = Task.Run(async () =>
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
                    _stopResult.Text = result.Message;
                    if (result.Status is { } status)
                    {
                        Apply(status);
                    }

                    then?.Invoke();
                });
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        });
    }

    /// <summary>Closes the interface. A motion this interface holds the lease for is stopped first, as Ctrl+C does for 'open'.</summary>
    public void Quit()
    {
        QuitRequested = true;
        if (HoldsLease && Status is { IsMoving: true })
        {
            Say("Stopping the roof, which moves on this interface's lease, before closing.");
            Stop(then: () => _app.RequestStop());
            return;
        }

        _app.RequestStop();
    }

    /// <summary>
    /// Runs <paramref name="work"/> off the interface's thread with the current client. A failure is shown on the
    /// message line in the shared wording. <paramref name="busy"/>, when given, is shown until the work says more.
    /// Returns false, having said why, when there is no controller to send to.
    /// </summary>
    public bool Run(string? busy, Func<RoofControllerClient, CancellationToken, Task> work)
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

        Interlocked.Increment(ref _pending);
        _ = Task.Run(async () =>
        {
            try
            {
                await work(client, _closing.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                Post(() => ShowError(error));
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        });
        return true;
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
        _message.SchemeName = error ? "Error" : null;
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
        if (!status.IsMoving)
        {
            _leaseTicks = -1;
        }

        _page?.StatusChanged();
    }

    /// <summary>
    /// After Open or Close was accepted here: renews the operator lease while the roof moves, as 'hvo-roof open' does.
    /// </summary>
    public void HoldLease(RoofStatusResponse status) => _leaseTicks = NextRenewal(status);

    /// <summary>Shows a prompt over the page. Esc (or Cancel) closes it; F9 still sends Stop.</summary>
    public void Ask(RoofUiPrompt prompt)
    {
        ClosePanel();
        var panel = new RoofUiPanel(prompt, closing =>
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
        Window.Title = $"HVO roof: {connection.Controller}";
        _header.Text = $"{who} · status {DescribeFeed()}";
    }

    private void UpdateFeedState()
    {
        if (_feed is not { } feed)
        {
            StaleSince = null;
            _banner.Text = Connection is null ? "No controller is configured: use Setup (F5)." : string.Empty;
            return;
        }

        var wasStale = IsStale;
        StaleSince = Status is null ? null : feed.StaleSince;
        if (Status is null)
        {
            _banner.Text = feed.State == RoofStatusFeedState.Unauthorized
                ? "No status: the controller refused the credential. Sign in on Setup (F5)."
                : "No status from the controller yet.";
        }
        else if (StaleSince is { } since)
        {
            _banner.Text = $"STALE: no status since {RoofCliFormat.Time(since)}. Showing the last known state; Stop still works.";
        }
        else
        {
            _banner.Text = string.Empty;
        }

        _banner.SchemeName = IsStale ? "Error" : null;
        UpdateHeader();
        if (wasStale != IsStale)
        {
            _page?.StatusChanged();
        }
    }

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
        var feed = _feed;
        var client = _client;
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

        _closing.Cancel();
        var feed = _feed;
        var client = _client;
        _feed = null;
        _client = null;
        if (feed is not null)
        {
            // Off the interface's thread: the feed's handlers only post to it, and posting stops once closed.
            Task.Run(async () => await feed.DisposeAsync().ConfigureAwait(false)).Wait(TimeSpan.FromSeconds(5));
        }

        client?.Dispose();
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
