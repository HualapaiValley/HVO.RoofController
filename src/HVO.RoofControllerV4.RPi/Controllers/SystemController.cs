using System;
using HVO.Core.Results;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using HVO.RoofControllerV4.RPi.Models.System;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// Provides APIs for system-level administration and diagnostics.
/// </summary>
[ApiController, ApiVersion("1.0"), Produces("application/json")]
[Route("api/v{version:apiVersion}/System")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Tags("System Administration")]
public class SystemController : ControllerBase
{
    private readonly ILogger<SystemController> _logger;
    private readonly IHostEnvironment _environment;
    private readonly Assembly _entryAssembly;

    public SystemController(ILogger<SystemController> logger, IHostEnvironment environment)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _entryAssembly = Assembly.GetEntryAssembly() ?? typeof(Program).Assembly;
    }

    /// <summary>
    /// Retrieves high-level information about the running application and host.
    /// </summary>
    /// <returns>Application and environment metadata.</returns>
    [HttpGet("info", Name = nameof(GetSystemInformation))]
    [ProducesResponseType(typeof(SystemInformationResponse), StatusCodes.Status200OK)]
    public ActionResult<SystemInformationResponse> GetSystemInformation()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var generatedAtUtc = DateTimeOffset.UtcNow;
            var startTimeUtc = process.StartTime.ToUniversalTime();
            var uptimeSeconds = Math.Max((generatedAtUtc - startTimeUtc).TotalSeconds, 0d);
            var assemblyName = _entryAssembly.GetName();

            var response = new SystemInformationResponse(
                ApplicationName: _environment.ApplicationName ?? assemblyName.Name ?? "Unknown",
                EnvironmentName: _environment.EnvironmentName ?? Environments.Production,
                MachineName: Environment.MachineName,
                OperatingSystemDescription: RuntimeInformation.OSDescription,
                FrameworkDescription: RuntimeInformation.FrameworkDescription,
                ApplicationVersion: assemblyName.Version?.ToString() ?? "Unknown",
                ProcessStartTimeUtc: startTimeUtc,
                UptimeSeconds: uptimeSeconds,
                GeneratedAtUtc: generatedAtUtc);

            _logger.LogDebug(
                "System information generated at {GeneratedAtUtc} with uptime {UptimeSeconds}s",
                generatedAtUtc,
                uptimeSeconds);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve system information");
            return Problem(
                title: "System Information Error",
                detail: "Unable to retrieve system information at this time.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Retrieves runtime metrics for the current application process.
    /// </summary>
    /// <returns>Process-level diagnostics including memory usage and CPU consumption.</returns>
    [HttpGet("metrics", Name = nameof(GetRuntimeMetrics))]
    [ProducesResponseType(typeof(SystemRuntimeMetricsResponse), StatusCodes.Status200OK)]
    public ActionResult<SystemRuntimeMetricsResponse> GetRuntimeMetrics()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var generatedAtUtc = DateTimeOffset.UtcNow;
            var startTimeUtc = process.StartTime.ToUniversalTime();
            var uptimeSeconds = Math.Max((generatedAtUtc - startTimeUtc).TotalSeconds, 0d);
            var totalProcessorTime = process.TotalProcessorTime;
            var cpuUsagePercent = uptimeSeconds <= 0 || Environment.ProcessorCount <= 0
                ? 0d
                : Math.Min(
                    100d,
                    totalProcessorTime.TotalSeconds / (uptimeSeconds * Environment.ProcessorCount) * 100d);

            var response = new SystemRuntimeMetricsResponse(
                WorkingSetBytes: process.WorkingSet64,
                PrivateMemoryBytes: process.PrivateMemorySize64,
                PeakWorkingSetBytes: process.PeakWorkingSet64,
                ManagedMemoryBytes: GC.GetTotalMemory(forceFullCollection: false),
                ThreadCount: process.Threads.Count,
                CpuUsagePercent: Math.Round(cpuUsagePercent, 2, MidpointRounding.AwayFromZero),
                TotalProcessorTime: totalProcessorTime,
                UptimeSeconds: uptimeSeconds,
                GeneratedAtUtc: generatedAtUtc);

            _logger.LogTrace(
                "Runtime metrics generated at {GeneratedAtUtc} (WorkingSet: {WorkingSetBytes} bytes)",
                generatedAtUtc,
                process.WorkingSet64);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve runtime metrics");
            return Problem(
                title: "Runtime Metrics Error",
                detail: "Unable to retrieve runtime metrics at this time.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
