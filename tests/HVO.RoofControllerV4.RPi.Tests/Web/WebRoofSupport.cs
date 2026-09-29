using System.Security.Claims;
using HVO.Core.Results;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Roof;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// One live page's <see cref="WebRoofConsole"/> against the controller's real API in process, with a mocked roof: the
/// person signs in to it, and their session's client reaches it over REST and the status hub. The controller's clock is
/// frozen, so it sends no heartbeat; the web UI's clock moves only when a test advances it (the lease timer and the
/// feed's staleness follow it).
/// </summary>
internal sealed class WebRoofHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private RoofStatusResponse _current = RoofServiceMock.Snapshot();
    private long _version = 11;

    private WebRoofHarness()
    {
        Roof = RoofServiceMock.Create();
        Roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => Current);
        Roof.Setup(service => service.RenewLease()).Returns(() => Result<RoofStatusResponse>.Success(Report(Current)));
        Roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(() =>
        {
            Report(RoofServiceMock.Snapshot(RoofControllerStatus.Stopped));
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Stopped);
        });
        Host = RoofClientApiTests.CreateHost(Roof, services => services.AddSingleton<TimeProvider>(ServerClock));
        Logs = new RecordingLoggerProvider();
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddProvider(Logs));
        Store = new WebSessionStore(WebStopKey.None, new HostConnector(this), Clock, LoggerFactory.CreateLogger<WebSessionStore>());
    }

    /// <summary>The roof the controller reports: its commands, its lease renewal and the hub's first message read it.</summary>
    public RoofStatusResponse Current => Volatile.Read(ref _current);

    public Mock<IRoofControllerServiceV4> Roof { get; }

    public RoofApiTestHost Host { get; }

    public ManualTimeProvider ServerClock { get; } = new(Start);

    /// <summary>The web UI's clock.</summary>
    public ManualTimeProvider Clock { get; } = new(Start);

    public RecordingLoggerProvider Logs { get; }

    /// <summary>
    /// When it returns true for a request, the controller handles the request but its answer never arrives: the
    /// connection ends first.
    /// </summary>
    public Func<HttpRequestMessage, bool>? CutAnswer { get; set; }

    public ILoggerFactory LoggerFactory { get; }

    public WebSessionStore Store { get; }

    /// <summary>The person's session, or null for a signed-out page.</summary>
    public WebSession? Session { get; private set; }

    public WebCircuitMonitor Circuit { get; } = new();

    public WebSessionAccessor Accessor { get; private set; } = default!;

    public WebRoofConsole Console { get; private set; } = default!;

    /// <summary>A page of a person with <paramref name="role"/> (none: a signed-out page), not started yet.</summary>
    public static async Task<WebRoofHarness> CreateAsync(string? role = RoofControllerApiContract.OperatorRole)
    {
        var harness = new WebRoofHarness();
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        if (role is not null)
        {
            var name = role == RoofControllerApiContract.ViewerRole ? "vic" : "olga";
            await RoofClientApiTests.AddUserAsync(harness.Host, name, role);
            using var anonymous = ClientTestSupport.CreateClient(harness.Host);
            harness.Session = harness.Store.Open(await anonymous.Auth.SignInAsync(name, Security.TestSecrets.Password));
            user = WebAuthentication.CreatePrincipal(harness.Session);
        }

        harness.Accessor = new WebSessionAccessor(new FixedAuthentication(user), harness.Store);
        harness.Console = new WebRoofConsole(harness.Accessor, harness.Circuit, harness.Clock, harness.LoggerFactory.CreateLogger<WebRoofConsole>());
        return harness;
    }

    /// <summary>
    /// A roof that is opening, with <paramref name="leaseSeconds"/> of operator lease left: 6 s is renewed every 2 s,
    /// within the feed's 3 s before a quiet feed is stale.
    /// </summary>
    public static RoofStatusResponse Opening(double? leaseSeconds = 6)
        => RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = leaseSeconds };

    /// <summary>Starts the console and waits for the first status from the hub.</summary>
    public async Task StartLiveAsync()
    {
        await Console.StartAsync();
        await WaitForAsync(view => view.FeedLabel == "live", "the live status");
    }

    public Task WaitForAsync(Func<WebRoofView, bool> condition, string what)
        => ClientTestSupport.WaitUntilAsync(() => condition(Console.View), what);

    /// <summary>The roof is now <paramref name="status"/>, as the controller's next snapshot (a newer version).</summary>
    public RoofStatusResponse Report(RoofStatusResponse status)
    {
        var reported = status with { StatusVersion = Interlocked.Increment(ref _version) };
        Volatile.Write(ref _current, reported);
        return reported;
    }

    /// <summary>The controller reports a new status; the hub pushes it.</summary>
    public void Push(RoofStatusResponse status)
        => Roof.Raise(service => service.StatusChanged += null, new RoofStatusChangedEventArgs(Report(status)));

    /// <summary>Open sets the roof opening, with the operator lease of <see cref="Opening"/>.</summary>
    public void OpensTheRoof(Action? whileOpening = null)
        => Roof.Setup(service => service.Open()).Returns(() =>
        {
            whileOpening?.Invoke();
            Report(Opening());
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });

    /// <summary>How many times the roof service's <paramref name="method"/> was called.</summary>
    public int Calls(string method) => Roof.Invocations.Count(invocation => invocation.Method.Name == method);

    public void Dispose()
    {
        Console?.Dispose();
        Store.Dispose();
        Host.Dispose();
        LoggerFactory.Dispose();
    }

    private sealed class HostConnector(WebRoofHarness harness)
        : RoofControllerConnector(Options.Create(new RoofWebOptions()), NullLoggerFactory.Instance, harness.Clock)
    {
        public override RoofControllerClient Create(RoofCredential? credential, TimeSpan requestTimeout)
            => ClientTestSupport.CreateClient(
                harness.Host,
                credential,
                time: harness.Clock,
                requestTimeout: requestTimeout,
                handler: () => new CuttingHandler(harness, ClientTestSupport.CreateHandler(() => harness.Host.Server)));
    }

    private sealed class CuttingHandler(WebRoofHarness harness, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (harness.CutAnswer?.Invoke(request) == true)
            {
                response.Dispose();
                throw new HttpRequestException(HttpRequestError.ResponseEnded, "The connection ended before the answer arrived.");
            }

            return response;
        }
    }

    private sealed class FixedAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }
}
