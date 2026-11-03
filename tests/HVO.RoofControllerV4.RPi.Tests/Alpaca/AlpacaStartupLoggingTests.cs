using FluentAssertions;
using HVO.RoofControllerV4.Alpaca.Roof;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

[TestClass]
public sealed class AlpacaStartupLoggingTests
{
    [TestMethod]
    public async Task RefusedController_ActualSignalRStartupErrors_AreQuietThenReturnAfterGrace()
    {
        var time = new ManualTimeProvider();
        using var logs = new RecordingLoggerProvider();
        using var inner = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        using var factory = new AlpacaStartupLoggerFactory(inner, time);
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost/"),
            CreateHandler = () => new RefusedHandler(),
            LoggerFactory = factory,
            TimeProvider = time,
        });

        async Task FailToConnectAsync()
        {
            await using var feed = client.CreateStatusFeed();
            feed.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (feed.LastError is null)
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        await FailToConnectAsync();
        var failures = logs.Entries.Where(entry => entry.Category.EndsWith(".HttpConnection", StringComparison.Ordinal)
            && entry.Message.Contains("Failed to start connection", StringComparison.Ordinal)).ToArray();
        failures.Should().NotBeEmpty().And.OnlyContain(entry => entry.Level == LogLevel.Debug);
        logs.Serious.Should().BeEmpty();

        time.Advance(TimeSpan.FromSeconds(30));
        await FailToConnectAsync();
        logs.Serious.Should().Contain(entry => entry.Category.EndsWith(".HttpConnection", StringComparison.Ordinal)
            && entry.Message.Contains("Failed to start connection", StringComparison.Ordinal)
            && entry.Level == LogLevel.Error);
    }

    private sealed class RefusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Connection refused (test).");
    }

    [TestMethod]
    public void ConnectionFailure_IsQuietUntilGraceExpires_ThenReportsAnUnreachableController()
    {
        var time = new ManualTimeProvider();
        using var logs = new RecordingLoggerProvider();
        using var inner = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        using var factory = new AlpacaStartupLoggerFactory(inner, time);
        var logger = factory.CreateLogger("Microsoft.AspNetCore.Http.Connections.Client.HttpConnection");
        logger.LogError(new HttpRequestException("Connection refused"), "Failed to start connection");
        logs.Entries.Single().Level.Should().Be(LogLevel.Debug);
        time.Advance(TimeSpan.FromSeconds(30));
        logger.LogError(new HttpRequestException("Connection refused"), "Failed to start connection");
        logs.Entries.Last().Level.Should().Be(LogLevel.Error);
    }

    [TestMethod]
    public void SuccessfulConnection_EndsGrace_EvenBeforeItsDeadline()
    {
        using var logs = new RecordingLoggerProvider();
        using var inner = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        using var factory = new AlpacaStartupLoggerFactory(inner, new ManualTimeProvider());
        factory.ControllerConnected();
        factory.CreateLogger("Microsoft.AspNetCore.SignalR.Client.HubConnection")
            .LogError(new HttpRequestException("Connection refused"), "Failed to start connection");
        logs.Entries.Single().Level.Should().Be(LogLevel.Error);
    }

    [TestMethod]
    public void OtherCategoriesAndNonConnectionErrors_KeepTheirLevel_AndFactoryDoesNotOwnHostLogging()
    {
        using var logs = new RecordingLoggerProvider();
        using var inner = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        var factory = new AlpacaStartupLoggerFactory(inner, new ManualTimeProvider());
        factory.CreateLogger("Other").LogError(new HttpRequestException("Failed"), "Other error");
        factory.CreateLogger("Microsoft.AspNetCore.Http.Connections.Client.HttpConnection")
            .LogError(new InvalidOperationException("Failed"), "Other error");
        factory.Dispose();
        inner.CreateLogger("Other").LogError("Still usable");
        logs.Entries.Should().HaveCount(3).And.OnlyContain(entry => entry.Level == LogLevel.Error);
    }
}
