using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Mac;
using HVO.RoofControllerV4.RPi.Tests.Web;
using HVO.RoofControllerV4.Screens;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;
using MacProgram = HVO.RoofControllerV4.Mac.Program;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The Mac app's own windows, drawn headless: <c>--check</c>, which CI runs on a Mac against the signed bundle; the page
/// shown when the settings are refused; and the icon, drawn at each size macOS uses and compared with the bundle's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MacAppTests
{
    /// <summary>
    /// Set to a folder, the icon test saves the icon there at each size, as icon-SIZE.png, to be packed into the bundle's
    /// AppIcon.icns with bundle/make-icns.py (docs/mac.md).
    /// </summary>
    public const string IconVariable = "HVO_MAC_ICON_DIR";

    private static readonly int[] IconSizes = [16, 32, 64, 128, 256, 512, 1024];

    // The .icns elements (bundle/make-icns.py) and the size of each.
    private static readonly Dictionary<string, int> IconElements = new()
    {
        ["icp4"] = 16, ["icp5"] = 32, ["ic11"] = 32, ["icp6"] = 64, ["ic12"] = 64, ["ic07"] = 128,
        ["ic08"] = 256, ["ic13"] = 256, ["ic09"] = 512, ["ic14"] = 512, ["ic10"] = 1024
    };

    public TestContext TestContext { get; set; } = null!;

    // ---- --check --------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheCheck_ExitsWith0_OnceTheWindowIsDrawn_AndOnlyOnce()
    {
        var lifetime = new Lifetime();
        using var output = new StringWriter();
        var window = await OnUiAsync(() =>
        {
            var window = new Window { Width = 400, Height = 300, Background = KioskTheme.Background };
            MacCheck.Watch(window, lifetime.Shutdown, output, TimeSpan.FromSeconds(1));
            window.Show();
            return window;
        });

        await UntilAsync(
            () =>
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                return lifetime.ExitCodes.Count > 0;
            },
            "the check to end");
        await RunForAsync(TimeSpan.FromSeconds(1.5));
        await OnUiAsync(window.Close);

        lifetime.ExitCodes.Should().Equal([0], "the time allowed ran out after the window was drawn, and changes nothing");
        output.ToString().Should().Be($"HVO Roof check: the window opened and was drawn at 400x300 points, scale 1.{Environment.NewLine}");
    }

    [TestMethod]
    public async Task TheCheck_ExitsWith1_WhenTheWindowIsNotDrawnInTime_AndOnlyOnce()
    {
        var lifetime = new Lifetime();
        using var output = new StringWriter();
        var window = await OnUiAsync(() =>
        {
            var window = new Window { Width = 400, Height = 300 };
            MacCheck.Watch(window, lifetime.Shutdown, output, TimeSpan.FromSeconds(1));
            return window;
        });

        await RunForAsync(TimeSpan.FromSeconds(1.5));
        lifetime.ExitCodes.Should().Equal([MacCheck.NotDrawnExitCode]);
        await OnUiAsync(window.Show);
        for (var tick = 0; tick < 5; tick++)
        {
            await OnUiAsync(() => AvaloniaHeadlessPlatform.ForceRenderTimerTick());
        }

        await OnUiAsync(window.Close);

        lifetime.ExitCodes.Should().Equal([MacCheck.NotDrawnExitCode], "the window was drawn too late, and that changes nothing");
        output.ToString().Should().Be($"HVO Roof check: the window was not drawn within 1 s.{Environment.NewLine}");
    }

    [TestMethod]
    public async Task TheCheck_ExitsWith1_AtOnce_WhenThereIsNoWindow_AndOnlyOnce()
    {
        var lifetime = new Lifetime();
        using var output = new StringWriter();
        await OnUiAsync(() =>
        {
            MacCheck.Watch(null, lifetime.Shutdown, output, TimeSpan.FromSeconds(1));
            lifetime.ExitCodes.Should().BeEmpty("the lifetime is not yet running its loop, and would lose the shutdown");
        });

        await UntilAsync(() => lifetime.ExitCodes.Count > 0, "the check to end");
        lifetime.ExitCodes.Should().Equal([MacCheck.NotDrawnExitCode]);
        await RunForAsync(TimeSpan.FromSeconds(1.5));

        lifetime.ExitCodes.Should().Equal([MacCheck.NotDrawnExitCode], "the time allowed ran out after the check ended, and changes nothing");
        output.ToString().Should().Be($"HVO Roof check: no window was opened.{Environment.NewLine}");
    }

    // ---- The refusal ----------------------------------------------------------------------------------------------------

    /// <summary>A Mac's settings folder (<see cref="MacSettingsFolder"/>), for the paths the refusal shows.</summary>
    private const string MacFolder = "/Users/olga/Library/Application Support/HVO Roof";

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TheRefusal_SaysWhy_AndWhereTheSettingsAre_AndQuits(bool fileExists)
    {
        using var directory = new WebTestSupport.TempDirectory();
        string settingsFile;
        string reason;
        if (fileExists)
        {
            settingsFile = directory.File(MacProgram.SettingsFile);
            File.WriteAllText(settingsFile, "{ }");

            // The longest reason the app gives on its own: a device key file with more than a key in it.
            reason = $"Mac:DeviceKeyFile names {MacFolder}/device-key, which must hold one API key on one line. {RoofCredential.InvalidHeaderValue}";
        }
        else
        {
            // The first launch, as docs/mac.md shows it: no settings file, so no controller.
            settingsFile = $"{MacFolder}/{MacProgram.SettingsFile}";
            File.Exists(settingsFile).Should().BeFalse();
            reason = string.Join(" ", new MacOptions().Validate());
        }

        var quits = 0;
        var window = await OnUiAsync(() =>
        {
            var window = new Window
            {
                Width = MacRefusal.WindowSize.Width,
                Height = MacRefusal.WindowSize.Height,
                Background = KioskTheme.Background,
                Content = MacRefusal.Content(reason, settingsFile, () => quits++)
            };
            window.Show();
            return window;
        });

        await OnUiAsync(() =>
        {
            window.UpdateLayout();
            Texts(window).Should().ContainInOrder(
                "HVO Roof did not start",
                reason,
                fileExists ? "Its settings file is:" : "Its settings file, which does not exist yet, is:",
                settingsFile);
            var quit = Named(window, "refusal-quit");
            var bottom = quit.TranslatePoint(new Point(0, quit.Bounds.Height), window)!.Value.Y;
            bottom.Should().BeLessThanOrEqualTo(window.ClientSize.Height, "Quit is in the window without scrolling");
            quit.Bounds.Height.Should().BeGreaterThanOrEqualTo(KioskMetrics.Desk.Touch);
        });
        if (!fileExists)
        {
            await OnUiAsync(() => Save(window, "mac-refusal"));
        }

        await OnUiAsync(() =>
        {
            var quit = Named(window, "refusal-quit");
            var centre = quit.TranslatePoint(new Point(quit.Bounds.Width / 2, quit.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        });
        foreach (var key in new[] { PhysicalKey.Enter, PhysicalKey.Escape })
        {
            await OnUiAsync(() =>
            {
                window.KeyPressQwerty(key, RawInputModifiers.None);
                window.KeyReleaseQwerty(key, RawInputModifiers.None);
            });
        }

        await OnUiAsync(window.Close);

        quits.Should().Be(3, "Quit is clicked, or Return or Escape is pressed");
    }

    // ---- The icon -------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheIcon_IsDrawnAtEachSize_AndTheBundlesIconIsTheSameDrawing()
    {
        var drawn = await OnUiAsync(() => IconSizes.ToDictionary(size => size, DrawIcon));
        var folder = Environment.GetEnvironmentVariable(IconVariable);
        foreach (var (size, png) in drawn)
        {
            if (folder is { Length: > 0 })
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, $"icon-{size}.png"), png);
            }

            var pixels = await OnUiAsync(() => Decode(png));
            pixels.Size.Should().Be(new PixelSize(size, size));
            pixels.Alpha(0, 0).Should().Be(0, "the corners outside the rounded body are clear");
            pixels.Alpha(size / 2, size / 2).Should().Be(255, "the body is opaque");
        }

        var icon = File.ReadAllBytes(MacProgramTests.BundleFile("AppIcon.icns"));
        var elements = ReadIcns(icon);
        elements.Keys.Should().BeEquivalentTo(IconElements.Keys);
        foreach (var (kind, png) in elements)
        {
            var pixels = await OnUiAsync(() => Decode(png));
            pixels.Size.Should().Be(new PixelSize(IconElements[kind], IconElements[kind]), $"{kind} is the {IconElements[kind]}-pixel image");
        }

        // The bundle's largest image is what MacIcon draws now: change the drawing, and AppIcon.icns is made again.
        var bundled = await OnUiAsync(() => Decode(elements["ic10"]));
        var current = await OnUiAsync(() => Decode(drawn[1024]));
        bundled.Differences(current, tolerance: 8).Should().BeLessThan(1024 * 1024 / 1000,
            $"AppIcon.icns should be MacIcon's drawing; make it again as docs/mac.md says (set {IconVariable})");
    }

    private static byte[] DrawIcon(int size)
    {
        var icon = new MacIcon();
        icon.Measure(new Size(size, size));
        icon.Arrange(new Rect(0, 0, size, size));
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(icon);
        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        return png.ToArray();
    }

    private static Dictionary<string, byte[]> ReadIcns(byte[] icon)
    {
        System.Text.Encoding.ASCII.GetString(icon, 0, 4).Should().Be("icns");
        BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(4)).Should().Be((uint)icon.Length);
        var elements = new Dictionary<string, byte[]>();
        for (var at = 8; at < icon.Length;)
        {
            var kind = System.Text.Encoding.ASCII.GetString(icon, at, 4);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(at + 4));
            elements.Add(kind, icon[(at + 8)..(at + length)]);
            at += length;
        }

        return elements;
    }

    private static Pixels Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var bitmap = WriteableBitmap.Decode(stream);
        using var buffer = bitmap.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return new Pixels(bytes, buffer.RowBytes, buffer.Size);
    }

    private static List<string> Texts(Window window) => window.GetVisualDescendants().OfType<TextBlock>()
        .Select(block => block.Text ?? string.Empty)
        .Where(text => text.Length > 0)
        .ToList();

    private static Control Named(Window window, string name)
        => window.GetVisualDescendants().OfType<Control>().First(control => control.Name == name);

    private void Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Avalonia drew no frame.");
        var directory = Environment.GetEnvironmentVariable(KioskScreen.RendersVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), "kiosk");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}-{window.ClientSize.Width:0}x{window.ClientSize.Height:0}.png");
        frame.Save(path, PngBitmapEncoderOptions.Default);
        TestContext.AddResultFile(path);
    }

    /// <summary>A decoded image's pixels, four bytes each (alpha last).</summary>
    private sealed record Pixels(byte[] Bytes, int RowBytes, PixelSize Size)
    {
        public byte Alpha(int x, int y) => Bytes[(y * RowBytes) + (x * 4) + 3];

        /// <summary>How many pixels differ from <paramref name="other"/>'s by more than <paramref name="tolerance"/> in a channel.</summary>
        public int Differences(Pixels other, int tolerance)
        {
            Size.Should().Be(other.Size);
            var differences = 0;
            for (var y = 0; y < Size.Height; y++)
            {
                for (var x = 0; x < Size.Width; x++)
                {
                    var at = (y * RowBytes) + (x * 4);
                    var there = (y * other.RowBytes) + (x * 4);
                    for (var channel = 0; channel < 4; channel++)
                    {
                        if (Math.Abs(Bytes[at + channel] - other.Bytes[there + channel]) > tolerance)
                        {
                            differences++;
                            break;
                        }
                    }
                }
            }

            return differences;
        }
    }

    /// <summary>The app's lifetime, as far as <see cref="MacCheck"/> uses it: the exit codes it is shut down with.</summary>
    private sealed class Lifetime
    {
        public List<int> ExitCodes { get; } = [];

        public void Shutdown(int exitCode) => ExitCodes.Add(exitCode);
    }
}
