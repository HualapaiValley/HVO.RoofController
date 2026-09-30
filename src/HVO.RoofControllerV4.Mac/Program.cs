using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Screens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.Mac;

/// <summary>
/// The roof controller's Mac app: the kiosk's screens in a window, where a person signs in with their name and password
/// instead of a PIN. It reaches the controller only through the client library, with the device key made for this Mac.
/// </summary>
public static class Program
{
    /// <summary>The app's name: the window's title and the menu bar's.</summary>
    public const string Title = "HVO Roof";

    /// <summary>Exit code for settings the app cannot start with (EX_CONFIG).</summary>
    public const int SettingsExitCode = 78;

    /// <summary>The settings file in the settings folder (<see cref="MacSettingsFolder"/>).</summary>
    public const string SettingsFile = "appsettings.Local.json";

    /// <summary>Opens the window, draws it and exits (<see cref="MacCheck"/>). Settings it refuses are only written out.</summary>
    public const string CheckFlag = "--check";

    /// <summary>How long <see cref="CheckFlag"/> waits for the window to be drawn.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(60);

    [STAThread]
    public static int Main(string[] args) => Run(args, MacSettingsFolder.Find(), Console.Out, Console.Error, MacRefusal.Show);

    /// <summary>
    /// The app, from its settings in <paramref name="folder"/> to its exit code. Settings it cannot start with are
    /// written to <paramref name="error"/>, shown by <paramref name="showRefusal"/> (the reason and the settings file)
    /// unless this is a check, and give <see cref="SettingsExitCode"/>.
    /// </summary>
    internal static int Run(string[] args, string folder, TextWriter output, TextWriter error, Action<string, string>? showRefusal = null)
    {
        var check = args.Contains(CheckFlag, StringComparer.Ordinal);
        args = [.. args.Where(arg => arg != CheckFlag)];
        var settingsFile = Path.Combine(folder, SettingsFile);
        IConfiguration configuration;
        MacOptions options;
        string keyFile;
        string deviceKey;
        try
        {
            configuration = BuildConfiguration(args, folder);
            options = ReadOptions(configuration);
            keyFile = options.DeviceKeyPath(folder);
            deviceKey = KioskDeviceKey.Load(keyFile, $"{MacOptions.SectionName}:DeviceKeyFile", "you");
        }
        catch (KioskSettingsException ex)
        {
            return Refuse(error, ex.Message, settingsFile, check ? null : showRefusal);
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
        if (KioskDeviceKey.IsOpenToOthers(keyFile))
        {
            logger.LogWarning("The device key file {Path} can be read by others than you: make it 0600 (chmod 600)", keyFile);
        }

        using var client = Connect(options, deviceKey, loggerFactory, error);
        if (client is null)
        {
            return SettingsExitCode;
        }

        var console = new KioskConsole(
            client,
            new KioskConsoleOptions
            {
                Wording = KioskWording.Desktop,
                Blanking = false,
                IdleLock = TimeSpan.FromSeconds(options.IdleLockSeconds)
            },
            loggerFactory.CreateLogger<KioskConsole>());
        var metrics = new KioskMetrics(options.PixelsPerMillimetre);
        KioskApp.Title = Title;
        KioskApp.WindowSize = new Size(MacOptions.WindowSize.Width, MacOptions.WindowSize.Height);
        KioskApp.MinimumWindowSize = new Size(MacOptions.MinimumWindowSize.Width, MacOptions.MinimumWindowSize.Height);
        KioskApp.CreateShell = () => new KioskShell(console, metrics);

        // Quitting from a terminal (Ctrl+C) or a kill: close the window, then sign out before exiting.
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Shutdown);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Shutdown);

        logger.LogInformation("{Title} for controller {Controller}, settings in {Folder}", Title, options.ControllerUrl, folder);
        console.Start();
        try
        {
            return AppBuilder.Configure<KioskApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .StartWithClassicDesktopLifetime(args, lifetime =>
                {
                    lifetime.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    if (check)
                    {
                        lifetime.Startup += (_, _) =>
                        {
                            if (lifetime.MainWindow is { } window)
                            {
                                MacCheck.Watch(window, code => lifetime.Shutdown(code), output, CheckTimeout);
                            }
                        };
                    }
                });
        }
        finally
        {
            Close(console, logger);
        }
    }

    /// <summary>
    /// The settings: appsettings.Local.json in <paramref name="folder"/> (optional), then the environment, then the
    /// command line.
    /// </summary>
    internal static IConfiguration BuildConfiguration(string[] args, string folder)
    {
        try
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Default"] = "Information",
                    ["Logging:LogLevel:System.Net.Http"] = "Warning"
                })
                .AddJsonFile(Path.Combine(folder, SettingsFile), optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new KioskSettingsException($"The settings could not be read: {ex.Message}");
        }
    }

    /// <summary>The <c>Mac</c> section, checked.</summary>
    internal static MacOptions ReadOptions(IConfiguration configuration)
    {
        MacOptions options;
        try
        {
            options = configuration.GetSection(MacOptions.SectionName).Get<MacOptions>() ?? new MacOptions();
        }
        catch (InvalidOperationException ex)
        {
            throw new KioskSettingsException($"The {MacOptions.SectionName} settings could not be read: {ex.Message}");
        }

        // An empty pin (as in "ServerCertificateSha256": "") is no pin.
        options.ServerCertificateSha256 = string.IsNullOrWhiteSpace(options.ServerCertificateSha256) ? null : options.ServerCertificateSha256.Trim();
        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new KioskSettingsException(string.Join(" ", problems));
        }

        return options;
    }

    /// <summary>
    /// The client for the controller the settings name, or null, with the reason written to <paramref name="error"/>,
    /// when the client refuses them. <see cref="MacOptions.Validate"/> checks the same things first.
    /// </summary>
    internal static RoofControllerClient? Connect(MacOptions options, string deviceKey, ILoggerFactory loggerFactory, TextWriter error)
    {
        try
        {
            return new RoofControllerClient(new RoofConnectionOptions
            {
                BaseAddress = options.ControllerUrl!,
                Credential = new RoofKioskCredential(deviceKey),
                ServerCertificateSha256 = options.ServerCertificateSha256,
                LoggerFactory = loggerFactory
            });
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"{Title} did not start: {ex.Message}");
            return null;
        }
    }

    private static int Refuse(TextWriter error, string reason, string settingsFile, Action<string, string>? showRefusal)
    {
        error.WriteLine($"{Title} did not start: {reason}");
        error.WriteLine($"Its settings file is {settingsFile}{(File.Exists(settingsFile) ? string.Empty : ", which does not exist yet")}.");
        showRefusal?.Invoke(reason, settingsFile);
        return SettingsExitCode;
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
            // Sign out first: it ends the person's session at the controller, so it does not outlive the app.
            console.LockAsync().Wait(TimeSpan.FromSeconds(10));
            console.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(15));
        }
        catch (AggregateException ex)
        {
            logger.LogWarning(ex.InnerException ?? ex, "The app did not close cleanly");
        }
    }
}
