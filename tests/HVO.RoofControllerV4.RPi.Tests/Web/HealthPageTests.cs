using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Sessions;
using HVO.RoofControllerV4.Web.Supervision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The health page checks the controller again every RoofWeb:StatusRefreshSeconds and stops when it is closed. It reads
/// the controller's health checks with the person's session, the worst first.
/// </summary>
[TestClass]
public sealed class HealthPageTests
{
    [TestMethod]
    public async Task Health_ChecksAgain_EveryRefreshInterval()
    {
        var answer = HttpStatusCode.ServiceUnavailable;
        var handler = new WebTestSupport.StubHandler(_ => WebTestSupport.Text(answer, answer == HttpStatusCode.OK ? "Healthy" : "Unhealthy"));
        using var fixture = new WebSessionStoreTests.Fixture();
        await using var context = Context(handler, fixture, session: null, out var time);

        var cut = context.Render<Health>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=controller-status] h2").TextContent.Should().Be("The controller is running but not ready"));
        cut.Find("[data-testid=controller-status]").GetAttribute("data-level").Should().Be("Warning");
        cut.Find(".web-checked").TextContent.Should().Be("Checked at 2026-09-29 00:00:00Z, every 5 s.");

        answer = HttpStatusCode.OK;
        time.Advance(TimeSpan.FromSeconds(4));
        cut.Find("[data-testid=controller-status] h2").TextContent.Should().Be("The controller is running but not ready", "the interval has not passed");

        time.Advance(TimeSpan.FromSeconds(1));
        cut.WaitForAssertion(() => cut.Find("[data-testid=controller-status] h2").TextContent.Should().Be("The controller is ready"));
        cut.Find(".web-checked").TextContent.Should().Be("Checked at 2026-09-29 00:00:05Z, every 5 s.");
    }

    [TestMethod]
    public async Task Health_WhenClosed_StopsChecking()
    {
        var handler = new WebTestSupport.StubHandler(_ => WebTestSupport.Text(HttpStatusCode.OK, "Healthy"));
        using var fixture = new WebSessionStoreTests.Fixture();
        await using var context = Context(handler, fixture, session: null, out var time);
        var cut = context.Render<Health>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=controller-status] h2").TextContent.Should().Be("The controller is ready"));
        time.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => handler.Requests.Should().HaveCount(2));

        await cut.Instance.DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(20));
        await Task.Delay(100);

        handler.Requests.Should().HaveCount(2, "only the probe counts here: the health checks go through the person's session");
    }

    [TestMethod]
    public async Task SignedOut_TheHealthChecks_AreNotRead_AndThePageSaysWhy()
    {
        var handler = new WebTestSupport.StubHandler(_ => WebTestSupport.Text(HttpStatusCode.OK, "Healthy"));
        using var fixture = new WebSessionStoreTests.Fixture();
        await using var context = Context(handler, fixture, session: null, out _);

        var cut = context.Render<Health>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=health-report-error]").TextContent
            .Should().Be("You are signed out. Sign in again to read the controller's health checks."));
        fixture.Controller.Logged(HttpMethod.Get, "health").Should().BeEmpty();
    }

    [TestMethod]
    public async Task SignedIn_TheHealthChecks_AreRead_WithTheSession_TheWorstFirst()
    {
        var handler = new WebTestSupport.StubHandler(_ => WebTestSupport.Text(HttpStatusCode.OK, "Healthy"));
        using var fixture = new WebSessionStoreTests.Fixture();
        fixture.Controller.OtherAnswer = request => request.RequestUri!.AbsolutePath == "/health"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent.Create(new HealthReportPayload
                {
                    Status = "Unhealthy",
                    TotalDuration = "00:00:00.0120000",
                    Checks =
                    [
                        new HealthCheckEntry { Name = "roof", Status = "Healthy", Description = "The roof controller is running." },
                        new HealthCheckEntry { Name = "hat", Status = "Unhealthy", Description = "The HAT does not answer." },
                        new HealthCheckEntry { Name = "inputs", Status = "Degraded" },
                        new HealthCheckEntry { Name = "clock", Status = "Healthy" },
                    ],
                }),
            }
            : null;
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "vic", RoofControllerApiContract.ViewerRole));
        await using var context = Context(handler, fixture, session, out _);

        var cut = context.Render<Health>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=health-overall]").TextContent
            .Should().Be("Overall: Unhealthy, in 00:00:00.0120000. Read at 2026-09-29 00:00:00Z."));
        cut.FindAll("[data-testid=health-check]").Select(Describe).ToList().Should().Equal(
            "Unhealthy | hat | The HAT does not answer.",
            "Degraded | inputs | ",
            "Healthy | clock | ",
            "Healthy | roof | The roof controller is running.");
        cut.FindAll("[data-testid=health-check] .badge").Select(badge => badge.ClassName).ToList().Should().Equal(
            "badge bg-danger", "badge bg-warning text-dark", "badge bg-success", "badge bg-success");
        fixture.Controller.Logged(HttpMethod.Get, "health").Should().ContainSingle()
            .Which.Authorization.Should().Be("Bearer token-vic");
    }

    [TestMethod]
    public async Task HealthChecksThatCannotBeRead_SaySo_AndRefreshReadsThemAgain()
    {
        var handler = new WebTestSupport.StubHandler(_ => WebTestSupport.Text(HttpStatusCode.OK, "Healthy"));
        using var fixture = new WebSessionStoreTests.Fixture();
        var refuse = true;
        fixture.Controller.OtherAnswer = request => request.RequestUri!.AbsolutePath != "/health" ? null
            : refuse ? FakeController.Problem(HttpStatusCode.InternalServerError, RoofControllerErrorCode.Unknown)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new HealthReportPayload { Status = "Healthy" }) };
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "vic", RoofControllerApiContract.ViewerRole));
        await using var context = Context(handler, fixture, session, out _);

        var cut = context.Render<Health>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=health-report-error]").TextContent.Should().StartWith("The health checks could not be read: "));
        refuse = false;
        cut.Find("[data-testid=health-refresh]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=health-overall]").TextContent.Should().StartWith("Overall: Healthy. Read at "));
        cut.FindAll("[data-testid=health-report-error]").Should().BeEmpty();
        cut.Markup.Should().Contain("The controller reported no checks.");
    }

    private static string Describe(AngleSharp.Dom.IElement check)
        => $"{check.QuerySelector(".badge")!.TextContent} | {check.QuerySelector("strong")!.TextContent} | {check.QuerySelector(".web-health-description")?.TextContent}";

    private static BunitContext Context(HttpMessageHandler handler, WebSessionStoreTests.Fixture fixture, WebSession? session, out ManualTimeProvider time)
    {
        time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero));
        var options = new RoofWebOptions { StatusRefreshSeconds = 5 };
        var client = new HVO.RoofControllerV4.Client.RoofControllerClient(new HVO.RoofControllerV4.Client.RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost:8080"),
            CreateHandler = () => handler,
        });
        var context = new BunitContext();
        var authorization = context.AddAuthorization();
        if (session is not null)
        {
            authorization.SetAuthorized(session.Name);
            authorization.SetClaims(new Claim(WebAuthentication.SessionIdClaimType, session.Id));
        }

        context.Services.AddSingleton<TimeProvider>(time);
        context.Services.AddSingleton(Options.Create(options));
        context.Services.AddSingleton(new RoofWebStatusProbe(client, new SupervisorStateReader(WebTestSupport.Monitor(options)), time));
        context.Services.AddSingleton(fixture.Store);
        context.Services.AddScoped<WebSessionAccessor>();
        context.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        context.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        return context;
    }
}
