using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using HVO.Core.Results;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Cli.Ui;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Terminal.Gui.Input;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// The closed-issue audit's reproducers for #255 (follow-up F2: BB-1 and BM-1, found at
/// 6e044ef18fb63d07884a955659cd9c043ede3d9c), ported, with the exit-7 hint loop and V2-F1.
/// <para>
/// The real <c>hvo-roof</c> command line and terminal interface, in this process, against the real controller in
/// process (its API, its safety interlock in Warn mode, its native assertion exchange and revalidation). Only three
/// things are doubles, as in the repository's own local-mode tests (<c>NativeSettingsRig</c>): the roof service (a
/// mock, so nothing can move), the native helper (signs an assertion with a key made for the test, in place of sudo and
/// the installed helper) and the revalidation worker (a Unix socket in the test's own directory).
/// </para>
/// <para>
/// <c>Expected_*</c> asserts what the documentation says (docs/cli.md, docs/safety-service.md), each in both ways local
/// mode is selected: <c>--local</c> on the command line, and <c>authMode: local</c> saved in the credentials file (what
/// the installer writes). Before #255 they failed: the command line refused the two query variants itself (exit 6,
/// nothing sent), and the printed hint dropped an explicit <c>--local</c>. <c>Control_*</c> shows the same harness
/// sends the neighbouring commands.
/// </para>
/// </summary>
[TestClass, DoNotParallelize]
public sealed class F2LocalRouteReproTests
{
    private const string OpenRoute = "/api/v4.0/RoofControl/Open";
    private const string OpenWithOverride = "/api/v4.0/RoofControl/Open?overrideSafety=true";
    private const string ClearFaultRoute = "/api/v4.0/RoofControl/ClearFault";
    private const string ClearFaultWithPulse = "/api/v4.0/RoofControl/ClearFault?pulseMs=250";
    private const string LocalSession = "/api/v4.0/Auth/LocalSession";

    public TestContext TestContext { get; set; } = null!;

