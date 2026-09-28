using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Emulator;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Camera;
using HVO.RoofControllerV4.Simulation.Emulator;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

/// <summary>
/// The emulated camera: its JPEG encoder (checked against an independent decoder), its view of the plant, and the
/// emulator host's MJPEG endpoint with the failure modes the console must survive.
/// </summary>
[TestClass]
public sealed class EmulatedCameraTests
{
    private const string Stream = "/mjpg/cam02/video.mjpg";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly HatEmulatorStatus Closed = new()
    {
        Elapsed = TimeSpan.FromSeconds(3723.4),
        TravelMeters = 5.5,
        PositionMeters = 0,
        ClosedLimitActuated = true
    };

    [TestMethod]
    public void TheEncoder_WritesABaselineGreyscaleJfif()
    {
        var jpeg = CameraFrameRenderer.RenderJpeg(Closed, 2, 0);

        var decoded = BaselineJpegDecoder.Decode(jpeg);

        decoded.Markers.Should().Equal(0xD8, 0xE0, 0xDB, 0xC0, 0xC4, 0xDA, 0xD9);
        (decoded.Width, decoded.Height).Should().Be((CameraFrameRenderer.Width, CameraFrameRenderer.Height));
    }

    [TestMethod]
    public void TheEncoder_RoundTripsAFrame_WithinTheQuantizationError()
    {
        var pixels = CameraFrameRenderer.Render(Closed, 2, 0);

        var decoded = BaselineJpegDecoder.Decode(JpegEncoder.EncodeGreyscale(pixels, CameraFrameRenderer.Width, CameraFrameRenderer.Height));

        MeanError(pixels, decoded.Pixels).Should().BeLessThan(4, "quality 75 keeps a frame close to its pixels");
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(13, 21)]
    [DataRow(64, 9)]
    public void TheEncoder_RoundTripsNoise_AtAnySize_AtQuality100(int width, int height)
    {
        var random = new Random(width * 1000 + height);
        var pixels = new byte[width * height];
        random.NextBytes(pixels);
        // Runs of 255 put 0xFF bytes in the entropy data, which must be stuffed.
        pixels.AsSpan(0, Math.Min(8, pixels.Length)).Fill(255);

        var decoded = BaselineJpegDecoder.Decode(JpegEncoder.EncodeGreyscale(pixels, width, height, quality: 100));

        (decoded.Width, decoded.Height).Should().Be((width, height));
        pixels.Zip(decoded.Pixels, (a, b) => Math.Abs(a - b)).Max().Should().BeLessThanOrEqualTo(4, "quality 100 quantizes by 1");
    }

