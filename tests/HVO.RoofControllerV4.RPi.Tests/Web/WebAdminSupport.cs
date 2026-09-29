using System.Security.Claims;
using Bunit;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Sessions;
using HVO.RoofControllerV4.Web.Supervision;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The Settings, People and System pages against the controller's real API in process, with the roof double of the
/// settings tests. The settings file and the managed secrets are files in a temporary directory, so a hand edit is a
/// write to the file. People sign in with a password and the pages reach the controller with their session. Both clocks
/// are frozen at <see cref="Start"/>; the web UI's moves only when a test advances it.
/// </summary>
internal sealed class WebAdminHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly List<IDisposable> _owned = [];

    public WebAdminHarness(IDictionary<string, string?>? settings = null)
    {
        Directory = new WebTestSupport.TempDirectory();
        Roof = new SettingsApiTests.RoofDouble();
        Host = new RoofApiTestHost(
            Roof.Mock,
            settings: settings,
            configureServices: services =>
            {
                // A cheap hash so the tests run quickly; production uses the ASP.NET Core default.
                services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000);
                services.AddSingleton<TimeProvider>(ServerClock);
            },
            settingsFilePath: SettingsPath,
            secretsFilePath: Path.Combine(Directory.Path, "secrets", "managed-secrets.json"));
        Roof.StartFrom(Host);
        Logs = new RecordingLoggerProvider();
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddProvider(Logs));
        Store = new WebSessionStore(WebStopKey.None, new HostConnector(this), Clock, LoggerFactory.CreateLogger<WebSessionStore>());
    }

    public WebTestSupport.TempDirectory Directory { get; }

    public string SettingsPath => Path.Combine(Directory.Path, "config", "appsettings.Local.json");

    /// <summary>The supervisor's control directory, where the web UI asks for a forced restart.</summary>
    public string ControlPath => Path.Combine(Directory.Path, "control");

    public string SupervisorStatePath => Path.Combine(Directory.Path, "supervisor-state.json");

    public SettingsApiTests.RoofDouble Roof { get; }

    public RoofApiTestHost Host { get; }

    public ManualTimeProvider ServerClock { get; } = new(Start);

    /// <summary>The web UI's clock.</summary>
    public ManualTimeProvider Clock { get; } = new(Start);

    public RecordingLoggerProvider Logs { get; }

    public ILoggerFactory LoggerFactory { get; }

    public WebSessionStore Store { get; }

    /// <summary>Adds a person with <paramref name="role"/> and the test password, and signs them in to the web UI.</summary>
    public async Task<WebSession> SignInAsync(string name, string role)
    {
        await RoofClientApiTests.AddUserAsync(Host, name, role);
        using var anonymous = ClientTestSupport.CreateClient(Host);
        return Store.Open(await anonymous.Auth.SignInAsync(name, Security.TestSecrets.Password));
    }

    /// <summary>An admin client with the test API key, to set things up and check them behind the pages.</summary>
    public RoofControllerClient Admin()
        => Own(ClientTestSupport.CreateClient(Host, new RoofApiKeyCredential(TestApiKeys.Admin)));

    /// <summary>Edits the settings file by hand.</summary>
    public void EditSettingsFile(string json)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, json);
    }

    /// <summary>
    /// A page context for <paramref name="session"/> (null: signed out), with the web UI's services the admin pages use.
    /// The supervisor is configured when <paramref name="supervised"/> is true: its state file and its control directory.
    /// </summary>
    public BunitContext Context(WebSession? session, bool supervised = false)
    {
        var options = new RoofWebOptions();
        if (supervised)
        {
            System.IO.Directory.CreateDirectory(ControlPath);
            options.SupervisorControlPath = ControlPath;
            options.SupervisorStatePath = SupervisorStatePath;
        }

        var monitor = WebTestSupport.Monitor(options);
        var context = new BunitContext();
        var authorization = context.AddAuthorization();
        if (session is not null)
        {
            authorization.SetAuthorized(session.Name);
            authorization.SetRoles([.. WebRoles.Implied(session.Role)]);
            authorization.SetClaims(new Claim(WebAuthentication.SessionIdClaimType, session.Id));
        }

        context.Services.AddSingleton<TimeProvider>(Clock);
        context.Services.AddSingleton(monitor);
        context.Services.AddSingleton(new RoofWebStatusProbe(Own(ClientTestSupport.CreateClient(Host)), new SupervisorStateReader(monitor), Clock));
        context.Services.AddSingleton(new ControllerForcedRestart(monitor));
        context.Services.AddSingleton(Store);
        context.Services.AddScoped<WebSessionAccessor>();
        context.Services.AddSingleton(LoggerFactory);
        context.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        return context;
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        Store.Dispose();
        Host.Dispose();
        LoggerFactory.Dispose();
        Directory.Dispose();
    }

    private T Own<T>(T disposable)
        where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private sealed class HostConnector(WebAdminHarness harness)
        : RoofControllerConnector(Options.Create(new RoofWebOptions()), NullLoggerFactory.Instance, harness.Clock)
    {
        public override RoofControllerClient Create(RoofCredential? credential, TimeSpan requestTimeout)
            => ClientTestSupport.CreateClient(harness.Host, credential, time: harness.Clock, requestTimeout: requestTimeout);
    }
}
