using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Camera;
using Microsoft.AspNetCore.Mvc;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// The controller's camera proxy against the emulated camera over a loopback socket, as it reads Blue Iris: the frames
/// relayed as sent, and each camera failure turned into the answer the console handles.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class CameraProxyScenarios
{
    private const string CameraStream = "/api/v1.0/Camera/2/mjpeg";

    [TestMethod]
    public async Task TheProxy_RelaysTheCamerasFrames_ToAViewer()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(new EmulatedRoofRigOptions { Camera = true });
        using var client = rig.CreateApiClient(TestApiKeys.Viewer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var response = await client.GetAsync(CameraStream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be(EmulatedCamera.ContentType);
        var reader = new MjpegReader(await response.Content.ReadAsStreamAsync(timeout.Token), EmulatedCamera.Boundary);
        var frames = new List<byte[]>();
        for (var i = 0; i < 3; i++)
        {
            frames.Add((await reader.ReadPartAsync(timeout.Token))!.Body);
        }

        frames.Should().AllSatisfy(frame => BaselineJpegDecoder.Decode(frame).Height.Should().Be(CameraFrameRenderer.Height));
        frames[0].Should().NotEqual(frames[1]);
        rig.Camera.GetStatus().Should().Match<EmulatedCameraStatus>(s => s.OpenStreams == 1 && s.StreamsServed == 1);

        response.Dispose();
        await WaitForAsync(() => rig.Camera.GetStatus().OpenStreams == 0, "the proxy to close the camera stream when the viewer leaves");
    }

    [TestMethod]
    [DataRow(EmulatedCameraMode.Unavailable, 503)]
    [DataRow(EmulatedCameraMode.Unauthorized, 401)]
    public async Task ACameraThatRefusesTheProxy_IsABadGateway(EmulatedCameraMode mode, int answer)
    {
        await using var rig = await EmulatedRoofRig.StartAsync(new EmulatedRoofRigOptions { Camera = true });
        rig.Camera.Mode = mode;
        using var client = rig.CreateApiClient(TestApiKeys.Viewer);

        using var response = await client.GetAsync(CameraStream);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        problem.Title.Should().Be("Camera unavailable");
        problem.Detail.Should().Be($"The camera server answered HTTP {answer}.");
        rig.Logs.Entries.Should().Contain(e => e.Message == $"Camera 2: Blue Iris answered {answer}");
    }

    [TestMethod]
    public async Task AFrozenCamera_AbortsTheViewersStream_AfterTheIdleTimeout()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(new EmulatedRoofRigOptions
        {
            Camera = true,
            Settings = new Dictionary<string, string?> { ["BlueIris:StreamIdleTimeout"] = "00:00:01" }
        });
        using var client = rig.CreateApiClient(TestApiKeys.Viewer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(CameraStream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var reader = new MjpegReader(await response.Content.ReadAsStreamAsync(timeout.Token), EmulatedCamera.Boundary);
        (await reader.ReadPartAsync(timeout.Token)).Should().NotBeNull();

        rig.Camera.Mode = EmulatedCameraMode.Frozen;

        // The viewer sees the connection fail rather than end: a frozen picture must not look like a finished stream.
        var read = async () =>
        {
            while (await reader.ReadPartAsync(timeout.Token) is not null)
            {
            }
        };
        await read.Should().ThrowAsync<Exception>().Where(e => e is IOException || e is HttpRequestException);
        timeout.IsCancellationRequested.Should().BeFalse();
        rig.Logs.Entries.Should().Contain(e => e.Message.StartsWith("Camera 2 stream failed after it started: No camera data received for 00:00:01", StringComparison.Ordinal));
        await WaitForAsync(() => rig.Camera.GetStatus().OpenStreams == 0, "the proxy to close the frozen camera stream");
    }

    [TestMethod]
    public async Task ACameraServerRestart_EndsTheViewersStream_AndTheNextViewerIsServed()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(new EmulatedRoofRigOptions { Camera = true });
        using var client = rig.CreateApiClient(TestApiKeys.Viewer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var first = await client.GetAsync(CameraStream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var reader = new MjpegReader(await first.Content.ReadAsStreamAsync(timeout.Token), EmulatedCamera.Boundary);
        (await reader.ReadPartAsync(timeout.Token)).Should().NotBeNull();

        rig.Camera.DisconnectAll();

        while (await reader.ReadPartAsync(timeout.Token) is not null)
        {
        }

        using var second = await client.GetAsync(CameraStream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await new MjpegReader(await second.Content.ReadAsStreamAsync(timeout.Token), EmulatedCamera.Boundary).ReadPartAsync(timeout.Token))
            .Should().NotBeNull();
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            timeout.IsCancellationRequested.Should().BeFalse($"waiting for {because}");
            await Task.Delay(20);
        }
    }
}