    // ---------------------------------------------------------------------------------------------------------------
    // BB-1, command line.
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod, TestCategory("F2Expected")]
    [DataRow(true, DisplayName = "BB-1 CLI: --local open --override-safety")]
    [DataRow(false, DisplayName = "BB-1 CLI: open --override-safety with authMode local saved")]
    public async Task Expected_BB1_Cli_LocalOpenOverrideSafety_SendsOpenWithTheOverride(bool viaFlag)
    {
        using var native = new F2LocalRig("Warn", installedDefault: !viaFlag);
        string[] arguments = [.. Local(viaFlag), "open", "--no-wait", "--override-safety"];

        var result = await native.Cli.RunAsync(arguments);
        Evidence(native, result, arguments);

        using (new AssertionScope())
        {
            result.ExitCode.Should().Be(0, "docs/cli.md and docs/safety-service.md: 'open --override-safety' sends the override");
            result.Out.Should().StartWith("Open accepted.");
            native.Operations.Should().Equal(["operate"], "Open is an operate request");
            native.ClientSent.Should().Equal([$"POST {LocalSession}", $"POST {OpenWithOverride}"], "the override is a query on Open");
            native.ClientCredentials.Should().Equal([$"POST {LocalSession}: none", $"POST {OpenWithOverride}: session"],
                "the Open carries the fresh native session, and the exchange no credential");
            native.ServerSaw.Should().Equal($"POST {LocalSession}", $"POST {OpenWithOverride}");
            native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1, "the controller lets Open through with the override");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The exit-7 hint (and V2-F1): the command it prints, run exactly as printed, opens in the same authorization mode.
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod, TestCategory("F2Expected")]
    [DataRow(true, false, DisplayName = "Hint: --local, the credentials file names only the controller")]
    [DataRow(true, true, DisplayName = "Hint: --local, a saved operator API key must not substitute")]
    [DataRow(false, false, DisplayName = "Hint: authMode local saved")]
    [DataRow(false, true, DisplayName = "Hint: authMode local saved, with a saved operator API key that must not substitute")]
    public async Task Expected_HintLoop_TheDisplayedCommand_AsksTheHelperForOperate_AndOpens(bool viaFlag, bool savedRemoteKey)
    {
        using var native = new F2LocalRig("Warn", installedDefault: !viaFlag, savedApiKey: savedRemoteKey ? TestApiKeys.Operator : null);
        string[] first = [.. Local(viaFlag), "open", "--no-wait"];

        // Warn with no safety source: the conditions are Unknown, so the controller refuses; the hint says what to run.
        var refused = await native.Cli.RunAsync(first);
        Evidence(native, refused, first);
        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain("[SafetyOverrideRequired]");
        native.Operations.Should().Equal("operate");
        native.ClientSent.Should().Equal($"POST {LocalSession}", $"POST {OpenRoute}");
        native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0);

        var displayed = DisplayedCommand(refused.Error);
        TestContext.WriteLine($"[evidence] displayed command: {string.Join(' ', displayed)}");
        displayed[0].Should().Be(RoofCli.CommandName);
        native.Reset();

        // Exactly as printed. The test adds only what its fixture needs: --no-wait (the mocked roof never finishes a
        // motion) and the rig's own --credentials-file. Never --local.
        string[] followed = [.. displayed[1..], "--no-wait"];
        var opened = await native.Cli.RunAsync(followed);
        Evidence(native, opened, followed);

        using (new AssertionScope())
        {
            if (viaFlag)
            {
                displayed.Should().Contain("--local", "the operator chose local mode on the command line, so the printed command keeps it (V2-F1)");
            }
            else
            {
                displayed.Should().NotContain("--local", "the saved authMode already selects local mode for the printed command");
            }

            opened.ExitCode.Should().Be(0, "the hint's command opens with the override");
            native.Operations.Should().Equal(["operate"], "the native helper is asked for operate; no saved remote credential substitutes");
            native.ClientSent.Should().Equal([$"POST {LocalSession}", $"POST {OpenWithOverride}"]);
            native.ClientCredentials.Should().Equal([$"POST {LocalSession}: none", $"POST {OpenWithOverride}: session"],
                "the Open carries the fresh native session, never the saved API key or no credential at all");
            native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
        }
    }

    [TestMethod, TestCategory("F2Expected")]
    public async Task Expected_HintLoop_AnExplicitRemote_IsKeptInTheDisplayedCommand()
    {
        // A computer whose installed default is local, used with --remote and a saved operator key: the printed command
        // keeps --remote, so it does not change to local authorization.
        using var native = new F2LocalRig("Warn", installedDefault: true, savedApiKey: TestApiKeys.Operator);

        var refused = await native.Cli.RunAsync("--remote", "open", "--no-wait");
        Evidence(native, refused, ["--remote", "open", "--no-wait"]);
        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        native.Operations.Should().BeEmpty();

        var displayed = DisplayedCommand(refused.Error);
        TestContext.WriteLine($"[evidence] displayed command: {string.Join(' ', displayed)}");
        native.Reset();

        string[] followed = [.. displayed[1..], "--no-wait"];
        var opened = await native.Cli.RunAsync(followed);
        Evidence(native, opened, followed);

        using (new AssertionScope())
        {
            displayed.Should().Equal(RoofCli.CommandName, "--remote", "open", "--override-safety");
            opened.ExitCode.Should().Be(0, opened.ToString());
            native.Operations.Should().BeEmpty("remote mode never asks the native helper");
            native.ClientCredentials.Should().Equal($"POST {OpenWithOverride}: api-key");
            native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BB-1, terminal interface ("Open anyway").
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod, TestCategory("F2Expected")]
    [DataRow(true, DisplayName = "BB-1 TUI: hvo-roof --local ui, Open anyway")]
    [DataRow(false, DisplayName = "BB-1 TUI: hvo-roof ui with authMode local saved, Open anyway")]
    public void Expected_BB1_Tui_OpenAnyway_InLocalMode_SendsOpenWithTheOverride(bool viaFlag)
    {
        using var native = new F2LocalRig("Warn", installedDefault: !viaFlag, movesOnOpen: true, recordClientSide: false);
        using var tui = Started(native.Cli, local: viaFlag);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.BlockReason(RoofMotionDirection.Opening).Should().BeNull("Open is offered to the local operator");

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the question", () => tui.Ui.Panel is not null);
        var panel = tui.Ui.Panel!;
        panel.Prompt.Title.Should().Be(RoofSafetyText.OverrideTitle);
        panel.Buttons.Select(button => button.Text).Should().Equal(RoofSafetyText.OverrideConfirm, RoofSafetyText.OverrideCancel);
        native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0, "nothing is sent before the person confirms");

        panel.Press(RoofSafetyText.OverrideConfirm);
        tui.WaitIdle("the answer to Open anyway", () => !tui.Ui.Message.StartsWith("Sending", StringComparison.Ordinal));
        Settle(tui);
        Evidence(native, tui);

        using (new AssertionScope())
        {
            tui.Ui.Message.Should().StartWith("Open accepted.", "docs/cli.md: Open anyway sends Open with the override");
            native.Operations.Should().Contain("operate", "Open is an operate request");
            native.ServerSaw.Should().Contain($"POST {OpenWithOverride}");
            native.ServerSaw.Should().NotContain($"POST {OpenRoute}", "the only Open is the one with the override");
            native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1, "the controller lets Open through with the override");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BM-1, command line.
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod, TestCategory("F2Expected")]
    [DataRow(true, 250, DisplayName = "BM-1 CLI: --local clear-fault --pulse-ms 250")]
    [DataRow(false, 250, DisplayName = "BM-1 CLI: clear-fault --pulse-ms 250 with authMode local saved")]
    [DataRow(true, 300, DisplayName = "BM-1 CLI: --local clear-fault --pulse-ms 300 (not the controller's default)")]
    [DataRow(false, 300, DisplayName = "BM-1 CLI: clear-fault --pulse-ms 300 with authMode local saved (not the controller's default)")]
    public async Task Expected_BM1_Cli_LocalClearFaultPulseMs_SendsThePulseLength(bool viaFlag, int pulse)
    {
        using var native = new F2LocalRig("Off", installedDefault: !viaFlag);
        string[] arguments = [.. Local(viaFlag), "clear-fault", "--pulse-ms", pulse.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        var withPulse = $"{ClearFaultRoute}?pulseMs={pulse}";

        var result = await native.Cli.RunAsync(arguments);
        Evidence(native, result, arguments);

        using (new AssertionScope())
        {
            result.ExitCode.Should().Be(0, "docs/cli.md: 'clear-fault [--pulse-ms]' clears a latched fault");
            result.Out.Should().StartWith("Clear fault accepted.");
            native.Operations.Should().Equal(["operate"], "ClearFault is an operate request");
            native.ClientSent.Should().Equal([$"POST {LocalSession}", $"POST {withPulse}"], "the pulse length is a query on ClearFault");
            native.ClientCredentials.Should().Equal([$"POST {LocalSession}: none", $"POST {withPulse}: session"]);
            native.ServerSaw.Should().Equal($"POST {LocalSession}", $"POST {withPulse}");

            // 250 is also the controller's default, so the query above is what shows the length was sent; 300 is not.
            native.Roof.Verify(service => service.ClearFault(pulse, It.IsAny<CancellationToken>()), Times.Once());
            native.Calls(nameof(IRoofControllerServiceV4.ClearFault)).Should().Be(1);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Controls: the same harness sends the neighbouring commands.
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod, TestCategory("F2Control")]
    [DataRow(true, DisplayName = "Control CLI: --local open (no override), --local flag")]
    [DataRow(false, DisplayName = "Control CLI: --local open (no override), authMode local")]
    public async Task Control_Cli_LocalOpen_WithoutOverride_IsSentAndAccepted(bool viaFlag)
    {
        using var native = new F2LocalRig("Off", installedDefault: !viaFlag);
        string[] arguments = [.. Local(viaFlag), "open", "--no-wait"];

        var result = await native.Cli.RunAsync(arguments);
        Evidence(native, result, arguments);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().StartWith("Open accepted.");
        native.Operations.Should().Equal("operate");
        native.ClientSent.Should().Equal($"POST {LocalSession}", $"POST {OpenRoute}");
        native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
    }

    [TestMethod, TestCategory("F2Control")]
    public async Task Control_Cli_ApiKey_OpenWithOverride_IsSentAndAccepted()
    {
        using var keyed = new F2KeyRig("Warn");

        var refused = await keyed.Cli.RunAsync("open", "--no-wait");
        Evidence(keyed, refused, ["open", "--no-wait"]);
        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain("[SafetyOverrideRequired]").And.Contain(RoofCliContext.SafetyOverrideHint);
        keyed.ClientSent.Should().Equal($"POST {OpenRoute}");
        keyed.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0);

        keyed.Reset();

        var opened = await keyed.Cli.RunAsync("open", "--no-wait", "--override-safety");
        Evidence(keyed, opened, ["open", "--no-wait", "--override-safety"]);
        opened.Code.Should().Be(RoofExitCode.Success, opened.ToString());
        keyed.ClientSent.Should().Equal($"POST {OpenWithOverride}");
        keyed.ServerSaw.Should().Equal($"POST {OpenWithOverride}");
        keyed.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
    }

    [TestMethod, TestCategory("F2Control")]
    public async Task Control_Cli_ApiKey_ClearFaultWithPulse_IsSentAndAccepted()
    {
        using var keyed = new F2KeyRig("Off");

        var result = await keyed.Cli.RunAsync("clear-fault", "--pulse-ms", "250");
        Evidence(keyed, result, ["clear-fault", "--pulse-ms", "250"]);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        keyed.ClientSent.Should().Equal($"POST {ClearFaultWithPulse}");
        keyed.ServerSaw.Should().Equal($"POST {ClearFaultWithPulse}");
        keyed.Roof.Verify(service => service.ClearFault(250, It.IsAny<CancellationToken>()), Times.Once());
    }

    [TestMethod, TestCategory("F2Control")]
    public void Control_Tui_LocalOpen_WithoutOverride_IsSentAndAccepted()
    {
        using var native = new F2LocalRig("Off", movesOnOpen: true, recordClientSide: false);
        using var tui = Started(native.Cli);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("Open", () => tui.Ui.HoldsLease);
        Evidence(native, tui);

        tui.Ui.Panel.Should().BeNull("with the interlock off nothing is asked");
        tui.Ui.Message.Should().StartWith("Open accepted.");
        native.Operations.Should().Contain("operate");
        native.ServerSaw.Should().Contain($"POST {OpenRoute}");
        native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
    }

    [TestMethod, TestCategory("F2Control")]
    public void Control_Tui_ApiKey_OpenAnyway_IsSentAndAccepted()
    {
        using var keyed = new F2KeyRig("Warn", movesOnOpen: true, recordClientSide: false);
        using var tui = Started(keyed.Cli);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the question", () => tui.Ui.Panel is not null);
        tui.Ui.Panel!.Press(RoofSafetyText.OverrideConfirm);
        tui.WaitIdle("Open with the override", () => tui.Ui.HoldsLease);
        Evidence(keyed, tui);

        tui.Ui.Message.Should().StartWith("Open accepted.");
        keyed.ServerSaw.Should().Contain($"POST {OpenWithOverride}");
        keyed.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
    }

    /// <summary>
    /// The controller itself accepts both requests from a native "operate" session. The session is exchanged as the
    /// command line exchanges it, then sent as a plain credential, which skips <c>RoofLocalAuthorization.Operation</c>.
    /// </summary>
    [TestMethod, TestCategory("F2Control")]
    public async Task Control_Controller_AcceptsBothRequests_FromANativeOperateSession()
    {
        using var native = new F2LocalRig("Warn");
        using var exchange = ClientTestSupport.CreateClient(native.Controller);

        var forOpen = await exchange.Auth.SignInLocallyAsync(await native.Cli.ObtainLocalAssertion!("operate", CancellationToken.None), CancellationToken.None);
        using (var client = ClientTestSupport.CreateClient(native.Controller, forOpen))
        {
            await client.Roof.OpenAsync(overrideSafety: true);
        }

        var forClear = await exchange.Auth.SignInLocallyAsync(await native.Cli.ObtainLocalAssertion!("operate", CancellationToken.None), CancellationToken.None);
        using (var client = ClientTestSupport.CreateClient(native.Controller, forClear))
        {
            await client.Roof.ClearFaultAsync(250);
        }

        TestContext.WriteLine("[evidence] requests seen by the controller: " + Lines(native.ServerSaw));
        native.ServerSaw.Should().Contain($"POST {OpenWithOverride}").And.Contain($"POST {ClearFaultWithPulse}");
        native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
        native.Roof.Verify(service => service.ClearFault(250, It.IsAny<CancellationToken>()), Times.Once());
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static string[] Local(bool viaFlag) => viaFlag ? ["--local"] : [];

    /// <summary>The command in the exit-7 hint, split into its words, exactly as printed.</summary>
    private static string[] DisplayedCommand(string error)
    {
        var match = Regex.Match(error, "To open anyway: '(?<command>[^']+)'");
        match.Success.Should().BeTrue($"the refusal prints the hint:\n{error}");
        return match.Groups["command"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private void Evidence(F2Recorders rig, CliResult result, string[] arguments)
    {
        TestContext.WriteLine($"[evidence] hvo-roof {string.Join(' ', arguments)}");
        TestContext.WriteLine($"[evidence]   exit code: {result.ExitCode} ({result.Code})");
        TestContext.WriteLine($"[evidence]   stdout: {OneLine(result.Out)}");
        TestContext.WriteLine($"[evidence]   stderr: {OneLine(result.Error)}");
        TestContext.WriteLine($"[evidence]   native helper asked for: {Lines(rig.Operations)}");
        TestContext.WriteLine($"[evidence]   requests sent by the client: {Lines(rig.ClientCredentials)}");
        TestContext.WriteLine($"[evidence]   requests seen by the controller: {Lines(rig.ServerSaw)}");
        TestContext.WriteLine($"[evidence]   roof service calls: Open={rig.Calls(nameof(IRoofControllerServiceV4.Open))} ClearFault={rig.Calls(nameof(IRoofControllerServiceV4.ClearFault))}");
    }

    private void Evidence(F2Recorders rig, TuiDriver tui)
    {
        TestContext.WriteLine($"[evidence] terminal interface message line: {OneLine(tui.Ui.Message)}");
        TestContext.WriteLine($"[evidence]   holds the operator lease: {tui.Ui.HoldsLease}; panel open: {tui.Ui.Panel is not null}");
        TestContext.WriteLine($"[evidence]   native helper asked for (distinct): {Lines(rig.Operations.Distinct())}");
        TestContext.WriteLine($"[evidence]   RoofControl requests seen by the controller: {Lines(rig.ServerSaw.Where(line => line.Contains("/RoofControl/", StringComparison.Ordinal) && !line.EndsWith("/Status", StringComparison.Ordinal)))}");
        TestContext.WriteLine($"[evidence]   roof service calls: Open={rig.Calls(nameof(IRoofControllerServiceV4.Open))}");
    }

    private static string OneLine(string text) => text.Trim().Length == 0 ? "(empty)" : text.Trim().ReplaceLineEndings(" | ");

    private static string Lines(IEnumerable<string> lines) => lines.ToArray() is { Length: > 0 } all ? string.Join(", ", all) : "(none)";

    /// <summary>Runs the loop for long enough that a request sent now would have reached the controller.</summary>
    private static void Settle(TuiDriver tui)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
        while (DateTime.UtcNow < until)
        {
            tui.Pump();
            Thread.Sleep(10);
        }
    }

    private static TuiDriver Started(CliRig rig, bool local = false)
    {
        var tui = new TuiDriver(rig, local: local);
        try
        {
            tui.WaitIdle("the first status and the caller", () => tui.Ui.Status is not null && !tui.Ui.IsStale && tui.Ui.Caller is not null);
            return tui;
        }
        catch
        {
            tui.Dispose();
            throw;
        }
    }
}

/// <summary>What a run asked of the native helper, sent from the client, and what reached the controller.</summary>
internal abstract class F2Recorders
{
    /// <summary>The operations the native helper was asked to authorize, in order.</summary>
    public ConcurrentQueue<string> Operations { get; } = new();

    /// <summary>"METHOD /path?query" of each request the client's HTTP handler was given (when recorded).</summary>
    public ConcurrentQueue<string> ClientSent { get; } = new();

    /// <summary>"METHOD /path?query: credential" of each of the same requests: session, api-key or none.</summary>
    public ConcurrentQueue<string> ClientCredentials { get; } = new();

    /// <summary>"METHOD /path?query" of each request that entered the controller's pipeline.</summary>
    public ConcurrentQueue<string> ServerSaw { get; } = new();

    public Mock<IRoofControllerServiceV4> Roof { get; protected init; } = null!;

    public int Calls(string method) => Roof.Invocations.Count(invocation => invocation.Method.Name == method);

    public void Reset()
    {
        Operations.Clear();
        ClientSent.Clear();
        ClientCredentials.Clear();
        ServerSaw.Clear();
        Roof.Invocations.Clear();
    }

    /// <summary>
    /// The mocked roof service: nothing moves. With <paramref name="movesOnOpen"/>, its status then says Opening on a
    /// lease.
    /// </summary>
    protected static Mock<IRoofControllerServiceV4> CreateRoof(bool movesOnOpen)
    {
        var roof = RoofServiceMock.Create();
        if (!movesOnOpen)
        {
            return roof;
        }

        var moving = false;
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => Volatile.Read(ref moving)
            ? RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = 30 }
            : RoofServiceMock.Snapshot());
        roof.Setup(service => service.Open()).Returns(() =>
        {
            Volatile.Write(ref moving, true);
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        roof.Setup(service => service.RenewLease()).Returns(() => Result<RoofStatusResponse>.Success(
            RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = 30 }));
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(() =>
        {
            Volatile.Write(ref moving, false);
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Stopped);
        });
        return roof;
    }

    protected Action<IServiceCollection> RecordAtTheController => services => services.AddSingleton<IStartupFilter>(new ServerRecorder(ServerSaw));

    protected Func<HttpMessageHandler, HttpMessageHandler> RecordAtTheClient => inner => new ClientRecorder(inner, ClientSent, ClientCredentials);

    private sealed class ServerRecorder(ConcurrentQueue<string> seen) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                seen.Enqueue($"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
                await following(context);
            });
            next(app);
        };
    }

    private sealed class ClientRecorder(HttpMessageHandler inner, ConcurrentQueue<string> sent, ConcurrentQueue<string> credentials)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var line = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            sent.Enqueue(line);

            // Which kind of credential, never its value.
            var credential = request.Headers.Contains("X-Api-Key") ? "api-key"
                : request.Headers.Authorization is { Scheme: "Bearer" } ? "session"
                : request.Headers.Authorization is not null ? "other" : "none";
            credentials.Enqueue($"{line}: {credential}");
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>The command line with a saved operator API key (remote mode), against the controller in process.</summary>
internal sealed class F2KeyRig : F2Recorders, IDisposable
{
    public F2KeyRig(string interlockMode, bool movesOnOpen = false, bool recordClientSide = true)
    {
        Roof = CreateRoof(movesOnOpen);
        Controller = RoofClientApiTests.CreateHost(Roof, RecordAtTheController,
            new Dictionary<string, string?> { [RoofApiTestHost.SafetyInterlockModeKey] = interlockMode });
        Cli = new CliRig(Controller);
        if (recordClientSide)
        {
            Cli.WrapHandler = RecordAtTheClient;
        }

        Cli.UseApiKey(TestApiKeys.Operator);
        Cli.ObtainLocalAssertion = (_, _) => throw new AssertFailedException("Remote mode must not use the native helper.");
    }

