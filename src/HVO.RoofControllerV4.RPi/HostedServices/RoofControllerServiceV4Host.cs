using System;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.HostedServices;

/// <summary>
/// Initializes the roof controller (retrying on failure) and performs a verified shutdown stop when the host stops.
/// </summary>
/// <remarks>
/// <para>Shutdown is requested as early as possible: on <see cref="IHostApplicationLifetime.ApplicationStopping"/> (when
/// the lifetime is available) and again from <see cref="StopAsync"/> and the end of <see cref="ExecuteAsync"/>. Triggers
/// that arrive while a shutdown call is in flight share it; a later trigger after an unverified result calls again
/// (<see cref="IRoofControllerServiceV4.ShutdownAsync"/> is idempotent). A failed, unverified, throwing or overdue shutdown
/// stop is logged as Critical.</para>
/// <para>The controller's first stop attempt performs synchronous HAT I/O under its lock, so the call is offloaded to the
/// thread pool and the wait is bounded independently of it. A wedged I2C transfer cannot be interrupted from managed
/// code: after the bound the host abandons the wait (the blocked thread is left behind), logs Critical and lets the host
/// continue stopping. The relay state is then unknown and the independent hardware stop is the only safe stop.</para>
/// </remarks>
public class RoofControllerServiceV4Host : BackgroundService
{
    /// <summary>
    /// How long the controller's shutdown call is given (its cancellation token fires then, and it reports whether its
    /// all-off retry verified).
    /// </summary>
    internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Extra time after the cancellation before the host abandons a shutdown call that has not returned (for example one
    /// blocked in HAT I/O).
    /// </summary>
    internal static readonly TimeSpan ShutdownAbandonGrace = TimeSpan.FromSeconds(1);

    private readonly ILogger<RoofControllerServiceV4Host> _logger;
    private readonly IRoofControllerServiceV4 _roofControllerServiceV4;
    private readonly RoofControllerHostOptionsV4 _options;
    private readonly CancellationTokenRegistration _stoppingRegistration;
    private readonly object _shutdownGate = new();
    private Task<bool>? _shutdownInFlight;
    private int _shutdownVerified;

    public RoofControllerServiceV4Host(
        ILogger<RoofControllerServiceV4Host> logger,
        IOptions<RoofControllerHostOptionsV4> options,
        IRoofControllerServiceV4 roofControllerServiceV4,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        _logger = logger;
        _options = options.Value;
        _roofControllerServiceV4 = roofControllerServiceV4;

        if (applicationLifetime is not null)
        {
            // Stop the roof before other services start tearing down.
            _stoppingRegistration = applicationLifetime.ApplicationStopping.Register(() => _ = ShutdownControllerAsync("application stopping"));
        }
    }

    /// <summary>How long the controller's shutdown call is given; <see cref="ShutdownTimeout"/> unless a test shortens it.</summary>
    internal TimeSpan ShutdownWaitTimeout { get; init; } = ShutdownTimeout;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ShutdownControllerAsync("host stop").ConfigureAwait(false);
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _stoppingRegistration.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{backgroundServiceName} is starting.", nameof(RoofControllerServiceV4Host));
        stoppingToken.Register(() => _logger.LogInformation("{backgroundServiceName} is stopping.", nameof(RoofControllerServiceV4Host)));

        try
        {
            // Attempt initialization with backoff until successful or cancellation requested
            while (!stoppingToken.IsCancellationRequested && !_roofControllerServiceV4.IsInitialized)
            {
                try
                {
                    var initResult = await _roofControllerServiceV4.Initialize(stoppingToken).ConfigureAwait(false);
                    if (!initResult.IsSuccessful)
                    {
                        _logger.LogError("Failed to initialize roof controller: {Error}", initResult.Error?.Message ?? "Unknown error");
                        await Task.Delay(TimeSpan.FromSeconds(_options.RestartOnFailureWaitTime), stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogDebug("{backgroundServiceName} initialization canceled.", nameof(RoofControllerServiceV4Host));
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{backgroundServiceName} initialization error. Retrying in {RestartDelay} seconds.", nameof(RoofControllerServiceV4Host), _options.RestartOnFailureWaitTime);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_options.RestartOnFailureWaitTime), stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            if (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("{backgroundServiceName} canceled before run loop.", nameof(RoofControllerServiceV4Host));
                return;
            }

            var logInterval = TimeSpan.FromMinutes(5);
            var nextLogTime = DateTime.UtcNow.Add(logInterval);

            // Run loop
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.UtcNow >= nextLogTime)
                    {
                        _logger.LogInformation("{backgroundServiceName} heartbeat at: {time}", nameof(RoofControllerServiceV4Host), DateTimeOffset.Now);
                        nextLogTime = DateTime.UtcNow.Add(logInterval);
                    }

                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("{backgroundServiceName} run loop canceled.", nameof(RoofControllerServiceV4Host));
                    break;
                }
            }
        }
        finally
        {
            // Do not dispose the singleton service here; the container owns it. Stop the roof with a verified shutdown.
            await ShutdownControllerAsync("execute loop exit").ConfigureAwait(false);
            _logger.LogInformation("{backgroundServiceName} has stopped.", nameof(RoofControllerServiceV4Host));
        }
    }

