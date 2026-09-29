using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Roof;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The roof page's camera (<c>GET /camera/{id}/mjpeg</c>): relayed from the controller's camera proxy with the person's
/// session, for the cameras the page shows, and ended with the session. The controller's answers about the camera
/// server are passed on, so the player can say what happened.
/// </summary>
[TestClass]
public sealed class WebCameraEndpointTests
{
    private const string Frame = "--frame\r\nContent-Type: image/jpeg\r\nContent-Length: 4\r\n\r\nJPEG\r\n";
    private const string MjpegType = "multipart/x-mixed-replace; boundary=frame";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ASignedInPerson_GetsTheCamera_FromTheControllerWithTheirSession()
    {
        await using var host = await StartAsync(_ => Mjpeg(Frame + Frame));
        using var browser = host.Browser();
        await browser.SignInAsync("vic", FakeController.AdaPassword);

        using var response = await browser.GetAsync(WebCameraEndpoint.StreamPath(RoofWebOptions.DefaultCameraId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be(MjpegType);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.CacheControl.NoCache.Should().BeTrue();
        response.Headers.GetValues("X-Accel-Buffering").Should().Equal("no");
        response.Headers.GetValues("Content-Security-Policy").Should().Equal(WebCameraEndpoint.ContentSecurityPolicy);
        (await response.Content.ReadAsStringAsync()).Should().Be(Frame + Frame);
        var asked = host.Controller.Logged(HttpMethod.Get, RoofApiRoutesTest.Camera(2)).Should().ContainSingle().Subject;
        asked.Authorization.Should().Be("Bearer token-1-vic");
        asked.HasApiKey.Should().BeFalse("the page's camera is the person's, not the web UI's");
    }

    [TestMethod]
    [DataRow("text/html; charset=utf-8")]
    [DataRow("application/javascript")]
    [DataRow("image/svg+xml")]
    [DataRow("multipart/form-data; boundary=x")]
    public async Task AnAnswerThatIsNotACameraStream_Is502_AndNotRelayed(string contentType)
    {
        await using var host = await StartAsync(_ =>
        {
            var content = new StringContent("<script>alert(1)</script>", Encoding.UTF8);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var browser = host.Browser();
        await browser.SignInAsync("vic", FakeController.AdaPassword);

        using var response = await browser.GetAsync(WebCameraEndpoint.StreamPath(RoofWebOptions.DefaultCameraId));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(WebCameraEndpoint.NotACameraStream).And.NotContain("<script>");
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("multipart/x-mixed-replace", true)]
    [DataRow("Multipart/X-Mixed-Replace; boundary=--myboundary", true)]
    [DataRow("image/jpeg", true)]
    [DataRow("text/html", false)]
    [DataRow("text/plain", false)]
    [DataRow("image/svg+xml", false)]
    [DataRow("", false)]
    [DataRow("not a type", false)]
    public void OnlyACamerasTypes_AreRelayed(string? contentType, bool relayed)
    {
        WebCameraEndpoint.IsCameraContentType(contentType).Should().Be(relayed);
    }

    [TestMethod]
    public async Task ASignedOutPage_Gets401_NotTheSignInPage()
    {
        await using var host = await StartAsync(_ => Mjpeg(Frame));
        using var browser = host.Browser();

        using var response = await browser.GetAsync("camera/2/mjpeg");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the player says the page is signed out; a redirect would read as a broken stream");
        response.Headers.Location.Should().BeNull();
        host.Controller.Logged(HttpMethod.Get, RoofApiRoutesTest.Camera(2)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task OnlyTheCamerasThePageShows_AreRelayed()
    {
        await using var host = await StartAsync(_ => Mjpeg(Frame), "--RoofWeb:CameraIds:0=5", "--RoofWeb:CameraIds:1=7");
        using var browser = host.Browser();
        await browser.SignInAsync("vic", FakeController.AdaPassword);

        foreach (var (camera, expected) in new[] { (5, HttpStatusCode.OK), (7, HttpStatusCode.OK), (2, HttpStatusCode.NotFound), (1, HttpStatusCode.NotFound) })
        {
            using var response = await browser.GetAsync($"camera/{camera}/mjpeg");
            response.StatusCode.Should().Be(expected, $"camera {camera}");
        }

        using (var outOfRange = await browser.GetAsync("camera/100/mjpeg"))
        {
            outOfRange.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        host.Controller.Requests.Where(request => request.Path.Contains("/Camera/", StringComparison.Ordinal)).Select(request => request.Path).ToList()
            .Should().Equal("/" + RoofApiRoutesTest.Camera(5), "/" + RoofApiRoutesTest.Camera(7));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadGateway, HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable)]
    [DataRow(HttpStatusCode.GatewayTimeout, HttpStatusCode.GatewayTimeout)]
    [DataRow(HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.Forbidden, HttpStatusCode.BadGateway)]
    public async Task TheControllersAnswerAboutTheCamera_IsPassedOn(HttpStatusCode controller, HttpStatusCode expected)
    {
        await using var host = await StartAsync(_ =>
        {
            var problem = FakeController.Problem(controller, null, TimeSpan.FromSeconds(5));
            problem.Content = JsonContent.Create(new ProblemDetails { Status = (int)controller, Title = "Camera unavailable", Detail = "The camera server answered HTTP 503." });
            return problem;
        });
        using var browser = host.Browser();
        await browser.SignInAsync("vic", FakeController.AdaPassword);

        using var response = await browser.GetAsync("camera/2/mjpeg");

        response.StatusCode.Should().Be(expected);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be("The camera server answered HTTP 503.");
        host.Sessions.TryGet("session-1", out _).Should().BeTrue("a camera that fails leaves the person signed in");
    }

    [TestMethod]
    public async Task ASessionTheControllerRefuses_Is401_AndEndsThePersonsSession()
    {
        await using var host = await StartAsync(_ => Mjpeg(Frame));
        using var browser = host.Browser();
        await browser.SignInAsync("vic", FakeController.AdaPassword);
        host.Controller.EndSession("token-1-vic");

        using var response = await browser.GetAsync("camera/2/mjpeg");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.Sessions.TryGet("session-1", out _).Should().BeFalse("the person's pages say they are signed out");
    }

    [TestMethod]
    public async Task AControllerThatCannotBeReached_Is502()
    {
        await using var host = await StartAsync(_ => throw new HttpRequestException("Connection refused"));
        using var browser = host.Browser();
        await browser.SignInAsync("vic", FakeController.AdaPassword);

        using var response = await browser.GetAsync("camera/2/mjpeg");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail.Should().Be("The roof controller could not be reached.");
    }

    [TestMethod]
    public async Task TheStream_IsRelayedAsItComes_AndEndsWhenTheControllersDoes()
    {
        var camera = new CameraFeed();
        await using var host = await StartAsync(_ => camera.Answer());
        using var browser = host.Browser();
        await browser.SignInAsync("vic", FakeController.AdaPassword);
        using var response = await browser.OpenStreamAsync("camera/2/mjpeg");
        await using var viewer = await response.Content.ReadAsStreamAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the headers go before the first frame");
        (await camera.SendAsync(Frame)).Should().BeTrue();
        (await ReadAsync(viewer, Frame.Length)).Should().Be(Frame);
        (await camera.SendAsync(Frame)).Should().BeTrue();
        (await ReadAsync(viewer, Frame.Length)).Should().Be(Frame);

        camera.End();

        (await viewer.ReadAsync(new byte[16]).AsTask().WaitAsync(Patience)).Should().Be(0, "the stream ended as the controller's did");
    }

    [TestMethod]
    public async Task AStreamThatBreaksOff_IsCutForTheViewer_NotLeftFrozen()
    {
        var camera = new CameraFeed();
        await using var host = await StartAsync(_ => camera.Answer());
        using var browser = host.Browser();
        await browser.SignInAsync("vic", FakeController.AdaPassword);
        using var response = await browser.OpenStreamAsync("camera/2/mjpeg");
        await using var viewer = await response.Content.ReadAsStreamAsync();
        await camera.SendAsync(Frame);
        await ReadAsync(viewer, Frame.Length);

        camera.BreakOff();

        await ShouldBeCutAsync(viewer);
    }

    [TestMethod]
    public async Task SigningOut_EndsTheStream_AndClosesTheControllers()
    {
        var camera = new CameraFeed();
        await using var host = await StartAsync(_ => camera.Answer());
        using var browser = host.Browser();
        await browser.SignInAsync("vic", FakeController.AdaPassword);
        using var response = await browser.OpenStreamAsync("camera/2/mjpeg");
        await using var viewer = await response.Content.ReadAsStreamAsync();
        await camera.SendAsync(Frame);
        await ReadAsync(viewer, Frame.Length);

        // Signed out in another tab.
        (await browser.PostFormAsync("/account/signout", new Dictionary<string, string>())).Dispose();

        await ShouldBeCutAsync(viewer);
        await camera.WaitUntilClosedAsync();
    }

    [TestMethod]
    public async Task TheSessionExpiring_EndsTheStream()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var camera = new CameraFeed();
        var controller = new FakeController(clock) { OtherAnswer = request => IsCamera(request) ? camera.Answer() : null };
        await using var host = await WebHost.StartAsync([], controller, builder => builder.Services.AddSingleton<TimeProvider>(clock));
        using var browser = host.Browser();
        await browser.SignInAsync("vic", FakeController.AdaPassword);
        using var response = await browser.OpenStreamAsync("camera/2/mjpeg");
        await using var viewer = await response.Content.ReadAsStreamAsync();
        await camera.SendAsync(Frame);
        await ReadAsync(viewer, Frame.Length);

        clock.Advance(TimeSpan.FromHours(11));
        (await camera.SendAsync(Frame)).Should().BeTrue();
        (await ReadAsync(viewer, Frame.Length)).Should().Be(Frame, "the session has an hour left");

        clock.Advance(TimeSpan.FromHours(1));

        await ShouldBeCutAsync(viewer);
        await camera.WaitUntilClosedAsync();
    }

    [TestMethod]
    public async Task TheViewerLeaving_ClosesTheControllersStream()
    {
        var camera = new CameraFeed();
        await using var host = await StartAsync(_ => camera.Answer());
        using var browser = host.Browser();
        await browser.SignInAsync("vic", FakeController.AdaPassword);
        var response = await browser.OpenStreamAsync("camera/2/mjpeg");
        var viewer = await response.Content.ReadAsStreamAsync();
        await camera.SendAsync(Frame);
        await ReadAsync(viewer, Frame.Length);

        await viewer.DisposeAsync();
        response.Dispose();

        await camera.WaitUntilClosedAsync();
    }

    private static Task<WebHost> StartAsync(Func<HttpRequestMessage, HttpResponseMessage> camera, params string[] args)
        => WebHost.StartAsync(args, new FakeController { OtherAnswer = request => IsCamera(request) ? camera(request) : null });

    private static bool IsCamera(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.StartsWith("/api/v1.0/Camera/", StringComparison.Ordinal);

    private static HttpResponseMessage Mjpeg(string body)
    {
        var content = new StringContent(body, Encoding.ASCII);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MjpegType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // The viewer's stream fails (the connection was aborted): not a clean end, which reads as the camera stopping, and
    // not left open, which reads as a frozen frame.
    private static async Task ShouldBeCutAsync(Stream viewer)
    {
        var read = viewer.ReadAsync(new byte[16]).AsTask();
        (await Task.WhenAny(read, Task.Delay(Patience))).Should().BeSameAs(read, "the viewer's stream is cut, not left open");
        read.IsCompletedSuccessfully.Should().BeFalse("the viewer's stream is aborted, not ended");
    }

    private static async Task<string> ReadAsync(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(Patience);
        return Encoding.ASCII.GetString(buffer);
    }

    /// <summary>The controller's camera stream, written as the test goes.</summary>
    private sealed class CameraFeed
    {
        private readonly Pipe _pipe = new();

        public HttpResponseMessage Answer()
        {
            var content = new StreamContent(_pipe.Reader.AsStream());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(MjpegType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        /// <summary>Sends <paramref name="text"/>; false once the stream's reader has closed it.</summary>
        public async Task<bool> SendAsync(string text)
            => !(await _pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask().WaitAsync(Patience)).IsCompleted;

        public void End() => _pipe.Writer.Complete();

        public void BreakOff() => _pipe.Writer.Complete(new IOException("The camera server went away."));

        public async Task WaitUntilClosedAsync()
        {
            using var patience = new CancellationTokenSource(Patience);
            while (await SendAsync(string.Empty))
            {
                await Task.Delay(20, patience.Token);
            }
        }
    }
}
