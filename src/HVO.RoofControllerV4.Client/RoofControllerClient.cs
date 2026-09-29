using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// A client of one roof controller: typed REST calls grouped by area, Stop on its own connection, and the live status
/// feed. Every client (web UI, CLI, kiosk) goes through this class, so they behave and word things the same way.
/// </summary>
/// <remarks>
/// Commands go over REST; the status hub only pushes status. Stop never waits behind another request: it has its own
/// HTTP handler and connection pool (<see cref="StopAsync"/>). Thread-safe.
/// </remarks>
public sealed class RoofControllerClient : IDisposable
{
    private readonly RoofHttp _http;
    private readonly RoofStopper _stopper;
    private RoofCredential? _credential;

    public RoofControllerClient(RoofConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        _credential = options.Credential;
        Logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RoofControllerClient>();

        _http = new RoofHttp(options, () => Credential, options.RequestTimeout);
        _stopper = new RoofStopper(options, () => Credential, Logger);
        Roof = new RoofControlApi(_http);
        Auth = new RoofAuthApi(this, _http);
        Identity = new RoofIdentityApi(_http);
        Settings = new RoofSettingsApi(_http);
        System = new RoofSystemApi(_http);
        Health = new RoofHealthApi(_http);
        Camera = new RoofCameraApi(_http);
    }

    public RoofConnectionOptions Options { get; }

    public Uri BaseAddress => _http.BaseAddress;

    /// <summary>The credential sent with every request. Setting it reconnects the status feed with the new credential.</summary>
    public RoofCredential? Credential
    {
        get => Volatile.Read(ref _credential);
        set
        {
            if (!ReferenceEquals(Interlocked.Exchange(ref _credential, value), value))
            {
                CredentialChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Raised after <see cref="Credential"/> is replaced.</summary>
    public event EventHandler? CredentialChanged;

    /// <summary>Roof commands, status and the controller's configuration.</summary>
    public RoofControlApi Roof { get; }

    /// <summary>Sign-in, PIN sign-in, the caller and password changes.</summary>
    public RoofAuthApi Auth { get; }

    /// <summary>Users, API keys and sessions (admin).</summary>
    public RoofIdentityApi Identity { get; }

    /// <summary>The settings catalogue and remote configuration.</summary>
    public RoofSettingsApi Settings { get; }

    /// <summary>Restart, system information and runtime metrics (admin).</summary>
    public RoofSystemApi System { get; }

    public RoofHealthApi Health { get; }

    public RoofCameraApi Camera { get; }

    internal ILogger Logger { get; }

    /// <summary>
    /// Sends Stop at once on its own connection, never queued behind another request, and reports what the controller
    /// answered: acknowledged, unverified (confirm at the roof) or failed. Does not throw for a refusal, a timeout or an
    /// unreachable controller; the result says what happened in the shared wording.
    /// </summary>
    public Task<RoofStopResult> StopAsync(CancellationToken cancellationToken = default) => _stopper.StopAsync(cancellationToken);

    /// <summary>
    /// Creates a feed of live status from the controller's status hub, using this client's address, handler and
    /// credential. Call <see cref="RoofStatusFeed.Start"/> to connect; dispose it when done.
    /// </summary>
    public RoofStatusFeed CreateStatusFeed() => new(this);

    public void Dispose()
    {
        _http.Dispose();
        _stopper.Dispose();
    }
}
