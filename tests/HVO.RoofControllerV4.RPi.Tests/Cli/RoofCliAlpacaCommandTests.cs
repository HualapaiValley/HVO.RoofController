using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.Extensions.DependencyInjection;
using TestSecrets = HVO.RoofControllerV4.RPi.Tests.Security.TestSecrets;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// <c>hvo-roof alpaca</c> (#220) against the controller in process with Alpaca Close on: the state, clearing the Stop hold,
/// override grants (by alert, with exactly its reasons, or by reason for scripts), revoking, alerts and acknowledging,
/// their JSON, and the refusal of an API key for everything only a person may do, before anything is sent. And
/// <c>keys add|set</c> taking the Alpaca accounts' roles.
/// </summary>
[TestClass]
public sealed class RoofCliAlpacaCommandTests
{
    [TestMethod]
    public async Task Status_ShowsTheCommandsTheHoldTheLeaseTheWarningsTheGrantsAndTheAlerts()
    {
        using var host = AlpacaCliSupport.CreateHost();
        AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("alpaca", "status");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        var alert = AlpacaCliSupport.Commands(host).Alerts().Single();
        result.Out.Should().MatchRegex(@"Commands: +Alpaca may close the roof \(never open it\)")
            .And.MatchRegex(@"Stop hold: +Alpaca commands are held after a Stop \(the controller's start\): a person must allow them again\.")
            .And.MatchRegex(@"Operator lease: +No operator lease: no person's interface holds Alpaca commands off\.")
            .And.MatchRegex(@"Warning: +Alpaca Close checks no telescope: no telescope is set to block the roof\.")
            .And.MatchRegex(@"Grants: +No override grant is in force\.")
            .And.Contain($"Alert {alert.Id} (NEW): {RoofAlpacaText.DescribeAlert(alert)}")
            .And.MatchRegex(@"Blocking telescopes: +0")
            .And.MatchRegex(@"Override window: +5 min");
        alert.ClientAddress.Should().Be(AlpacaCliSupport.DocumentationAddress, "the fixture's client address is a documentation address");
    }

