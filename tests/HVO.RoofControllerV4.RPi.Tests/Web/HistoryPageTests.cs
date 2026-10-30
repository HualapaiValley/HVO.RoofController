using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The History page (#206) against the controller's real API in process: everyone's commands for an admin, with a
/// person or key to pick; a person's own otherwise; the days to show; and the pages of a long history.
/// </summary>
[TestClass]
public sealed class HistoryPageTests
{
    [TestMethod]
    public async Task AnAdmin_ReadsEveryonesCommands_AndPicksOnePerson()
    {
        using var harness = new WebAdminHarness();
        var ada = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        var olive = await harness.SignInAsync("olive", RoofControllerApiContract.OperatorRole);
        await olive.Client.Roof.OpenAsync();
        await ada.Client.StopAsync();
        await using var context = harness.Context(ada);

        var cut = context.Render<History>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=history-entry]").Should().HaveCount(2));

        cut.Find("[data-testid=history-scope]").TextContent.Should().StartWith("Everyone's roof commands (admin).");
        Rows(cut).Should().BeEquivalentTo(
            new[]
            {
                new[] { "2026-09-29 12:00:00Z", "Stop", "ada (signed in)", "Accepted", "Closed" },
                new[] { "2026-09-29 12:00:00Z", "Open", "olive (signed in)", "Accepted (conditions unknown)", "Closed" }
            },
            options => options.WithStrictOrdering());
        cut.Find("[data-testid=history-range]").TextContent.Should().Be("1–2 of 2 commands.");

        cut.Change("[data-testid=history-user]", "olive");
        cut.Find("[data-testid=history-filters]").Submit();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=history-entry]").Should().ContainSingle());
        Rows(cut).Single()[2].Should().Be("olive (signed in)");
    }

    [TestMethod]
    public async Task AnOperator_ReadsOnlyTheirOwn_WithNoOneElseToPick()
    {
        using var harness = new WebAdminHarness();
        var ada = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        var olive = await harness.SignInAsync("olive", RoofControllerApiContract.OperatorRole);
        await ada.Client.Roof.CloseAsync();
        await olive.Client.StopAsync();
        await using var context = harness.Context(olive);

        var cut = context.Render<History>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=history-entry]").Should().ContainSingle());

        cut.Find("[data-testid=history-scope]").TextContent.Should().StartWith("Your own roof commands.");
        Rows(cut).Single()[1].Should().Be("Stop");
        cut.FindAll("[data-testid=history-user]").Should().BeEmpty("only an admin reads someone else's commands");
    }

    [TestMethod]
    public async Task NoCommands_IsSaid_AndSoIsNoneOnTheDaysChosen()
    {
        using var harness = new WebAdminHarness();
        var ada = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(ada);

        var cut = context.Render<History>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=history-empty]").TextContent.Should().Be(RoofHistoryText.Empty));
        cut.Find("[data-testid=history-scope]").TextContent.Should().Contain("It keeps them in memory only");

        await ada.Client.StopAsync();
        cut.Change("[data-testid=history-from]", "2026-09-30");
        cut.Find("[data-testid=history-filters]").Submit();

        cut.WaitForAssertion(() => cut.Find("[data-testid=history-empty]").TextContent.Should().Be(RoofHistoryText.EmptyFiltered));
    }

    [TestMethod]
    public async Task TheDays_AreWholeUtcDays_AndToBeforeFromIsNotSent()
    {
        using var harness = new WebAdminHarness();
        var ada = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await ada.Client.StopAsync();
        await using var context = harness.Context(ada);
        var cut = context.Render<History>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=history-entry]").Should().ContainSingle());

        cut.Change("[data-testid=history-from]", "2026-09-29");
        cut.Change("[data-testid=history-to]", "2026-09-29");
        cut.Find("[data-testid=history-filters]").Submit();
        cut.WaitForAssertion(() => cut.Find("[data-testid=history-range]").TextContent.Should().Be("1–1 of 1 command."));

        cut.Change("[data-testid=history-from]", "2026-09-30");
        cut.Find("[data-testid=history-filters]").Submit();

        cut.WaitForAssertion(() => cut.Find("[data-testid=page-message]").TextContent.Should().Be("The To date is before the From date."));
        cut.FindAll("[data-testid=history-entry]").Should().ContainSingle("nothing was sent");
    }

    [TestMethod]
    public async Task ALongHistory_IsReadAPageAtATime()
    {
        using var harness = new WebAdminHarness();
        var ada = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        using var operatorClient = Client.ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Operator));
        for (var i = 0; i < History.PageSize + 5; i++)
        {
            await operatorClient.StopAsync();
        }

        await using var context = harness.Context(ada);
        var cut = context.Render<History>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=history-entry]").Should().HaveCount(History.PageSize));
        cut.Find("[data-testid=history-newer]").HasAttribute("disabled").Should().BeTrue();

        cut.Click("[data-testid=history-older]");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=history-entry]").Should().HaveCount(5));
        cut.Find("[data-testid=history-range]").TextContent.Should().Be("26–30 of 30 commands.");
        cut.Find("[data-testid=history-older]").HasAttribute("disabled").Should().BeTrue();
        cut.Click("[data-testid=history-newer]");
        cut.WaitForAssertion(() => cut.Find("[data-testid=history-range]").TextContent.Should().Be("1–25 of 30 commands."));
    }

    [TestMethod]
    public async Task AFailedRead_IsSaid()
    {
        using var harness = new WebAdminHarness();
        var ada = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        harness.LoseAnswer = request => request.RequestUri!.AbsolutePath.EndsWith("/History", StringComparison.Ordinal);
        await using var context = harness.Context(ada);

        var cut = context.Render<History>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=page-message]").TextContent
            .Should().StartWith("The roof command history could not be read. " + RoofText.Unreachable));
        cut.Find("[data-testid=history-loading]").TextContent.Should().Be("The roof command history is not read.");
    }

    [TestMethod]
    public void TheWebUi_NamesItselfInItsCommands_HoweverItIsHosted()
    {
        var connector = new RoofControllerConnector(Options.Create(new RoofWebOptions()), NullLoggerFactory.Instance, TimeProvider.System);

        using var client = connector.Create(null, TimeSpan.FromSeconds(5));

        client.Options.ProgramAssembly.Should().BeSameAs(typeof(RoofWebOptions).Assembly);
        RoofHistoryText.DescribeClient(RoofUserAgent.Create(client.Options.ProgramAssembly).ToString()).Should().Be("web UI");
    }

    private static List<string[]> Rows(IRenderedComponent<History> cut)
        => cut.FindAll("[data-testid=history-entry]")
            .Select(row => row.QuerySelectorAll("td").Take(5).Select(cell => cell.TextContent).ToArray())
            .ToList();
}
