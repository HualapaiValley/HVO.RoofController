using System;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.HostedServices;

public class RoofControllerServiceV4Host : BackgroundService
{
    private readonly ILogger<RoofControllerServiceV4Host> _logger;
    private readonly IRoofControllerServiceV4 _roofControllerServiceV4;
    private readonly RoofControllerHostOptionsV4 _options;


    public RoofControllerServiceV4Host(ILogger<RoofControllerServiceV4Host> logger, IOptions<RoofControllerHostOptionsV4> options, IRoofControllerServiceV4 roofControllerServiceV4)
    {
        _logger = logger;
        _options = options.Value;
        _roofControllerServiceV4 = roofControllerServiceV4;
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
                catch (TaskCanceledException)
                {
                    _logger.LogDebug("{backgroundServiceName} initialization canceled.", nameof(RoofControllerServiceV4Host));
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{backgroundServiceName} initialization error. Retrying in {RestartDelay} seconds.", nameof(RoofControllerServiceV4Host), _options.RestartOnFailureWaitTime);
                    await Task.Delay(TimeSpan.FromSeconds(_options.RestartOnFailureWaitTime), stoppingToken).ConfigureAwait(false);
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
                catch (TaskCanceledException)
                {
                    _logger.LogDebug("{backgroundServiceName} run loop canceled.", nameof(RoofControllerServiceV4Host));
                    break;
                }
            }
        }
        finally
        {
            // Do not dispose the singleton service here; allow the host to shut down gracefully
            try
            {
                _roofControllerServiceV4.Stop(RoofControllerStopReason.SystemDisposal);
            }
            catch { /* best-effort stop */ }

            _logger.LogInformation("{backgroundServiceName} has stopped.", nameof(RoofControllerServiceV4Host));
        }
    }
}