    public RoofApiTestHost Controller { get; }

    public CliRig Cli { get; }

    public void Dispose()
    {
        Cli.Dispose();
        Controller.Dispose();
    }
}

/// <summary>
/// The command line in local authorization mode, as <c>NativeSettingsRig</c> sets it up: the real native assertion
/// verification, session exchange, revalidation and API authorization; no PAM, no sudo, no installed helper. The
/// enrolled Linux user is an operator. With <paramref name="installedDefault"/> the credentials file says
/// <c>authMode: local</c> (what the installer writes); without it the file names only the controller, and the test
/// passes <c>--local</c>. <paramref name="savedApiKey"/> also saves a remote credential, which local mode must never
/// use.
/// </summary>
internal sealed class F2LocalRig : F2Recorders, IDisposable
{
    private const string Audience = "f2-local-route-repro";
    private readonly TemporaryDirectory _directory = new();
    private readonly WebApplication _worker;
    private readonly byte[] _assertionKey = RandomNumberGenerator.GetBytes(32);

    public F2LocalRig(string interlockMode, bool installedDefault = true, bool movesOnOpen = false, bool recordClientSide = true, string? savedApiKey = null)
    {
        if (!Socket.OSSupportsUnixDomainSockets) Assert.Inconclusive("Native sessions require Unix sockets.");
        var enrollment = new RoofPrincipalEnrollRequest
        {
            Provider = "linux", Subject = Guid.NewGuid().ToString("N"), Name = "fixture",
            EnrollmentStamp = Guid.NewGuid().ToString("N"), Role = RoofControllerApiContract.OperatorRole
        };
        var socketPath = _directory.File("worker.sock");
        var keyPath = _directory.File("assertion.key");
        var keyOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) keyOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var keyFile = new FileStream(keyPath, keyOptions)) keyFile.Write(_assertionKey);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socketPath));
        _worker = builder.Build();
        _worker.MapPost("/v1/revalidate", () => Microsoft.AspNetCore.Http.Results.Json(new { valid = true, userName = enrollment.Name }));
        _worker.StartAsync().GetAwaiter().GetResult();

        Roof = CreateRoof(movesOnOpen);
        Controller = new RoofApiTestHost(Roof,
            configureServices: RecordAtTheController,
            settings: new Dictionary<string, string?>
            {
                ["RoofControllerSecurity:Identity:StorePath"] = _directory.File("identity.json"),
                ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true",
                ["RoofControllerSecurity:Identity:LinuxAuth:SocketPath"] = socketPath,
                ["RoofControllerSecurity:Identity:LinuxAuth:AssertionKeyPath"] = keyPath,
                ["RoofControllerSecurity:Identity:LinuxAuth:Audience"] = Audience,
                [RoofApiTestHost.SafetyInterlockModeKey] = interlockMode
            });
        Controller.Services.GetRequiredService<RoofIdentityStore>().EnrollLinux(enrollment).Succeeded.Should().BeTrue();
        Cli = new CliRig(Controller);
        if (recordClientSide)
        {
            Cli.WrapHandler = RecordAtTheClient;
        }

        RoofCredentialStore.Save(Cli.CredentialsPath, new RoofStoredCredentials
        {
            Controller = new Uri("http://localhost/"), AuthMode = installedDefault ? "local" : null, ApiKey = savedApiKey
        });
        Cli.ObtainLocalAssertion = (operation, _) =>
        {
            Operations.Enqueue(operation);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = new RoofNativeAssertion(1, "local-admin", "linux", enrollment.Subject, enrollment.Name,
                enrollment.EnrollmentStamp, Audience, operation, now, now + 30,
                Encode(RandomNumberGenerator.GetBytes(32)));
            var payload = JsonSerializer.SerializeToUtf8Bytes(claims, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(Encode(payload) + "." + Encode(HMACSHA256.HashData(_assertionKey, payload)));
        };

        // Starting the host and enrolling are not part of what a test measures.
        ServerSaw.Clear();
    }

    public RoofApiTestHost Controller { get; }

    public CliRig Cli { get; }

    public void Dispose()
    {
        Cli.Dispose();
        Controller.Dispose();
        _worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        CryptographicOperations.ZeroMemory(_assertionKey);
        _directory.Dispose();
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
