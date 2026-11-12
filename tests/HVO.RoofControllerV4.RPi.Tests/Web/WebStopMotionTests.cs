using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Versioning;
using HVO.RoofControllerV4.Web.Components.Layout;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using static HVO.RoofControllerV4.RPi.Tests.Web.WebAlpacaSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// What the Stop bar's colour follows, on every signed-in page and not only the Roof page (#208): the layout's
/// <see cref="RoofMotionMarker"/> says whether the page's newest status shows the roof moving, and the stylesheet
/// brightens Stop from it. Here on <see cref="WebRoofHarness"/>: the controller's real API in process, with a mocked
/// roof. The colours a browser draws from it, on the emulated roof, are in <c>Browser/WebStopBrowserTests</c>.
/// </summary>
[TestClass]
public sealed class WebStopMotionTests
{
    private static readonly string WebProject = Path.Combine(ProductVersionTests.RepositoryRoot(), "src", "HVO.RoofControllerV4.Web");

    [TestMethod]
    public async Task TheMarker_IsIdleWhileTheRoofRests_AndMovingWhileItOpensOrCloses()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await using var context = Context(harness);

        var cut = context.Render<RoofMotionMarker>();

        await cut.WaitForAssertionAsync(() =>
        {
            harness.Console.View.FeedLabel.Should().Be("live");
            Motion(cut).Should().Be("idle", "the closed roof is at rest: app.css keeps Stop's dim red");
        });

        await harness.Console.OpenAsync();
        await cut.WaitForAssertionAsync(() => Motion(cut).Should().Be("moving", "the roof is opening: app.css brightens Stop"));

        harness.Push(RoofServiceMock.Snapshot(RoofControllerStatus.Stopped));
        await cut.WaitForAssertionAsync(() => Motion(cut).Should().Be("idle", "the roof stopped part way"));

        harness.Push(RoofServiceMock.Snapshot(RoofControllerStatus.Closing, RoofMotionDirection.Closing));
        await cut.WaitForAssertionAsync(() => Motion(cut).Should().Be("moving", "the roof is closing"));

