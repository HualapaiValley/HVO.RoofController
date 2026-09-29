using System.Net;
using System.Text;
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
using Terminal.Gui.ViewBase;

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

    /// <summary>
    /// Wraps the HTTP handler of every connection (to delay or fail some requests). The status hub then cannot be
    /// reached over its WebSocket, so a hub request goes through the wrapped handler too, and can be failed there.
    /// </summary>
    public Func<HttpMessageHandler, HttpMessageHandler>? WrapHandler { get; set; }

    /// <summary>For <c>hvo-roof ui</c>: drives the interface in place of the real loop.</summary>
    public Action<IApplication, IRunnable>? RunApplication { get; set; }

    public Func<IApplication>? CreateApplication { get; set; }

    /// <summary>The process's termination signals, as the real process has them; none unless a test gives one.</summary>
    public RoofCliTermination? Termination { get; set; }

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
        CreateHandler = _server is null ? null : () => (WrapHandler ?? (handler => handler))(ClientTestSupport.CreateHandler(_server)),
        WebSocketFactory = _server is null ? null
            : WrapHandler is null ? ClientTestSupport.WebSocketFactory(_server)
            : (_, _, _) => throw new HttpRequestException("The status hub's WebSocket is not offered (test)."),
        StatusFeed = ClientTestSupport.FastFeed,
        CreateApplication = CreateApplication ?? (() => throw new InvalidOperationException("This test gives no terminal application.")),
        RunApplication = RunApplication ?? ((_, _) => { }),
        Termination = Termination
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

    /// <summary>
    /// The colours the screen shows <paramref name="text"/> in: the attribute of the cell with its first character, where
    /// it first appears (reading from the top). Fails when the text is not on the screen.
    /// </summary>
    public Terminal.Gui.Drawing.Attribute ColoursOf(string text)
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

    /// <summary>The attribute of every cell on the screen that was drawn.</summary>
    public IReadOnlyList<Terminal.Gui.Drawing.Attribute> DrawnAttributes()
    {
        App.LayoutAndDraw(true);
        var contents = App.Driver!.Contents!;
        var drawn = new List<Terminal.Gui.Drawing.Attribute>();
        for (var row = 0; row < contents.GetLength(0); row++)
        {
            for (var column = 0; column < contents.GetLength(1); column++)
            {
                if (contents[row, column].Attribute is { } attribute)
                {
                    drawn.Add(attribute);
                }
            }
        }

        return drawn;
    }

    /// <summary>The colours of the first cell of <paramref name="view"/> on the screen (a button's bracket, for one).</summary>
    public Terminal.Gui.Drawing.Attribute ColoursAt(View view)
    {
        App.LayoutAndDraw(true);
        var origin = view.FrameToScreen().Location;
        return App.Driver!.Contents![origin.Y, origin.X].Attribute ?? throw new AssertFailedException($"{view} is drawn with no colours.");
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

    /// <summary>
    /// Runs the loop until <paramref name="until"/> holds, or fails after 10 s: for waiting while a request is held on
    /// its way, which <see cref="WaitIdle"/> would wait for.
    /// </summary>
    public void WaitFor(string what, Func<bool> until) => PumpUntil(App, what, until);

    /// <summary>Runs <paramref name="app"/>'s loop until <paramref name="until"/> holds, or fails after 10 s.</summary>
    public static void PumpUntil(IApplication app, string what, Func<bool> until)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            app.TimedEvents!.RunTimers();
            if (until())
            {
                app.TimedEvents!.RunTimers();
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                app.LayoutAndDraw(true);
                throw new AssertFailedException($"Timed out waiting for {what}. Screen:\n{app.Driver!.ToString()}");
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

/// <summary>
/// Sends each request to the controller except those whose path contains one of <paramref name="paths"/> (the status
/// hub's, the lease renewal's), which cannot reach it; <paramref name="tried"/> is set when the first of them is tried.
/// </summary>
internal sealed class UnreachableHandler(HttpMessageHandler inner, TaskCompletionSource? tried, params string[] paths) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!paths.Any(path => request.RequestUri!.AbsolutePath.Contains(path, StringComparison.OrdinalIgnoreCase)))
        {
            return base.SendAsync(request, cancellationToken);
        }

        tried?.TrySetResult();
        throw new HttpRequestException("Connection refused (test).");
    }
}

/// <summary>
/// Answers the request whose path ends in <paramref name="path"/> itself, as a failing server or proxy would, while
/// <paramref name="answering"/> says so (always, when not given).
/// </summary>
internal sealed class StubAnswerHandler(
    HttpMessageHandler inner, string path, HttpStatusCode status, string mediaType, string body, Func<bool>? answering = null) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase) && (answering?.Invoke() ?? true)
            ? Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, mediaType) })
            : base.SendAsync(request, cancellationToken);
}

/// <summary>Holds the request whose path ends in <paramref name="path"/> until <paramref name="gate"/> opens, then sends it.</summary>
internal sealed class GatedHandler(HttpMessageHandler inner, string path, Task gate) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase))
        {
            await gate.WaitAsync(cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Holds the answer to the request whose path ends in <paramref name="path"/> (while <paramref name="holding"/> says so,
/// always when not given) until <paramref name="gate"/> opens, and sets <paramref name="reached"/> when it is held. With
/// <paramref name="forwardFirst"/> the controller gets the request at once and only its answer waits; without it, the
/// request itself waits, then is answered by <paramref name="heldAnswer"/> or sent on.
/// </summary>
internal sealed class HeldAnswerHandler(
    HttpMessageHandler inner,
    string path,
    TaskCompletionSource reached,
    Task gate,
    Func<bool>? holding = null,
    bool forwardFirst = true,
    Func<HttpRequestMessage, HttpResponseMessage>? heldAnswer = null) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase) || !(holding?.Invoke() ?? true))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        if (!forwardFirst)
        {
            reached.TrySetResult();
            await gate.WaitAsync(cancellationToken);
            return heldAnswer?.Invoke(request) ?? await base.SendAsync(request, cancellationToken);
        }

        var response = await base.SendAsync(request, CancellationToken.None);
        reached.TrySetResult();
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
    }
}

/// <summary>
/// A request whose bytes are on their way when the caller gives up: the one whose path ends in <paramref name="path"/>
/// reaches the controller only when <paramref name="gate"/> opens, and cancelling the caller's wait does not take it
/// back, as it cannot on a real network. <paramref name="onItsWay"/> is set when it has been sent.
/// </summary>
internal sealed class InFlightRequestHandler(HttpMessageHandler inner, string path, TaskCompletionSource onItsWay, Task gate)
    : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase))
        {
            return base.SendAsync(request, cancellationToken);
        }

        onItsWay.TrySetResult();
        return DeliverAsync(request).WaitAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> DeliverAsync(HttpRequestMessage request)
    {
        await gate;
        return await base.SendAsync(request, CancellationToken.None);
    }
}

/// <summary>
/// Sends the request whose path ends in <paramref name="path"/> to the controller, then loses its answer: throws
/// <paramref name="error"/>, as a connection dropping before the answer arrives does.
/// </summary>
internal sealed class DroppedAnswerHandler(HttpMessageHandler inner, string path, Exception error) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (!request.RequestUri!.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        response.Dispose();
        throw error;
    }
}