    [TestMethod]
    public async Task Status_AlsoReportsWhetherTheSeparateServerIsDeployed()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var text = await rig.RunAsync("alpaca", "status");
        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().MatchRegex(@"Alpaca server: +Off");
        var json = await rig.RunAsync("alpaca", "status", "--json");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("server").GetProperty("enabled").GetBoolean().Should().BeFalse();
        json.Json.GetProperty("mode").GetString().Should().Be("CloseOnly", "the existing JSON command state remains available");
    }

    [TestMethod]
    public async Task Status_Json_IsTheControllersAlpacaState()
    {
        using var host = AlpacaCliSupport.CreateHost();
        AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("alpaca", "status", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("mode").GetString().Should().Be("CloseOnly");
        result.Json.GetProperty("hold").GetProperty("held").GetBoolean().Should().BeTrue();
        result.Json.GetProperty("operatorLease").GetProperty("held").GetBoolean().Should().BeFalse();
        result.Json.GetProperty("alerts")[0].GetProperty("reasonKeys").EnumerateArray().Select(key => key.GetString()).Should().Equal("scope-unknown:Main");
        result.Json.GetProperty("overrideWindowMinutes").GetDouble().Should().Be(5);
    }

    [TestMethod]
    public async Task Status_FromAControllerWithoutTheAlpacaState_SaysSo()
    {
        using var host = AlpacaCliSupport.CreateHost();
        var older = JsonSerializer.Serialize(RoofServiceMock.Snapshot() with { Alpaca = null }, RoofClientJson.Create());
        using var rig = new CliRig(host)
        {
            WrapHandler = inner => new StubAnswerHandler(inner, "/RoofControl/Status", HttpStatusCode.OK, "application/json", older)
        };
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("alpaca", "status");

        result.Code.Should().Be(RoofExitCode.Failed, result.ToString());
        result.Error.Should().Contain(RoofCliAlpaca.NoState);
    }

    [TestMethod]
    public async Task ClearHold_WithAPersonsSession_ClearsTheHold()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");
        AlpacaCliSupport.Commands(host).GetStatus().Hold.Held.Should().BeTrue("the controller's start sets it");

        var result = await rig.RunAsync("alpaca", "clear-hold");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be(RoofCliAlpaca.HoldCleared + Environment.NewLine);
        AlpacaCliSupport.Commands(host).GetStatus().Hold.Held.Should().BeFalse();

        var json = await rig.RunAsync("alpaca", "clear-hold", "--json");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("held").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    [DataRow("clear-hold")]
    [DataRow("revoke")]
    [DataRow("ack", "1")]
    [DataRow("grant", "--alert", "1")]
    public async Task APersonsAction_WithAnApiKey_IsRefusedBeforeAnythingIsSent(params string[] args)
    {
        using var host = AlpacaCliSupport.CreateHost();
        AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        var requests = new ConcurrentQueue<string>();
        using var rig = new CliRig(host) { WrapHandler = inner => new RequestLog(inner, requests) };
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync(["alpaca", .. args]);

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        result.Error.Should().Be(RoofCliAlpaca.PersonOnly + Environment.NewLine);
        result.Out.Should().BeEmpty();
        requests.Should().BeEmpty("an API key is refused before anything is sent");
        AlpacaCliSupport.Commands(host).GetStatus().Hold.Held.Should().BeTrue();
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
        AlpacaCliSupport.Commands(host).Alerts().Should().OnlyContain(alert => !alert.Acknowledged);

        var json = await rig.RunAsync(["alpaca", .. args, "--json"]);
        json.Code.Should().Be(RoofExitCode.Forbidden, json.ToString());
        var error = json.Json.GetProperty("error");
        error.GetProperty("exitCode").GetInt32().Should().Be((int)RoofExitCode.Forbidden);
        error.GetProperty("kind").GetString().Should().Be("Forbidden");
        error.GetProperty("message").GetString().Should().Be(RoofCliAlpaca.PersonOnly);
        requests.Should().BeEmpty();
    }

    [TestMethod]
    public void PersonOnly_SaysWhatToDo_AndNamesNoCredential()
    {
        RoofCliAlpaca.PersonOnly.Should().Be(
            "Only a person's session may do this, and this credential is an API key: the controller refuses every API key for the "
            + "Stop hold, override grants, the operator lease and alerts. Sign in with 'hvo-roof login' first. Nothing was sent.");
    }

    [TestMethod]
    public async Task Grant_ByAlert_AsksWithWhatItLifts_AndSendsExactlyTheAlertsReasonKeys()
    {
        using var host = AlpacaCliSupport.CreateHost();
        var alert = AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main", "scope-not-clear:Guide");
        using var rig = new CliRig(host) { Interactive = true };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");
        rig.Input.Enqueue("y");

        var result = await rig.RunAsync("alpaca", "grant", "--alert", alert.Id);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        foreach (var line in RoofAlpacaText.DescribeGrantQuestion(alert, 5))
        {
            result.Error.Should().Contain(line);
        }

        rig.Prompts.Should().ContainSingle().Which.Should().Be(($"{RoofAlpacaText.GrantCloseLabel}? [y/N] ", false));
        var grant = AlpacaCliSupport.Commands(host).Grants().Should().ContainSingle().Subject;
        grant.Kind.Should().Be(RoofAlpacaCommandKind.Close);
        grant.ReasonKeys.Should().Equal(alert.ReasonKeys);
        grant.GrantedBy.Should().Contain("ada");
        result.Out.Should().Be($"Granted. {RoofAlpacaText.DescribeGrant(grant with { SecondsRemaining = 300 })}" + Environment.NewLine);
    }

    [TestMethod]
    public async Task Grant_NotConfirmed_SendsNothing()
    {
        using var host = AlpacaCliSupport.CreateHost();
        var alert = AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        using var rig = new CliRig(host) { Interactive = true };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");
        rig.Input.Enqueue("n");

        var answered = await rig.RunAsync("alpaca", "grant", "--alert", alert.Id);

        answered.Code.Should().Be(RoofExitCode.ConfirmationRequired, answered.ToString());
        answered.Error.Should().Contain("Not confirmed; nothing was sent.");
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(false, DisplayName = "no terminal")]
    [DataRow(true, DisplayName = "--json")]
    public async Task Grant_IsAPersonsDecisionAtATerminal_WithoutOne_ItIsRefused_AndNothingIsSent(bool json)
    {
        // Roy's decision on PR #230 (Opus F3): an override is a person's, asked and answered at a terminal; no script,
        // cron job or answers file can grant one.
        using var host = AlpacaCliSupport.CreateHost();
        var alert = AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        var requests = new ConcurrentQueue<string>();
        using var rig = new CliRig(host) { Interactive = json, WrapHandler = inner => new RequestLog(inner, requests) };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");
        requests.Clear();

        var result = json
            ? await rig.RunAsync("alpaca", "grant", "--alert", alert.Id, "--json")
            : await rig.RunAsync("alpaca", "grant", "--alert", alert.Id);

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        if (json)
        {
            result.Json.GetProperty("error").GetProperty("message").GetString().Should().Be(RoofCliAlpaca.GrantNeedsATerminal);
        }
        else
        {
            result.Error.Should().Contain(RoofCliAlpaca.GrantNeedsATerminal);
        }

        requests.Should().BeEmpty("nothing is read or sent");
        rig.Prompts.Should().BeEmpty();
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("--yes")]
    [DataRow("-y")]
    [DataRow("--reason", "scope-unknown:Main")]
    public async Task Grant_HasNoScriptingOptions(params string[] args)
    {
        using var host = AlpacaCliSupport.CreateHost();
        var alert = AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        using var rig = new CliRig(host) { Interactive = true };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");

        var result = await rig.RunAsync(["alpaca", "grant", "--alert", alert.Id, .. args]);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task Grant_NeedsAnAlert()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");

        var result = await rig.RunAsync("alpaca", "grant");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("--alert");
    }

    [TestMethod]
    public async Task Grant_FromAnAlertAGrantCannotLift_OrNoSuchAlert_IsRefused()
    {
        using var host = AlpacaCliSupport.CreateHost();
        var held = AlpacaCliSupport.Commands(host);
        held.RaiseAlert("Refused", RoofAlpacaCommandKind.Close, "An Alpaca Close was refused: the human Stop hold is set (by ada).", ["stop-hold"], null);
        var alert = held.Alerts().Single();
        using var rig = new CliRig(host) { Interactive = true };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");

        var refused = await rig.RunAsync("alpaca", "grant", "--alert", alert.Id);

        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain(RoofCliAlpaca.NotGrantable(alert));

        var missing = await rig.RunAsync("alpaca", "grant", "--alert", "999");
        missing.Code.Should().Be(RoofExitCode.Refused, missing.ToString());
        missing.Error.Should().Contain("There is no Alpaca alert '999'. 'hvo-roof alpaca alerts' lists them.");
        held.Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task Revoke_RevokesTheCloseGrant_AndThenSaysThereIsNone()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");
        AlpacaCliSupport.Commands(host).Grant(AlpacaCommandRig.Person("ada"), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-unknown:Main"]))
            .Grant.Should().NotBeNull();

        var result = await rig.RunAsync("alpaca", "revoke");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be("Revoked the Close grant." + Environment.NewLine);
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();

        var again = await rig.RunAsync("alpaca", "revoke", "--kind", "close", "--json");
        again.Code.Should().Be(RoofExitCode.Refused, again.ToString());
        again.Json.GetProperty("error").GetProperty("detail").GetString().Should().Be("No Close grant is in force.");
    }

    // ---- Open grants (#220, phase 4) ----------------------------------------------------------------------------------

    [TestMethod]
    public async Task Grant_ByARefusedOpensAlert_AsksWithWhatItLifts_AndGrantsAnOpenWithExactlyItsReasonKeys()
    {
        using var host = AlpacaCliSupport.CreateHost(settings: AlpacaOpenUiSupport.OpenAndClose());
        var alert = AlpacaCliSupport.RaiseOpenRefusal(host, "weather-unsafe:Sky", "scope-unknown:Main", RoofAlpacaContract.NoRequiredWeatherKey);
        using var rig = new CliRig(host) { Interactive = true };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");
        rig.Input.Enqueue("y");

        var result = await rig.RunAsync("alpaca", "grant", "--alert", alert.Id);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        foreach (var line in RoofAlpacaText.DescribeGrantQuestion(alert, 5))
        {
            result.Error.Should().Contain(line);
        }

        result.Error.Should().Contain("Once open, the roof stays open if the weather turns");
        rig.Prompts.Should().ContainSingle().Which.Should().Be(($"{RoofAlpacaText.GrantOpenLabel}? [y/N] ", false));
        var grant = AlpacaCliSupport.Commands(host).Grants().Should().ContainSingle().Subject;
        grant.Kind.Should().Be(RoofAlpacaCommandKind.Open);
        grant.ReasonKeys.Should().Equal(alert.ReasonKeys);
        grant.GrantedBy.Should().Contain("ada");
        result.Out.Should().Be($"Granted. {RoofAlpacaText.DescribeGrant(grant with { SecondsRemaining = 300 })}" + Environment.NewLine)
            .And.StartWith("Granted. One Open allowed by ada");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "no terminal")]
    [DataRow(true, DisplayName = "--json")]
    public async Task Grant_ForAnOpen_IsAPersonsDecisionAtATerminal_WithoutOne_ItIsRefused_AndNothingIsSent(bool json)
    {
        using var host = AlpacaCliSupport.CreateHost(settings: AlpacaOpenUiSupport.OpenAndClose());
        var alert = AlpacaCliSupport.RaiseOpenRefusal(host, "weather-unsafe:Sky");
        var requests = new ConcurrentQueue<string>();
        using var rig = new CliRig(host) { Interactive = json, WrapHandler = inner => new RequestLog(inner, requests) };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");
        requests.Clear();

        var result = json
            ? await rig.RunAsync("alpaca", "grant", "--alert", alert.Id, "--json")
            : await rig.RunAsync("alpaca", "grant", "--alert", alert.Id);

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        (json ? result.Json.GetProperty("error").GetProperty("message").GetString() : result.Error).Should().Contain(RoofCliAlpaca.GrantNeedsATerminal);
        requests.Should().BeEmpty("nothing is read or sent");
        rig.Prompts.Should().BeEmpty();
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("--yes")]
    [DataRow("--kind", "Open")]
    [DataRow("--reason", "weather-unsafe:Sky")]
    public async Task Grant_ForAnOpen_HasNoScriptingOptions(params string[] args)
    {
        using var host = AlpacaCliSupport.CreateHost(settings: AlpacaOpenUiSupport.OpenAndClose());
        var alert = AlpacaCliSupport.RaiseOpenRefusal(host, "weather-unsafe:Sky");
        using var rig = new CliRig(host) { Interactive = true };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");

        var result = await rig.RunAsync(["alpaca", "grant", "--alert", alert.Id, .. args]);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task Grant_ForAnOpen_InBlockMode_IsRefusedWithTheReason_BeforeAnythingIsAskedOrSent()
    {
        using var host = AlpacaCliSupport.CreateHost(settings: AlpacaOpenUiSupport.OpenAndClose(block: true));
        var alert = AlpacaCliSupport.RaiseOpenRefusal(host, "weather-unsafe:Sky");
        var requests = new ConcurrentQueue<string>();
        using var rig = new CliRig(host) { Interactive = true, WrapHandler = inner => new RequestLog(inner, requests) };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");
        rig.Input.Enqueue("y");

        var result = await rig.RunAsync("alpaca", "grant", "--alert", alert.Id);

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain($"{RoofAlpacaText.DescribeGrantUnavailable(RoofAlpacaCommandKind.Open, RoofAlpacaCommands.OpenGrantsBlocked)} Nothing was sent.");
        result.Error.Should().NotContain("Allow one Alpaca Open despite", "nothing is asked");
        rig.Prompts.Should().BeEmpty();
        requests.Should().NotContain(request => request.StartsWith("POST", StringComparison.Ordinal));
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task Grant_ForAnOpen_ThatTheControllerRefuses_SaysItsReason()
    {
        // The interlock turns to Block after the status was read, just before the grant: the controller refuses it (403,
        // BlockMode).
        using var host = AlpacaCliSupport.CreateHost(settings: AlpacaOpenUiSupport.OpenAndClose());
        var alert = AlpacaCliSupport.RaiseOpenRefusal(host, "weather-unsafe:Sky");
        var requests = new ConcurrentQueue<string>();
        using var rig = new CliRig(host)
        {
            Interactive = true,
            WrapHandler = inner => new RequestLog(new AlpacaOpenUiSupport.BeforeSending(inner, request => AlpacaOpenUiSupport.BlockBeforeTheGrant(host.Services, request)), requests)
        };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");
        rig.Input.Enqueue("y");

        var result = await rig.RunAsync("alpaca", "grant", "--alert", alert.Id);

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        result.Error.Should().Contain(RoofAlpacaCommands.OpenGrantsBlocked);
        requests.Should().ContainSingle(request => request.StartsWith("POST /api/v4.0/alpaca/grants", StringComparison.Ordinal));
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task Grant_FromAnOpensAlertAGrantCannotLift_IsRefused()
    {
        using var host = AlpacaCliSupport.CreateHost(settings: AlpacaOpenUiSupport.OpenAndClose());
        var alert = AlpacaCliSupport.RaiseOpenRefusal(host, "weather-unsafe:Sky", "stop-hold");
        using var rig = new CliRig(host) { Interactive = true };
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");

        var refused = await rig.RunAsync("alpaca", "grant", "--alert", alert.Id);

        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain(RoofCliAlpaca.NotGrantable(alert));
        RoofCliAlpaca.NotGrantable(alert).Should().Be(
            $"Alert {alert.Id} cannot be granted from: only a refused Close or Open that is not acknowledged, whose every reason a grant "
            + "can lift, can be (for a Close, a telescope not clear of the roof or not known; for an Open, also a required weather "
            + "monitor's unsafe or unknown weather, no required weather source, or no telescope set to block the roof). Nothing was sent.");
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task Revoke_KindOpen_RevokesTheOpenGrant()
    {
        using var host = AlpacaCliSupport.CreateHost(settings: AlpacaOpenUiSupport.OpenAndClose());
        using var rig = new CliRig(host);
        await AlpacaCliSupport.SignInAsync(host, rig, "ada");
        AlpacaCliSupport.Commands(host).Grant(AlpacaCommandRig.Person("ada"), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["weather-unsafe:Sky"]))
            .Grant.Should().NotBeNull();

        var result = await rig.RunAsync("alpaca", "revoke", "--kind", "open");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be("Revoked the Open grant." + Environment.NewLine);
        AlpacaCliSupport.Commands(host).Grants().Should().BeEmpty();
    }

    [TestMethod]
    public async Task Alerts_SayAGrantCanLiftARefusedOpensWeather()
    {
        using var host = AlpacaCliSupport.CreateHost(settings: AlpacaOpenUiSupport.OpenAndClose());
        var refused = AlpacaCliSupport.RaiseOpenRefusal(host, "weather-unknown:Sky");
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("alpaca", "alerts");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        var lines = result.Out.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines[1].Should().MatchRegex($@"^{refused.Id} +NEW +yes +{System.Text.RegularExpressions.Regex.Escape(RoofAlpacaText.DescribeAlert(refused))}$");
        lines[2].Should().Be(RoofCliAlpaca.GrantHint)
            .And.Be("To allow one Close or Open despite an alert's reasons: 'hvo-roof alpaca grant --alert ID' (GRANT yes).");
        lines.Should().HaveCount(3);
    }

    [TestMethod]
    public async Task Alerts_InBlockMode_SayARefusedOpensGrantIsUnavailable_AndWhy_WhileACloseCanStillBeGranted()
    {
        using var host = AlpacaCliSupport.CreateHost(settings: AlpacaOpenUiSupport.OpenAndClose(block: true));
        var refusedOpen = AlpacaCliSupport.RaiseOpenRefusal(host, "weather-unknown:Sky");
        var refusedClose = AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("alpaca", "alerts");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        var lines = result.Out.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().MatchRegex("^ID +STATE +GRANT +ALERT$");
        lines[1].Should().MatchRegex($@"^{refusedClose.Id} +NEW +yes +{System.Text.RegularExpressions.Regex.Escape(RoofAlpacaText.DescribeAlert(refusedClose))}$");
        lines[2].Should().MatchRegex($@"^{refusedOpen.Id} +NEW +unavailable +{System.Text.RegularExpressions.Regex.Escape(RoofAlpacaText.DescribeAlert(refusedOpen))}$");
        lines[3].Should().Be(RoofCliAlpaca.GrantHint);
        lines[4].Should().Be(RoofAlpacaText.DescribeGrantUnavailable(RoofAlpacaCommandKind.Open, RoofAlpacaCommands.OpenGrantsBlocked));
        lines.Should().HaveCount(5);

        var json = await rig.RunAsync("alpaca", "alerts", "--json");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.EnumerateArray().Select(entry => entry.GetProperty("id").GetString()).Should().Equal(refusedClose.Id, refusedOpen.Id);
    }

    [TestMethod]
    public async Task Alerts_AreListedNewestFirst_WithWhetherAGrantCanLiftThem()
    {
        using var host = AlpacaCliSupport.CreateHost();
        var grantable = AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        AlpacaCliSupport.Commands(host).RaiseAlert("Refused", RoofAlpacaCommandKind.Close, "An Alpaca Close was refused: the human Stop hold is set (by ada).", ["stop-hold"], null);
        var newest = AlpacaCliSupport.Commands(host).Alerts()[0];
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("alpaca", "alerts");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        var lines = result.Out.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().MatchRegex("^ID +STATE +GRANT +ALERT$");
        lines[1].Should().MatchRegex($@"^{newest.Id} +NEW +no +{System.Text.RegularExpressions.Regex.Escape(RoofAlpacaText.DescribeAlert(newest))}$");
        lines[2].Should().MatchRegex($@"^{grantable.Id} +NEW +yes +{System.Text.RegularExpressions.Regex.Escape(RoofAlpacaText.DescribeAlert(grantable))}$");
        lines[3].Should().Be(RoofCliAlpaca.GrantHint);

        var json = await rig.RunAsync("alpaca", "alerts", "--json");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.EnumerateArray().Select(entry => entry.GetProperty("id").GetString()).Should().Equal(newest.Id, grantable.Id);
    }

    [TestMethod]
    public async Task Alerts_WhenThereAreNone_SaysSo()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("alpaca", "alerts");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be(RoofCliAlpaca.NoAlerts + Environment.NewLine);
    }

    [TestMethod]
    public async Task Ack_AcknowledgesTheAlert_WithThePersonsName()
    {
        using var host = AlpacaCliSupport.CreateHost();
        var alert = AlpacaCliSupport.RaiseRefusal(host, "scope-unknown:Main");
        using var rig = new CliRig(host);
        await AlpacaCliSupport.SignInAsync(host, rig, "ada", RoofControllerApiContract.ViewerRole);

        var result = await rig.RunAsync("alpaca", "ack", alert.Id);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be($"Acknowledged alert {alert.Id}: {alert.Message}" + Environment.NewLine);
        var acknowledged = AlpacaCliSupport.Commands(host).Alerts().Single();
        acknowledged.Acknowledged.Should().BeTrue();
        acknowledged.AcknowledgedBy.Should().Contain("ada");

        var missing = await rig.RunAsync("alpaca", "ack", "999", "--json");
        missing.Code.Should().Be(RoofExitCode.Refused, missing.ToString());
        missing.Json.GetProperty("error").GetProperty("detail").GetString().Should().Be("No alert has that id.");
    }

    [TestMethod]
    public async Task AViewersSession_MayNotClearTheHold()
    {
        using var host = AlpacaCliSupport.CreateHost();
        using var rig = new CliRig(host);
        await AlpacaCliSupport.SignInAsync(host, rig, "ada", RoofControllerApiContract.ViewerRole);

        var result = await rig.RunAsync("alpaca", "clear-hold");

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        AlpacaCliSupport.Commands(host).GetStatus().Hold.Held.Should().BeTrue();
    }

    // ---- keys: the Alpaca accounts' roles -----------------------------------------------------------------------------

    [TestMethod]
    [DataRow("alpaca-commander", RoofAlpacaContract.CommanderRole)]
    [DataRow("alpaca-server", RoofAlpacaContract.ServerRole)]
    [DataRow("RoofAlpacaCommander", RoofAlpacaContract.CommanderRole)]
    public async Task KeysAdd_And_Set_TakeTheAlpacaAccountsRoles(string given, string role)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var added = await rig.RunAsync("keys", "add", "alpaca-client", "--role", given);

        added.Code.Should().Be(RoofExitCode.Success, added.ToString());
        added.Error.Should().StartWith($"Added alpaca-client ({RoofCliFormat.Role(role)}).");
        var list = await rig.RunAsync("keys", "list");
        list.Out.Should().MatchRegex($@"alpaca-client +{RoofCliFormat.Role(role)} +no +managed");

        var set = await rig.RunAsync("keys", "set", "alpaca-client", "--role", "viewer");
        set.Code.Should().Be(RoofExitCode.Success, set.ToString());
        var back = await rig.RunAsync("keys", "set", "alpaca-client", "--role", given, "--json");
        back.Code.Should().Be(RoofExitCode.Success, back.ToString());
        back.Json.GetProperty("role").GetString().Should().Be(role);
    }

    [TestMethod]
    public async Task KeysAdd_AnAlpacaAccountsKioskKey_IsRefusedByTheController()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("keys", "add", "alpaca-kiosk", "--role", "alpaca-commander", "--kiosk");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Out.Should().BeEmpty("no key was made, so there is no value");
    }

    [TestMethod]
    public void Roles_AreShownAndParsedWithTheirCliNames()
    {
        RoofCliFormat.Role(RoofAlpacaContract.CommanderRole).Should().Be("alpaca-commander");
        RoofCliFormat.Role(RoofAlpacaContract.ServerRole).Should().Be("alpaca-server");
        RoofCliFormat.ParseKeyRole("Alpaca-Server").Should().Be(RoofAlpacaContract.ServerRole);
        RoofCliFormat.ParseKeyRole("operator").Should().Be(RoofControllerApiContract.OperatorRole);
        RoofCliFormat.ParseRole("alpaca-commander").Should().BeNull("a person's role is never an Alpaca account's");
        RoofCliFormat.ParseKeyRole("root").Should().BeNull();
    }
}

