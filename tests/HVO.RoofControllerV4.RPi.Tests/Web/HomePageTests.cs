using System.Net;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Supervision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>The status page checks again every RoofWeb:StatusRefreshSeconds, and stops when it is closed.</summary>
[TestClass]
public sealed class HomePageTests
{
    [TestMethod]
    public async Task Home_ChecksAgain_EveryRefreshInterval()
    {
        var answer = HttpStatusCode.ServiceUnavailable;
        var handler = new WebTestSupport.StubHandler(_ => WebTestSupport.Text(answer, answer == HttpStatusCode.OK ? "Healthy" : "Unhealthy"));
        await using var context = Context(handler, out var time);

        var cut = context.Render<Home>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=controller-status] h2").TextContent.Should().Be("The controller is running but not ready"));
        cut.Find("[data-testid=controller-status]").GetAttribute("data-level").Should().Be("Warning");
        cut.Find(".web-checked").TextContent.Should().Be("Checked at 00:00:00 UTC, every 5 s.");

        answer = HttpStatusCode.OK;
        time.Advance(TimeSpan.FromSeconds(4));
        cut.Find("[data-testid=controller-status] h2").TextContent.Should().Be("The controller is running but not ready", "the interval has not passed");

        time.Advance(TimeSpan.FromSeconds(1));
        cut.WaitForAssertion(() => cut.Find("[data-testid=controller-status] h2").TextContent.Should().Be("The controller is ready"));
        cut.Find(".web-checked").TextContent.Should().Be("Checked at 00:00:05 UTC, every 5 s.");
    }

    [TestMethod]
    public async Task Home_WhenClosed_StopsChecking()
    {
        var handler = new WebTestSupport.StubHandler(_ => WebTestSupport.Text(HttpStatusCode.OK, "Healthy"));
        await using var context = Context(handler, out var time);
        var cut = context.Render<Home>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=controller-status] h2").TextContent.Should().Be("The controller is ready"));
        time.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => handler.Requests.Should().HaveCount(2));

        await cut.Instance.DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(20));
        await Task.Delay(100);

        handler.Requests.Should().HaveCount(2);
    }

    private static BunitContext Context(HttpMessageHandler handler, out ManualTimeProvider time)
    {
        time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero));
        var options = new RoofWebOptions { StatusRefreshSeconds = 5 };
        var client = new HVO.RoofControllerV4.Client.RoofControllerClient(new HVO.RoofControllerV4.Client.RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost:8080"),
            CreateHandler = () => handler,
        });
        var context = new BunitContext();
        context.Services.AddSingleton<TimeProvider>(time);
        context.Services.AddSingleton(Options.Create(options));
        context.Services.AddSingleton(new RoofWebStatusProbe(client, new SupervisorStateReader(WebTestSupport.Monitor(options)), time));
        context.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        context.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        return context;
    }
}
