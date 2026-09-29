using System.Text.Json;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Cli.Ui;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.AspNetCore.TestHost;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// Runs <c>hvo-roof</c> in this process against an in-process controller: its own credentials directory (mode 0700),
/// its own environment (nothing from the real one), and scripted answers to its prompts.
/// </summary>
internal sealed class CliRig : IDisposable
{
    private readonly Func<TestServer>? _server;

    public CliRig(RoofApiTestHost? host = null)
        : this(host is null ? null : () => host.Server)
    {
    }

    public CliRig(Func<TestServer>? server)
    {
        _server = server;
        Directory = Path.Combine(Path.GetTempPath(), "hvo-roof-cli-tests", Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        else
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Directory)!);
            System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        CredentialsPath = Path.Combine(Directory, "credentials.json");
    }

    public string Directory { get; }

    public string CredentialsPath { get; }

    /// <summary>The environment the command sees; empty unless a test sets a variable.</summary>
    public Dictionary<string, string?> Environment { get; } = new(StringComparer.Ordinal);

    /// <summary>Answers to prompts, in order; a prompt past the end reads the end of the input (null).</summary>
    public Queue<string?> Input { get; } = new();

    /// <summary>Every prompt shown, and whether it was for a secret.</summary>
    public List<(string Prompt, bool Secret)> Prompts { get; } = [];

    /// <summary>Whether the input is a terminal (a person can answer prompts).</summary>
    public bool Interactive { get; set; }

    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>For <c>hvo-roof ui</c>: drives the interface in place of the real loop.</summary>
    public Action<IApplication, IRunnable>? RunApplication { get; set; }

    public Func<IApplication>? CreateApplication { get; set; }

    public RoofCliHost CreateHost(TextWriter output, TextWriter error) => new()
    {
        Out = output,
        Error = error,
        GetEnvironmentVariable = name => Environment.GetValueOrDefault(name),
        ReadLine = (prompt, secret) =>
        {
            lock (Prompts)
            {
                Prompts.Add((prompt, secret));
                return Input.Count > 0 ? Input.Dequeue() : null;
            }
        },
        IsInteractive = Interactive,
        Time = Time,
        CreateHandler = _server is null ? null : () => ClientTestSupport.CreateHandler(_server),
        WebSocketFactory = _server is null ? null : ClientTestSupport.WebSocketFactory(_server),
        StatusFeed = ClientTestSupport.FastFeed,
        CreateApplication = CreateApplication ?? (() => throw new InvalidOperationException("This test gives no terminal application.")),
        RunApplication = RunApplication ?? ((_, _) => { })
    };

    /// <summary>A context as the command line would make it, with this rig's credentials file.</summary>
    public RoofCliContext CreateContext(TextWriter? output = null, TextWriter? error = null)
        => new(CreateHost(output ?? TextWriter.Null, error ?? TextWriter.Null), json: false, controller: null, credentialsFile: CredentialsPath);

    /// <summary>Runs one command line, with <c>--credentials-file</c> pointing at this rig's file.</summary>
    public async Task<CliResult> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await RoofCli.RunAsync([.. args, "--credentials-file", CredentialsPath], CreateHost(output, error));
        return new CliResult(code, output.ToString(), error.ToString());
    }

    /// <summary>Saves the in-process controller's address and <paramref name="apiKey"/>, as <c>setup</c> would.</summary>
    public void UseApiKey(string? apiKey) => RoofCredentialStore.Save(
        CredentialsPath,
        new RoofStoredCredentials { Controller = ClientTestSupport.BaseAddress, ApiKey = apiKey });

    public RoofStoredCredentials? Stored => RoofCredentialStore.Load(CredentialsPath);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>One run of a command: its exit code and what it wrote.</summary>
internal sealed record CliResult(int ExitCode, string Out, string Error)
{
    public RoofExitCode Code => (RoofExitCode)ExitCode;

    public JsonElement Json => JsonDocument.Parse(Out).RootElement;

    public override string ToString() => $"exit {ExitCode}\n--- out ---\n{Out}\n--- error ---\n{Error}";
}

/// <summary>
/// The terminal interface on a virtual screen and clock. The test drives it from its own thread and never awaits
/// there (the loop's thread is the test's); requests run in the background and their answers are posted to the loop,
/// which <see cref="WaitIdle"/> runs.
/// </summary>
internal sealed class TuiDriver : IDisposable
{
    private SessionToken? _session;

    public TuiDriver(CliRig rig, int width = 120, int height = 36)
    {
        App = Application.Create(Clock);
        App.Init(DriverRegistry.Names.ANSI);
        App.Driver!.SetScreenSize(width, height);
        Ui = new RoofTerminalUi(rig.CreateContext(), App);
        Ui.Start();
        _session = App.Begin(Ui.Window);
    }

    /// <summary>The interface's clock: the one-second timer (lease renewal, staleness) moves only with <see cref="Tick"/>.</summary>
    public VirtualTimeProvider Clock { get; } = new();

    public IApplication App { get; }

    public RoofTerminalUi Ui { get; }

    /// <summary>The screen as text, after a full layout and draw.</summary>
    public string Screen
    {
        get
        {
            App.LayoutAndDraw(true);
            return App.Driver!.ToString() ?? string.Empty;
        }
    }

    public void Press(Key key)
    {
        App.InjectKey(key);
        Pump();
    }

    /// <summary>Types <paramref name="text"/> into the focused view.</summary>
    public void Type(string text)
    {
        foreach (var c in text)
        {
            App.InjectKey(new Key(c));
        }

        Pump();
    }

    /// <summary>Runs what the background requests posted to the loop.</summary>
    public void Pump() => App.TimedEvents!.RunTimers();

    /// <summary>Moves the interface's clock on by whole seconds, one timer tick at a time.</summary>
    public void Tick(int seconds = 1)
    {
        for (var i = 0; i < seconds; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Pump();
        }
    }

    /// <summary>Runs the loop until no request is on its way and <paramref name="until"/> holds, or fails after 10 s.</summary>
    public void WaitIdle(string what, Func<bool>? until = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            Pump();
            if (Ui.PendingOperations == 0 && (until?.Invoke() ?? true))
            {
                Pump();
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException(
                    $"Timed out waiting for {what}. Pending: {Ui.PendingOperations}. Message: '{Ui.Message}'. Screen:\n{Screen}");
            }

            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        if (_session is { } session)
        {
            _session = null;
            App.End(session);
        }

        Ui.Dispose();
        App.Dispose();
    }
}