        harness.Push(RoofServiceMock.Snapshot(RoofControllerStatus.Closed));
        await cut.WaitForAssertionAsync(() => Motion(cut).Should().Be("idle", "the roof reached its closed limit"));
        cut.Find("[data-testid=roof-motion]").HasAttribute("hidden").Should().BeTrue("the marker is read by the stylesheet, never shown");
    }

    [TestMethod]
    public async Task TheMarker_StaysMoving_OnAStaleStatusThatLastShowedMotion()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        await using var context = Context(harness);
        var cut = context.Render<RoofMotionMarker>();
        await cut.WaitForAssertionAsync(() => harness.Console.View.FeedLabel.Should().Be("live"));
        // Started elsewhere, so this page holds no lease whose renewals would keep its status fresh.
        harness.Push(RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening));
        await cut.WaitForAssertionAsync(() => Motion(cut).Should().Be("moving"));

        harness.Clock.Advance(ClientTestSupport.FastFeed.StaleAfter);

        await harness.WaitForAsync(view => view.FeedLabel == "STALE", "the stale status");
        await cut.WaitForAssertionAsync(() =>
        {
            harness.Console.View.IsStale.Should().BeTrue();
            Motion(cut).Should().Be("moving", "the roof may still be moving, so Stop stays bright");
        });
    }

    [TestMethod]
    public async Task ASignedOutPage_HasNoStatus_AndKeepsTheIdleMarker()
    {
        using var harness = await WebRoofHarness.CreateAsync(role: null);
        await using var context = Context(harness);

        var cut = context.Render<RoofMotionMarker>();

        await cut.WaitForAssertionAsync(() =>
        {
            harness.Console.View.IsStarted.Should().BeTrue();
            harness.Console.View.Status.Should().BeNull("a signed-out page is sent no status");
            Motion(cut).Should().Be("idle");
        });
    }

    [TestMethod]
    public async Task APageOtherThanTheRoofPage_CarriesTheMovingMarker_WhileTheRoofMoves()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await using var context = LayoutContext(harness);

        // The History page in the layout every signed-in page has: the Roof page is not on the screen.
        var cut = context.Render<MainLayout>(parameters => parameters.Add(
            layout => layout.Body,
            (RenderFragment)(builder =>
            {
                builder.OpenComponent<History>(0);
                builder.CloseComponent();
            })));

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("[data-testid=history-scope]").TextContent.Should().StartWith("Your own roof commands.");
            cut.FindAll("[data-testid=dashboard]").Should().BeEmpty();
            harness.Console.View.FeedLabel.Should().Be("live");
            Motion(cut).Should().Be("idle");
        });
        cut.FindAll("[data-roof-motion]").Should().ContainSingle("one marker decides Stop's colour");

        await harness.Console.OpenAsync();

        await cut.WaitForAssertionAsync(() =>
        {
            harness.Console.View.Status!.Status.Should().Be(RoofControllerStatus.Opening);
            Motion(cut).Should().Be("moving", "Stop is bright on the History page while the roof moves");
        });

        await harness.Session!.StopAsync(CancellationToken.None);
        harness.Push(RoofServiceMock.Snapshot(RoofControllerStatus.Stopped));

        await cut.WaitForAssertionAsync(() => Motion(cut).Should().Be("idle", "the roof stopped: Stop is dim again"));
        harness.Calls("Stop").Should().Be(1);
    }

    [TestMethod]
    public void TheStylesheet_BrightensEveryStop_FromTheMarkerAnywhereOnThePage()
    {
        var css = Regex.Replace(File.ReadAllText(Path.Combine(WebProject, "wwwroot", "css", "app.css")), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var app = File.ReadAllText(Path.Combine(WebProject, "Components", "App.razor"));

        // One rule reads the marker: Stop, hovered, focused or pressed, anywhere on a page whose marker says the roof
        // moves. Nothing narrows it to the Roof page.
        var rule = Regex.Matches(css, @"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}")
            .Where(candidate => candidate.Groups["selectors"].Value.Contains("data-roof-motion", StringComparison.Ordinal))
            .Should().ContainSingle("one rule brightens Stop from the page's marker").Which;
        rule.Groups["selectors"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Should().BeEquivalentTo(
            [
                "body:has([data-roof-motion=\"moving\"]) .web-stop-action",
                "body:has([data-roof-motion=\"moving\"]) .web-stop-action:hover",
                "body:has([data-roof-motion=\"moving\"]) .web-stop-action:focus",
                "body:has([data-roof-motion=\"moving\"]) .web-stop-action:active"
            ]);
        // RoofUiPalette's StopButtonActive trio: dark text on the bright danger tone.
        Regex.Replace(rule.Groups["body"].Value, @"\s+", " ").Should()
            .Contain("color: var(--roof-background) !important")
            .And.Contain("background: var(--roof-danger) !important")
            .And.Contain("border: 1px solid var(--roof-danger-text) !important");

        // Both Stop buttons take it (the bar's and the reconnect dialog's), and the layout's marker is the only one.
        Regex.Matches(app, @"<button class=""btn web-stop-action[ ""]").Should().HaveCount(2);
        File.ReadAllText(Path.Combine(WebProject, "Components", "Layout", "MainLayout.razor")).Should().Contain("<RoofMotionMarker />");
        File.ReadAllText(Path.Combine(WebProject, "Components", "Pages", "Dashboard.razor")).Should().NotContain("data-roof-motion");
    }

    private static string? Motion<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent
        => cut.Find("[data-testid=roof-motion]").GetAttribute("data-roof-motion");

    // The roof page's context, with what the layout and a page that talks to the controller use besides.
    private static BunitContext LayoutContext(WebRoofHarness harness)
    {
        var context = Context(harness);
        var session = harness.Session!;
        var authorization = context.AddAuthorization();
        authorization.SetAuthorized(session.Name);
        authorization.SetRoles([.. WebRoles.Implied(session.Role)]);
        authorization.SetClaims(new Claim(WebAuthentication.SessionIdClaimType, session.Id));
        context.Services.AddSingleton(harness.Connector);
        context.Services.AddSingleton(harness.Accessor);
        context.Services.AddSingleton<TimeProvider>(harness.Clock);
        return context;
    }
}
