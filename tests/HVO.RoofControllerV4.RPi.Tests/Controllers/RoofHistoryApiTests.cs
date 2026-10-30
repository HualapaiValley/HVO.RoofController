using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Storage;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Simulation.Safety;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// The roof command history through the real pipeline (#206): every roof command, accepted or refused, is recorded with
/// who sent it and the roof afterwards, and <c>GET RoofControl/History</c> pages it by role: an admin reads everyone's,
/// anyone else their own.
/// </summary>
[TestClass]
public sealed class RoofHistoryApiTests
{
    private const string HistoryPath = "/api/v4.0/RoofControl/History";

    [TestMethod]
    public async Task AcceptedAndRefusedCommands_AreRecorded_WithTheCallerTheCodeAndTheRoofAfter()
    {
        using var host = RoofClientApiTests.CreateHost();
        var latched = RoofServiceMock.Snapshot(RoofControllerStatus.Closed, faultLatched: true);
        using var operatorClient = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator));
        using var viewer = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        await operatorClient.Roof.OpenAsync();
        host.RoofService.Setup(service => service.Close()).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.FaultLatched, "A fault is latched; clear it first.", latched)));
        await RoofClientApiTests.RefusedAsync(() => operatorClient.Roof.CloseAsync());
        (await viewer.StopAsync()).IsAcknowledged.Should().BeTrue();
        await operatorClient.Roof.ClearFaultAsync();
        await operatorClient.Roof.RenewLeaseAsync();
        host.RoofService.Setup(service => service.RenewLease()).Returns(Result<RoofStatusResponse>.Failure(
            new RoofControllerException(RoofControllerErrorCode.LeaseNotActive, "No leased motion is active.")));
        await RoofClientApiTests.RefusedAsync(() => operatorClient.Roof.RenewLeaseAsync());

        var page = await admin.Roof.GetHistoryAsync();

        page.AllUsers.Should().BeTrue();
        page.TotalCount.Should().Be(5, "an accepted lease renewal only keeps a recorded motion going");
        page.Entries.Select(entry => (entry.Command, entry.User, entry.Accepted, entry.Code)).Should().Equal(
            (RoofHistoryCommand.Lease, "test-operator", false, nameof(RoofControllerErrorCode.LeaseNotActive)),
            (RoofHistoryCommand.ClearFault, "test-operator", true, null),
            (RoofHistoryCommand.Stop, "test-viewer", true, null),
            (RoofHistoryCommand.Close, "test-operator", false, nameof(RoofControllerErrorCode.FaultLatched)),
            (RoofHistoryCommand.Open, "test-operator", true, null));
        var refused = page.Entries[3];
        refused.Detail.Should().Be("A fault is latched; clear it first.");
        refused.Status.Should().Be(RoofControllerStatus.Closed);
        refused.IsFaultLatched.Should().BeTrue("the refusal's own snapshot is the roof afterwards");
        page.Entries.Should().AllSatisfy(entry =>
        {
            entry.PrincipalKind.Should().Be(RoofHistoryPrincipalKind.ApiKey);
            entry.CredentialKind.Should().Be(RoofCredentialKind.ApiKey);
            entry.Client.Should().NotBeNullOrEmpty("the client library names the program in its User-Agent");
        });
        page.Entries.Select(entry => entry.Id).Should().BeInDescendingOrder();
    }

    [TestMethod]
    public async Task AnonymousStops_AllReachTheRoof_AndFromOneSourceAreOneEntry()
    {
        using var host = new RoofApiTestHost(
            settings: new Dictionary<string, string?> { ["RoofControllerSecurity:AllowAnonymousStop"] = "true" },
            remoteIp: IPAddress.Parse("192.168.1.66"));
        using var anonymous = host.CreateApiClient();
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        for (var i = 0; i < 10; i++)
        {
            (await anonymous.PostAsync("/api/v4.0/RoofControl/Stop", null)).StatusCode.Should().Be(HttpStatusCode.OK, "Stop is never limited");
        }

        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Exactly(10));
        var page = await ApiJson.ReadAsync<RoofCommandHistoryPage>(await admin.GetAsync(HistoryPath));
        var entry = page.Entries.Should().ContainSingle().Which;
        entry.Command.Should().Be(RoofHistoryCommand.Stop);
        entry.PrincipalKind.Should().Be(RoofHistoryPrincipalKind.Anonymous);
        entry.PrincipalId.Should().BeNull();
        entry.User.Should().BeNull();
        entry.Accepted.Should().BeTrue();
        entry.Count.Should().Be(10);
        entry.RemoteAddress.Should().Be("192.168.1.66");
    }

    [TestMethod]
    public async Task APersonRemovedAndAddedAgain_UnderTheSameName_DoesNotReadTheFormerOnesCommands()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole);
        string formerId;
        using (var former = await SignedInAsync(host, "olive"))
        {
            await former.Roof.OpenAsync();
            formerId = (await former.Roof.GetHistoryAsync()).Entries.Single().PrincipalId!;
        }

        await admin.Identity.RemoveUserAsync("olive");
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole);
        using var olive = await SignedInAsync(host, "olive");
        await olive.Roof.CloseAsync();

        var own = await olive.Roof.GetHistoryAsync();
        own.Entries.Should().ContainSingle().Which.Command.Should().Be(RoofHistoryCommand.Close);
        own.Entries[0].PrincipalId.Should().NotBe(formerId);
        (await admin.Roof.GetHistoryAsync(new RoofHistoryQuery { User = "olive" })).Entries
            .Should().ContainSingle("the name finds whoever has it now").Which.Command.Should().Be(RoofHistoryCommand.Close);
        (await admin.Roof.GetHistoryAsync(new RoofHistoryQuery { PrincipalId = formerId })).Entries
            .Should().ContainSingle("the identifier finds someone since removed").Which.Command.Should().Be(RoofHistoryCommand.Open);
        (await RoofClientApiTests.RefusedAsync(() => olive.Roof.GetHistoryAsync(new RoofHistoryQuery { PrincipalId = formerId })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task AKeyNamedLikeAPerson_ReadsOnlyItsOwnCommands()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole);
        var key = await admin.Identity.AddApiKeyAsync(new RoofApiKeyCreateRequest { Name = "olive", Role = RoofControllerApiContract.OperatorRole });
        using var person = await SignedInAsync(host, "olive");
        using var namesake = CreateClient(host, new RoofApiKeyCredential(key.Secret));

        await person.Roof.OpenAsync();
        await namesake.StopAsync();

        (await namesake.Roof.GetHistoryAsync()).Entries.Should().ContainSingle().Which.Command.Should().Be(RoofHistoryCommand.Stop);
        (await person.Roof.GetHistoryAsync()).Entries.Should().ContainSingle().Which.Command.Should().Be(RoofHistoryCommand.Open);
        (await admin.Roof.GetHistoryAsync(new RoofHistoryQuery { User = "olive" })).TotalCount.Should().Be(2, "an admin's search finds both now named so");
    }

    [TestMethod]
    public async Task ARotatedKey_IsANewCredential_ItsEarlierCommandsStayUnderTheOldIdentifier()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        var key = await admin.Identity.AddApiKeyAsync(new RoofApiKeyCreateRequest { Name = "script", Role = RoofControllerApiContract.OperatorRole });
        string oldId;
        using (var before = CreateClient(host, new RoofApiKeyCredential(key.Secret)))
        {
            await before.Roof.OpenAsync();
            oldId = (await before.Roof.GetHistoryAsync()).Entries.Single().PrincipalId!;
        }

        var rotated = await admin.Identity.RotateApiKeyAsync("script");
        using var after = CreateClient(host, new RoofApiKeyCredential(rotated.Secret));
        await after.Roof.CloseAsync();

        oldId.Should().StartWith("key:");
        (await after.Roof.GetHistoryAsync()).Entries.Should().ContainSingle().Which.Command.Should().Be(RoofHistoryCommand.Close);
        (await admin.Roof.GetHistoryAsync(new RoofHistoryQuery { PrincipalId = oldId })).Entries
            .Should().ContainSingle().Which.Command.Should().Be(RoofHistoryCommand.Open);
    }

    [TestMethod]
    public async Task TheWebUisForwardedAddress_IsRecorded_FromTheLoopback()
    {
        using var host = RoofClientApiTests.CreateHost(remoteIp: IPAddress.Loopback);
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole);
        using var anonymous = CreateClient(host);
        var credential = new RoofWebCredential(await anonymous.Auth.SignInAsync("olive", TestSecrets.Password), stopKey: null);
        credential.ForwardFor(IPAddress.Parse("::ffff:192.168.1.20"));
        using var web = CreateClient(host, credential);
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        await web.Roof.OpenAsync();
        await web.StopAsync();

        var entries = (await admin.Roof.GetHistoryAsync()).Entries;
        entries.Should().HaveCount(2).And.AllSatisfy(entry =>
        {
            entry.RemoteAddress.Should().Be("192.168.1.20", "the person's browser, as the web UI named it");
            entry.ForwardedBy.Should().Be("127.0.0.1");
            entry.User.Should().Be("olive");
        });
    }

    [TestMethod]
    public async Task AForwardedAddress_FromTheNetwork_IsIgnored()
    {
        using var host = new RoofApiTestHost(remoteIp: IPAddress.Parse("192.168.1.5"));
        using var operatorClient = host.CreateApiClient(TestApiKeys.Operator);
        operatorClient.DefaultRequestHeaders.Add(RoofCommandHistoryContract.ForwardedForHeaderName, "10.1.2.3");
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        (await operatorClient.PostAsync("/api/v4.0/RoofControl/Open", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = (await ApiJson.ReadAsync<RoofCommandHistoryPage>(await admin.GetAsync(HistoryPath))).Entries.Single();
        entry.RemoteAddress.Should().Be("192.168.1.5", "only the controller's own loopback is believed");
        entry.ForwardedBy.Should().BeNull();
    }

    [TestMethod]
    public async Task ARequestRefusedBeforeTheRoof_IsNotACommand_AndIsNotRecorded()
    {
        using var host = new RoofApiTestHost();
        using var anonymous = host.CreateApiClient();
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);

        (await anonymous.PostAsync("/api/v4.0/RoofControl/Open", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await viewer.PostAsync("/api/v4.0/RoofControl/Open", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        host.Services.GetRequiredService<RoofCommandHistory>().Count.Should().Be(0, "nobody unauthenticated can fill the history and push real commands out");
    }

    [TestMethod]
    public async Task People_ReadTheirOwn_AndAnAdminReadsEveryonesOrOnePersons()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "ada", RoofControllerApiContract.AdminRole);
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole);
        await RoofClientApiTests.AddUserAsync(host, "vic", RoofControllerApiContract.ViewerRole);
        using var ada = await SignedInAsync(host, "ada");
        using var olive = await SignedInAsync(host, "olive");
        using var vic = await SignedInAsync(host, "vic");

        await olive.Roof.OpenAsync();
        await olive.StopAsync();
        await vic.StopAsync();
        await ada.Roof.CloseAsync();

        var oliveOwn = await olive.Roof.GetHistoryAsync();
        oliveOwn.AllUsers.Should().BeFalse();
        oliveOwn.Entries.Select(entry => entry.Command).Should().Equal(RoofHistoryCommand.Stop, RoofHistoryCommand.Open);
        oliveOwn.Entries.Should().AllSatisfy(entry =>
        {
            entry.User.Should().Be("olive");
            entry.PrincipalKind.Should().Be(RoofHistoryPrincipalKind.Person);
            entry.CredentialKind.Should().Be(RoofCredentialKind.Session);
        });
        (await vic.Roof.GetHistoryAsync()).Entries.Should().ContainSingle("a viewer reads their own Stops").Which.User.Should().Be("vic");
        (await olive.Roof.GetHistoryAsync(new RoofHistoryQuery { User = "OLIVE" })).TotalCount.Should().Be(2, "naming yourself is allowed");

        var everyone = await ada.Roof.GetHistoryAsync();
        everyone.AllUsers.Should().BeTrue();
        everyone.TotalCount.Should().Be(4);
        var onePerson = await ada.Roof.GetHistoryAsync(new RoofHistoryQuery { User = "olive" });
        onePerson.AllUsers.Should().BeTrue();
        onePerson.Entries.Select(entry => entry.User).Should().Equal("olive", "olive");
    }

    [TestMethod]
    public async Task NamingSomeoneElse_IsRefusedForAnyoneButAnAdmin()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole);
        using var olive = await SignedInAsync(host, "olive");
        using var viewerKey = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));

        var refused = await RoofClientApiTests.RefusedAsync(() => olive.Roof.GetHistoryAsync(new RoofHistoryQuery { User = "ada" }));
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        refused.Detail.Should().Contain("Only an admin");
        (await RoofClientApiTests.RefusedAsync(() => viewerKey.Roof.GetHistoryAsync(new RoofHistoryQuery { User = "test-operator" })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task AKioskPinSession_ReadsOnlyItsPersonsCommands_EvenAnAdminsPin()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "ada", RoofControllerApiContract.AdminRole, TestSecrets.Pin);
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole);
        using var olive = await SignedInAsync(host, "olive");
        await olive.Roof.OpenAsync();
        var kiosk = new RoofKioskCredential(RoofClientApiTests.KioskKey);
        using var client = CreateClient(host, kiosk);
        await client.Auth.SignInWithPinAsync("ada", TestSecrets.Pin);

        await client.Roof.CloseAsync();
        await client.StopAsync();

        var page = await client.Roof.GetHistoryAsync();
        page.AllUsers.Should().BeFalse("a PIN session is never an admin's");
        page.Entries.Should().HaveCount(2).And.AllSatisfy(entry =>
        {
            entry.User.Should().Be("ada");
            entry.CredentialKind.Should().Be(RoofCredentialKind.Pin);
            entry.Device.Should().Be("test-kiosk");
        });
    }

    [TestMethod]
    public async Task TheTimeFiltersAndPaging_ReachTheStore()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var operatorClient = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator));
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        for (var i = 0; i < 5; i++)
        {
            await operatorClient.StopAsync();
        }

        var page = await admin.Roof.GetHistoryAsync(new RoofHistoryQuery { Skip = 1, Take = 2 });
        page.Entries.Select(entry => entry.Id).Should().Equal(4, 3);
        page.TotalCount.Should().Be(5);
        (await admin.Roof.GetHistoryAsync(new RoofHistoryQuery { From = DateTimeOffset.UtcNow.AddMinutes(1) })).TotalCount.Should().Be(0);
        (await admin.Roof.GetHistoryAsync(new RoofHistoryQuery { To = DateTimeOffset.UtcNow.AddMinutes(1) })).TotalCount.Should().Be(5);
    }

    [TestMethod]
    [DataRow("take=0")]
    [DataRow("take=501")]
    [DataRow("skip=-1")]
    [DataRow("from=not-a-time")]
    [DataRow("from=2026-10-30T12:00:00Z&to=2026-10-30T11:00:00Z")]
    public async Task ValuesOutOfRange_AreRefusedWith400(string query)
    {
        using var host = new RoofApiTestHost();
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var response = await admin.GetAsync($"{HistoryPath}?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task TheHistory_NeedsACredential()
    {
        using var host = new RoofApiTestHost();
        using var anonymous = host.CreateApiClient();

        (await anonymous.GetAsync(HistoryPath)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task TheHistory_IsKeptNextToTheIdentityStore_AndSurvivesARestart()
    {
        using var directory = new TemporaryDirectory();
        var settings = new Dictionary<string, string?> { ["RoofControllerSecurity:Identity:StorePath"] = directory.File("identity.json") };
        using (var host = new RoofApiTestHost(settings: settings))
        {
            using var operatorClient = host.CreateApiClient(TestApiKeys.Operator);
            (await operatorClient.PostAsync("/api/v4.0/RoofControl/Open", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        File.Exists(directory.File(RoofCommandHistoryOptions.DefaultFileName)).Should().BeTrue("stopping the host writes what was recorded");
        using var restarted = new RoofApiTestHost(settings: settings);
        using var admin = restarted.CreateApiClient(TestApiKeys.Admin);
        var page = await ApiJson.ReadAsync<RoofCommandHistoryPage>(await admin.GetAsync(HistoryPath));
        page.IsPersistent.Should().BeTrue();
        page.Entries.Should().ContainSingle().Which.Command.Should().Be(RoofHistoryCommand.Open);
    }

    // ---- The safety interlock (#207) -------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheSafetyInterlocksRefusal_AndAnOverride_AreRecorded_WithTheSafetyState()
    {
        // No safety source: the conditions are Unknown, never Safe.
        using var host = RoofClientApiTests.CreateHost(settings: new Dictionary<string, string?> { ["RoofSafety:InterlockMode"] = "Warn" });
        using var operatorClient = host.CreateApiClient(TestApiKeys.Operator);
        using var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));

        (await operatorClient.PostAsync("/api/v4.0/RoofControl/Open", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await operatorClient.PostAsync("/api/v4.0/RoofControl/Open?overrideSafety=true", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await operatorClient.PostAsync("/api/v4.0/RoofControl/Close", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = (await admin.Roof.GetHistoryAsync()).Entries;
        entries.Select(entry => (entry.Command, entry.Accepted, entry.Code, entry.SafetyState, entry.SafetyOverride)).Should().Equal(
            (RoofHistoryCommand.Close, true, null, null, null),
            (RoofHistoryCommand.Open, true, null, "Unknown", true),
            (RoofHistoryCommand.Open, false, nameof(RoofControllerErrorCode.SafetyOverrideRequired), "Unknown", null));
        RoofHistoryText.DescribeResult(entries[1]).Should().Be("Accepted (safety override, conditions unknown)");
        RoofHistoryText.DescribeResult(entries[2]).Should().Be("Refused [SafetyOverrideRequired]");
    }

    [TestMethod]
    public async Task AnOpenTheInterlockBlocks_IsRecordedAsARefusal_AndOneItOnlyAdvisesOn_AsAccepted()
    {
        using (var blocking = RoofClientApiTests.CreateHost(settings: new Dictionary<string, string?> { ["RoofSafety:InterlockMode"] = "Block" }))
        {
            using var admin = CreateClient(blocking, new RoofApiKeyCredential(TestApiKeys.Admin));
            await RoofClientApiTests.RefusedAsync(() => admin.Roof.OpenAsync(overrideSafety: true));

            var refused = (await admin.Roof.GetHistoryAsync()).Entries.Single();
            refused.Code.Should().Be(nameof(RoofControllerErrorCode.SafetyInterlockActive));
            refused.SafetyState.Should().Be("Unknown");
            refused.SafetyOverride.Should().BeNull("Block takes no override");
        }

        // A source that says unsafe: the interlock only advises, and the entry says what it found.
        var mount = new SimulatedTelescopeParkSource("Main", isParked: false);
        using var advising = RoofClientApiTests.CreateHost(configureServices: services => services.AddSingleton<ITelescopeParkSource>(mount));
        using var operatorClient = CreateClient(advising, new RoofApiKeyCredential(TestApiKeys.Operator));
        await WaitUntilAsync(() => operatorClient.Roof.GetSafetyAsync().GetAwaiter().GetResult().State == RoofSafetyState.Unsafe, "the mount to be read");
        await operatorClient.Roof.OpenAsync();

        var accepted = (await operatorClient.Roof.GetHistoryAsync()).Entries.Single();
        accepted.Accepted.Should().BeTrue("the interlock is off by default");
        accepted.SafetyState.Should().Be("Unsafe");
        accepted.SafetyOverride.Should().BeNull();
        RoofHistoryText.DescribeResult(accepted).Should().Be("Accepted (conditions unsafe)");
    }

    [TestMethod]
    public async Task AnOpenAcceptedWhileNothingWatchesTheConditions_HasNoSafetyNote_ButARefusalKeepsIt()
    {
        // The interlock Off (the default) and no safety source: the conditions are always Unknown, which says nothing (#216).
        using var host = RoofClientApiTests.CreateHost();
        using var operatorClient = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator));
        (await operatorClient.Roof.GetSafetyAsync()).Should().Match<RoofSafetyStatus>(safety =>
            safety.State == RoofSafetyState.Unknown && safety.InterlockMode == RoofSafetyInterlockMode.Off && safety.Sources.Count == 0);

        await operatorClient.Roof.OpenAsync();
        host.RoofService.Setup(service => service.Open()).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.FaultLatched, "A fault is latched (test).")));
        await RoofClientApiTests.RefusedAsync(() => operatorClient.Roof.OpenAsync());

        var entries = (await operatorClient.Roof.GetHistoryAsync()).Entries;
        entries.Select(entry => (entry.Accepted, entry.Code, entry.SafetyState, entry.SafetyOverride)).Should().Equal(
            (false, nameof(RoofControllerErrorCode.FaultLatched), "Unknown", null),
            (true, null, null, null));
        RoofHistoryText.DescribeResult(entries[0]).Should().Be("Refused [FaultLatched]");
        RoofHistoryText.DescribeResult(entries[1]).Should().Be("Accepted");
    }

    [TestMethod]
    [DataRow(SimulatedSourceBehaviour.Fails, false, "cannot be read")]
    [DataRow(SimulatedSourceBehaviour.Answers, true, "is older than")]
    public async Task AnOpenAcceptedWhileAConfiguredSourceIsUnknown_SaysSo(SimulatedSourceBehaviour behaviour, bool stale, string reason)
    {
        // The interlock Off, but a source is configured: one that cannot be read, or whose last reading is too old.
        var weather = new SimulatedWeatherSafetySource("Cloud") { Behaviour = behaviour, ObservedUtc = stale ? DateTimeOffset.UtcNow.AddHours(-1) : null };
        using var host = RoofClientApiTests.CreateHost(configureServices: services => services.AddSingleton<IWeatherSafetySource>(weather));
        using var operatorClient = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator));
        await WaitUntilAsync(
            () => operatorClient.Roof.GetSafetyAsync().GetAwaiter().GetResult().Sources.Single().Reason.Contains(reason, StringComparison.Ordinal),
            "the weather monitor to be read");

        await operatorClient.Roof.OpenAsync();

        var accepted = (await operatorClient.Roof.GetHistoryAsync()).Entries.Single();
        accepted.SafetyState.Should().Be("Unknown");
        RoofHistoryText.DescribeResult(accepted).Should().Be("Accepted (conditions unknown)");
    }

    private static async Task<RoofControllerClient> SignedInAsync(RoofApiTestHost host, string name)
    {
        using var anonymous = CreateClient(host);
        var session = await anonymous.Auth.SignInAsync(name, TestSecrets.Password);
        return CreateClient(host, new RoofSessionCredential(session.Token));
    }
}
