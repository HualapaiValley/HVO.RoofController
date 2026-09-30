using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Screens;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>A kiosk shell in a window the size of a touchscreen.</summary>
internal sealed class KioskScreen : IAsyncDisposable
{
    /// <summary>Where the renders go; unset, the test results' kiosk folder.</summary>
    public const string RendersVariable = "HVO_KIOSK_RENDERS_DIR";

    private static readonly IBrush MaskBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x32, 0x37));

    private KioskScreen(Window window, KioskShell shell, KioskMetrics metrics)
    {
        Window = window;
        Shell = shell;
        Metrics = metrics;
    }

    public Window Window { get; }

    public KioskShell Shell { get; }

    public KioskMetrics Metrics { get; }

    private string Size => $"{Window.ClientSize.Width:0}x{Window.ClientSize.Height:0}";

    public static Task<KioskScreen> ShowAsync(KioskHarness harness, int width, int height, double pixelsPerMillimetre)
        => ShowAsync(harness.Console, width, height, pixelsPerMillimetre);

    public static Task<KioskScreen> ShowAsync(KioskConsole console, int width, int height, double pixelsPerMillimetre) => OnUiAsync(() =>
    {
        var metrics = new KioskMetrics(pixelsPerMillimetre);
        var shell = new KioskShell(console, metrics);
        var window = new Window
        {
            Width = width,
            Height = height,
            SizeToContent = SizeToContent.Manual,
            Background = KioskTheme.Background,
            Content = shell
        };
        window.Show();
        return new KioskScreen(window, shell, metrics);
    });

    /// <summary>
    /// Draws the screen, saves it as <c>name-WIDTHxHEIGHT.png</c> and checks its buttons and Stop. Returns how many
    /// of its pixels are lit (not black).
    /// </summary>
    public Task<int> RenderAsync(string name, TestContext context, bool checkStop = true) => OnUiAsync(() =>
    {
        Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        using var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Avalonia drew no frame.");
        frame.PixelSize.Should().Be(new PixelSize((int)Window.ClientSize.Width, (int)Window.ClientSize.Height));
        var directory = Environment.GetEnvironmentVariable(RendersVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(context.TestRunResultsDirectory ?? Path.GetTempPath(), "kiosk");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}-{Size}.png");
        frame.Save(path, PngBitmapEncoderOptions.Default);
        context.AddResultFile(path);

        var pixels = Read(frame);
        CheckLayout(name);
        if (checkStop)
        {
            CheckStop(name, pixels);
        }

        return pixels.Lit;
    });

    /// <summary>Lays the screen out and runs <see cref="RenderAsync"/>'s layout checks, without drawing or saving it.</summary>
    public Task CheckAsync(string name) => OnUiAsync(() =>
    {
        Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        CheckLayout(name);
    });

    /// <summary>Lays the screen out and draws it, as the kiosk does when something on it changes, without saving it.</summary>
    public Task DrawAsync() => OnUiAsync(() =>
    {
        Window.UpdateLayout();
        using var frame = Window.CaptureRenderedFrame();
    });

    /// <summary>A touch on the middle of <paramref name="target"/>, as the touchscreen sends it.</summary>
    public Task TouchAsync(Control target) => OnUiAsync(() =>
    {
        var centre = Centre(target);
        var finger = Window.TouchBegin(centre, RawInputModifiers.None);
        Window.TouchEnd(finger, centre, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    });

    /// <summary>A click on the middle of <paramref name="target"/>, as the Mac app's mouse or trackpad sends it.</summary>
    public Task ClickAsync(Control target) => OnUiAsync(() =>
    {
        var centre = Centre(target);
        Window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        Window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    });

    /// <summary>Types <paramref name="text"/> on the keyboard, into what has the keyboard.</summary>
    public Task TypeAsync(string text) => OnUiAsync(() =>
    {
        Window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    });

    /// <summary>Presses and lets go of <paramref name="key"/>, on what has the keyboard.</summary>
    public Task PressAsync(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) => OnUiAsync(() =>
    {
        Window.KeyPressQwerty(key, modifiers);
        Window.KeyReleaseQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    });

    public Control? Find(string name)
        => Window.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name);

    public bool Visible(string name) => Find(name) is { IsEffectivelyVisible: true };

    public string? Text(string name) => Find(name) switch
    {
        TextBlock block => block.Text,
        Button button => KioskTheme.GetText(button),
        Border { Child: TextBlock block } => block.Text,
        null => throw new InvalidOperationException($"Nothing called {name} is on the screen."),
        var other => throw new InvalidOperationException($"{name} is a {other.GetType().Name}, which has no text.")
    };

    /// <summary>
    /// Hides every visible text that is exactly <paramref name="text"/> behind a grey bar, as the web UI's screenshots
    /// hide the host name: the renders are published, and the machine that drew them is nobody's business. Returns how
    /// many were hidden.
    /// </summary>
    public Task<int> MaskAsync(string text) => OnUiAsync(() =>
    {
        var blocks = Window.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && block.Text == text)
            .ToList();
        foreach (var block in blocks)
        {
            block.Foreground = Brushes.Transparent;
            block.Background = MaskBrush;
        }

        return blocks.Count;
    });

    /// <summary>
    /// The visible controls whose names <paramref name="which"/> picks that are not wholly on the screen as it is: cut
    /// off by what holds them, or below the page's fold, so that they are reached only by scrolling.
    /// </summary>
    public IReadOnlyList<string> OffScreen(Func<string, bool> which) => Window.GetVisualDescendants().OfType<Control>()
        .Where(control => control.IsEffectivelyVisible && control.Name is { } name && which(name))
        .Select(control => (control.Name, Shown: Shown(control), At: new Rect(control.TranslatePoint(default, Window) ?? default, control.Bounds.Size)))
        .Where(control => !control.Shown.Inflate(1).Contains(control.At))
        .Select(control => $"{control.Name} at {control.At} (the screen shows {control.Shown})")
        .ToList();

    public ValueTask DisposeAsync() => new(OnUiAsync(Window.Close));

    private Point Centre(Control target)
        => target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), Window)
            ?? throw new InvalidOperationException($"{target.Name} is not in the window.");

    /// <summary>The part of the window that shows <paramref name="visual"/>: what every clipping ancestor holds.</summary>
    private Rect Shown(Visual visual)
    {
        var shown = new Rect(Window.ClientSize);
        foreach (var holder in visual.GetVisualAncestors().OfType<Visual>().Where(ancestor => ancestor.ClipToBounds))
        {
            if (holder.TranslatePoint(default, Window) is { } at)
            {
                shown = shown.Intersect(new Rect(at, holder.Bounds.Size));
            }
        }

        return shown;
    }

    /// <summary>The texts shortened on purpose: the controller's name, the person's name on its pill, and the value being typed.</summary>
    private static bool ShortenedOnPurpose(TextBlock block)
        => block.Name is "title" or "editor-value" || block.GetVisualAncestors().OfType<Control>().Any(ancestor => ancestor.Name == "unlocked-by");

    private void CheckLayout(string name)
    {
        var screen = new Rect(Window.ClientSize);
        var buttons = Window.GetVisualDescendants().OfType<Button>()
            // A scroll bar's own buttons are not the kiosk's: a page scrolls by dragging it.
            .Where(button => button.IsEffectivelyVisible && !button.GetVisualAncestors().OfType<ScrollBar>().Any())
            .ToList();
        buttons.Should().Contain(Shell.Stop);
        var problems = new List<string>();
        foreach (var button in buttons)
        {
            var label = $"{button.Name ?? KioskTheme.GetText(button)} ({button.Bounds.Width:0}x{button.Bounds.Height:0})";
            if (button.Bounds.Width < Metrics.Touch || button.Bounds.Height < Metrics.Touch)
            {
                problems.Add($"{label} is smaller than {KioskMetrics.MinimumTouchMillimetres} mm ({Metrics.Touch} px)");
            }

            var at = button.TranslatePoint(default, Window) ?? throw new InvalidOperationException($"{label} is not in the window.");
            if (at.X < screen.Left || at.X + button.Bounds.Width > screen.Right + 0.5)
            {
                problems.Add($"{label} runs off the side of the screen at x={at.X:0}");
            }
        }

        foreach (var block in Window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible))
        {
            // A word too long for its box is broken inside the word, and a line that does not fit the box's height
            // is not laid out at all: the text shown is shorter than the text.
            var text = block.Text ?? string.Empty;
            var shown = block.TextLayout.TextLines.Sum(line => line.Length);
            if (shown < text.Length)
            {
                problems.Add($"\"{text}\" shows only \"{text[..shown]}\" in {block.Bounds.Width:0}x{block.Bounds.Height:0}");
            }

            // An ellipsis cuts a text short without shortening its lines, and a text that may not wrap can be wider than
            // its box. Only a text marked abbreviated may end in an ellipsis: its whole text is shown elsewhere. Those are
            // the three kiosk.md lists; the mark on any other is a fault.
            if (block.Classes.Contains(KioskTheme.Abbreviated))
            {
                if (!ShortenedOnPurpose(block))
                {
                    problems.Add($"\"{text}\" is marked abbreviated, but is not one of the texts kiosk.md says are shortened on purpose");
                }
            }
            else
            {
                if (block.TextLayout.TextLines.Any(line => line.HasCollapsed))
                {
                    problems.Add($"\"{text}\" is cut short with an ellipsis in {block.Bounds.Width:0}x{block.Bounds.Height:0}");
                }
                else if (block.TextWrapping == TextWrapping.NoWrap && block.TextLayout.WidthIncludingTrailingWhitespace > block.Bounds.Width + 1)
                {
                    problems.Add($"\"{text}\" ({block.TextLayout.WidthIncludingTrailingWhitespace:0} px) is wider than its box ({block.Bounds.Width:0} px)");
                }
            }

            foreach (var line in block.TextLayout.TextLines.Skip(1))
            {
                var at = line.FirstTextSourceIndex;
                if (at > 0 && at < text.Length && char.IsLetterOrDigit(text[at - 1]) && char.IsLetterOrDigit(text[at]))
                {
                    problems.Add($"\"{text}\" is broken inside a word, before \"{text[at..]}\" ({block.Bounds.Width:0} px wide)");
                }
            }

            // A text wider than what holds it is cut off at the side: every clipping ancestor up to the page holds all
            // of it, and the page all of its width. The page scrolls up and down (never sideways), so what is below it
            // is scrolled to, not cut off, and what holds the page holds the page's width.
            foreach (var holder in block.GetVisualAncestors().OfType<Visual>().Where(visual => visual.ClipToBounds))
            {
                var within = block.TranslatePoint(default, holder);
                if (within is not { } corner)
                {
                    continue;
                }

                var scrolls = holder is ScrollViewer or ScrollContentPresenter;
                var sideways = corner.X < -1 || corner.X + block.Bounds.Width > holder.Bounds.Width + 1;
                var upOrDown = !scrolls && (corner.Y < -1 || corner.Y + block.Bounds.Height > holder.Bounds.Height + 1);
                if (sideways || upOrDown)
                {
                    problems.Add($"\"{text}\" ({block.Bounds.Width:0}x{block.Bounds.Height:0}) is cut off by a {holder.GetType().Name} ({holder.Bounds.Width:0}x{holder.Bounds.Height:0})");
                    break;
                }

                if (holder is ScrollViewer)
                {
                    break;
                }
            }
        }

        problems.AddRange(Overlaps(buttons));
        problems.Should().BeEmpty(
            $"every button on {name} at {Size} is a whole touch target on the screen, no text is cut off (but for one abbreviated on purpose), and nothing is drawn over anything else");
    }

    /// <summary>
    /// The buttons and texts drawn over one another: what the screen shows of each (a button and the text on it are one
    /// thing) meets what it shows of another.
    /// </summary>
    private List<string> Overlaps(IEnumerable<Button> buttons)
    {
        var things = buttons.Cast<Control>()
            .Concat(Window.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text) && !block.GetVisualAncestors().OfType<Button>().Any()))
            .Select(thing => (Thing: thing, Drawn: Shown(thing).Intersect(new Rect(thing.TranslatePoint(default, Window) ?? default, thing.Bounds.Size)).Deflate(0.5)))
            .Where(thing => thing.Drawn.Width > 0 && thing.Drawn.Height > 0)
            .ToList();
        var overlaps = new List<string>();
        for (var i = 0; i < things.Count; i++)
        {
            for (var j = i + 1; j < things.Count; j++)
            {
                if (things[i].Drawn.Intersects(things[j].Drawn)
                    && !things[i].Thing.IsVisualAncestorOf(things[j].Thing)
                    && !things[j].Thing.IsVisualAncestorOf(things[i].Thing))
                {
                    overlaps.Add($"{Describe(things[i].Thing)} at {things[i].Drawn} is drawn over {Describe(things[j].Thing)} at {things[j].Drawn}");
                }
            }
        }

        return overlaps;

        static string Describe(Control thing) => thing switch
        {
            TextBlock block => $"\"{block.Text}\"",
            Button button => button.Name ?? KioskTheme.GetText(button),
            _ => thing.GetType().Name
        };
    }

    private void CheckStop(string name, KioskPixels pixels)
    {
        var stop = Shell.Stop;
        stop.IsEffectivelyVisible.Should().BeTrue($"Stop is on {name}");
        stop.IsEffectivelyEnabled.Should().BeTrue($"Stop is never disabled, not on {name}");
        var at = stop.TranslatePoint(default, Window)!.Value;
        var bounds = new Rect(at, stop.Bounds.Size);
        new Rect(Window.ClientSize).Contains(bounds).Should().BeTrue($"all of Stop is on the screen ({bounds})");
        bounds.Height.Should().BeGreaterThanOrEqualTo(Metrics.Touch * 2, "Stop is two touch targets tall");
        bounds.Width.Should().BeGreaterThanOrEqualTo(Metrics.Touch * 2, "Stop is two touch targets wide");

        var hit = Window.InputHitTest(bounds.Center);
        (hit is Visual visual && (visual == stop || visual.GetVisualAncestors().Contains(stop)))
            .Should().BeTrue($"nothing covers Stop on {name}; a touch on it reached {hit?.GetType().Name ?? "nothing"}");

        // Inside its black edge, beside its label: Stop is drawn in HVO Dark's Stop colour.
        var colour = pixels.At((int)(bounds.X + 6), (int)bounds.Center.Y);
        var expected = Color.Parse(RoofUiPalette.StopButton);
        new[] { colour.R - expected.R, colour.G - expected.G, colour.B - expected.B }
            .Should().OnlyContain(difference => Math.Abs(difference) <= 2, $"Stop is {RoofUiPalette.StopButton}, drawn {colour}");
    }

    private static KioskPixels Read(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return new KioskPixels(bytes, buffer.RowBytes, buffer.Size, buffer.Format == PixelFormat.Rgba8888);
    }
}

/// <summary>A drawn frame's pixels, 4 bytes each.</summary>
internal sealed record KioskPixels(byte[] Bytes, int RowBytes, PixelSize Size, bool Rgba)
{
    public Color At(int x, int y)
    {
        var i = (y * RowBytes) + (x * 4);
        return Rgba
            ? Color.FromArgb(Bytes[i + 3], Bytes[i], Bytes[i + 1], Bytes[i + 2])
            : Color.FromArgb(Bytes[i + 3], Bytes[i + 2], Bytes[i + 1], Bytes[i]);
    }

    public int Lit
    {
        get
        {
            var lit = 0;
            for (var y = 0; y < Size.Height; y++)
            {
                for (var x = 0; x < Size.Width; x++)
                {
                    var i = (y * RowBytes) + (x * 4);
                    if (Bytes[i] != 0 || Bytes[i + 1] != 0 || Bytes[i + 2] != 0)
                    {
                        lit++;
                    }
                }
            }

            return lit;
        }
    }
}
