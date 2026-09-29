using System.Net;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Roof;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The sign-in page's mode banner (review finding 5 on #58): the controller's anonymous mode read, shared by every page
/// and asked again only after the status refresh; nothing is claimed while the controller has not said.
/// </summary>
[TestClass]
public sealed class WebControllerModeTests
{
    [TestMethod]
    public async Task SignInPage_ShowsTheModeBanner_WhileTheRoofIsNotDrivenAsInNormalUse()
    {
        var controller = new FakeController { Mode = new RoofModeResponse(RoofHatMode.Simulation, true) };
        await using var host = await WebHost.StartAsync(controller: controller);
        using var browser = host.Browser();

        using var response = await browser.GetAsync("/signin");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("data-testid=\"mode-banner\"")
            .And.Contain(RoofStatusText.Simulation)
            .And.Contain(RoofStatusText.LimitsIgnored);
        controller.Logged(HttpMethod.Get, RoofApiRoutesTest.Mode).Should().ContainSingle()
            .Which.Should().Match<FakeControllerRequest>(request => request.Authorization == null && !request.HasApiKey, "nobody has signed in");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "The observatory roof, in normal use")]
    [DataRow(true, DisplayName = "A controller that does not say")]
    public async Task SignInPage_HasNoModeBanner_InNormalUse_OrWhenTheControllerDoesNotSay(bool silent)
    {
        var controller = new FakeController { Mode = silent ? null : new RoofModeResponse(RoofHatMode.Physical, false) };
        if (silent)
        {
            controller.OtherAnswer = request => request.RequestUri!.AbsolutePath == "/" + RoofApiRoutesTest.Mode
                ? FakeController.Problem(HttpStatusCode.ServiceUnavailable, null)
                : null;
        }

        await using var host = await WebHost.StartAsync(controller: controller);
        using var browser = host.Browser();

        using var response = await browser.GetAsync("/signin");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("data-testid=\"sign-in\"").And.NotContain("mode-banner");
        controller.Logged(HttpMethod.Get, RoofApiRoutesTest.Mode).Should().ContainSingle();
    }

    [TestMethod]
    public async Task SignInPage_AndItsStop_AreSent_WhileTheControllerHasNotAnswered()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new FakeController
        {
            Mode = new RoofModeResponse(RoofHatMode.Simulation, false),
            Hold = request => request.RequestUri!.AbsolutePath == "/" + RoofApiRoutesTest.Mode ? answer.Task : Task.CompletedTask,
        };
        await using var host = await WebHost.StartAsync(controller: controller);
        using var browser = host.Browser();

        using var response = await browser.OpenStreamAsync("/signin").WaitAsync(TimeSpan.FromSeconds(10));
        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);
        var html = new StringBuilder();
        var buffer = new char[4096];
        while (!html.ToString().Contains("data-testid=\"sign-in\"", StringComparison.Ordinal))
        {
            var read = await reader.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            read.Should().BePositive("the page is sent before the controller answers");
            html.Append(buffer, 0, read);
        }

        html.ToString().Should().Contain("data-web-stop", "Stop is on the page as soon as it arrives").And.NotContain("mode-banner");
        answer.SetResult();
        html.Append(await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        html.ToString().Should().Contain(RoofStatusText.Simulation, "the banner follows when the controller answers");
    }

    [TestMethod]
    public async Task Pages_ShareOneRead_UntilTheStatusRefresh()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var controller = new FakeController(clock) { Mode = new RoofModeResponse(RoofHatMode.Emulated, false) };
        var mode = Create(controller, clock, refreshSeconds: 2);

        var reads = await Task.WhenAll(mode.ReadAsync(), mode.ReadAsync(), mode.ReadAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        var stillShared = await mode.ReadAsync();

        reads.Should().AllBeEquivalentTo(new RoofModeResponse(RoofHatMode.Emulated, false));
        stillShared.Should().Be(reads[0]);
        controller.Logged(HttpMethod.Get, RoofApiRoutesTest.Mode).Should().ContainSingle("pages share one read");

        controller.Mode = new RoofModeResponse(RoofHatMode.Physical, false);
        clock.Advance(TimeSpan.FromSeconds(1));

        (await mode.ReadAsync()).Should().Be(new RoofModeResponse(RoofHatMode.Physical, false), "a restarted controller may run another way");
        controller.Logged(HttpMethod.Get, RoofApiRoutesTest.Mode).Should().HaveCount(2);
    }

    [TestMethod]
    public async Task AControllerThatStopsAnswering_IsNotTakenAtItsLastWord()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var controller = new FakeController(clock) { Mode = new RoofModeResponse(RoofHatMode.Emulated, false) };
        var mode = Create(controller, clock, refreshSeconds: 2);
        (await mode.ReadAsync()).Should().NotBeNull();

        controller.OtherAnswer = _ => throw new HttpRequestException("Connection refused (test).");
        controller.Mode = null;
        clock.Advance(TimeSpan.FromSeconds(2));

        (await mode.ReadAsync()).Should().BeNull("the page claims nothing it was not told");
    }

    [TestMethod]
    public async Task AReadThatFailsUnexpectedly_ClaimsNothing_AndIsLogged_SoNoSignInPageFails()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var controller = new FakeController(clock) { OtherAnswer = _ => throw new InvalidOperationException("unexpected (test)"), Mode = null };
        var logger = new CapturingLogger<WebControllerMode>();

        (await Create(controller, clock, refreshSeconds: 2, logger).ReadAsync()).Should().BeNull("every sign-in page awaits this read");
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException);
    }

    private static WebControllerMode Create(FakeController controller, TimeProvider clock, int refreshSeconds, ILogger<WebControllerMode>? logger = null)
    {
        var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost:8080"),
            CreateHandler = controller.CreateHandler,
            RequestTimeout = TimeSpan.FromSeconds(5),
        });
        return new WebControllerMode(client, Options.Create(new RoofWebOptions { StatusRefreshSeconds = refreshSeconds }), clock, logger ?? NullLogger<WebControllerMode>.Instance);
    }
}
