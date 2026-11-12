using FluentAssertions;
using FluentAssertions.Execution;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Cli.Ui;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Client;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// Refs #260. The person-only <c>hvo-roof alpaca</c> commands in local authorization mode, each in both ways the mode
/// is selected (<c>--local</c>, and <c>authMode: local</c> saved), against the real controller in process as
/// <see cref="F2LocalRig"/> sets it up: its API, its native assertion exchange and revalidation, and its Alpaca command
/// state. The roof service is a mock, so nothing can move.
/// <para>
/// With the routes matched exactly, each command still asks the native helper for its own category and sends its own
/// request with the fresh native session. And an alert ID whose request would go to another path is refused before the
/// helper is asked or anything is sent, and the command after it is unaffected.
/// </para>
/// </summary>
[TestClass, DoNotParallelize]
public sealed class RoofCliLocalAlpacaCommandTests
{
    private const string LocalSession = "/api/v4.0/Auth/LocalSession";
    private const string Status = "/api/v4.0/RoofControl/Status";
    private const string Grants = "/api/v4.0/alpaca/grants";
    private const string Hold = "/api/v4.0/alpaca/hold";
    private const string Alerts = "/api/v4.0/alpaca/alerts";

    [TestMethod]
    [DataRow(true, DisplayName = "--local alpaca clear-hold")]
    [DataRow(false, DisplayName = "alpaca clear-hold with authMode local saved")]
    public async Task ClearHold_InLocalMode_AsksForOperate_AndClearsTheHold(bool viaFlag)
    {
        using var native = new F2LocalRig("Off", installedDefault: !viaFlag);
        var commands = AlpacaCliSupport.Commands(native.Controller);
        commands.GetStatus().Hold.Held.Should().BeTrue("the controller's start sets it");

        var result = await native.Cli.RunAsync([.. Local(viaFlag), "alpaca", "clear-hold"]);

        using (new AssertionScope())
        {
            result.Code.Should().Be(RoofExitCode.Success, result.ToString());
            result.Out.Should().Be(RoofCliAlpaca.HoldCleared + Environment.NewLine);
            native.Operations.Should().Equal(["operate"], "clearing the Stop hold is a person's command");
            native.ClientCredentials.Should().Equal($"POST {LocalSession}: none", $"DELETE {Hold}: session");
            native.ServerSaw.Should().Equal($"POST {LocalSession}", $"DELETE {Hold}");
            commands.GetStatus().Hold.Held.Should().BeFalse();
        }
    }

    [TestMethod]
    [DataRow(true, RoofAlpacaCommandKind.Close, DisplayName = "--local alpaca revoke --kind close")]
    [DataRow(false, RoofAlpacaCommandKind.Close, DisplayName = "alpaca revoke --kind close with authMode local saved")]
    [DataRow(true, RoofAlpacaCommandKind.Open, DisplayName = "--local alpaca revoke --kind open")]
    [DataRow(false, RoofAlpacaCommandKind.Open, DisplayName = "alpaca revoke --kind open with authMode local saved")]
    public async Task Revoke_InLocalMode_AsksForOperate_AndRevokesTheGrantOfThatKind(bool viaFlag, RoofAlpacaCommandKind kind)
    {
        using var native = new F2LocalRig("Off", installedDefault: !viaFlag);
        var commands = AlpacaCliSupport.Commands(native.Controller);
        foreach (var each in Enum.GetValues<RoofAlpacaCommandKind>())
        {
            commands.Grant(AlpacaCommandRig.Person("grace"), new RoofOverrideGrantRequest(each, ["scope-unknown:Main"])).Grant.Should().NotBeNull();
        }

        var result = await native.Cli.RunAsync([.. Local(viaFlag), "alpaca", "revoke", "--kind", kind.ToString().ToLowerInvariant()]);

        using (new AssertionScope())
        {
            result.Code.Should().Be(RoofExitCode.Success, result.ToString());
            result.Out.Should().Be($"Revoked the {kind} grant." + Environment.NewLine);
            native.Operations.Should().Equal(["operate"], "revoking a grant is a person's command");
            native.ClientCredentials.Should().Equal($"POST {LocalSession}: none", $"DELETE {Grants}/{kind}: session");
            native.ServerSaw.Should().Equal($"POST {LocalSession}", $"DELETE {Grants}/{kind}");
            commands.Grants().Select(grant => grant.Kind).Should().BeEquivalentTo(Enum.GetValues<RoofAlpacaCommandKind>().Where(other => other != kind),
                "only the grant of that kind is revoked");
        }
    }

