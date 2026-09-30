using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using HVO.RoofControllerV4.Screens;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// Avalonia's headless platform for the kiosk's tests: the kiosk's application, drawn by Skia with the kiosk's font.
/// Avalonia starts once in a process, so the session starts with the first test that needs it and stops when the test
/// run ends.
/// </summary>
[TestClass]
public sealed class KioskAvalonia
{
    private static readonly Lock Gate = new();
    private static HeadlessUnitTestSession? _session;

    private static HeadlessUnitTestSession Session
    {
        get
        {
            lock (Gate)
            {
                return _session ??= HeadlessUnitTestSession.StartNew(typeof(KioskAvalonia), AvaloniaTestIsolationLevel.PerAssembly);
            }
        }
    }

    /// <summary>The headless session's application: the kiosk's, drawn by Skia with the kiosk's font.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<KioskApp>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    [AssemblyCleanup]
    public static void StopAvalonia()
    {
        lock (Gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>Runs <paramref name="action"/> on Avalonia's thread, after what is waiting there (the shell's updates).</summary>
    public static Task OnUiAsync(Action action) => OffUiAsync(Session.Dispatch(
        () =>
        {
            Dispatcher.UIThread.RunJobs();
            action();
            return true;
        },
        CancellationToken.None));

    public static Task<T> OnUiAsync<T>(Func<T> read) => OffUiAsync(Session.Dispatch(
        () =>
        {
            Dispatcher.UIThread.RunJobs();
            return read();
        },
        CancellationToken.None));

    /// <summary>
    /// Runs Avalonia's thread for <paramref name="time"/>, as a running app does: its timers fire. Between the tests'
    /// dispatches the thread only waits for the next one.
    /// </summary>
    public static Task RunForAsync(TimeSpan time) => OffUiAsync(Session.Dispatch(
        async () =>
        {
            await Task.Delay(time);
            return true;
        },
        CancellationToken.None));

    /// <summary>
    /// The result of a dispatch, with the test going on on the thread pool. A dispatch completes on Avalonia's thread,
    /// where an await would go on: the test would hold that thread, and after the last test MSTest would stop the
    /// session (<see cref="StopAvalonia"/>) on it, waiting for it to finish.
    /// </summary>
    private static async Task<T> OffUiAsync<T>(Task<T> dispatch)
    {
        var result = await dispatch.ConfigureAwait(false);
        await Task.Yield();
        return result;
    }

    /// <summary>Waits until <paramref name="condition"/>, read on Avalonia's thread, holds, for up to <paramref name="timeout"/> (15 s).</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!await OnUiAsync(condition))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(20);
        }
    }
}
