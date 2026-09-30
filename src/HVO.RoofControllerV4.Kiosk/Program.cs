using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LinuxFramebuffer;
using Avalonia.LinuxFramebuffer.Input.LibInput;
using Avalonia.Threading;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Screens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.Kiosk;

/// <summary>
/// The roof controller's touchscreen kiosk: full screen on the Pi's display through DRM and libinput (no X11 or
/// Wayland), started at boot by systemd (deploy/hvo-roof-kiosk.service). It reaches the controller only through the
/// client library, with its device key.
/// </summary>
public static class Program
{
    /// <summary>Exit code for settings the kiosk cannot start with (EX_CONFIG): systemd does not restart it for these.</summary>
    public const int SettingsExitCode = 78;

    /// <summary>The settings file next to the program. Optional.</summary>
    public const string SettingsFile = "appsettings.Local.json";

    [STAThread]
    public static int Main(string[] args)
    {
        IConfiguration configuration;
        KioskOptions options;
        string deviceKey;
        try
        {
            configuration = BuildConfiguration(args, AppContext.BaseDirectory);
            options = ReadOptions(configuration);
            deviceKey = KioskDeviceKey.Load(options.DeviceKeyFile!);
        }
        catch (KioskSettingsException ex)
        {
            Console.Error.WriteLine($"The roof kiosk did not start: {ex.Message}");
            return SettingsExitCode;
        }

        using var loggerFactory = LoggerFactory.Create(logging => logging
            .AddConfiguration(configuration.GetSection("Logging"))
            .AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z' ";
                console.UseUtcTimestamp = true;
            }));
        var logger = loggerFactory.CreateLogger(typeof(Program));
        if (KioskDeviceKey.IsOpenToOthers(options.DeviceKeyFile!))
        {
            logger.LogWarning("The device key file {Path} can be read by others than its owner: make it 0400 or 0600", options.DeviceKeyFile);
        }

        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = options.ControllerUrl,
            Credential = new RoofKioskCredential(deviceKey),
            ServerCertificateSha256 = options.ServerCertificateSha256,
            LoggerFactory = loggerFactory
        });
        var console = new KioskConsole(
            client,
            new KioskConsoleOptions { IdleLock = TimeSpan.FromSeconds(options.IdleLockSeconds) },
            loggerFactory.CreateLogger<KioskConsole>());
        var metrics = new KioskMetrics(options.PixelsPerMillimetre);
        KioskApp.CreateShell = () => new KioskShell(console, metrics);
        using var backlight = string.IsNullOrWhiteSpace(options.BacklightFile)
            ? null
            : new KioskBacklight(console, options.BacklightFile, options.BacklightOn, options.BacklightOff, loggerFactory.CreateLogger<KioskBacklight>());

        // systemd stops the kiosk with SIGTERM: close the screen, then lock (end the PIN session) before exiting.
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Shutdown);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Shutdown);

        console.Start();
        int exitCode;
        try
        {
            var builder = AppBuilder.Configure<KioskApp>().UseSkia().WithInterFont();
            if (options.Window)
            {
                KioskOptions.TryParseSize(options.WindowSize, out var width, out var height);
                KioskApp.WindowSize = new Size(width, height);
                logger.LogInformation("Roof kiosk in a {Width}x{Height} window, controller {Controller}", width, height, options.ControllerUrl);
                exitCode = builder.UsePlatformDetect().StartWithClassicDesktopLifetime(args);
            }
            else
            {
                var card = string.IsNullOrWhiteSpace(options.Card) ? KioskDisplay.FindCard() : options.Card;
                logger.LogInformation(
                    "Roof kiosk on {Card} turned {Rotation}°, input {Input}, controller {Controller}",
                    card ?? "the first DRM card",
                    options.Rotation,
                    options.InputDevices is { Length: > 0 } devices ? string.Join(", ", devices) : "every device on the seat",
                    options.ControllerUrl);
                var input = new LibInputBackend(new LibInputBackendOptions { Events = options.InputDevices is { Length: > 0 } ? options.InputDevices : null });
                exitCode = builder.StartLinuxDrm(
                    args,
                    card,
                    connectorsForceProbe: false,
                    new DrmOutputOptions { Orientation = KioskDisplay.Orientation(options.Rotation) },
                    input);
            }
        }
        finally
        {
            Close(console, logger);
        }

        return exitCode;
    }

    /// <summary>
    /// The settings: appsettings.Local.json in <paramref name="baseDirectory"/> (optional), then the environment, then
    /// the command line. --window is short for --Kiosk:Window=true.
    /// </summary>
    internal static IConfiguration BuildConfiguration(string[] args, string baseDirectory)
    {
        var commandLine = args.Select(arg => arg == "--window" ? $"--{KioskOptions.SectionName}:Window=true" : arg).ToArray();
        try
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Default"] = "Information",
                    ["Logging:LogLevel:System.Net.Http"] = "Warning"
                })
                .AddJsonFile(Path.Combine(baseDirectory, SettingsFile), optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .AddCommandLine(commandLine)
                .Build();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException)
        {
            throw new KioskSettingsException($"The settings could not be read: {ex.Message}");
        }
    }

    /// <summary>The <c>Kiosk</c> section, checked.</summary>
    internal static KioskOptions ReadOptions(IConfiguration configuration)
    {
        KioskOptions options;
        try
        {
            options = configuration.GetSection(KioskOptions.SectionName).Get<KioskOptions>() ?? new KioskOptions();
        }
        catch (InvalidOperationException ex)
        {
            throw new KioskSettingsException($"The {KioskOptions.SectionName} settings could not be read: {ex.Message}");
        }

        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new KioskSettingsException(string.Join(" ", problems));
        }

        return options;
    }

    private static void Shutdown(PosixSignalContext context)
    {
        context.Cancel = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IControlledApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
        });
    }

    private static void Close(KioskConsole console, ILogger logger)
    {
        try
        {
            // Lock first: it ends the PIN session at the controller, so nobody's session outlives the kiosk.
            console.LockAsync().Wait(TimeSpan.FromSeconds(10));
            console.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(15));
        }
        catch (AggregateException ex)
        {
            logger.LogWarning(ex.InnerException ?? ex, "The kiosk did not close cleanly");
        }
    }
}