/// <summary>The Alpaca command state for the CLI and terminal interface tests (#220).</summary>
internal static class AlpacaCliSupport
{
    /// <summary>A documentation address (RFC 5737), for the client the Alpaca server reports: never a real one.</summary>
    public const string DocumentationAddress = "192.0.2.50";

    /// <summary>
    /// The controller in process with Alpaca Close on (no telescope blocks the roof), or as <paramref name="settings"/> set
    /// it (Alpaca Open and Close, say: <see cref="AlpacaOpenUiSupport.OpenAndClose"/>).
    /// </summary>
    public static RoofApiTestHost CreateHost(string mode = "CloseOnly", IDictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?> { ["RoofController:Alpaca:AllowCommands"] = mode };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }

        var host = RoofClientApiTests.CreateHost(settings: all);
        _ = host.Server;
        return host;
    }

    public static RoofAlpacaCommands Commands(RoofApiTestHost host) => host.Services.GetRequiredService<RoofAlpacaCommands>();

    /// <summary>
    /// An Alpaca Close refused for <paramref name="reasons"/>, as the controller's admission raises it, from ClientID 7 at a
    /// documentation address.
    /// </summary>
    public static RoofAlpacaAlert RaiseRefusal(RoofApiTestHost host, params string[] reasons)
        => RaiseRefusal(host, RoofAlpacaCommandKind.Close, reasons);

    /// <summary>An Alpaca Open refused for <paramref name="reasons"/> (#220, phase 4), from ClientID 7 at a documentation address.</summary>
    public static RoofAlpacaAlert RaiseOpenRefusal(RoofApiTestHost host, params string[] reasons)
        => RaiseRefusal(host, RoofAlpacaCommandKind.Open, reasons);

    private static RoofAlpacaAlert RaiseRefusal(RoofApiTestHost host, RoofAlpacaCommandKind command, string[] reasons)
    {
        var commands = Commands(host);
        commands.RaiseAlert(
            "Refused",
            command,
            $"An Alpaca {command} was refused: {RoofAlpacaText.DescribeReasons(reasons)}.",
            reasons,
            new RoofAlpacaClient(7, 1, DocumentationAddress));
        return commands.Alerts()[0];
    }

    /// <summary>
    /// Adds <paramref name="name"/> with <paramref name="role"/> and saves their password session, as <c>hvo-roof login</c>
    /// would.
    /// </summary>
    public static async Task SignInAsync(RoofApiTestHost host, CliRig rig, string name, string role = RoofControllerApiContract.OperatorRole)
    {
        await RoofClientApiTests.AddUserAsync(host, name, role);
        using var anonymous = ClientTestSupport.CreateClient(host);
        var session = await anonymous.Auth.SignInAsync(name, TestSecrets.Password);
        RoofCredentialStore.Save(rig.CredentialsPath, new RoofStoredCredentials
        {
            Controller = ClientTestSupport.BaseAddress,
            Session = new RoofStoredSession(session.Token, session.Name, session.Role, session.SessionId, session.ExpiresUtc)
        });
    }
}

/// <summary>Records the method, path and query of every request but the status hub's.</summary>
internal sealed class RequestLog(HttpMessageHandler inner, ConcurrentQueue<string> requests) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.RequestUri!.AbsolutePath.Contains("/hubs/", StringComparison.OrdinalIgnoreCase))
        {
            requests.Enqueue($"{request.Method} {request.RequestUri.PathAndQuery}");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
