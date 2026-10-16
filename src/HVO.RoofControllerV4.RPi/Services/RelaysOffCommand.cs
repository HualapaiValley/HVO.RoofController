using System.Diagnostics;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Services;

/// <summary>
/// <c>dotnet HVO.RoofControllerV4.RPi.dll --relays-off</c>: turns every relay on the HAT off, verifies the relay register by
/// reading it back, and exits. The container's supervisor runs it whenever the controller is not running: after each exit
/// of the controller (a crash, a kill, a failed start), before each start, after a failed try while the controller is not
/// running, and when the supervisor itself stops (docs/deployment.md, "The relays when the controller exits"). The SM-I-010
/// holds its relays as they were last written until it loses power, so a controller that dies during a move would
/// otherwise leave the drive running.
/// </summary>
/// <remarks>
/// <para>
/// It reaches the HAT as the controller does: <see cref="RoofHatConnection"/> chooses the HAT emulator (the
/// <c>HatEmulator</c> settings) or the Pi's I2C bus, at the HAT library's default bus and address, which the controller
/// uses too (<c>Program.ConfigureServices</c>). It needs nothing else from the controller's settings and does not validate
/// them, so it works when the controller cannot start because of them. When the settings cannot be read at all (a file
/// that is not valid JSON), the HAT's are read from the environment, the command line and the secrets directory, where
/// the deploy script and Compose put them.
/// </para>
/// <para>
/// One write of the relay register (register 0) turns all four relays off in a single I2C transaction: the write the
/// controller falls back to when its relay-by-relay writes do not verify. It needs no relay mapping. Nothing is ever
/// turned on, so this cannot start motion. The read-back verifies the HAT's register, not the relay contacts.
/// </para>
/// <para>
/// It must not run while a controller drives the HAT: it would stop that controller's move, and the controller would latch
/// a relay verification fault. The supervisor runs it only while the controller is not running.
/// </para>
/// </remarks>
public static class RelaysOffCommand
{
    /// <summary>Command-line switch that runs this instead of the controller.</summary>
    public const string CommandLineSwitch = "--relays-off";

    /// <summary>Every relay is off: the register read back with no relay bit set.</summary>
    public const int VerifiedExitCode = 0;

    /// <summary>The relays could not be verified off (or the HAT could not be reached).</summary>
    public const int NotVerifiedExitCode = 1;

    /// <summary>Write-and-read attempts before giving up.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>The pause between attempts.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The start of each line written, so the container's log says where it came from.</summary>
    internal const string Prefix = "[relays-off]";

    // RLY1-RLY4: bits 0-3 of the relay register.
    private const int RelayBits = 0x0F;

    /// <summary>
    /// Turns the relays off on the HAT that <paramref name="configuration"/> and <paramref name="environment"/> select, as
    /// they select it for the controller, and verifies them. Writes what it did to <paramref name="output"/>. Returns
    /// <see cref="VerifiedExitCode"/> when the register reads back with every relay off, otherwise
    /// <see cref="NotVerifiedExitCode"/>.
    /// </summary>
    /// <param name="hat">The HAT's bus and address; the controller's (the HAT library's defaults) when null.</param>
    public static int Run(IConfiguration configuration, IHostEnvironment environment, TextWriter output, FourRelayFourInputHatOptions? hat = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(output);
        hat ??= new FourRelayFourInputHatOptions();

        RoofHatConnection connection;
        try
        {
            var emulator = configuration.GetSection(HatEmulatorOptions.SectionName).Get<HatEmulatorOptions>() ?? new HatEmulatorOptions();
            connection = new RoofHatConnection(Options.Create(emulator), environment);
        }
        catch (InvalidOperationException ex)
        {
            // Settings the controller would refuse too (it does not start with them), so they cannot name the HAT it drives.
            return Fail(output, $"the HAT emulator settings cannot be used, so nothing was written: {ex.Message}");
        }

        var started = Stopwatch.GetTimestamp();
        var where = connection.IsEmulated
            ? $"the HAT emulator at {connection.EmulatorEndpoint}"
            : $"the HAT on I2C bus {hat.I2cBusId} at address 0x{hat.BaseAddress + hat.Stack:X2}";
        using var device = new FourRelayFourInputHat(
            connection.CreateClient(hat, NullLoggerFactory.Instance), ownsClient: true, NullLogger<FourRelayFourInputHat>.Instance);
        if (!connection.IsEmulated && !device.IsHardwareBacked)
        {
            return Fail(output,
                $"I2C bus {hat.I2cBusId} could not be opened, so no HAT was written (the controller would run its in-memory register simulation)");
        }

        for (var attempt = 1; ; attempt++)
        {
            var write = device.SetRelaysMask(0);
            var read = device.GetRelaysMask();
            if (read.IsSuccessful && (read.Value & RelayBits) == 0)
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var note = write.IsSuccessful ? string.Empty : $"; the write failed ({Describe(write.Error)}), but the register was already off";
                output.WriteLine($"{Prefix} Every relay is off on {where}: the relay register reads back 0x00 (attempt {attempt}, {elapsed:0} ms){note}.");
                return VerifiedExitCode;
            }

            var problem = !read.IsSuccessful
                ? $"the read-back failed ({Describe(read.Error)})"
                : $"the relay register reads back 0x{read.Value & RelayBits:X2}"
                  + (write.IsSuccessful ? string.Empty : $" after the write failed ({Describe(write.Error)})");
            output.WriteLine($"{Prefix} Attempt {attempt} of {MaxAttempts} on {where}: {problem}.");
            if (attempt == MaxAttempts)
            {
                return Fail(output, $"the relays on {where} could not be verified off after {MaxAttempts} attempts");
            }

            Thread.Sleep(RetryDelay);
        }
    }

    /// <summary>
    /// The settings the HAT's connection needs when the controller's cannot be read: the environment variables, then
    /// <paramref name="args"/>, then the files in <paramref name="secretsDirectory"/>, in the controller's order (the
    /// secrets directory last). The environment's name is the controller's: <c>--environment</c>, then
    /// <c>ASPNETCORE_ENVIRONMENT</c>, then <c>DOTNET_ENVIRONMENT</c>, then Production. A secrets directory that cannot be
    /// read is left out.
    /// </summary>
    internal static (IConfigurationRoot Configuration, IHostEnvironment Environment) EnvironmentSettings(string[] args, string secretsDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        IConfigurationRoot configuration;
        try
        {
            configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args)
                .AddKeyPerFile(secretsDirectory, optional: true).Build();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
        }

        var name = configuration["environment"] ?? configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["DOTNET_ENVIRONMENT"];
        return (configuration, new SettingsEnvironment(string.IsNullOrWhiteSpace(name) ? Environments.Production : name));
    }

    /// <summary>Writes why the relays were not verified off and returns <see cref="NotVerifiedExitCode"/>.</summary>
    internal static int Fail(TextWriter output, string reason)
    {
        output.WriteLine($"{Prefix} FAILED: {reason}. The relays are not verified off.");
        return NotVerifiedExitCode;
    }

    // The type and message: the HAT library, the socket client and the configuration loaders name the device, the
    // endpoint or the file, never a setting's value.
    private static string Describe(Exception? error) => error is null ? "no error given" : $"{error.GetType().Name}: {error.Message}";

    /// <summary>The environment's name alone: all <see cref="RoofHatConnection"/> asks of it.</summary>
    private sealed class SettingsEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = typeof(RelaysOffCommand).Assembly.GetName().Name ?? string.Empty;

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
