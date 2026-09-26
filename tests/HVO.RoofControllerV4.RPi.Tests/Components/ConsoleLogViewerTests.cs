using System;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Components.ConsoleLog;
using HVO.RoofControllerV4.RPi.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Components;

[TestClass]
public class ConsoleLogViewerTests
{
    [TestMethod]
    public async Task BurstOfEntriesFromBackgroundThreads_IsCoalescedIntoFewRenders()
    {
        var buffer = CreateBuffer();
        await using var context = CreateContext(buffer);
        var countNotifications = 0;
        var cut = context.Render<ConsoleLogViewer>(parameters => parameters
            .Add(p => p.EntryCountChanged, (int _) => Interlocked.Increment(ref countNotifications)));
        var initialNotifications = Volatile.Read(ref countNotifications);

        await Task.Run(() => Parallel.For(0, 50, i => buffer.Append(Entry(i))));

        cut.WaitForAssertion(() => cut.Find(".hvo-console-meta").TextContent.Should().Be("50 log entries"), TimeSpan.FromSeconds(5));
        (Volatile.Read(ref countNotifications) - initialNotifications).Should().BeLessThan(10);
        cut.FindAll(".hvo-console-line").Should().HaveCount(50);
    }

    [TestMethod]
    public async Task EntriesAfterDispose_AreIgnored()
    {
        var buffer = CreateBuffer();
        await using var context = CreateContext(buffer);
        context.Render<ConsoleLogViewer>();

        await context.DisposeComponentsAsync();

        var append = () => buffer.Append(Entry(1));
        append.Should().NotThrow();
    }

    [TestMethod]
    public void Buffer_IsolatesFailingSubscribers()
    {
        var buffer = CreateBuffer();
        var delivered = 0;
        buffer.EntryAdded += (_, _) => throw new InvalidOperationException("subscriber failed");
        buffer.EntryAdded += (_, _) => delivered++;

        var append = () => buffer.Append(Entry(1));

        append.Should().NotThrow();
        delivered.Should().Be(1);
        buffer.GetSnapshot().Should().ContainSingle();
    }

    private static BunitContext CreateContext(ConsoleLogBuffer buffer)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(buffer);
        return context;
    }

    private static ConsoleLogBuffer CreateBuffer()
    {
        var options = new Mock<IOptionsMonitor<ConsoleLogBufferOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new ConsoleLogBufferOptions());
        return new ConsoleLogBuffer(options.Object);
    }

    private static ConsoleLogEntry Entry(int index) =>
        new(DateTimeOffset.UtcNow, LogLevel.Information, "Tests", $"Entry {index}", null, Array.Empty<string>());
}
