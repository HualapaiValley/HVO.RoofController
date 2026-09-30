using System.Runtime.Versioning;
using System.Text;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Wizard;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.ViewBase;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The installer's wizard on a fake machine, drawn by Terminal.Gui's ANSI driver in memory: keys go in, and the screen
/// comes out as text, as the colours of its cells, and as ANSI (<see cref="Render"/>), which CI keeps and turns into the
/// docs' screenshots. Work the wizard sends off its thread (the plan's check, the install) comes back only when the
/// loop is pumped (<see cref="Pump"/>, <see cref="WaitIdle"/>), so a test can look at the screen while it is on its way.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class WizardDriver : IDisposable
{
    /// <summary>Where the renders go; unset, the test results' installer folder.</summary>
    public const string RendersVariable = "HVO_INSTALLER_RENDERS_DIR";

    /// <summary>The installer's version, with its commit, as a release build has it.</summary>
    public const string Version = "4.0.0+0123456789abcdef0123456789abcdef01234567";

    private SessionToken? _session;

    private WizardDriver(IApplication app, InstallerWizard wizard)
    {
        App = app;
        Wizard = wizard;
        _session = App.Begin(Wizard.Window);
    }

    public IApplication App { get; }

    public InstallerWizard Wizard { get; }

    public InstallerSession Session => Wizard.Session;

    public WizardPage Page => Wizard.Page;

    /// <summary>The wizard's page of type <typeparamref name="T"/>, shown or not.</summary>
    public T PageOf<T>()
        where T : WizardPage
        => Wizard.Pages.OfType<T>().Single();

    public bool Closed => ((IRunnable)Wizard.Window).StopRequested;

    /// <summary>
    /// Starts the wizard on <paramref name="machine"/> as the installer does: the machine surveyed, every command
    /// logged to <paramref name="log"/>. <paramref name="commands"/> stands in for the machine's programs (to hold one).
    /// </summary>
    public static async Task<WizardDriver> StartAsync(FakeMachine machine, InstallLog? log = null, ICommandRunner? commands = null, int width = 100, int height = 30)
    {
        var onMachine = commands is null ? machine.Machine : machine.Machine.WithCommands(commands);
        var session = await InstallerSession.StartAsync(onMachine, log ?? InstallLog.None, Version, FakeMachine.Clock);
        var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        return new WizardDriver(app, new InstallerWizard(app, session, onMachine.Environment));
    }

    /// <summary>The screen as text, after a full layout and draw.</summary>
    public string Screen
    {
        get
        {
            App.LayoutAndDraw(true);
            return App.Driver!.ToString() ?? string.Empty;
        }
    }

    /// <summary>The screen's rows, as text.</summary>
    public string[] Rows => Screen.Split('\n').Select(row => row.TrimEnd('\r')).ToArray();

    /// <summary>The colours of every cell drawn.</summary>
    public IEnumerable<TuiAttribute> DrawnAttributes()
    {
        App.LayoutAndDraw(true);
        var contents = App.Driver!.Contents!;
        for (var row = 0; row < contents.GetLength(0); row++)
        {
            for (var column = 0; column < contents.GetLength(1); column++)
            {
                if (contents[row, column].Attribute is { } attribute)
                {
                    yield return attribute;
                }
            }
        }
    }

    /// <summary>
    /// The colours the screen shows <paramref name="text"/> in: the attribute of the cell with its first character, where
    /// it first appears (reading from the top). Fails when the text is not on the screen.
    /// </summary>
    public TuiAttribute ColoursOf(string text)
    {
        App.LayoutAndDraw(true);
        var contents = App.Driver!.Contents!;
        for (var row = 0; row < contents.GetLength(0); row++)
        {
            var line = new StringBuilder();
            var columns = new List<int>();
            for (var column = 0; column < contents.GetLength(1); column++)
            {
                var grapheme = contents[row, column].Grapheme is { Length: > 0 } drawn ? drawn : " ";
                line.Append(grapheme);
                columns.AddRange(Enumerable.Repeat(column, grapheme.Length));
            }

            var index = line.ToString().IndexOf(text, StringComparison.Ordinal);
            if (index >= 0)
            {
                return contents[row, columns[index]].Attribute ?? throw new AssertFailedException($"'{text}' is drawn with no colours.");
            }
        }

        throw new AssertFailedException($"'{text}' is not on the screen:\n{Screen}");
    }

    /// <summary>The colours of the first cell of <paramref name="view"/> on the screen.</summary>
    public TuiAttribute ColoursAt(View view)
    {
        App.LayoutAndDraw(true);
        var origin = view.FrameToScreen().Location;
        return App.Driver!.Contents![origin.Y, origin.X].Attribute ?? throw new AssertFailedException($"{view} is drawn with no colours.");
    }

    /// <summary>Presses <paramref name="key"/>, then runs what the wizard posted to the loop.</summary>
    public void Press(Key key)
    {
        App.InjectKey(key);
        Pump();
    }

    /// <summary>Presses <paramref name="key"/> and leaves what the wizard sent off its thread on its way.</summary>
    public void PressOnly(Key key) => App.InjectKey(key);

    /// <summary>Types <paramref name="text"/> into the focused view.</summary>
    public void Type(string text)
    {
        foreach (var c in text)
        {
            App.InjectKey(new Key(c));
        }

        Pump();
    }

    /// <summary>Runs what the wizard's work posted back to the loop.</summary>
    public void Pump() => App.TimedEvents!.RunTimers();

    /// <summary>Runs the loop until no work is on its way and <paramref name="until"/> holds, or fails after 10 s.</summary>
    public void WaitIdle(string what, Func<bool>? until = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            Pump();
            if (Wizard.PendingOperations == 0 && (until?.Invoke() ?? true))
            {
                Pump();
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException(
                    $"Timed out waiting for {what}. Pending: {Wizard.PendingOperations}. Message: '{Wizard.Message}'. Screen:\n{Screen}");
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>Runs the loop until <paramref name="until"/> holds, with work still on its way, or fails after 10 s.</summary>
    public void PumpUntil(string what, Func<bool> until)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            Pump();
            if (until())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException($"Timed out waiting for {what}. Message: '{Wizard.Message}'. Screen:\n{Screen}");
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>Goes on to page <paramref name="index"/> with Enter (Next), waiting for each page's work.</summary>
    public void NextTo(int index)
    {
        while (Wizard.PageIndex < index)
        {
            var from = Wizard.PageIndex;
            Press(Key.Enter);
            WaitIdle($"page {from + 2}");
            if (Wizard.PageIndex == from)
            {
                throw new AssertFailedException($"Next stayed on {Page.PageTitle}: '{Wizard.Message}'. Screen:\n{Screen}");
            }
        }
    }

    /// <summary>
    /// Saves the screen as ANSI, <paramref name="name"/>.ans, in <c>HVO_INSTALLER_RENDERS_DIR</c> or the test results'
    /// installer folder, and attaches it to the test's results. CI draws each as an SVG for the docs.
    /// </summary>
    public string Render(TestContext context, string name)
    {
        App.LayoutAndDraw(true);
        var directory = Environment.GetEnvironmentVariable(RendersVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(context.TestRunResultsDirectory ?? Path.GetTempPath(), "installer");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.ans");
        File.WriteAllText(path, App.Driver!.ToAnsi());
        context.AddResultFile(path);
        return path;
    }

    public void Dispose()
    {
        if (_session is { } session)
        {
            _session = null;
            App.End(session);
        }

        Wizard.Dispose();
        App.Dispose();
    }
}

/// <summary>
/// The machine's programs, with <c>docker container inspect</c> held once <see cref="Hold"/> is called, until
/// <see cref="Release"/>: for looking at the wizard while its check is on its way.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class HeldInspect(ICommandRunner inner) : ICommandRunner, IDisposable
{
    private readonly ManualResetEventSlim _released = new(initialState: true);

    public void Hold() => _released.Reset();

    public void Release() => _released.Set();

    public string? Find(string program) => inner.Find(program);

    public async Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken = default)
    {
        if (command.Arguments is ["container", "inspect", _])
        {
            await Task.Run(() => _released.Wait(TimeSpan.FromSeconds(30), cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        return await inner.RunAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _released.Set();
        _released.Dispose();
    }
}

/// <summary>
/// The system clock for the install's log, which stops, once, when <paramref name="holdWhen"/> first holds as the log
/// reads it, until <see cref="Release"/>: so the install waits there with what it has done so far on the screen.
/// </summary>
internal sealed class HeldClock(Func<bool> holdWhen) : TimeProvider, IDisposable
{
    private readonly ManualResetEventSlim _released = new(initialState: false);
    private int _held;

    /// <summary>True once the clock has stopped (and the install with it).</summary>
    public bool Holding => Volatile.Read(ref _held) == 1 && !_released.IsSet;

    public void Release() => _released.Set();

    public override DateTimeOffset GetUtcNow()
    {
        if (Volatile.Read(ref _held) == 0 && holdWhen() && Interlocked.Exchange(ref _held, 1) == 0)
        {
            _released.Wait(TimeSpan.FromSeconds(30));
        }

        return System.GetUtcNow();
    }

    public void Dispose()
    {
        _released.Set();
        _released.Dispose();
    }
}
