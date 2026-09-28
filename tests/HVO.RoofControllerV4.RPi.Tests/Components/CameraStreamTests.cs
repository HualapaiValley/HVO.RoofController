using System;
using System.Threading.Tasks;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Components;

[TestClass]
public class CameraStreamTests
{
    private const string Status = "[data-testid=camera-status]";
    private const string Overlay = "[data-testid=camera-overlay]";

    [TestMethod]
    public async Task Player_IsCreated_ForSameOriginStream_AndControlsEnable()
    {
        await using var context = CreateContext(out var module, out _);

        var cut = context.Render<CameraStream>();

        cut.WaitForAssertion(() => cut.Find("button[title=Pause]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("button[title=Snapshot]").HasAttribute("disabled").Should().BeTrue("there is no frame to capture yet");
        var createPlayer = module.VerifyInvoke("createPlayer");
        createPlayer.Arguments[^1].Should().Be("api/v1.0/camera/02/mjpeg");
    }

    [TestMethod]
    public async Task Status_IsNotLive_UntilFramesArrive()
    {
        await using var context = CreateContext(out _, out _);

        var cut = context.Render<CameraStream>();

        cut.Find(Status).TextContent.Should().Be("Connecting");
        cut.Find(Overlay).TextContent.Should().Contain("Connecting to the camera");
        cut.Find(".player-frame").ClassList.Should().Contain("is-stale");
    }

    [TestMethod]
    public async Task Live_HidesOverlay_AndStallShowsLastFrameTime()
    {
        await using var context = CreateContext(out _, out _);
        var cut = context.Render<CameraStream>();
        var frameTime = DateTimeOffset.UtcNow;

        await cut.Instance.OnStreamStateChanged("live", null, frameTime.ToUnixTimeMilliseconds());

        cut.WaitForAssertion(() => cut.Find(Status).TextContent.Should().Be("Live"));
        cut.Find(Status).ClassList.Should().Contain("status-chip--live");
        cut.FindAll(Overlay).Should().BeEmpty();
        cut.Find(".player-frame").ClassList.Should().NotContain("is-stale");
        cut.Find("button[title=Snapshot]").HasAttribute("disabled").Should().BeFalse();

        await cut.Instance.OnStreamStateChanged("stalled", "No frame for 5 s", frameTime.ToUnixTimeMilliseconds());

        cut.WaitForAssertion(() => cut.Find(Status).TextContent.Should().Be("Stalled"));
        cut.Find(Status).ClassList.Should().Contain("status-chip--bad");
        cut.Find(Overlay).TextContent.Should().Contain("No live video — last frame").And.Contain("No frame for 5 s");
        cut.Find(".player-frame").ClassList.Should().Contain("is-stale");
    }

    [TestMethod]
    public async Task PlayerStartFailure_ShowsUnavailable_WithoutThrowing()
    {
        await using var context = new BunitContext();
        context.Services.AddLogging();
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()))
            .Returns(ValueTask.FromException<IJSObjectReference>(new JSException("module failed to load")));
        context.Services.AddSingleton(js.Object);

        var cut = context.Render<CameraStream>();

        cut.WaitForAssertion(() => cut.Find(Status).TextContent.Should().Be("Unavailable"));
        cut.Find(Overlay).TextContent.Should().Contain("Camera view unavailable");
        cut.Find("button[title=Pause]").HasAttribute("disabled").Should().BeTrue();
    }

    [TestMethod]
    public async Task Dispose_ReleasesPlayer_AndToleratesDisconnectedCircuit()
    {
        await using var context = CreateContext(out _, out var player);
        player.SetupVoid("dispose").SetException(new JSDisconnectedException("circuit gone"));
        var cut = context.Render<CameraStream>();
        cut.WaitForAssertion(() => cut.Find("button[title=Pause]").HasAttribute("disabled").Should().BeFalse());
        var instance = cut.Instance;

        var dispose = async () => await context.DisposeComponentsAsync();

        await dispose.Should().NotThrowAsync();
        player.VerifyInvoke("dispose");
        await instance.OnStreamStateChanged("live", null, 0);
    }

    [TestMethod]
    public async Task Dispose_WhenThePlayerThrows_StillReleasesThePlayerAndTheModule()
    {
        await using var context = new BunitContext();
        var (player, module) = RenderWithAPlayerWhoseDisposeThrows(context, new JSException("dispose failed"));

        var dispose = async () => await context.DisposeComponentsAsync();

        await dispose.Should().NotThrowAsync();
        player.Verify(p => p.InvokeAsync<IJSVoidResult>("dispose", It.IsAny<object?[]?>()), Times.Once);
        player.Verify(p => p.DisposeAsync(), Times.Once);
        module.Verify(m => m.DisposeAsync(), Times.Once);
    }

    [TestMethod]
    public async Task Dispose_AfterTheCircuitIsGone_MakesNoFurtherJsCalls()
    {
        await using var context = new BunitContext();
        var (player, module) = RenderWithAPlayerWhoseDisposeThrows(context, new JSDisconnectedException("circuit gone"));

        var dispose = async () => await context.DisposeComponentsAsync();

        await dispose.Should().NotThrowAsync();
        player.Verify(p => p.InvokeAsync<IJSVoidResult>("dispose", It.IsAny<object?[]?>()), Times.Once);
        player.Verify(p => p.DisposeAsync(), Times.Never, "the circuit is gone, so the call cannot reach the browser");
        module.Verify(m => m.DisposeAsync(), Times.Never, "the circuit is gone, so the call cannot reach the browser");
    }

    private static (Mock<IJSObjectReference> Player, Mock<IJSObjectReference> Module) RenderWithAPlayerWhoseDisposeThrows(
        BunitContext context, Exception failure)
    {
        context.Services.AddLogging();
        var player = new Mock<IJSObjectReference>();
        player.Setup(p => p.InvokeAsync<IJSVoidResult>("dispose", It.IsAny<object?[]?>()))
            .Returns(ValueTask.FromException<IJSVoidResult>(failure));
        var module = new Mock<IJSObjectReference>();
        module.Setup(m => m.InvokeAsync<IJSObjectReference>("createPlayer", It.IsAny<object?[]?>()))
            .ReturnsAsync(player.Object);
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()))
            .ReturnsAsync(module.Object);
        context.Services.AddSingleton(js.Object);
        var cut = context.Render<CameraStream>();
        cut.WaitForAssertion(() => cut.Find("button[title=Pause]").HasAttribute("disabled").Should().BeFalse());
        return (player, module);
    }

    private static BunitContext CreateContext(out BunitJSInterop module, out BunitJSInterop player)
    {
        var context = new BunitContext();
        context.Services.AddLogging();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        module = context.JSInterop.SetupModule(CameraStream.ModulePath);
        player = module.SetupModule("createPlayer", _ => true);
        return context;
    }
}
