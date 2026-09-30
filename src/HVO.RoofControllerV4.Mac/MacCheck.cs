using Avalonia.Controls;
using Avalonia.Threading;

namespace HVO.RoofControllerV4.Mac;

/// <summary>
/// <c>--check</c>: the app opens its window, draws it, says so and exits 0; or exits 1 if nothing is drawn in time. On a
/// Mac this proves what a build on Linux cannot: that macOS runs the signed program, and that Avalonia's native
/// windowing and Skia load and draw.
/// </summary>
public static class MacCheck
{
    /// <summary>The exit code when the window was not drawn in time.</summary>
    public const int NotDrawnExitCode = 1;

    /// <summary>
    /// Watches <paramref name="window"/>: two frames after it opens, the app is shut down (<paramref name="shutdown"/>)
    /// with 0. If that has not happened <paramref name="timeout"/> after the watch starts, it is shut down with
    /// <see cref="NotDrawnExitCode"/>. Either way, once.
    /// </summary>
    public static void Watch(Window window, Action<int> shutdown, TextWriter output, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(output);
        var done = false;
        var timer = DispatcherTimer.RunOnce(
            () =>
            {
                if (!done)
                {
                    done = true;
                    output.WriteLine($"{Program.Title} check: the window was not drawn within {timeout.TotalSeconds:0} s.");
                    shutdown(NotDrawnExitCode);
                }
            },
            timeout);
        window.Opened += (_, _) =>
            // The first frame draws the window; the second shows that drawing goes on.
            window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ =>
            {
                if (!done)
                {
                    done = true;
                    timer.Dispose();
                    output.WriteLine(
                        $"{Program.Title} check: the window opened and was drawn at {window.ClientSize.Width:0}x{window.ClientSize.Height:0} points, scale {window.RenderScaling:0.##}.");
                    shutdown(0);
                }
            }));
    }
}