    [TestMethod]
    [DataRow(true, DisplayName = "--local alpaca revoke")]
    [DataRow(false, DisplayName = "alpaca revoke with authMode local saved")]
    public async Task Revoke_WithoutKind_InLocalMode_ReadsTheGrants_ThenRevokesTheOnlyOne(bool viaFlag)
    {
        using var native = new F2LocalRig("Off", installedDefault: !viaFlag);
        var commands = AlpacaCliSupport.Commands(native.Controller);
        commands.Grant(AlpacaCommandRig.Person("grace"), new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["scope-unknown:Main"]))
            .Grant.Should().NotBeNull();

        var result = await native.Cli.RunAsync([.. Local(viaFlag), "alpaca", "revoke"]);

        using (new AssertionScope())
        {
            result.Code.Should().Be(RoofExitCode.Success, result.ToString());
            result.Out.Should().Be("Revoked the Open grant." + Environment.NewLine);
            native.Operations.Should().Equal(["read", "operate"], "the grants are read, then the only one is revoked");
            native.ClientCredentials.Should().Equal(
                $"POST {LocalSession}: none", $"GET {Grants}: session", $"POST {LocalSession}: none", $"DELETE {Grants}/Open: session");
            commands.Grants().Should().BeEmpty();
        }
    }

    /// <summary>
    /// <c>alpaca grant</c> in local mode: the status and the alerts are read, the person confirms, and the grant is sent
    /// under operate with exactly the alert's reasons.
    /// </summary>
    [TestMethod]
    [DataRow(true, DisplayName = "--local alpaca grant")]
    [DataRow(false, DisplayName = "alpaca grant with authMode local saved")]
    public async Task Grant_InLocalMode_ReadsThenAsksForOperate_AndGrantsExactlyTheAlertsReasons(bool viaFlag)
    {
        using var native = new F2LocalRig("Off", installedDefault: !viaFlag);
        var alert = AlpacaCliSupport.RaiseRefusal(native.Controller, "scope-unknown:Main", "scope-not-clear:Guide");
        native.Cli.Interactive = true;
        native.Cli.AnswerPrompt = Confirm;

        var result = await native.Cli.RunAsync([.. Local(viaFlag), "alpaca", "grant", "--alert", alert.Id]);

        using (new AssertionScope())
        {
            result.Code.Should().Be(RoofExitCode.Success, result.ToString());
            native.Operations.Should().Equal(["read", "read", "operate"], "the status and the alerts are read; the grant is a person's command");
            native.ClientCredentials.Should().Equal(
                $"POST {LocalSession}: none", $"GET {Status}: session",
                $"POST {LocalSession}: none", $"GET {Alerts}: session",
                $"POST {LocalSession}: none", $"POST {Grants}: session");
            var grants = AlpacaCliSupport.Commands(native.Controller).Grants();
            grants.Select(grant => grant.Kind).Should().Equal(RoofAlpacaCommandKind.Close);
            grants.SelectMany(grant => grant.ReasonKeys).Should().Equal(alert.ReasonKeys);
        }
    }

    /// <summary>
    /// Refs #267. <c>alpaca ack</c> must be authorized by the real Viewer route and acknowledge the alert, with a
    /// fresh native read session in both ways local mode is selected.
    /// </summary>
    [TestMethod]
    [DataRow(true, DisplayName = "--local alpaca ack")]
    [DataRow(false, DisplayName = "alpaca ack with authMode local saved")]
    public async Task Ack_InLocalMode_AsksForRead_AndAcknowledgesThatAlert(bool viaFlag)
    {
        using var native = new F2LocalRig("Off", installedDefault: !viaFlag);
        var alert = AlpacaCliSupport.RaiseRefusal(native.Controller, "scope-unknown:Main");

        var result = await native.Cli.RunAsync([.. Local(viaFlag), "alpaca", "ack", alert.Id]);

        using (new AssertionScope())
        {
            result.Code.Should().Be(RoofExitCode.Success, result.ToString());
            native.Operations.Should().Equal(["read"], "the acknowledgment route requires the native read scope");
            native.ClientCredentials.Should().Equal($"POST {LocalSession}: none", $"POST {Alerts}/{alert.Id}/acknowledge: session");
            native.ServerSaw.Should().Equal($"POST {LocalSession}", $"POST {Alerts}/{alert.Id}/acknowledge");
            AlpacaCliSupport.Commands(native.Controller).Alerts().Single(item => item.Id == alert.Id).Acknowledged.Should().BeTrue();
        }
    }

    /// <summary>
    /// Refs #267. Lease release has the same Viewer policy as acknowledgment. The terminal UI sends no lease requests
    /// in local mode, but the client library's release must still request read and pass the real route's policy.
    /// </summary>
    [TestMethod]
    public async Task ReleaseLease_InLocalMode_AsksForRead_AndIsAuthorized()
    {
        using var native = new F2LocalRig("Off");
        using var exchange = ClientTestSupport.CreateClient(native.Controller);
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = ClientTestSupport.BaseAddress,
            CreateHandler = () => ClientTestSupport.CreateHandler(() => native.Controller.Server),
            LocalAuthorization = async (operation, cancellationToken) => await exchange.Auth.SignInLocallyAsync(
                await native.Cli.ObtainLocalAssertion!(operation, cancellationToken), cancellationToken)
        });

        await client.Alpaca.ReleaseLeaseAsync();

        native.Operations.Should().Equal(["read"], "releasing a lease is a Viewer request, even though taking it requires operate");
        native.ServerSaw.Should().Equal($"POST {LocalSession}", "DELETE /api/v4.0/alpaca/lease");
        AlpacaCliSupport.Commands(native.Controller).GetStatus().OperatorLease.Held.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(true, DisplayName = "--local alpaca alerts")]
    [DataRow(false, DisplayName = "alpaca alerts with authMode local saved")]
    public async Task Alerts_InLocalMode_AskForRead(bool viaFlag)
    {
        using var native = new F2LocalRig("Off", installedDefault: !viaFlag);
        var alert = AlpacaCliSupport.RaiseRefusal(native.Controller, "scope-unknown:Main");

        var result = await native.Cli.RunAsync([.. Local(viaFlag), "alpaca", "alerts"]);

        using (new AssertionScope())
        {
            result.Code.Should().Be(RoofExitCode.Success, result.ToString());
            result.Out.Should().Contain(alert.Message);
            native.Operations.Should().Equal(["read", "read"], "the alerts, then the status that says whether a grant can be made");
            native.ClientCredentials.Should().Equal(
                $"POST {LocalSession}: none", $"GET {Alerts}: session", $"POST {LocalSession}: none", $"GET {Status}: session");
            AlpacaCliSupport.Commands(native.Controller).Alerts().Single().Acknowledged.Should().BeFalse("listing acknowledges nothing");
        }
    }

    /// <summary>
    /// An alert's ID that is a dot segment: the request's address would resolve it away, so the request would go to
    /// another path. Local mode refuses it itself (exit 6), before the native helper is asked or anything is sent.
    /// </summary>
    [TestMethod]
    [DataRow(true, ".", false, DisplayName = "--local alpaca ack .")]
    [DataRow(true, "..", false, DisplayName = "--local alpaca ack ..")]
    [DataRow(false, "..", false, DisplayName = "alpaca ack .. with authMode local saved")]
    [DataRow(true, "..", true, DisplayName = "--local alpaca ack .. --json")]
    public async Task Ack_OfADotSegment_InLocalMode_IsRefusedBeforeTheHelperIsAsked(bool viaFlag, string id, bool json)
    {
        using var native = new F2LocalRig("Off", installedDefault: !viaFlag);
        AlpacaCliSupport.RaiseRefusal(native.Controller, "scope-unknown:Main");
        string[] arguments = json ? [.. Local(viaFlag), "alpaca", "ack", id, "--json"] : [.. Local(viaFlag), "alpaca", "ack", id];

        var result = await native.Cli.RunAsync(arguments);

        using (new AssertionScope())
        {
            result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
            if (json)
            {
                result.Json.GetProperty("error").GetProperty("code").GetString().Should().Be("LocalRouteUnavailable");
            }
            else
            {
                result.Error.Should().Contain("unavailable in local authorization mode");
            }

            native.Operations.Should().BeEmpty("the native helper is asked for nothing");
            native.ClientSent.Should().BeEmpty("nothing is sent, not even the assertion's exchange");
            native.ServerSaw.Should().BeEmpty();
            AlpacaCliSupport.Commands(native.Controller).Alerts().Single().Acknowledged.Should().BeFalse();
        }

        // The refusal leaves nothing behind: the next command is asked, sent and done.
        var next = await native.Cli.RunAsync([.. Local(viaFlag), "alpaca", "clear-hold"]);

        using (new AssertionScope())
        {
            next.Code.Should().Be(RoofExitCode.Success, next.ToString());
            native.Operations.Should().Equal("operate");
            native.ServerSaw.Should().Equal($"POST {LocalSession}", $"DELETE {Hold}");
            AlpacaCliSupport.Commands(native.Controller).GetStatus().Hold.Held.Should().BeFalse();
        }
    }

    /// <summary>
    /// The terminal interface in local mode: Allow Alpaca again clears the hold under operate. It holds no operator
    /// lease there (a native session is made for each request), so it sends no lease heartbeat at all.
    /// </summary>
    [TestMethod]
    public void Tui_InLocalMode_AllowAlpacaAgain_ClearsTheHold_AndSendsNoOperatorLease()
    {
        using var native = new F2LocalRig("Off", recordClientSide: false);
        var commands = AlpacaCliSupport.Commands(native.Controller);
        using var tui = RoofTerminalUiAlpacaTests.Started(native.Cli);
        var page = RoofTerminalUiAlpacaTests.ShowAlpaca(tui);

        RoofTerminalUiAlpacaTests.Enter(tui, page, RoofUiAlpacaItemKind.Hold);
        commands.GetStatus().Hold.Held.Should().BeTrue("nothing is sent before the person chooses");
        tui.Ui.Panel!.Press(RoofAlpacaText.ClearHoldLabel);
        tui.WaitIdle("the hold to clear", () => tui.Ui.Message == RoofCliAlpaca.HoldCleared);

        using (new AssertionScope())
        {
            commands.GetStatus().Hold.Held.Should().BeFalse();
            native.Operations.Should().Contain("operate", "clearing the Stop hold is a person's command");
            native.ServerSaw.Should().Contain($"DELETE {Hold}");
            tui.Ui.OperatorLease.Should().BeNull("local mode keeps no session to hold a lease with");
            native.ServerSaw.Should().NotContain(line => line.Contains("/alpaca/lease", StringComparison.Ordinal));
            commands.GetStatus().OperatorLease.Held.Should().BeFalse();
        }
    }

    private static string[] Local(bool viaFlag) => viaFlag ? ["--local"] : [];

    /// <summary>Types the confirmation the grant's prompt shows, as the person would.</summary>
    private static string Confirm(string prompt, bool secret)
    {
        secret.Should().BeFalse("the challenge is a confirmation, not a credential");
        var match = System.Text.RegularExpressions.Regex.Match(prompt, @"Type '(Open|Close) ([0-9]{6})' to confirm: $");
        match.Success.Should().BeTrue("the typed confirmation includes its kind and a fresh displayed value");
        return $"{match.Groups[1].Value} {match.Groups[2].Value}";
    }
}
