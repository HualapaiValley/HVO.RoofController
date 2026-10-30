using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// <c>hvo-roof history</c> against the real API, in process (#206): the table and the JSON, the filters it sends, and
/// what it refuses before sending anything.
/// </summary>
[TestClass]
public sealed class RoofCliHistoryCommandTests
{
    [TestMethod]
    public async Task History_ShowsTheCommandsNewestFirst_WithWhoSentThemAndTheRoofAfter()
    {
        using var host = RoofClientApiTests.CreateHost();
        await SendAsync(host, TestApiKeys.Operator, client => client.Roof.OpenAsync());
        await SendAsync(host, TestApiKeys.Viewer, client => client.StopAsync());
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("history");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        var lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        lines[0].Should().Be("Everyone's roof commands (admin).");
        lines[1].Should().MatchRegex("^TIME +COMMAND +BY +RESULT +ROOF AFTER +FROM$");
        lines[2].Should().MatchRegex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\dZ +Stop +test-viewer \(API key\) +Accepted +Closed +");
        lines[3].Should().MatchRegex(@"Open +test-operator \(API key\) +Accepted \(conditions unknown\) +Closed +", "no safety source is configured, so the conditions are not known");
        lines[4].Should().Be("1–2 of 2 commands.");
        lines[5].Should().Be("The controller keeps commands for 90 days, up to 10,000. It keeps them in memory only, so a restart loses them.");
    }

    [TestMethod]
    public async Task History_Json_IsThePage()
    {
        using var host = RoofClientApiTests.CreateHost();
        await SendAsync(host, TestApiKeys.Operator, client => client.Roof.CloseAsync());
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("history", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("allUsers").GetBoolean().Should().BeFalse();
        result.Json.GetProperty("totalCount").GetInt32().Should().Be(1);
        var entry = result.Json.GetProperty("entries")[0];
        entry.GetProperty("command").GetString().Should().Be("Close");
        entry.GetProperty("user").GetString().Should().Be("test-operator");
        entry.GetProperty("accepted").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task History_SendsTheUserTheTimesAndThePage()
    {
        using var host = RoofClientApiTests.CreateHost();
        for (var i = 0; i < 3; i++)
        {
            await SendAsync(host, TestApiKeys.Operator, client => client.StopAsync());
        }

        await SendAsync(host, TestApiKeys.Viewer, client => client.StopAsync());
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var mine = await rig.RunAsync("history", "--user", "test-operator", "--since", "1d", "--take", "2", "--skip", "1", "--json");
        var none = await rig.RunAsync("history", "--since", DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd"));

        mine.Code.Should().Be(RoofExitCode.Success, mine.ToString());
        mine.Json.GetProperty("totalCount").GetInt32().Should().Be(3);
        mine.Json.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("id").GetInt64()).Should().Equal(2, 1);
        none.Code.Should().Be(RoofExitCode.Success, none.ToString());
        none.Out.Should().Contain(RoofHistoryText.EmptyFiltered);
    }

    [TestMethod]
    public async Task History_ShowsTheSafetyInterlocksRefusal_AndAnOpenOnASafetyOverride()
    {
        using var host = RoofClientApiTests.CreateHost(settings: new Dictionary<string, string?> { ["RoofSafety:InterlockMode"] = "Warn" });
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        (await rig.RunAsync("open", "--no-wait")).Code.Should().Be(RoofExitCode.Refused);
        (await rig.RunAsync("open", "--no-wait", "--override-safety")).Code.Should().Be(RoofExitCode.Success);
        var result = await rig.RunAsync("history");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        var rows = result.Out.Split('\n').Where(line => line.Contains(" Open ", StringComparison.Ordinal)).ToArray();
        rows.Should().HaveCount(2);
        rows[0].Should().Contain("Accepted (safety override, conditions unknown)");
        rows[1].Should().Contain("Refused [SafetyOverrideRequired]");
    }

    [TestMethod]
    public async Task History_OfSomeoneElse_IsForbiddenToAnyoneButAnAdmin()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("history", "--user", "ada");

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
    }

    [TestMethod]
    [DataRow("--take", "0")]
    [DataRow("--take", "501")]
    [DataRow("--skip", "-1")]
    [DataRow("--since", "yesterday")]
    [DataRow("--until", "12x")]
    public async Task History_RefusesAValueItCanCheck_BeforeSendingAnything(string option, string value)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("history", option, value);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
    }

    [TestMethod]
    public async Task History_RefusesUntilBeforeSince()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("history", "--since", "2026-10-30", "--until", "2026-10-29");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("--until is before --since.");
    }

    [TestMethod]
    public void HistoryTimes_AreDatesTimesOrHowLongAgo()
    {
        var now = new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.Zero);

        RoofCli.CommandBuilder.ParseHistoryTime("30m", now, "--since").Should().Be(now.AddMinutes(-30));
        RoofCli.CommandBuilder.ParseHistoryTime("12H", now, "--since").Should().Be(now.AddHours(-12));
        RoofCli.CommandBuilder.ParseHistoryTime("7d", now, "--since").Should().Be(now.AddDays(-7));
        RoofCli.CommandBuilder.ParseHistoryTime("2026-10-29", now, "--since").Should().Be(new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero), "a date is UTC");
        RoofCli.CommandBuilder.ParseHistoryTime("2026-10-29T08:00-07:00", now, "--since").Should().Be(new DateTimeOffset(2026, 10, 29, 15, 0, 0, TimeSpan.Zero));
        var wrong = () => RoofCli.CommandBuilder.ParseHistoryTime("soon", now, "--until");
        wrong.Should().Throw<RoofCliUsageException>().WithMessage("--until 'soon' is not a time.*");
    }

    private static async Task SendAsync(RoofApiTestHost host, string key, Func<RoofControllerClient, Task> send)
    {
        using var client = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(key));
        await send(client);
    }
}
