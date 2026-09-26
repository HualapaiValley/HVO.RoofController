using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Controllers;
using HVO.RoofControllerV4.RPi.Controllers.Camera;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// Camera proxy: credentials come from configuration and are only sent upstream, callers need a Viewer key, the console
/// cookie or a short-lived ticket, and upstream failures map to 502/503/504 instead of hanging.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CameraControllerTests
{
    private const string FakeBlueIris = "http://blueiris.test";
    private static readonly byte[] FakeFrames = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\n\r\nFAKEJPEG\r\n");

    [TestMethod]
    public async Task Ticket_Viewer_ReturnsRelativeStreamUrlValidFor60Seconds()
    {
        using var host = CreateHost(Upstream.Frames());
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.PostAsync("/api/v1.0/Camera/3/ticket", content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var ticket = await ApiJson.ReadAsync<CameraStreamTicketResponse>(response);
        ticket.Url.Should().StartWith("/api/v1.0/Camera/3/mjpeg?ticket=");
        ticket.ExpiresUtc.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(60), TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Ticket_WithoutKey_Returns401()
    {
        using var host = CreateHost(Upstream.Frames());
        using var client = host.CreateApiClient();

        var response = await client.PostAsync("/api/v1.0/Camera/3/ticket", content: null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(100)]
    public async Task Ticket_CameraOutOfRange_Returns404(int cameraId)
    {
        using var host = CreateHost(Upstream.Frames());
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.PostAsync($"/api/v1.0/Camera/{cameraId}/ticket", content: null);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Mjpeg_Anonymous_Returns401AndNeverCallsUpstream()
    {
        var upstream = Upstream.Frames();
        using var host = CreateHost(upstream);
        using var client = host.CreateApiClient();

        var response = await client.GetAsync("/api/v1.0/Camera/3/mjpeg");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        upstream.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Mjpeg_WithTicket_ProxiesUpstreamWithoutCredentialsWhenNoneConfigured()
    {
        var upstream = Upstream.Frames();
        using var host = CreateHost(upstream);
        using var issuer = host.CreateApiClient(TestApiKeys.Viewer);
        var ticket = await ApiJson.ReadAsync<CameraStreamTicketResponse>(await issuer.PostAsync("/api/v1.0/Camera/3/ticket", content: null));
        using var viewer = host.CreateApiClient();

        var response = await viewer.GetAsync(ticket.Url);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("multipart/x-mixed-replace", response.Content.Headers.ContentType?.MediaType);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(FakeFrames);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        var request = upstream.Requests.Should().ContainSingle().Subject;
        Assert.AreEqual("blueiris.test", request.Uri?.Host);
        Assert.AreEqual("/mjpg/cam03/video.mjpg", request.Uri?.AbsolutePath);
        Assert.IsNull(request.Authorization);
    }

    [TestMethod]
    public async Task Mjpeg_WithConfiguredCredentials_SendsBasicAuthUpstreamOnly()
    {
        var upstream = Upstream.Frames();
        using var host = CreateHost(upstream, new Dictionary<string, string?>
        {
            ["BlueIris:UserName"] = "test-user",
            ["BlueIris:Password"] = "test-password"
        });
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync("/api/v1.0/Camera/1/mjpeg");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var authorization = upstream.Requests.Should().ContainSingle().Subject.Authorization;
        Assert.IsNotNull(authorization);
        Assert.AreEqual("Basic", authorization.Scheme);
        Assert.AreEqual("test-user:test-password", Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Parameter!)));
        response.Headers.Contains("Authorization").Should().BeFalse();
        (await response.Content.ReadAsStringAsync()).Should().NotContain("test-password");
    }

    [TestMethod]
    public async Task Mjpeg_TicketForAnotherCamera_Returns401()
    {
        var upstream = Upstream.Frames();
        using var host = CreateHost(upstream);
        using var issuer = host.CreateApiClient(TestApiKeys.Viewer);
        var ticket = await ApiJson.ReadAsync<CameraStreamTicketResponse>(await issuer.PostAsync("/api/v1.0/Camera/3/ticket", content: null));
        using var viewer = host.CreateApiClient();

        var response = await viewer.GetAsync(ticket.Url.Replace("/Camera/3/", "/Camera/4/", StringComparison.Ordinal));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        upstream.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Mjpeg_TamperedTicket_Returns401()
    {
        var upstream = Upstream.Frames();
        using var host = CreateHost(upstream);
        using var issuer = host.CreateApiClient(TestApiKeys.Viewer);
        var ticket = await ApiJson.ReadAsync<CameraStreamTicketResponse>(await issuer.PostAsync("/api/v1.0/Camera/3/ticket", content: null));
        var value = Uri.UnescapeDataString(ticket.Url[(ticket.Url.IndexOf("ticket=", StringComparison.Ordinal) + "ticket=".Length)..]);
        var tampered = value[..^1] + (value[^1] == 'A' ? 'B' : 'A');
        using var viewer = host.CreateApiClient();

        var response = await viewer.GetAsync("/api/v1.0/Camera/3/mjpeg?ticket=" + Uri.EscapeDataString(tampered));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        upstream.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Mjpeg_ProxyNotConfigured_Returns503()
    {
        using var host = new RoofApiTestHost();
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync("/api/v1.0/Camera/1/mjpeg");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [TestMethod]
    public async Task Mjpeg_OnlyUserNameConfigured_Returns503()
    {
        var upstream = Upstream.Frames();
        using var host = CreateHost(upstream, new Dictionary<string, string?> { ["BlueIris:UserName"] = "test-user" });
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync("/api/v1.0/Camera/1/mjpeg");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        upstream.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Mjpeg_UpstreamError_Returns502()
    {
        using var host = CreateHost(new FakeUpstreamHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))));
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync("/api/v1.0/Camera/1/mjpeg");

        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [TestMethod]
    public async Task Mjpeg_UpstreamUnreachable_Returns502()
    {
        using var host = CreateHost(new FakeUpstreamHandler((_, _) => throw new HttpRequestException("connection refused (test)")));
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync("/api/v1.0/Camera/1/mjpeg");

        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [TestMethod]
    public async Task Mjpeg_UpstreamNeverAnswers_Returns504AfterHeaderTimeout()
    {
        var upstream = new FakeUpstreamHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var host = CreateHost(upstream, new Dictionary<string, string?> { ["BlueIris:ResponseHeadersTimeout"] = "00:00:00.200" });
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync("/api/v1.0/Camera/1/mjpeg").WaitAsync(TimeSpan.FromSeconds(15));

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [TestMethod]
    public async Task Mjpeg_StreamCapReached_Returns503WithRetryAfter()
    {
        var upstream = Upstream.Frames();
        using var host = CreateHost(upstream, new Dictionary<string, string?> { ["BlueIris:MaxConcurrentStreams"] = "1" });
        using var client = host.CreateApiClient(TestApiKeys.Viewer);
        using var heldSlot = host.Services.GetRequiredService<CameraStreamLimiter>().TryAcquire();
        Assert.IsNotNull(heldSlot);

        var response = await client.GetAsync("/api/v1.0/Camera/1/mjpeg");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        upstream.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Mjpeg_OpenStream_EndsWhenTheHostStops()
    {
        // CONC-07: an open viewer must not hold up graceful shutdown (and with it the roof stop).
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(FakeFrames);
        var upstream = new FakeUpstreamHandler((_, _) =>
        {
            var content = new StreamContent(pipe.Reader.AsStream());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/x-mixed-replace; boundary=frame");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var host = CreateHost(upstream);
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        using var response = await client.GetAsync("/api/v1.0/Camera/1/mjpeg", HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync();
        var first = new byte[FakeFrames.Length];
        await body.ReadExactlyAsync(first).AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        var rest = new MemoryStream();
        var drain = body.CopyToAsync(rest);
        var finished = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.AreSame(drain, finished, "the proxied stream should end once ApplicationStopping fires");
        await pipe.Writer.CompleteAsync();
    }

    [TestMethod]
    public async Task CopyWithIdleTimeout_CopiesUntilEndOfStream()
    {
        using var source = new MemoryStream(FakeFrames);
        using var destination = new MemoryStream();

        await CameraController.CopyWithIdleTimeoutAsync(source, destination, TimeSpan.FromSeconds(5), CancellationToken.None);

        destination.ToArray().Should().Equal(FakeFrames);
    }

    [TestMethod]
    public async Task CopyWithIdleTimeout_StalledSource_ThrowsTimeoutException()
    {
        var pipe = new Pipe();
        await using var source = pipe.Reader.AsStream();
        using var destination = new MemoryStream();

        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => CameraController.CopyWithIdleTimeoutAsync(source, destination, TimeSpan.FromMilliseconds(100), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15)));
        await pipe.Writer.CompleteAsync();
    }

    [TestMethod]
    public async Task CopyWithIdleTimeout_CallerCancellation_IsNotReportedAsTimeout()
    {
        var pipe = new Pipe();
        await using var source = pipe.Reader.AsStream();
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CameraController.CopyWithIdleTimeoutAsync(source, destination, TimeSpan.FromSeconds(30), cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(15)));
        await pipe.Writer.CompleteAsync();
    }

    [TestMethod]
    public void StreamLimiter_CapsConcurrentStreamsAndReleasesOnce()
    {
        using var limiter = new CameraStreamLimiter(Options.Create(new BlueIrisOptions { MaxConcurrentStreams = 2 }));

        var first = limiter.TryAcquire();
        var second = limiter.TryAcquire();
        var third = limiter.TryAcquire();

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsNull(third);
        Assert.AreEqual(2, limiter.ActiveStreams);

        first.Dispose();
        first.Dispose();
        Assert.AreEqual(1, limiter.ActiveStreams, "a double dispose must not free an extra slot");
        using var again = limiter.TryAcquire();
        Assert.IsNotNull(again);
        Assert.IsNull(limiter.TryAcquire());
        second.Dispose();
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(4, 4)]
    [DataRow(1000, 32)]
    public void StreamLimiter_ClampsConfiguredMaximum(int configured, int expected)
    {
        using var limiter = new CameraStreamLimiter(Options.Create(new BlueIrisOptions { MaxConcurrentStreams = configured }));

        Assert.AreEqual(expected, limiter.MaxStreams);
    }

    private static RoofApiTestHost CreateHost(FakeUpstreamHandler upstream, IDictionary<string, string?>? extraSettings = null)
    {
        var settings = new Dictionary<string, string?> { ["BlueIris:BaseUrl"] = FakeBlueIris };
        if (extraSettings is not null)
        {
            foreach (var (key, value) in extraSettings)
            {
                settings[key] = value;
            }
        }

        return new RoofApiTestHost(
            settings: settings,
            configureServices: services => services
                .AddHttpClient<BlueIrisCameraClient>()
                .ConfigurePrimaryHttpMessageHandler(() => upstream));
    }

    private static class Upstream
    {
        public static FakeUpstreamHandler Frames() => new((_, _) =>
        {
            var content = new ByteArrayContent(FakeFrames);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/x-mixed-replace; boundary=frame");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
    }
}