    [TestMethod]
    public void TheEncoder_RefusesPixelsThatDoNotMatchTheSize_AndAQualityOutOfRange()
    {
        FluentActions.Invoking(() => JpegEncoder.EncodeGreyscale(new byte[10], 3, 3)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => JpegEncoder.EncodeGreyscale(new byte[0], 0, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => JpegEncoder.EncodeGreyscale(new byte[9], 3, 3, quality: 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => JpegEncoder.EncodeGreyscale(new byte[9], 3, 3, quality: 101)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void TheFrames_ShowTheRoofAtItsPosition_TheActuatedLimit_AndAlwaysDiffer()
    {
        var closed = CameraFrameRenderer.Render(Closed, 2, 0);
        var next = CameraFrameRenderer.Render(Closed, 2, 1);
        var open = CameraFrameRenderer.Render(Closed with { PositionMeters = 5.5, ClosedLimitActuated = false, OpenLimitActuated = true }, 2, 0);

        next.Should().NotEqual(closed, "a live stream changes every frame");
        // The roof (190) spans x 40-160 closed and 160-280 open, 20 px above the track; the limits are lit at 255.
        Pixel(closed, 50, 170).Should().Be(190);
        Pixel(closed, 270, 170).Should().Be(32);
        Pixel(open, 50, 170).Should().Be(32);
        Pixel(open, 270, 170).Should().Be(190);
        (Pixel(closed, 38, 170), Pixel(closed, 282, 170)).Should().Be(((byte)255, (byte)70));
        (Pixel(open, 38, 170), Pixel(open, 282, 170)).Should().Be(((byte)70, (byte)255));
    }

    [TestMethod]
    public async Task TheStream_IsMultipartJpeg_OneFrameOfThePlantPerPart_AtTheFrameRate()
    {
        await using var host = new EmulatorHost(new() { ["Emulator:CameraFramesPerSecond"] = "10" });
        using var client = host.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await client.GetAsync(Stream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var reader = await ReaderAsync(response, timeout.Token);
        var clock = Stopwatch.StartNew();
        var parts = new List<MjpegPart>();
        for (var i = 0; i < 6; i++)
        {
            parts.Add((await reader.ReadPartAsync(timeout.Token))!);
        }

        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(0.45), "five frame intervals of 0.1 s separate six frames");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be(EmulatedCamera.ContentType);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        parts.Should().AllSatisfy(part =>
        {
            part.Headers["Content-Type"].Should().Be("image/jpeg");
            BaselineJpegDecoder.Decode(part.Body).Width.Should().Be(CameraFrameRenderer.Width);
        });
        parts.Select(p => Convert.ToBase64String(p.Body)).Should().OnlyHaveUniqueItems("every frame differs");
        host.Camera.GetStatus().Should().Match<EmulatedCameraStatus>(s => s.OpenStreams == 1 && s.StreamsServed == 1 && s.FramesSent >= 6);

        response.Dispose();
        await WaitForAsync(() => host.Camera.GetStatus().OpenStreams == 0, "the viewer's disconnect to end the stream");
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("100")]
    [DataRow("x")]
    public async Task ACameraOutsideOneTo99_IsNotFound(string camera)
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"/mjpg/cam{camera}/video.mjpg");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    [DataRow(EmulatedCameraMode.Unavailable, HttpStatusCode.ServiceUnavailable)]
    [DataRow(EmulatedCameraMode.Unauthorized, HttpStatusCode.Unauthorized)]
    public async Task ACameraThatRefusesViewers_EndsTheOpenStreams_AndAnswersNewOnesWithItsStatus(EmulatedCameraMode mode, HttpStatusCode refusal)
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var open = await client.GetAsync(Stream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var reader = await ReaderAsync(open, timeout.Token);
        (await reader.ReadPartAsync(timeout.Token)).Should().NotBeNull();

        (await PostCameraAsync(client, new { mode = mode.ToString() })).Camera.Mode.Should().Be(mode);

        (await ReadToEndAsync(reader, timeout.Token)).Should().BeTrue("the open stream ends");
        using var refused = await client.GetAsync(Stream, timeout.Token);
        refused.StatusCode.Should().Be(refusal);
        if (refusal == HttpStatusCode.Unauthorized)
        {
            refused.Headers.WwwAuthenticate.Should().ContainSingle().Which.Scheme.Should().Be("Basic");
        }

        await WaitForAsync(() => host.Camera.GetStatus().OpenStreams == 0, "the stream to close");
    }

    [TestMethod]
    public async Task AFrozenCamera_KeepsTheStreamOpen_AndSendsNothing_UntilItIsLiveAgain()
    {
        await using var host = new EmulatorHost(new() { ["Emulator:CameraFramesPerSecond"] = "20" });
        using var client = host.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(Stream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var reader = await ReaderAsync(response, timeout.Token);
        (await reader.ReadPartAsync(timeout.Token)).Should().NotBeNull();

        await PostCameraAsync(client, new { mode = "Frozen" });
        // A part being written when the mode changed may still arrive; after it, nothing does.
        await Task.Delay(200);
        var sent = host.Camera.GetStatus().FramesSent;
        for (var i = 0; i < sent - 1; i++)
        {
            await reader.ReadPartAsync(timeout.Token);
        }

        var next = reader.ReadPartAsync(timeout.Token);
        await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(1), timeout.Token));
        next.IsCompleted.Should().BeFalse("a frozen camera sends no frames");
        host.Camera.GetStatus().Should().Match<EmulatedCameraStatus>(s => s.OpenStreams == 1 && s.FramesSent == sent);

        await PostCameraAsync(client, new { mode = "Live" });
        (await next.WaitAsync(timeout.Token)).Should().NotBeNull("the live camera sends on the same stream");
    }

    [TestMethod]
    public async Task ADisconnect_EndsTheOpenStreams_AndTheNextViewerGetsANewOne()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var first = await client.GetAsync(Stream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var reader = await ReaderAsync(first, timeout.Token);
        (await reader.ReadPartAsync(timeout.Token)).Should().NotBeNull();

        (await PostCameraAsync(client, new { disconnect = true })).Camera.Mode.Should().Be(EmulatedCameraMode.Live);

        (await ReadToEndAsync(reader, timeout.Token)).Should().BeTrue();
        using var second = await client.GetAsync(Stream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        (await (await ReaderAsync(second, timeout.Token)).ReadPartAsync(timeout.Token)).Should().NotBeNull();
        host.Camera.GetStatus().StreamsServed.Should().Be(2);
    }

    [TestMethod]
    public async Task TheStatus_ReportsTheCamera_AndAResetMakesItLive_AtItsFrameRate()
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        var status = (await client.GetFromJsonAsync<EmulatorStatusResponse>("/api/emulator/status", Json))!;
        status.Camera.Should().Be(new EmulatedCameraStatus(EmulatedCameraMode.Live, 5, 0, 0, 0));

        (await PostCameraAsync(client, new { mode = "Unavailable", framesPerSecond = 2.5 })).Camera
            .Should().Match<EmulatedCameraStatus>(c => c.Mode == EmulatedCameraMode.Unavailable && c.FramesPerSecond == 2.5);
        using var reset = await client.PostAsJsonAsync("/api/emulator/reset", new { });
        (await reset.Content.ReadFromJsonAsync<EmulatorStatusResponse>(Json))!.Camera
            .Should().Match<EmulatedCameraStatus>(c => c.Mode == EmulatedCameraMode.Live && c.FramesPerSecond == 2.5);
    }

    [TestMethod]
    [DataRow("""{"framesPerSecond":0}""")]
    [DataRow("""{"framesPerSecond":31}""")]
    [DataRow("""{"framesPerSecond":2,"mode":"Unavailable","disconnect":"yes"}""")]
    [DataRow("""{"mode":"Bogus"}""")]
    [DataRow("""{"mode":2}""")]
    public async Task ACameraRequestThatIsInvalid_IsRefused_AndChangesNothing(string body)
    {
        await using var host = new EmulatorHost();
        using var client = host.CreateClient();

        using var response = await client.PostAsync("/api/emulator/camera", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Camera.GetStatus().Should().Be(new EmulatedCameraStatus(EmulatedCameraMode.Live, 5, 0, 0, 0));
    }

    [TestMethod]
    public async Task AFrameRateOutOfRange_StopsTheStart()
    {
        await using var host = new EmulatorHost(new() { ["Emulator:CameraFramesPerSecond"] = "0" });

        FluentActions.Invoking(() => host.CreateClient()).Should().Throw<ArgumentOutOfRangeException>()
            .Which.Message.Should().StartWith("The camera's frame rate must be between 0.1 and 30 frames per second.");
    }

    [TestMethod]
    public void TheCamera_RefusesAnUndefinedMode()
    {
        var camera = new EmulatedCamera(() => Closed);

        FluentActions.Invoking(() => camera.Mode = (EmulatedCameraMode)9).Should().Throw<ArgumentOutOfRangeException>();
        camera.Mode.Should().Be(EmulatedCameraMode.Live);
    }

    private static async Task<MjpegReader> ReaderAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return new MjpegReader(await response.Content.ReadAsStreamAsync(cancellationToken), EmulatedCamera.Boundary);
    }

    /// <summary>Reads the parts still in flight until the stream ends; true when it does.</summary>
    private static async Task<bool> ReadToEndAsync(MjpegReader reader, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await reader.ReadPartAsync(cancellationToken) is null)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<EmulatorStatusResponse> PostCameraAsync(HttpClient client, object request)
    {
        using var response = await client.PostAsJsonAsync("/api/emulator/camera", request, Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<EmulatorStatusResponse>(Json))!;
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), $"waiting for {because}");
            await Task.Delay(20);
        }
    }

    private static byte Pixel(byte[] pixels, int x, int y) => pixels[y * CameraFrameRenderer.Width + x];

    private static double MeanError(byte[] expected, byte[] actual)
        => expected.Zip(actual, (a, b) => Math.Abs(a - b)).Average();
}