    /// <summary>
    /// Requests the controller's verified shutdown stop, bounded by <see cref="ShutdownWaitTimeout"/> plus
    /// <see cref="ShutdownAbandonGrace"/> even when the call blocks. Concurrent triggers share one call. Never throws.
    /// </summary>
    internal Task<bool> ShutdownControllerAsync(string trigger)
    {
        if (Volatile.Read(ref _shutdownVerified) != 0)
        {
            // A previous trigger already produced a verified shutdown stop.
            return Task.FromResult(true);
        }

        lock (_shutdownGate)
        {
            if (_shutdownInFlight is { IsCompleted: false } inFlight)
            {
                return inFlight;
            }

            _shutdownInFlight = RunShutdownAsync(trigger);
            return _shutdownInFlight;
        }
    }

    private async Task<bool> RunShutdownAsync(string trigger)
    {
        var timeout = ShutdownWaitTimeout;
        var abandonAfter = timeout + ShutdownAbandonGrace;
        var cancellation = new CancellationTokenSource(timeout);
        try
        {
            // Offloaded, so a call blocked in synchronous HAT I/O cannot hold this thread (or an ApplicationStopping
            // callback) past the bound.
            var token = cancellation.Token;
            var shutdown = Task.Run(() => _roofControllerServiceV4.ShutdownAsync(token));
            _ = shutdown.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), cancellation,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            cancellation = null;

            var result = await shutdown.WaitAsync(abandonAfter).ConfigureAwait(false);

            if (result.IsSuccessful)
            {
                Volatile.Write(ref _shutdownVerified, 1);
                _logger.LogInformation("Roof controller shutdown stop completed ({Trigger}); relay register verified all-off.", trigger);
                return true;
            }

            var code = result.Error is RoofControllerException rce ? rce.Code.ToString() : "Unknown";
            _logger.LogCritical(result.Error,
                "Roof controller shutdown stop FAILED ({Trigger}, {Code}): {Error}. Relay state may be energized; use the independent hardware stop.",
                trigger, code, result.Error?.Message ?? "unknown error");
            return false;
        }
        catch (TimeoutException ex)
        {
            _logger.LogCritical(ex,
                "Roof controller shutdown stop did not complete within {Timeout} ({Trigger}); the call is abandoned and may be blocked in HAT I/O. Relay state is unknown; use the independent hardware stop.",
                abandonAfter, trigger);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Roof controller shutdown stop threw ({Trigger}). Relay state may be energized; use the independent hardware stop.", trigger);
            return false;
        }
        finally
        {
            cancellation?.Dispose();
        }
    }
}
