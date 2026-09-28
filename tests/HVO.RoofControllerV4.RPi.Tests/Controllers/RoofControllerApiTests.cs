using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// End-to-end tests of the RoofControl API through the real pipeline: API-key authentication, role policies, POST-only
/// commands, and ProblemDetails mapping (409 interlock / 503 not ready / 500 unexpected).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoofControllerApiTests
{
    private const string BasePath = "/api/v4.0/RoofControl";

    private RoofApiTestHost _host = null!;
    private Mock<IRoofControllerServiceV4> _roof = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _host = new RoofApiTestHost();
        _roof = _host.RoofService;
    }

    [TestCleanup]
    public void TestCleanup() => _host.Dispose();

    // ---- Authentication -------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Status_WithoutKey_Returns401WithApiKeyChallenge()
    {
        using var client = _host.CreateApiClient();

        var response = await client.GetAsync($"{BasePath}/Status");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        response.Headers.WwwAuthenticate.Select(h => h.Scheme).Should().Contain(RoofControllerApiContract.ApiKeyScheme);
        _roof.Verify(s => s.GetCurrentStatusSnapshot(), Times.Never);
    }

    [TestMethod]
    public async Task Status_WithUnknownKey_Returns401()
    {
        using var client = _host.CreateApiClient("test-unknown-key-not-configured-anywhere");

        var response = await client.GetAsync($"{BasePath}/Status");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        _roof.Verify(s => s.GetCurrentStatusSnapshot(), Times.Never);
    }

    [TestMethod]
    public async Task Status_Viewer_ReturnsSnapshotAfterHardwareRead()
    {
        _roof.Setup(s => s.GetCurrentStatusSnapshot()).Returns(RoofServiceMock.Snapshot(RoofControllerStatus.Open));
        using var client = _host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync($"{BasePath}/Status");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var payload = await ApiJson.ReadAsync<RoofStatusResponse>(response);
        Assert.AreEqual(RoofControllerStatus.Open, payload.Status);
        Assert.AreEqual(RoofRelayRegisterState.Verified, payload.RelayRegisterState);
        _roof.Verify(s => s.RefreshStatus(true), Times.Once);
        _roof.Verify(s => s.GetCurrentStatusSnapshot(), Times.Once);
    }

    [TestMethod]
    public async Task Status_KeyConfiguredBySha256WithLowercaseRole_IsAccepted()
    {
        using var client = _host.CreateApiClient(TestApiKeys.HashedViewer);

        var response = await client.GetAsync($"{BasePath}/Status");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Authorization and verbs ----------------------------------------------------------------------------------

    [TestMethod]
    public async Task Open_Viewer_Returns403AndNeverMoves()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.PostAsync($"{BasePath}/Open", content: null);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        _roof.Verify(s => s.Open(), Times.Never);
    }

    [TestMethod]
    [DataRow("Open")]
    [DataRow("Close")]
    [DataRow("Stop")]
    [DataRow("ClearFault")]
    [DataRow("Lease")]
    public async Task Command_WithoutKey_Returns401AndNeverActs(string command)
    {
        using var client = _host.CreateApiClient();

        var response = await client.PostAsync($"{BasePath}/{command}", content: null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        VerifyNoCommandSent();
    }

    [TestMethod]
    [DataRow("Open")]
    [DataRow("Close")]
    [DataRow("Stop")]
    [DataRow("ClearFault")]
    [DataRow("Lease")]
    public async Task Command_AsGet_Returns405AndNeverActs(string command)
    {
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.GetAsync($"{BasePath}/{command}");

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        VerifyNoCommandSent();
    }

    [TestMethod]
    public async Task Open_Operator_ReturnsSnapshotAfterCommand()
    {
        _roof.Setup(s => s.GetCurrentStatusSnapshot())
            .Returns(RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening, relayMask: 0x01));
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/Open", content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var payload = await ApiJson.ReadAsync<RoofStatusResponse>(response);
        Assert.AreEqual(RoofControllerStatus.Opening, payload.Status);
        Assert.AreEqual(RoofMotionDirection.Opening, payload.CommandedMotion);
        _roof.Verify(s => s.Open(), Times.Once);
    }

    [TestMethod]
    public async Task Close_Admin_IsAllowedByRoleHierarchy()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsync($"{BasePath}/Close", content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        _roof.Verify(s => s.Close(), Times.Once);
    }

    // ---- Problem mapping ------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Open_FaultLatched_Returns409WithCodeAndSnapshot()
    {
        var snapshot = RoofServiceMock.Snapshot(faultLatched: true);
        _roof.Setup(s => s.Open()).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.FaultLatched, "A fault is latched; clear it first.", snapshot)));
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/Open", content: null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var problem = await ApiJson.ReadElementAsync(response);
        Assert.AreEqual("FaultLatched", problem.GetProperty("code").GetString());
        Assert.AreEqual("urn:hvo:roof-controller:FaultLatched", problem.GetProperty("type").GetString());
        Assert.AreEqual("A fault is latched; clear it first.", problem.GetProperty("detail").GetString());
        Assert.IsTrue(problem.GetProperty("roofStatus").GetProperty("isFaultLatched").GetBoolean());
    }

    [TestMethod]
    public async Task Close_NotInitialized_Returns503WithRetryAfter()
    {
        _roof.Setup(s => s.Close()).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.NotInitialized, "The controller is not initialized.")));
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/Close", content: null);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(TimeSpan.FromSeconds(2), response.Headers.RetryAfter?.Delta);
        var problem = await ApiJson.ReadElementAsync(response);
        Assert.AreEqual("NotInitialized", problem.GetProperty("code").GetString());
        Assert.AreEqual(JsonValueKind.Object, problem.GetProperty("roofStatus").ValueKind, "the current snapshot is attached");
    }

    [TestMethod]
    public async Task Open_UnexpectedError_Returns500WithoutLeakingTheMessage()
    {
        _roof.Setup(s => s.Open()).Returns(Result<RoofControllerStatus>.Failure(
            new InvalidOperationException("internal-detail-that-must-not-leak")));
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/Open", content: null);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("internal-detail-that-must-not-leak");
        var problem = await ApiJson.ReadElementAsync(response);
        Assert.AreEqual("Unknown", problem.GetProperty("code").GetString());
    }

    // ---- Stop -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Stop_Viewer_IsAllowed()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.PostAsync($"{BasePath}/Stop", content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        _roof.Verify(s => s.Stop(RoofControllerStopReason.NormalStop), Times.Once);
    }

    [TestMethod]
    public async Task Stop_AnonymousWhenAllowed_IsAcceptedButOtherCommandsStillNeedAKey()
    {
        using var host = new RoofApiTestHost(settings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:AllowAnonymousStop"] = "true"
        });
        using var client = host.CreateApiClient();

        var stop = await client.PostAsync($"{BasePath}/Stop", content: null);
        var open = await client.PostAsync($"{BasePath}/Open", content: null);

        Assert.AreEqual(HttpStatusCode.OK, stop.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, open.StatusCode);
        host.RoofService.Verify(s => s.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once);
        host.RoofService.Verify(s => s.Open(), Times.Never);
    }

    [TestMethod]
    public async Task Stop_RelayStateUnverified_Returns503()
    {
        _roof.Setup(s => s.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(
                RoofControllerErrorCode.RelayStateUnverified,
                "Relay read-back did not confirm all relays off.",
                RoofServiceMock.Snapshot(relayState: RoofRelayRegisterState.Unverified, relayMask: null))));
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/Stop", content: null);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await ApiJson.ReadElementAsync(response);
        Assert.AreEqual("RelayStateUnverified", problem.GetProperty("code").GetString());
        Assert.AreEqual("Unverified", problem.GetProperty("roofStatus").GetProperty("relayRegisterState").GetString());
    }

    // ---- ClearFault and Lease -------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ClearFault_WithPulse_PassesPulseAndReturnsSnapshot()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/ClearFault?pulseMs=300", content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var payload = await ApiJson.ReadAsync<RoofStatusResponse>(response);
        Assert.IsFalse(payload.IsFaultLatched);
        _roof.Verify(s => s.ClearFault(300, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ClearFault_WithoutPulse_UsesDefault()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/ClearFault", content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        _roof.Verify(
            s => s.ClearFault(RoofControllerLimits.DefaultClearFaultPulseMilliseconds, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    [DataRow(10)]
    [DataRow(5000)]
    public async Task ClearFault_PulseOutOfRange_Returns400(int pulseMs)
    {
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/ClearFault?pulseMs={pulseMs}", content: null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        _roof.Verify(s => s.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ClearFault_OperationInProgress_Returns409()
    {
        _roof.Setup(s => s.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result<bool>.Failure(
            new RoofControllerException(RoofControllerErrorCode.OperationInProgress, "A clear-fault pulse is already running.")));
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/ClearFault", content: null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ApiJson.ReadElementAsync(response);
        Assert.AreEqual("OperationInProgress", problem.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Lease_Operator_Returns200()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/Lease", content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        _roof.Verify(s => s.RenewLease(), Times.Once);
    }

    [TestMethod]
    public async Task Lease_NotActive_Returns409()
    {
        _roof.Setup(s => s.RenewLease()).Returns(Result<RoofStatusResponse>.Failure(
            new RoofControllerException(RoofControllerErrorCode.LeaseNotActive, "No leased motion is active.")));
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/Lease", content: null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ApiJson.ReadElementAsync(response);
        Assert.AreEqual("LeaseNotActive", problem.GetProperty("code").GetString());
    }

    // ---- Configuration --------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task GetConfiguration_Operator_Returns403()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.GetAsync($"{BasePath}/Configuration");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task GetConfiguration_Admin_ReturnsSnapshotWithVersion()
    {
        var options = new RoofControllerOptionsV4 { SafetyWatchdogTimeout = TimeSpan.FromSeconds(120), LimitSwitchDebounce = TimeSpan.FromMilliseconds(40) };
        _roof.Setup(s => s.GetConfigurationState()).Returns(new RoofControllerConfigurationState(options, 7));
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.GetAsync($"{BasePath}/Configuration");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var payload = await ApiJson.ReadAsync<RoofConfigurationResponse>(response);
        Assert.AreEqual(7, payload.Version);
        Assert.AreEqual(120, payload.SafetyWatchdogTimeoutSeconds);
        Assert.AreEqual(40, payload.LimitSwitchDebounceMilliseconds);
        Assert.AreEqual(42, payload.RestartOnFailureWaitTimeSeconds);
    }

    [TestMethod]
    public async Task UpdateConfiguration_ValidRequest_AppliesWithExpectedVersion()
    {
        RoofControllerOptionsV4? captured = null;
        _roof.Setup(s => s.UpdateConfiguration(It.IsAny<RoofControllerOptionsV4>(), 7))
            .Callback<RoofControllerOptionsV4, long>((options, _) => captured = options)
            .Returns<RoofControllerOptionsV4, long>((options, _) => Result<RoofControllerOptionsV4>.Success(options));
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsJsonAsync($"{BasePath}/Configuration", ValidRequest());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var payload = await ApiJson.ReadAsync<RoofConfigurationResponse>(response);
        Assert.AreEqual(120, payload.SafetyWatchdogTimeoutSeconds);
        Assert.IsNotNull(captured);
        Assert.AreEqual(TimeSpan.FromSeconds(120), captured.SafetyWatchdogTimeout);
        Assert.AreEqual(TimeSpan.FromMilliseconds(75), captured.DigitalInputPollInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(5), captured.PeriodicVerificationInterval);
        _roof.Verify(s => s.UpdateConfiguration(It.IsAny<RoofControllerOptionsV4>(), 7), Times.Once);
    }

    [TestMethod]
    public async Task GetConfiguration_ReportsTheLocalOnlyDriveAndDepartureSettings()
    {
        var options = new RoofControllerOptionsV4
        {
            DriveStopConfirmationTimeout = TimeSpan.FromSeconds(6),
            DepartureReleaseTimeout = TimeSpan.FromSeconds(8)
        };
        _roof.Setup(s => s.GetConfigurationState()).Returns(new RoofControllerConfigurationState(options, 7));
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var payload = await ApiJson.ReadAsync<RoofConfigurationResponse>(await client.GetAsync($"{BasePath}/Configuration"));

        Assert.AreEqual(6, payload.DriveStopConfirmationTimeoutSeconds);
        Assert.AreEqual(8, payload.DepartureReleaseTimeoutSeconds);
    }

    [TestMethod]
    public async Task GetConfiguration_ReportsUnsetLocalOnlySettingsAsNull()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var payload = await ApiJson.ReadAsync<RoofConfigurationResponse>(await client.GetAsync($"{BasePath}/Configuration"));

        Assert.IsNull(payload.DriveStopConfirmationTimeoutSeconds);
        Assert.IsNull(payload.DepartureReleaseTimeoutSeconds);
    }

    [TestMethod]
    public async Task UpdateConfiguration_KeepsTheLocalOnlyDriveAndDepartureSettings()
    {
        var current = new RoofControllerOptionsV4
        {
            DriveStopConfirmationTimeout = TimeSpan.FromSeconds(6),
            DepartureReleaseTimeout = TimeSpan.FromSeconds(8)
        };
        _roof.Setup(s => s.GetConfigurationState()).Returns(new RoofControllerConfigurationState(current, 7));
        RoofControllerOptionsV4? captured = null;
        _roof.Setup(s => s.UpdateConfiguration(It.IsAny<RoofControllerOptionsV4>(), 7))
            .Callback<RoofControllerOptionsV4, long>((options, _) => captured = options)
            .Returns<RoofControllerOptionsV4, long>((options, _) => Result<RoofControllerOptionsV4>.Success(options));
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsJsonAsync($"{BasePath}/Configuration", ValidRequest());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(captured);
        Assert.AreEqual(TimeSpan.FromSeconds(6), captured.DriveStopConfirmationTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(8), captured.DepartureReleaseTimeout);
    }

    [TestMethod]
    public async Task UpdateConfiguration_StaleVersion_Returns409Conflict()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsJsonAsync($"{BasePath}/Configuration", ValidRequest() with { ExpectedVersion = 6 });

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ApiJson.ReadElementAsync(response);
        Assert.AreEqual("ConfigurationVersionConflict", problem.GetProperty("code").GetString());
        VerifyConfigurationNotApplied();
    }

    [TestMethod]
    public async Task UpdateConfiguration_UnconfirmedRelaySwap_Returns409Rejected()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsJsonAsync(
            $"{BasePath}/Configuration",
            ValidRequest() with { OpenRelayId = 2, CloseRelayId = 1 });

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ApiJson.ReadElementAsync(response);
        Assert.AreEqual("ConfigurationRejected", problem.GetProperty("code").GetString());
        VerifyConfigurationNotApplied();
    }

    [TestMethod]
    public async Task UpdateConfiguration_ConfirmedRelaySwap_IsApplied()
    {
        RoofControllerOptionsV4? captured = null;
        _roof.Setup(s => s.UpdateConfiguration(It.IsAny<RoofControllerOptionsV4>(), 7))
            .Callback<RoofControllerOptionsV4, long>((options, _) => captured = options)
            .Returns<RoofControllerOptionsV4, long>((options, _) => Result<RoofControllerOptionsV4>.Success(options));
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsJsonAsync(
            $"{BasePath}/Configuration",
            ValidRequest() with { OpenRelayId = 2, CloseRelayId = 1, ConfirmSafetyCriticalChange = true });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(captured);
        Assert.AreEqual(2, captured.OpenRelayId);
        Assert.AreEqual(1, captured.CloseRelayId);
    }

    [TestMethod]
    public async Task UpdateConfiguration_InvalidRequest_ReturnsValidationProblem()
    {
        var request = ValidRequest() with
        {
            SafetyWatchdogTimeoutSeconds = 0,
            CloseRelayId = 1,
            EnableDigitalInputPolling = false,
            DigitalInputPollIntervalMilliseconds = -10,
            EnablePeriodicVerificationWhileMoving = false,
            PeriodicVerificationIntervalSeconds = 0,
            LimitSwitchDebounceMilliseconds = -1,
            MaxConsecutiveInputReadFailures = 0
        };
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsJsonAsync($"{BasePath}/Configuration", request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiJson.ReadAsync<ValidationProblemDetails>(response);
        problem.Errors.Keys.Should().Contain(
        [
            nameof(RoofConfigurationRequest.SafetyWatchdogTimeoutSeconds),
            nameof(RoofConfigurationRequest.DigitalInputPollIntervalMilliseconds),
            nameof(RoofConfigurationRequest.EnablePeriodicVerificationWhileMoving),
            nameof(RoofConfigurationRequest.OpenRelayId),
            nameof(RoofConfigurationRequest.MaxConsecutiveInputReadFailures)
        ]);
        VerifyConfigurationNotApplied();
    }

    [TestMethod]
    public async Task UpdateConfiguration_AnyFieldLeftOut_ReturnsValidationProblemNamingIt()
    {
        // A partial body must be rejected, never defaulted: an omitted limit or fault setting could turn supervision off,
        // and an omitted lease or IN4 window would read as null, which turns it off.
        var complete = JsonSerializer.SerializeToNode(WithLeaseAndIn4(), JsonSerializerOptions.Web)!.AsObject();
        var fields = complete.Select(p => p.Key).Where(k => k != "confirmSafetyCriticalChange").ToList();
        fields.Should().Contain(
        [
            "ignorePhysicalLimitSwitches",
            "useNormallyClosedLimitSwitches",
            "faultInputActiveHigh",
            "operatorLeaseTimeoutSeconds",
            "atSpeedConfirmationTimeoutSeconds"
        ]);
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        foreach (var field in fields)
        {
            var body = complete.DeepClone().AsObject();
            body.Remove(field);

            var response = await client.PostAsJsonAsync($"{BasePath}/Configuration", body);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} was left out", field);
            var problem = await ApiJson.ReadAsync<ValidationProblemDetails>(response);
            string.Join(" ", problem.Errors.Values.SelectMany(messages => messages)).Should().Contain($"'{field}'");
        }
        VerifyConfigurationNotApplied();
    }

    [TestMethod]
    public async Task UpdateConfiguration_WithoutTheConfirmationField_IsApplied()
    {
        var captured = CaptureAppliedConfiguration();
        var body = JsonSerializer.SerializeToNode(ValidRequest(), JsonSerializerOptions.Web)!.AsObject();
        body.Remove("confirmSafetyCriticalChange").Should().BeTrue();
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsJsonAsync($"{BasePath}/Configuration", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the confirmation is optional and nothing safety-critical changes");
        captured.Value.Should().NotBeNull();
    }

    [TestMethod]
    public async Task UpdateConfiguration_NullSafetyField_ReturnsValidationProblem()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsJsonAsync(
            $"{BasePath}/Configuration",
            ValidRequest() with { IgnorePhysicalLimitSwitches = null });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiJson.ReadAsync<ValidationProblemDetails>(response);
        problem.Errors.Keys.Should().Contain(nameof(RoofConfigurationRequest.IgnorePhysicalLimitSwitches));
        VerifyConfigurationNotApplied();
    }

    [TestMethod]
    [DataRow(nameof(RoofConfigurationRequest.OperatorLeaseTimeoutSeconds))]
    [DataRow(nameof(RoofConfigurationRequest.AtSpeedConfirmationTimeoutSeconds))]
    public async Task UpdateConfiguration_TurningOffTheLeaseOrIn4_Unconfirmed_Returns409Rejected(string setting)
    {
        UseLeaseAndIn4();
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsJsonAsync($"{BasePath}/Configuration", WithLeaseAndIn4() with
        {
            OperatorLeaseTimeoutSeconds = setting == nameof(RoofConfigurationRequest.OperatorLeaseTimeoutSeconds) ? null : 30,
            AtSpeedConfirmationTimeoutSeconds = setting == nameof(RoofConfigurationRequest.AtSpeedConfirmationTimeoutSeconds) ? null : 3
        });

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ApiJson.ReadElementAsync(response);
        Assert.AreEqual("ConfigurationRejected", problem.GetProperty("code").GetString());
        problem.GetProperty("detail").GetString().Should().Contain("turns off the operator lease or the IN4 interlock");
        VerifyConfigurationNotApplied();
    }

    [TestMethod]
    public async Task UpdateConfiguration_TurningOffTheLeaseAndIn4_Confirmed_IsApplied()
    {
        UseLeaseAndIn4();
        var captured = CaptureAppliedConfiguration();
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsJsonAsync(
            $"{BasePath}/Configuration",
            ValidRequest() with { ConfirmSafetyCriticalChange = true });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(captured.Value);
        Assert.IsNull(captured.Value.OperatorLeaseTimeout);
        Assert.IsNull(captured.Value.AtSpeedConfirmationTimeout);
    }

    [TestMethod]
    public async Task UpdateConfiguration_TurningOnOrRetuningTheLeaseAndIn4_NeedsNoConfirmation()
    {
        var captured = CaptureAppliedConfiguration();
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        // From off to on.
        var response = await client.PostAsJsonAsync($"{BasePath}/Configuration", WithLeaseAndIn4());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(TimeSpan.FromSeconds(30), captured.Value?.OperatorLeaseTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(3), captured.Value?.AtSpeedConfirmationTimeout);

        // From one window to another.
        UseLeaseAndIn4();
        response = await client.PostAsJsonAsync(
            $"{BasePath}/Configuration",
            WithLeaseAndIn4() with { OperatorLeaseTimeoutSeconds = 60, AtSpeedConfirmationTimeoutSeconds = 5 });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(TimeSpan.FromSeconds(60), captured.Value?.OperatorLeaseTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(5), captured.Value?.AtSpeedConfirmationTimeout);
    }

    [TestMethod]
    public async Task UpdateConfiguration_HugeWatchdog_Returns400()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Admin);

        var response = await client.PostAsJsonAsync(
            $"{BasePath}/Configuration",
            ValidRequest() with { SafetyWatchdogTimeoutSeconds = 100_000 });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiJson.ReadAsync<ValidationProblemDetails>(response);
        problem.Errors.Keys.Should().Contain(nameof(RoofConfigurationRequest.SafetyWatchdogTimeoutSeconds));
        VerifyConfigurationNotApplied();
    }

    [TestMethod]
    public async Task UpdateConfiguration_Operator_Returns403()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsJsonAsync($"{BasePath}/Configuration", ValidRequest());

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        VerifyConfigurationNotApplied();
    }

    // ---- System and health ----------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(TestApiKeys.Viewer, HttpStatusCode.Forbidden)]
    [DataRow(TestApiKeys.Operator, HttpStatusCode.Forbidden)]
    [DataRow(TestApiKeys.Admin, HttpStatusCode.OK)]
    public async Task SystemInfo_RequiresAdmin(string key, HttpStatusCode expected)
    {
        using var client = _host.CreateApiClient(key);

        var response = await client.GetAsync("/api/v1.0/System/info");

        Assert.AreEqual(expected, response.StatusCode);
    }

    [TestMethod]
    public async Task HealthDetails_Anonymous_Returns401()
    {
        using var client = _host.CreateApiClient();

        var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task HealthDetails_Viewer_ReturnsJsonReportEvenWhenUnhealthy()
    {
        using var client = _host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
        var report = await ApiJson.ReadElementAsync(response);
        Assert.AreEqual(JsonValueKind.Array, report.GetProperty("checks").ValueKind);
    }

    [TestMethod]
    [DataRow("/health/live")]
    [DataRow("/health/ready")]
    public async Task HealthProbes_AreAnonymous(string path)
    {
        using var client = _host.CreateApiClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    // ---- Telemetry and error envelope -----------------------------------------------------------------------------

    [TestMethod]
    public async Task Open_Success_EmitsRoofCommandTraceAndMetric()
    {
        var activities = new ConcurrentQueue<Activity>();
        var commandMeasurements = new ConcurrentQueue<(long Value, string? Command, string? Outcome)>();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "HVO.RoofController.RPi",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "HVO.RoofController.RPi" && instrument.Name == "roof.controller.commands")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string? command = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "roof.command")
                {
                    command = tag.Value?.ToString();
                }
                else if (tag.Key == "roof.outcome")
                {
                    outcome = tag.Value?.ToString();
                }
            }

            commandMeasurements.Enqueue((measurement, command, outcome));
        });
        meterListener.Start();
        using var client = _host.CreateApiClient(TestApiKeys.Operator);

        var response = await client.PostAsync($"{BasePath}/Open", content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var activity = activities.SingleOrDefault(a => Equals(a.GetTagItem("roof.command"), "open"));
        Assert.IsNotNull(activity);
        Assert.AreEqual("roof.command", activity.OperationName);
        Assert.AreEqual("success", activity.GetTagItem("roof.outcome"));
        commandMeasurements.Should().Contain(m => m.Value == 1 && m.Command == "open" && m.Outcome == "success");
    }

    [TestMethod]
    public async Task UnhandledException_InProduction_ReturnsGenericProblemWithTraceFields()
    {
        using var host = new RoofApiTestHost(environment: "Production");
        host.RoofService.Setup(s => s.GetCurrentStatusSnapshot()).Throws(new InvalidOperationException("internal-detail-that-must-not-leak"));
        using var client = host.CreateApiClient(TestApiKeys.Viewer, https: true);

        var response = await client.GetAsync($"{BasePath}/Status");

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("internal-detail-that-must-not-leak");
        var problem = await ApiJson.ReadElementAsync(response);
        problem.GetProperty("instance").GetString().Should().EndWith($"{BasePath}/Status");
        Assert.AreEqual(JsonValueKind.String, problem.GetProperty("traceId").ValueKind);
    }

    private static RoofConfigurationRequest ValidRequest() => new()
    {
        ExpectedVersion = 7,
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

    private void VerifyNoCommandSent()
    {
        _roof.Verify(s => s.Open(), Times.Never);
        _roof.Verify(s => s.Close(), Times.Never);
        _roof.Verify(s => s.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never);
        _roof.Verify(s => s.RenewLease(), Times.Never);
        _roof.Verify(s => s.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static RoofConfigurationRequest WithLeaseAndIn4()
        => ValidRequest() with { OperatorLeaseTimeoutSeconds = 30, AtSpeedConfirmationTimeoutSeconds = 3 };

    /// <summary>The controller runs with a 30 s operator lease and the 3 s IN4 window, as <see cref="WithLeaseAndIn4"/> sends.</summary>
    private void UseLeaseAndIn4()
        => _roof.Setup(s => s.GetConfigurationState()).Returns(new RoofControllerConfigurationState(
            new RoofControllerOptionsV4
            {
                OperatorLeaseTimeout = TimeSpan.FromSeconds(30),
                AtSpeedConfirmationTimeout = TimeSpan.FromSeconds(3)
            },
            7));

    private StrongBox<RoofControllerOptionsV4?> CaptureAppliedConfiguration()
    {
        var captured = new StrongBox<RoofControllerOptionsV4?>();
        _roof.Setup(s => s.UpdateConfiguration(It.IsAny<RoofControllerOptionsV4>(), 7))
            .Callback<RoofControllerOptionsV4, long>((options, _) => captured.Value = options)
            .Returns<RoofControllerOptionsV4, long>((options, _) => Result<RoofControllerOptionsV4>.Success(options));
        return captured;
    }

    private void VerifyConfigurationNotApplied()
        => _roof.Verify(s => s.UpdateConfiguration(It.IsAny<RoofControllerOptionsV4>(), It.IsAny<long>()), Times.Never);
}
