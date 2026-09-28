using HVO.Iot.Devices.Abstractions;
using HVO.Iot.Devices.Implementation;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Services.HatEmulation;

/// <summary>
/// Chooses what answers the HAT's register accesses: the HAT emulator over TCP when <see cref="HatEmulatorOptions.Enabled"/>,
/// otherwise the Pi's I2C bus (or, where there is no bus, the in-memory register simulation, as before).
/// </summary>
/// <remarks>
/// Emulator mode is refused outside the Development environment unless <see cref="HatEmulatorOptions.AllowOutsideDevelopment"/>
/// is set: the constructor throws, so the controller does not start. <see cref="HatEmulatorStartup.ReportHatMode"/>
/// resolves this at startup.
/// </remarks>
public sealed class RoofHatConnection
{
    public RoofHatConnection(IOptions<HatEmulatorOptions> options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        Options = options.Value;
        if (!Options.Enabled)
        {
            return;
        }

        var problems = Options.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"The HAT emulator settings are invalid: {string.Join(" ", problems)}");
        }

        if (!environment.IsDevelopment() && !Options.AllowOutsideDevelopment)
        {
            throw new InvalidOperationException(
                $"HAT emulator mode ({HatEmulatorOptions.SectionName}:Enabled) is refused in the {environment.EnvironmentName} environment. " +
                $"It is for development and test rigs; to use it elsewhere set {HatEmulatorOptions.SectionName}:AllowOutsideDevelopment " +
                "(the deployment script's ALLOW_EMULATED_HAT=true). A controller in emulator mode does not operate the roof.");
        }

        IsEmulated = true;
    }

    public HatEmulatorOptions Options { get; }

    /// <summary>True when the HAT emulator answers the register accesses.</summary>
    public bool IsEmulated { get; }

    /// <summary>The emulator's <c>host:port</c> in emulator mode, otherwise null.</summary>
    public string? EmulatorEndpoint => IsEmulated ? Options.Endpoint : null;

    /// <summary>The register client for the HAT the options describe.</summary>
    public II2cRegisterClient CreateClient(FourRelayFourInputHatOptions hat, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(hat);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var busId = hat.I2cBusId;
        var address = hat.BaseAddress + hat.Stack;
        if (IsEmulated)
        {
            return new SocketI2cRegisterClient(Options, busId, address, loggerFactory.CreateLogger<SocketI2cRegisterClient>());
        }

        // What the HAT library does without a client factory: the I2C bus when it opens, otherwise the register simulation.
        return I2cRegisterClientFactory.CreateAutoSelecting(
            busId,
            address,
            hat.PostTransactionDelayMs,
            useRealHardware: null,
            simulationFactory: () => new FourRelayFourInputHatMemoryClient(busId, address));
    }

    /// <summary>The HAT mode a status snapshot reports.</summary>
    public RoofHatMode ModeFor(bool hardwareBacked) => IsEmulated ? RoofHatMode.Emulated : hardwareBacked ? RoofHatMode.Physical : RoofHatMode.Simulation;
}

/// <summary>The startup report of the HAT mode.</summary>
public static class HatEmulatorStartup
{
    public const string LoggerCategory = "HVO.RoofControllerV4.RPi.HatEmulation";

    /// <summary>
    /// Resolves the <see cref="RoofHatConnection"/> (so a refused emulator configuration stops the start) and warns when
    /// the controller runs against the HAT emulator.
    /// </summary>
    public static void ReportHatMode(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var connection = services.GetRequiredService<RoofHatConnection>();
        if (!connection.IsEmulated)
        {
            return;
        }

        services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory).LogWarning(
            "EMULATED HAT: the roof controller is connected to the HAT emulator at {Endpoint}, not the physical HAT. " +
            "Relay commands move an emulated roof; the observatory roof does not move.",
            connection.EmulatorEndpoint);
    }
}
