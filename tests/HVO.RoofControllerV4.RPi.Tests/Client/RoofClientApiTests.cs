using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Controllers.Camera;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Settings;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Moq;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The client library's REST calls against the controller's real API, in process with a mocked roof (#44): what each
/// endpoint returns, and how an error answer becomes a <see cref="RoofApiException"/> with the controller's code, its
/// status snapshot and the shared wording.
/// </summary>
[TestClass]
public sealed class RoofClientApiTests
{
    internal const string KioskKey = "test-kiosk-key-not-a-real-secret-05";

    internal static RoofApiTestHost CreateHost(
        Mock<IRoofControllerServiceV4>? roof = null,
        Action<IServiceCollection>? configureServices = null,
        IDictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:ApiKeys:4:Name"] = "test-kiosk",
            ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.ViewerRole,
            ["RoofControllerSecurity:ApiKeys:4:Key"] = KioskKey,
            ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "true"
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }

        return new RoofApiTestHost(
            roof,
            all,
            configureServices: services =>
            {
                // A cheap hash so the tests run quickly; production uses the ASP.NET Core default.
                services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000);
                configureServices?.Invoke(services);
            });
    }

    internal static async Task AddUserAsync(RoofApiTestHost host, string name, string role, string? pin = null)
    {
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        await admin.Identity.AddUserAsync(new RoofUserCreateRequest { Name = name, Role = role, Password = TestSecrets.Password, Pin = pin });
    }

    internal static async Task<RoofApiException> RefusedAsync(Func<Task> call)
        => (await call.Should().ThrowAsync<RoofApiException>()).Which;

    // ---- Roof commands ----------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task RoofCommands_ReachTheRoof_AndReturnItsStatus()
    {
        using var host = CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator));

        (await client.Roof.GetStatusAsync()).Status.Should().Be(RoofControllerStatus.Closed);
        (await client.Roof.OpenAsync()).StatusVersion.Should().Be(11);
        (await client.Roof.CloseAsync()).IsInitialized.Should().BeTrue();
        (await client.Roof.RenewLeaseAsync()).RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        await client.Roof.ClearFaultAsync();
        await client.Roof.ClearFaultAsync(500);

        host.RoofService.Verify(service => service.Open(), Times.Once());
        host.RoofService.Verify(service => service.Close(), Times.Once());
        host.RoofService.Verify(service => service.RenewLease(), Times.Once());
        host.RoofService.Verify(service => service.ClearFault(RoofControllerLimits.DefaultClearFaultPulseMilliseconds, It.IsAny<CancellationToken>()), Times.Once());
        host.RoofService.Verify(service => service.ClearFault(500, It.IsAny<CancellationToken>()), Times.Once());
    }

    [TestMethod]
    public async Task ARefusedCommand_CarriesTheCode_TheRoofStatus_AndTheSharedWording()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.Open()).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.FaultLatched, "A drive fault is latched (test).")));
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => RoofServiceMock.Snapshot(RoofControllerStatus.Error, faultLatched: true));
        using var host = CreateHost(roof);
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator));

        var refused = await RefusedAsync(() => client.Roof.OpenAsync());

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        refused.Code.Should().Be(RoofControllerErrorCode.FaultLatched);
        refused.CodeText.Should().Be("FaultLatched");
        refused.Detail.Should().Contain("A drive fault is latched (test).");
        refused.Message.Should().Be("A fault is latched. Resolve the cause, then clear the fault. [FaultLatched]");
        refused.RoofStatus.Should().NotBeNull();
        refused.RoofStatus!.IsFaultLatched.Should().BeTrue();
        refused.RoofStatus.Status.Should().Be(RoofControllerStatus.Error);
        refused.RetryAfter.Should().BeNull();
        RoofText.DescribeFailure(refused).Should().Be(refused.Message);
    }

    [TestMethod]
    public async Task AnUnavailableController_AsksTheClientToRetryLater()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.Close()).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.HardwareUnavailable, "The relay HAT did not answer (test).")));
        using var host = CreateHost(roof);
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator));

        var refused = await RefusedAsync(() => client.Roof.CloseAsync());

        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        refused.Code.Should().Be(RoofControllerErrorCode.HardwareUnavailable);
        refused.Message.Should().Be("The relay hardware is unavailable. [HardwareUnavailable]");
        refused.RetryAfter.Should().Be(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task AnInvalidRequest_CarriesTheFieldErrors()
    {
        using var host = CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator));

        var refused = await RefusedAsync(() => client.Roof.ClearFaultAsync(10));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refused.Code.Should().BeNull();
        refused.Message.Should().Be("The request was invalid.");
        refused.Errors.Keys.Should().Contain(key => key.Equals("pulseMs", StringComparison.OrdinalIgnoreCase));
        host.RoofService.Verify(service => service.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [TestMethod]
    public async Task NoCredential_AndTooLowARole_AreDescribedByStatus()
    {
        using var host = CreateHost();
        using var anonymous = CreateClient(host);
        using var viewer = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));

        var notSignedIn = await RefusedAsync(() => anonymous.Roof.GetStatusAsync());
        var forbidden = await RefusedAsync(() => viewer.Roof.OpenAsync());

        notSignedIn.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        notSignedIn.Message.Should().Be("Not signed in, or the credential is no longer valid. Sign in again.");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        forbidden.Message.Should().Be("Your role does not permit this.");
        host.RoofService.Verify(service => service.Open(), Times.Never());
    }

    [TestMethod]
    public async Task AnApiKeyActingForSomeone_SendsTheirNameForTheAudit()
    {
        using var host = CreateHost();
        var sent = new ConcurrentQueue<RecordedRequest>();
        using var client = CreateClient(
            host,
            new RoofApiKeyCredential(TestApiKeys.Operator, "  alice  "),
            handler: () => new RecordingHandler(host.Server, sent));

        var caller = await client.Auth.GetCallerAsync();
        var stop = await client.StopAsync();

        caller.Name.Should().Contain("test-operator");
        stop.IsAcknowledged.Should().BeTrue();
        sent.Should().HaveCount(2).And.OnlyContain(request =>
            request.Headers[RoofIdentityContract.OnBehalfOfHeaderName] == "alice"
            && request.Headers.ContainsKey(RoofControllerApiContract.ApiKeyHeaderName));
        client.Credential!.ToString().Should().Be("API key on behalf of alice");
    }

    [TestMethod]
    [DataRow("Zoë", DisplayName = "not ASCII")]
    [DataRow("alice\r\nX-Api-Key: other", DisplayName = "a header break")]
    [DataRow("-alice", DisplayName = "not starting with a letter or digit")]
    [DataRow("alice smith", DisplayName = "a space")]
    public void AnOnBehalfOfName_ThatIsNotAUserName_IsRefusedWhenTheCredentialIsMade(string name)
    {
        var make = () => new RoofApiKeyCredential(TestApiKeys.Operator, name);

        make.Should().Throw<ArgumentException>().Which.Message.Should().StartWith(RoofApiKeyCredential.InvalidOnBehalfOf);
        new Func<RoofApiKeyCredential>(() => new RoofApiKeyCredential(TestApiKeys.Operator, new string('a', 65)))
            .Should().Throw<ArgumentException>("a user name has at most 64 characters");
        new RoofApiKeyCredential(TestApiKeys.Operator, new string('a', 64)).OnBehalfOf.Should().HaveLength(64);
        new RoofApiKeyCredential(TestApiKeys.Operator, "   ").OnBehalfOf.Should().BeNull("a blank name means nobody");
    }

    [TestMethod]
    public async Task TheRoofConfiguration_IsReadAndChanged_WithItsVersion()
    {
        var roof = RoofServiceMock.Create();
        var applied = new RoofControllerOptionsV4();
        roof.Setup(service => service.GetConfigurationSnapshot()).Returns(() => applied);
        roof.Setup(service => service.ApplyConfiguration(It.IsAny<RoofControllerOptionsV4>(), false))
            .Callback<RoofControllerOptionsV4, bool>((options, _) => applied = options)
            .Returns<RoofControllerOptionsV4, bool>((options, _) => Result<RoofControllerOptionsV4>.Success(options));
        using var host = CreateHost(roof);
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        var current = await admin.Roof.GetConfigurationAsync();
        var updated = await admin.Roof.UpdateConfigurationAsync(ValidConfiguration(current.Version));
        var stale = await RefusedAsync(() => admin.Roof.UpdateConfigurationAsync(ValidConfiguration(current.Version)));

        current.Version.Should().Be(1);
        updated.Version.Should().Be(2);
        updated.SafetyWatchdogTimeoutSeconds.Should().Be(120);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        stale.Code.Should().Be(RoofControllerErrorCode.ConfigurationVersionConflict);
        stale.Detail.Should().Contain("expected version 1, current version 2");
    }

    private static RoofConfigurationRequest ValidConfiguration(long version) => new()
    {
        ExpectedVersion = version,
        SafetyWatchdogTimeoutSeconds = 120,
        OpenRelayId = 1,
        CloseRelayId = 2,
        ClearFaultRelayId = 3,
        StopRelayId = 4,
        EnableDigitalInputPolling = true,
        DigitalInputPollIntervalMilliseconds = 75,
        EnablePeriodicVerificationWhileMoving = true,
        PeriodicVerificationIntervalSeconds = 5,
        UseNormallyClosedLimitSwitches = true,
        LimitSwitchDebounceMilliseconds = 15,
        IgnorePhysicalLimitSwitches = false,
        FaultInputActiveHigh = true,
        MaxConsecutiveInputReadFailures = 3
    };

    // ---- Signing in -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task APasswordSession_SignsIn_Refreshes_ChangesThePassword_AndSignsOut()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var client = CreateClient(host);

        var session = await client.Auth.SignInAsync("alice", TestSecrets.Password);
        client.Credential.Should().BeNull("signing in does not change the client's credential by itself");
        client.Credential = session;
        var ended = 0;
        session.Ended += (_, _) => ended++;

        session.Name.Should().Be("alice");
        session.Role.Should().Be(RoofControllerApiContract.OperatorRole);
        session.SessionId.Should().NotBeNullOrEmpty();
        session.ToString().Should().Be("session for alice").And.NotContain(session.Token);
        (await client.Roof.OpenAsync()).Should().NotBeNull();
        var caller = await client.Auth.RefreshAsync();
        caller.Name.Should().Be("alice");
        caller.Kind.Should().Be(RoofCredentialKind.Session);
        session.ExpiresUtc.Should().Be(caller.ExpiresUtc);

        await client.Auth.ChangePasswordAsync(TestSecrets.Password, "test-password-not-real-03");
        (await client.Auth.GetCallerAsync()).Name.Should().Be("alice", "the session that changed the password stays open");

        await client.Auth.SignOutAsync();

        session.IsEnded.Should().BeTrue();
        ended.Should().Be(1);
        var refused = await RefusedAsync(() => client.Auth.GetCallerAsync());
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ended.Should().Be(1, "Ended is raised once");
    }

    [TestMethod]
    public async Task AFailedSignIn_IsReported_AndSendsNoCredential()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        var refused = await RefusedAsync(() => client.Auth.SignInAsync("alice", TestSecrets.OtherPassword));

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        refused.Code.Should().Be(RoofControllerErrorCode.SignInFailed);
        refused.Message.Should().Be("Sign-in failed. Check the name and the password or PIN. [SignInFailed]");
        client.Credential.Should().BeOfType<RoofApiKeyCredential>("a refused sign-in leaves the credential alone");
    }

    [TestMethod]
    public async Task ChangingThePassword_WithTheWrongOne_IsRefused()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var client = CreateClient(host);
        client.Credential = await client.Auth.SignInAsync("alice", TestSecrets.Password);

        var refused = await RefusedAsync(() => client.Auth.ChangePasswordAsync(TestSecrets.OtherPassword, "test-password-not-real-03"));

        refused.Code.Should().Be(RoofControllerErrorCode.SignInFailed);
        refused.Detail.Should().Be("The current password is not correct.");
        var session = (RoofSessionCredential)client.Credential!;
        session.IsEnded.Should().BeFalse("a wrong password in the request does not mean the session was refused");
        (await client.Auth.GetCallerAsync()).Name.Should().Be("alice");
    }

    [TestMethod]
    public async Task ARefresh_LeavesASessionSetDuringIt_Alone()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        await AddUserAsync(host, "bob", RoofControllerApiContract.ViewerRole);
        RoofControllerClient? owner = null;
        RoofSessionCredential? bob = null;
        using var client = CreateClient(host, handler: () => new RecordingHandler(host.Server, new ConcurrentQueue<RecordedRequest>(), request =>
        {
            // Bob signs in on this client while the refresh for Alice is on its way back.
            if (request.Path.EndsWith("/Auth/Me", StringComparison.Ordinal) && bob is not null)
            {
                owner!.Credential = bob;
            }
        }));
        owner = client;
        var alice = await client.Auth.SignInAsync("alice", TestSecrets.Password);
        var bobSession = await client.Auth.SignInAsync("bob", TestSecrets.Password);
        var bobExpires = bobSession.ExpiresUtc;
        client.Credential = alice;
        bob = bobSession;

        var caller = await client.Auth.RefreshAsync();

        caller.Name.Should().Be("alice");
        client.Credential.Should().BeSameAs(bobSession);
        bobSession.Name.Should().Be("bob");
        bobSession.Role.Should().Be(RoofControllerApiContract.ViewerRole);
        bobSession.ExpiresUtc.Should().Be(bobExpires);
    }

    [TestMethod]
    public async Task SigningOut_WithAnApiKey_IsRefused()
    {
        using var host = CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator));

        var refused = await RefusedAsync(() => client.Auth.SignOutAsync());

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refused.Code.Should().Be(RoofControllerErrorCode.InvalidRequest);
    }

    [TestMethod]
    public async Task AKiosk_ListsItsPinUsers_UnlocksWithAPin_AndLocksAgain()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
        var kiosk = new RoofKioskCredential(KioskKey);
        var changes = new List<string?>();
        kiosk.PinSessionChanged += (_, session) => changes.Add(session?.Name);
        using var client = CreateClient(host, kiosk);

        (await client.Auth.GetPinUsersAsync()).Should().ContainSingle(user => user.Name == "olive");
        (await RefusedAsync(() => client.Roof.OpenAsync())).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a locked kiosk is a viewer");
        var session = await client.Auth.SignInWithPinAsync("olive", TestSecrets.Pin);

        session.Kind.Should().Be(RoofCredentialKind.Pin);
        kiosk.PinSession.Should().BeSameAs(session);
        kiosk.ToString().Should().Be("kiosk unlocked by olive").And.NotContain(session.Token).And.NotContain(KioskKey);
        (await client.Roof.OpenAsync()).Should().NotBeNull();
        (await client.Auth.GetCallerAsync()).Name.Should().Be("olive");

        await client.Auth.SignOutAsync();

        kiosk.PinSession.Should().BeNull();
        kiosk.ToString().Should().Be("kiosk (locked)");
        changes.Should().Equal("olive", null);
        (await client.Auth.GetCallerAsync()).IsKiosk.Should().BeTrue();
        host.RoofService.Verify(service => service.Open(), Times.Once());
    }

    [TestMethod]
    public async Task AWrongPin_IsRefused_AndTheKioskStaysLocked()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
        var kiosk = new RoofKioskCredential(KioskKey);
        using var client = CreateClient(host, kiosk);

        var refused = await RefusedAsync(() => client.Auth.SignInWithPinAsync("olive", TestSecrets.OtherPin));

        refused.Code.Should().Be(RoofControllerErrorCode.SignInFailed);
        kiosk.PinSession.Should().BeNull();
    }

    [TestMethod]
    public async Task PinSignIn_NeedsAKioskCredential_AndAKioskKey()
    {
        using var host = CreateHost();
        using var keyClient = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));
        using var notAKiosk = CreateClient(host, new RoofKioskCredential(TestApiKeys.Viewer));

        await keyClient.Invoking(client => client.Auth.SignInWithPinAsync("olive", TestSecrets.Pin))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("PIN sign-in needs a kiosk credential*");
        var refused = await RefusedAsync(() => notAKiosk.Auth.GetPinUsersAsync());

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        refused.Code.Should().Be(RoofControllerErrorCode.KioskKeyRequired);
        refused.Message.Should().Be("PIN sign-in is accepted only from a kiosk device. [KioskKeyRequired]");
    }

    [TestMethod]
    public async Task AKioskRead_AfterItsPinSessionEnded_FallsBackToTheDeviceKey_ButACommandIsNotRepeated()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
        var kiosk = new RoofKioskCredential(KioskKey);
        using var client = CreateClient(host, kiosk);
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        var session = await client.Auth.SignInWithPinAsync("olive", TestSecrets.Pin);
        await admin.Identity.EndSessionAsync(session.SessionId);
        var caller = await client.Auth.GetCallerAsync();

        caller.IsKiosk.Should().BeTrue("the read was retried with the device key alone");
        caller.Name.Should().Be("test-kiosk");
        kiosk.PinSession.Should().BeNull();

        await client.Auth.SignInWithPinAsync("olive", TestSecrets.Pin);
        var sessions = await admin.Identity.GetSessionsAsync();
        await admin.Identity.EndSessionAsync(sessions.Single(candidate => candidate.Name == "olive").Id);
        var refused = await RefusedAsync(() => client.Roof.OpenAsync());

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a command is reported, not repeated as someone else");
        kiosk.PinSession.Should().BeNull();
        host.RoofService.Verify(service => service.Open(), Times.Never());
    }

    // ---- People, keys and sessions ------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnAdmin_ManagesPeople()
    {
        using var host = CreateHost();
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        var added = await admin.Identity.AddUserAsync(new RoofUserCreateRequest
        {
            Name = "bob.smith",
            Role = RoofControllerApiContract.ViewerRole,
            Password = TestSecrets.Password
        });
        var conflict = await RefusedAsync(() => admin.Identity.AddUserAsync(new RoofUserCreateRequest
        {
            Name = "bob.smith",
            Role = RoofControllerApiContract.ViewerRole,
            Password = TestSecrets.Password
        }));
        var updated = await admin.Identity.UpdateUserAsync("bob.smith", new RoofUserUpdateRequest { Role = RoofControllerApiContract.OperatorRole, Pin = TestSecrets.Pin });

        added.Name.Should().Be("bob.smith");
        added.HasPassword.Should().BeTrue();
        conflict.Code.Should().Be(RoofControllerErrorCode.IdentityNameConflict);
        updated.Role.Should().Be(RoofControllerApiContract.OperatorRole);
        updated.HasPin.Should().BeTrue();
        (await admin.Identity.GetUserAsync("bob.smith")).Role.Should().Be(RoofControllerApiContract.OperatorRole);
        (await admin.Identity.GetUsersAsync()).Should().ContainSingle(user => user.Name == "bob.smith");

        await admin.Identity.RemoveUserAsync("bob.smith");

        var missing = await RefusedAsync(() => admin.Identity.GetUserAsync("bob.smith"));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.Code.Should().Be(RoofControllerErrorCode.IdentityNotFound);
        (await RefusedAsync(() => admin.Identity.GetUserAsync("../ApiKeys"))).Code.Should().Be(
            RoofControllerErrorCode.IdentityNotFound, "a name is escaped into one path segment and cannot reach another route");
    }

    [TestMethod]
    public async Task AnAdmin_AddsUpdatesRotatesAndRemovesAnApiKey()
    {
        using var host = CreateHost();
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        var added = await admin.Identity.AddApiKeyAsync(new RoofApiKeyCreateRequest { Name = "ci", Role = RoofControllerApiContract.OperatorRole });
        using (var ci = CreateClient(host, new RoofApiKeyCredential(added.Secret)))
        {
            (await ci.Roof.OpenAsync()).Should().NotBeNull();
        }

        var updated = await admin.Identity.UpdateApiKeyAsync("ci", new RoofApiKeyUpdateRequest { Role = RoofControllerApiContract.ViewerRole, Kiosk = false });
        var rotated = await admin.Identity.RotateApiKeyAsync("ci");
        var readOnly = await RefusedAsync(() => admin.Identity.UpdateApiKeyAsync("test-kiosk", new RoofApiKeyUpdateRequest { Role = RoofControllerApiContract.ViewerRole, Kiosk = false }));

        added.Key.Source.Should().Be(RoofApiKeySource.Managed);
        updated.Role.Should().Be(RoofControllerApiContract.ViewerRole);
        rotated.Secret.Should().NotBe(added.Secret);
        readOnly.Code.Should().Be(RoofControllerErrorCode.IdentityReadOnly);
        using (var old = CreateClient(host, new RoofApiKeyCredential(added.Secret)))
        {
            (await RefusedAsync(() => old.Roof.GetStatusAsync())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await admin.Identity.GetApiKeysAsync()).Should().Contain(key => key.Name == "ci");
        await admin.Identity.RemoveApiKeyAsync("ci");
        (await admin.Identity.GetApiKeysAsync()).Should().NotContain(key => key.Name == "ci");
        (await RefusedAsync(() => admin.Identity.RemoveApiKeyAsync("ci"))).Code.Should().Be(RoofControllerErrorCode.IdentityNotFound);
    }

    [TestMethod]
    public async Task AnAdmin_ListsAndEndsSessions()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var alice = CreateClient(host);
        var session = await alice.Auth.SignInAsync("alice", TestSecrets.Password);
        alice.Credential = session;
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        var sessions = await admin.Identity.GetSessionsAsync();
        await admin.Identity.EndSessionAsync(sessions.Single(candidate => candidate.Name == "alice").Id);

        sessions.Should().ContainSingle(candidate => candidate.Id == session.SessionId);
        (await RefusedAsync(() => alice.Auth.GetCallerAsync())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        session.IsEnded.Should().BeTrue("the refusal marks the session ended");
        (await admin.Identity.GetSessionsAsync()).Should().NotContain(candidate => candidate.Name == "alice");
    }

    [TestMethod]
    public async Task ThePeopleEndpoints_AreRefusedToAPinSession()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.AdminRole, TestSecrets.Pin);
        using var client = CreateClient(host, new RoofKioskCredential(KioskKey));
        await client.Auth.SignInWithPinAsync("alice", TestSecrets.Pin);

        var refused = await RefusedAsync(() => client.Identity.GetUsersAsync());

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        refused.Code.Should().Be(RoofControllerErrorCode.CredentialNotAllowed);
    }

    // ---- System, health and camera ----------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnAdmin_ReadsTheSystemInformationAndMetrics()
    {
        using var host = CreateHost();
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        using var viewer = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));

        (await admin.System.GetInformationAsync()).EnvironmentName.Should().Be("Development");
        (await admin.System.GetMetricsAsync()).ThreadCount.Should().BePositive();
        (await RefusedAsync(() => viewer.System.GetInformationAsync())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task ARestart_IsAccepted_OnceTheRoofIsStopped()
    {
        using var host = CreateHost();
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        var restart = await admin.System.RestartAsync();

        restart.ExitCode.Should().Be(RoofSettingsContract.RestartExitCode);
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.HostShutdown), Times.Once());
        var signal = host.Services.GetRequiredService<RoofRestartSignal>();
        await WaitUntilAsync(() => signal.Requested, "the restart to be requested");
    }

    [TestMethod]
    public async Task ARestart_IsRefused_WhenTheStopCannotBeVerified()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "The relays could not be read back.")));
        using var host = CreateHost(roof);
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        var refused = await RefusedAsync(() => admin.System.RestartAsync());

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        refused.Code.Should().Be(RoofControllerErrorCode.RestartRefused);
        refused.Message.Should().Be("The controller refused to restart. [RestartRefused]");
        refused.Detail.Should().StartWith("The roof stop could not be verified");
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested.Should().BeFalse();
    }

    [TestMethod]
    public async Task TheHealthEndpoints_AreRead_EvenWhenUnhealthy()
    {
        using var host = CreateHost();
        using var viewer = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));
        using var anonymous = CreateClient(host);

        var report = await viewer.Health.GetReportAsync();
        var ready = await anonymous.Health.GetReadinessAsync();
        var live = await anonymous.Health.GetLivenessAsync();

        report.Status.Should().BeOneOf("Healthy", "Degraded", "Unhealthy");
        report.Checks.Should().NotBeEmpty();
        ready.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
        ready.IsHealthy.Should().Be(ready.StatusCode == HttpStatusCode.OK);
        ready.Status.Should().BeOneOf("Healthy", "Degraded", "Unhealthy");
        live.IsHealthy.Should().BeTrue();
        live.Status.Should().Be("Healthy");
        (await RefusedAsync(() => anonymous.Health.GetReportAsync())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task AnUnhealthyController_IsReadAsAReport_NotAnError()
    {
        using var host = CreateHost(configureServices: services => services.AddHealthChecks().AddCheck(
            "test_failing", () => HealthCheckResult.Unhealthy("Failing on purpose (test)."), tags: ["hardware"]));
        using var viewer = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));
        using var anonymous = CreateClient(host);

        var report = await viewer.Health.GetReportAsync();
        var ready = await anonymous.Health.GetReadinessAsync();

        report.Status.Should().Be("Unhealthy");
        report.Checks.Should().Contain(check => check.Name == "test_failing" && check.Status == "Unhealthy");
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        ready.IsHealthy.Should().BeFalse();
        ready.Status.Should().Be("Unhealthy");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ACameraStream_IsProxied_AndAnUpstreamFailureIsReported()
    {
        var frames = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\n\r\nFAKEJPEG\r\n");
        var fail = false;
        var upstream = new FakeUpstreamHandler((_, _) =>
        {
            if (Volatile.Read(ref fail))
            {
                throw new HttpRequestException("The camera server is down (test).");
            }

            var content = new ByteArrayContent(frames);
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/x-mixed-replace; boundary=frame");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var host = CreateHost(
            configureServices: services => services.AddHttpClient<BlueIrisCameraClient>().ConfigurePrimaryHttpMessageHandler(() => upstream),
            settings: new Dictionary<string, string?> { ["BlueIris:BaseUrl"] = "http://blueiris.test" });
        using var viewer = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));

        await using (var stream = await viewer.Camera.OpenAsync(3))
        {
            stream.ContentType.Should().Be("multipart/x-mixed-replace; boundary=frame");
            using var copy = new MemoryStream();
            await stream.Content.CopyToAsync(copy);
            copy.ToArray().Should().Equal(frames);
        }

        Volatile.Write(ref fail, true);
        var refused = await RefusedAsync(() => viewer.Camera.OpenAsync(3));

        refused.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        refused.Message.Should().Be("The controller reported an unexpected error.");
        await viewer.Invoking(client => client.Camera.OpenAsync(0)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await viewer.Invoking(client => client.Camera.OpenAsync(100)).Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ---- Transport ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnUnreachableController_AndOneThatDoesNotAnswer_AreReportedInTheSharedWording()
    {
        using var host = CreateHost();
        using var refusedClient = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer), handler: FailingHandler.Refused);
        using var silentClient = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer), requestTimeout: TimeSpan.FromMilliseconds(200), handler: FailingHandler.Silent);

        var unreachable = await refusedClient.Invoking(client => client.Roof.GetStatusAsync()).Should().ThrowAsync<HttpRequestException>();
        var timedOut = await silentClient.Invoking(client => client.Roof.GetStatusAsync()).Should().ThrowAsync<TimeoutException>();

        RoofText.DescribeFailure(unreachable.Which).Should().Be(RoofText.Unreachable);
        RoofText.DescribeFailure(timedOut.Which).Should().Be(RoofText.TimedOut);
        timedOut.Which.Message.Should().Contain("api/v4.0/RoofControl/Status").And.Contain("0.2 s");
    }

    [TestMethod]
    public async Task ACallerCancellation_IsNotReportedAsATimeout()
    {
        using var host = CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer), handler: FailingHandler.Silent);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await client.Invoking(c => c.Roof.GetStatusAsync(cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task AnAnswerThatIsNotJson_IsAProtocolError()
    {
        using var host = CreateHost();
        using var client = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer), handler: () => new FailingHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("<html>captive portal</html>") })));

        var error = await client.Invoking(c => c.Roof.GetStatusAsync()).Should().ThrowAsync<RoofProtocolException>();

        error.Which.Message.Should().Be("The controller's answer to GET /api/v4.0/RoofControl/Status could not be read.");
    }
}
