using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Supervision;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The controller's status on the sign-in page: nobody can sign in while the controller is stopped (sign-in goes through
/// it), so the page says why, as the Health page's headline (C11 step 7, <c>deploy-scenarios.sh supervisor</c>). Pages
/// share one check until the status refresh.
/// </summary>
[TestClass]
public sealed class WebControllerStatusTests
{
    [TestMethod]
    public async Task SignInPage_SaysTheControllerIsStopped_AfterRepeatedCrashes_WithoutTheDetails()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var statePath = directory.File("supervisor.json");
        await File.WriteAllTextAsync(statePath, WebTestSupport.SupervisorState("crash-loop", recentCrashes: 5));
        var controller = new FakeController { OtherAnswer = _ => throw new HttpRequestException("Connection refused (localhost:8080)") };
        await using var host = await WebHost.StartAsync(["--RoofWeb:SupervisorStatePath=" + statePath], controller);
        using var browser = host.Browser();

        using var response = await browser.GetAsync("/signin");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("data-testid=\"controller-status\" data-level=\"Bad\">The controller is stopped after repeated crashes</p>")
            .And.Contain("data-testid=\"sign-in\"")
            .And.NotContain("localhost:8080", "the details are for people signed in")
            .And.NotContain("Last exit", "the details are for people signed in")
            .And.NotContain("force-restart-controller", "the details are for people signed in");
    }

    [TestMethod]
    public async Task SignInPage_SaysTheControllerIsReady_WhenItIs()
    {
        await using var host = await WebHost.StartAsync();
        using var browser = host.Browser();

        using var response = await browser.GetAsync("/signin");
        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("data-testid=\"controller-status\" data-level=\"Good\">The controller is ready</p>");
        host.Controller.Logged(HttpMethod.Get, RoofApiRoutes.HealthReady).Should().ContainSingle()
            .Which.Should().Match<FakeControllerRequest>(request => request.Authorization == null && !request.HasApiKey, "nobody has signed in");
    }

    [TestMethod]
    public async Task Pages_ShareOneCheck_UntilTheStatusRefresh()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var controller = new FakeController(clock);
        var status = Create(controller, clock, refreshSeconds: 2);

        var reads = await Task.WhenAll(status.ReadAsync(), status.ReadAsync(), status.ReadAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        var stillShared = await status.ReadAsync();

        reads.Should().AllSatisfy(view => view!.Headline.Should().Be("The controller is ready"));
        stillShared.Should().BeSameAs(reads[0]);
        controller.Logged(HttpMethod.Get, RoofApiRoutes.HealthReady).Should().ContainSingle("pages share one check");

        controller.OtherAnswer = _ => throw new HttpRequestException("Connection refused (test).");
        clock.Advance(TimeSpan.FromSeconds(1));

        var later = await status.ReadAsync();
        later.Should().BeEquivalentTo(new RoofWebStatusView(RoofWebStatusLevel.Bad, "The controller is not answering", []), "an old answer is not kept");
        controller.Logged(HttpMethod.Get, RoofApiRoutes.HealthReady).Should().HaveCount(2);
    }

    [TestMethod]
    public async Task ACheckThatFails_ClaimsNothing()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var controller = new FakeController(clock) { OtherAnswer = _ => throw new OperationCanceledException("test") };

        (await Create(controller, clock, refreshSeconds: 2).ReadAsync()).Should().BeNull("the page claims nothing it was not told");
    }

    [TestMethod]
    public async Task ACheckThatFailsUnexpectedly_ClaimsNothing_AndIsLogged_SoNoSignInPageFails()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var controller = new FakeController(clock) { OtherAnswer = _ => throw new InvalidOperationException("unexpected (test)") };
        var logger = new CapturingLogger<WebControllerStatus>();

        (await Create(controller, clock, refreshSeconds: 2, logger).ReadAsync()).Should().BeNull("every sign-in page awaits this check");
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException);
    }

    private static WebControllerStatus Create(FakeController controller, TimeProvider clock, int refreshSeconds, ILogger<WebControllerStatus>? logger = null)
    {
        var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost:8080"),
            CreateHandler = controller.CreateHandler,
            RequestTimeout = TimeSpan.FromSeconds(5),
        });
        var options = new RoofWebOptions { StatusRefreshSeconds = refreshSeconds };
        var probe = new RoofWebStatusProbe(client, new SupervisorStateReader(WebTestSupport.Monitor(options)), clock);
        return new WebControllerStatus(probe, Options.Create(options), clock, logger ?? NullLogger<WebControllerStatus>.Instance);
    }
}
