using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Cli.Ui;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>The real CLI, native assertion exchange and settings API, with only native issuance/revalidation replaced.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RoofCliLocalSettingsTests
{
    private const string AllowCommands = "RoofController:Alpaca:AllowCommands";
    private const string DefaultCamera = "RoofControllerUi:DefaultCamera";

    // An Admin setting that is neither local-only nor safety-critical: for a native admin, only the scope decides it.
    private const string WebServerLogLevel = "Logging:LogLevel:Microsoft.AspNetCore";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LocalOperator_SettingsAreReadOnly_WithRemoteGuidance_InTextAndJson(bool explicitLocal)
    {
        using var native = new NativeSettingsRig(RoofControllerApiContract.OperatorRole);
        string[] mode = explicitLocal ? ["--local"] : [];
        var shown = await native.Cli.RunAsync([.. mode, "config", "show", "ui"]);
        shown.Code.Should().Be(RoofExitCode.Success, shown.ToString());
        shown.Out.Split('\n').Single(line => line.Contains(DefaultCamera)).Should().Contain("read-only");
        shown.Out.Should().Contain("--remote");
        var read = await native.Cli.RunAsync([.. mode, "config", "get", DefaultCamera]);
        read.Code.Should().Be(RoofExitCode.Success, read.ToString());
        read.Out.Should().Contain("read-only").And.Contain("Linux controller administrator").And.Contain("--remote")
            .And.NotContain("you may change it");

        var json = await native.Cli.RunAsync([.. mode, "--json", "config", "show", "ui"]);
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        var fields = json.Json.GetProperty("groups")[0].GetProperty("settings").EnumerateArray().ToArray();
        fields.Should().NotBeEmpty();
        foreach (var field in fields)
        {
            field.GetProperty("canWrite").GetBoolean().Should().BeFalse();
            field.GetProperty("readOnlyReason").GetString().Should().Contain("--remote");
        }
        native.Operations.Should().OnlyContain(operation => operation == "read");
        native.Cli.Stored!.Session.Should().BeNull();
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task LocalOperator_SettingsWriteIsRefusedSpecifically_WithoutRequestingConfigure(bool explicitLocal, bool json)
    {
        using var native = new NativeSettingsRig(RoofControllerApiContract.OperatorRole);
        string[] mode = explicitLocal ? ["--local"] : [];
        string[] format = json ? ["--json"] : [];
        var before = await native.Cli.RunAsync([.. mode, "--json", "config", "get", DefaultCamera]);
        native.Operations.Clear();

        var refused = await native.Cli.RunAsync([.. mode, .. format, "config", "set", DefaultCamera + "=overview"]);

        refused.Code.Should().Be(RoofExitCode.Forbidden, refused.ToString());
        var message = json ? refused.Json.GetProperty("error").GetProperty("detail").GetString() : refused.Error;
        message.Should().Contain("Linux controller administrator").And.Contain("--remote")
            .And.NotContain("Local Linux authorization failed");
        if (json) refused.Json.GetProperty("error").GetProperty("code").GetString().Should().Be("SettingNotPermitted");
        native.Operations.Should().Equal("read", "read");
        native.Logs.Entries.Should().NotContain(entry => entry.Message.Contains("AUDIT settings"));
        var after = await native.Cli.RunAsync([.. mode, "--json", "config", "get", DefaultCamera]);
        after.Json.GetProperty("value").GetRawText().Should().Be(before.Json.GetProperty("value").GetRawText());
        native.Cli.Stored!.Session.Should().BeNull();
    }

    [TestMethod]
    public void LocalOperator_TerminalSettings_ShowReadOnlyReason_AndDoNotOfferAnEdit()
    {
        using var native = new NativeSettingsRig(RoofControllerApiContract.OperatorRole);
        using var tui = new TuiDriver(native.Cli);
        tui.WaitIdle("the native local caller and initial status", () => tui.Ui.Status is not null && tui.Ui.Caller is not null);
        tui.Press(Key.F2);
        tui.WaitIdle("the settings form", () => ((RoofUiSettingsPage)tui.Ui.CurrentPage).Form is not null);
        var page = (RoofUiSettingsPage)tui.Ui.CurrentPage;
        page.Select(DefaultCamera);
        page.SelectedField!.CanWrite.Should().BeFalse();
        page.Describe().Should().Contain("(read-only)").And.Contain("Linux controller administrator").And.Contain("--remote")
            .And.NotContain("You may change it.");
        tui.Screen.Should().Contain("--remote");
        var version = page.Form!.Version;
        native.Operations.Clear();

        (FindButton(page, "Change") ?? throw new AssertFailedException("The Change button is missing.")).InvokeCommand(Command.Accept);
        tui.Pump();

        tui.Ui.Panel.Should().BeNull("read-only settings must not open an editor");
        tui.Ui.Message.Should().Contain("cannot be changed").And.Contain("--remote");
        page.Form.Version.Should().Be(version);
        native.Operations.Should().BeEmpty();
        native.Logs.Entries.Should().NotContain(entry => entry.Message.Contains("AUDIT settings"));
        tui.Render(TestContext, "256-local-operator-settings");
    }

    [TestMethod]
    [DataRow(RoofControllerApiContract.ViewerRole)]
    [DataRow(RoofControllerApiContract.OperatorRole)]
    public async Task LocalNonAdmin_ConfigureScopeRemainsDenied(string role)
    {
        using var native = new NativeSettingsRig(role);
        using var client = ClientTestSupport.CreateClient(native.Controller);
        Func<Task> authorize = async () => await client.Auth.SignInLocallyAsync(
            await native.Cli.ObtainLocalAssertion!("configure", CancellationToken.None));

        var refusal = (await authorize.Should().ThrowAsync<RoofApiException>()).Which;

        refusal.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        native.Logs.Entries.Should().NotContain(entry => entry.Message.Contains("opened session"));
        native.Operations.Should().Equal("configure");
        native.Cli.Stored!.Session.Should().BeNull();
    }

    [TestMethod]
    public async Task LocalOperator_AfterRefusal_CanExplicitlyUseRemotePasswordSession_ThenReadLocallyAgain()
    {
        using var native = new NativeSettingsRig(RoofControllerApiContract.OperatorRole);
        var refused = await native.Cli.RunAsync("--local", "config", "set", DefaultCamera + "=overview");
        refused.Code.Should().Be(RoofExitCode.Forbidden, refused.ToString());

        using var client = ClientTestSupport.CreateClient(native.Controller);
        var session = await client.Auth.SignInWithProviderAsync("linux", "fixture", TestSecrets.Password);
        session.Role.Should().Be(RoofControllerApiContract.OperatorRole);
        RoofCredentialStore.Save(native.Cli.CredentialsPath, native.Cli.Stored! with
        {
            Session = new RoofStoredSession(session.Token, session.Name, session.Role, session.SessionId, session.ExpiresUtc)
        });
        native.Operations.Clear();
        var remote = await native.Cli.RunAsync("--remote", "config", "get", DefaultCamera);
        remote.Out.Should().Contain("you may change it");
        var changed = await native.Cli.RunAsync("--remote", "config", "set", DefaultCamera + "=overview");
        changed.Code.Should().Be(RoofExitCode.Success, changed.ToString());
        native.Operations.Should().BeEmpty("explicit remote mode uses the password session");
        native.Logs.Entries.Should().Contain(entry => entry.Message.Contains("AUDIT settings") && entry.Message.Contains(DefaultCamera));

        var local = await native.Cli.RunAsync("--local", "config", "get", DefaultCamera);
        local.Out.Should().Contain("overview").And.Contain("read-only").And.Contain("--remote");
        native.Operations.Should().Equal("read", "read");
        var localWrite = await native.Cli.RunAsync("--local", "config", "set", DefaultCamera + "=other");
        localWrite.Code.Should().Be(RoofExitCode.Forbidden, localWrite.ToString());
        localWrite.Error.Should().Contain("--remote").And.NotContain("Local Linux authorization failed");
        native.Operations.Should().Equal("read", "read", "read", "read");
    }

    [TestMethod]
    [DataRow(RoofControllerApiContract.ViewerRole, "show")]
    [DataRow(RoofControllerApiContract.ViewerRole, "get")]
    [DataRow(RoofControllerApiContract.OperatorRole, "show")]
    [DataRow(RoofControllerApiContract.OperatorRole, "get")]
    public async Task LocalNonAdmin_SettingsRemainReadable_ThroughTheRealNativeExchange(string role, string command)
    {
        using var native = new NativeSettingsRig(role);
        var arguments = command == "show" ? new[] { "config", "show" } : new[] { "config", "get", "RoofControllerUi:DefaultCamera" };

        var read = await native.Cli.RunAsync(["--local", .. arguments]);

        read.Code.Should().Be(RoofExitCode.Success, read.ToString());
        read.Out.Should().Contain("DefaultCamera");
        native.Operations.Should().OnlyContain(operation => operation == "read");
        native.Cli.Stored!.Session.Should().BeNull();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LocalAdmin_SettingsFormDescribesTheConfigureWrite_AndSetStillRequiresSafetyConfirmation(bool show)
    {
        using var native = new NativeSettingsRig(RoofControllerApiContract.AdminRole);
        if (show)
        {
            var shown = await native.Cli.RunAsync("--local", "config", "show", "alpaca");
            shown.Code.Should().Be(RoofExitCode.Success, shown.ToString());
            shown.Out.Split('\n').Single(line => line.Contains(AllowCommands)).Should().NotContain("read-only");
            native.Operations.Should().Equal("read", "read");
            return;
        }

        var refused = await native.Cli.RunAsync("--local", "config", "set", AllowCommands + "=CloseOnly");
        refused.Code.Should().Be(RoofExitCode.ConfirmationRequired, refused.ToString());
        native.Operations.Should().Equal("read", "read");

        native.Operations.Clear();
        var changed = await native.Cli.RunAsync("--local", "config", "set", AllowCommands + "=CloseOnly", "--confirm-safety-critical");
        changed.Code.Should().Be(RoofExitCode.Success, changed.ToString());
        native.Operations.Should().Equal("read", "read", "configure");
        var reread = await native.Cli.RunAsync("--local", "config", "get", AllowCommands);
        reread.Out.Should().Contain("CloseOnly");
        native.Cli.Stored!.Session.Should().BeNull();
        native.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("AUDIT settings") && entry.Message.Contains("fixture")
            && entry.Message.Contains(AllowCommands) && entry.Message.Contains("CloseOnly"));
    }

    [TestMethod]
    [DataRow("RoofControllerOptionsV4:AtSpeedConfirmationTimeout", "", false)]
    [DataRow("RoofControllerOptionsV4:AllowIgnoringLimitSwitchesOnPhysicalHardware", "true", true)]
    public void LocalAdmin_TerminalSettings_SendsTheWholeRoofGroup_AfterSafetyConfirmation(string key, string value, bool localOnly)
    {
        using var native = new NativeSettingsRig(RoofControllerApiContract.AdminRole);
        using var tui = new TuiDriver(native.Cli);
        tui.WaitIdle("the native local caller and initial status", () => tui.Ui.Status is not null && tui.Ui.Caller is not null);
        tui.Press(Key.F2);
        tui.WaitIdle("the settings form", () => ((RoofUiSettingsPage)tui.Ui.CurrentPage).Form is not null);
        var page = (RoofUiSettingsPage)tui.Ui.CurrentPage;
        var before = page.Form!.Version;
        page.Select(key);
        page.SelectedField!.Setting.LocalOnly.Should().Be(localOnly);
        var change = FindButton(page, "Change") ?? throw new AssertFailedException("The Change button is missing.");
        change.InvokeCommand(Command.Accept);
        tui.Pump();
        tui.Ui.Panel.Should().NotBeNull("a verified native administrator may edit this setting");
        tui.Ui.Panel!.Fields[0].Text = value;
        tui.Ui.Panel.Press("Save");
        tui.Ui.Panel!.Prompt.Title.Should().Be("Safety-critical change");
        tui.WaitIdle("the confirmation, with nothing sent yet");
        page.Form.Version.Should().Be(before);

        tui.Ui.Panel.Press("Confirm and send");
        tui.WaitIdle("the complete roof-group update");

        tui.Ui.Message.Should().StartWith("Saved", "the read form must include every field the configure write requires");
        page.Form!.Version.Should().Be(before + 1);
        native.Operations.Should().Contain("configure");
        native.Logs.Entries.Should().Contain(entry => entry.Message.Contains("AUDIT settings") && entry.Message.Contains(key));
    }

    // PR #247 review F7: two independent guards would each answer 403 to a read-scoped write of a local-only setting,
    // the scope guard (the session lacks configure) and the settings store's local-credential check
    // (SettingNotPermitted). So the scope guard is isolated twice over: the refusal carries no settings code, and a
    // setting only the scope guard could refuse is refused the same way. The same requests with configure scope are
    // then accepted, so nothing but the scope stood in their way.
    [TestMethod]
    public async Task LocalAdmin_ReadFormPreviewsLocalWrites_ButReadScopeCannotSubmitThem()
    {
        using var native = new NativeSettingsRig(RoofControllerApiContract.AdminRole);
        using var exchange = ClientTestSupport.CreateClient(native.Controller);
        // Whatever a request needs, this client's native session has the read scope alone.
        using var client = NativeClient(native, exchange, _ => "read");
        var form = RoofSettingsForm.Create(await client.Settings.GetCatalogueAsync(), await client.Settings.GetAsync());
        var localOnly = form.FindField(AllowCommands)!;
        localOnly.Setting.LocalOnly.Should().BeTrue();
        localOnly.CanWrite.Should().BeTrue("the read form describes an admin's later configure write");
        var ordinary = form.FindField(WebServerLogLevel)!;
        ordinary.Setting.Should().BeEquivalentTo(
            new { LocalOnly = false, Safety = RoofSettingSafety.None, WriteRole = RoofControllerApiContract.AdminRole },
            "an Admin setting no local-credential or confirmation check can refuse");
        ordinary.CanWrite.Should().BeTrue();
        var alpaca = form.Edit(RoofSettingsContract.AlpacaGroup);
        alpaca.TrySet(AllowCommands, "CloseOnly", out var problem).Should().BeTrue(problem);
        var logging = form.Edit(RoofSettingsContract.LoggingGroup);
        logging.TrySet(WebServerLogLevel, nameof(LogLevel.Error), out problem).Should().BeTrue(problem);
        native.Operations.Clear();

        Func<Task> writeLocalOnly = () => client.Settings.UpdateAsync(RoofSettingsContract.AlpacaGroup, alpaca.ToRequest(true));
        Func<Task> writeOrdinary = () => client.Settings.UpdateAsync(RoofSettingsContract.LoggingGroup, logging.ToRequest());
        foreach (var (write, setting) in new[] { (writeLocalOnly, AllowCommands), (writeOrdinary, WebServerLogLevel) })
        {
            var refusal = (await write.Should().ThrowAsync<RoofApiException>("{0} needs the configure scope", setting)).Which;
            refusal.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            // The store's own refusal (the role, or a local-only setting) is SettingNotPermitted; the scope guard's has no code.
            refusal.Code.Should().BeNull("the scope guard refuses {0} before the settings store sees it", setting);
        }

        native.Operations.Should().Equal(["read", "read"], "each write was sent with a read-scoped session");
        native.Logs.Entries.Should().NotContain(entry => entry.Message.Contains("AUDIT settings"), "a refused write changes and audits nothing");
        var unchanged = RoofSettingsForm.Create(await client.Settings.GetCatalogueAsync(), await client.Settings.GetAsync());
        unchanged.Version.Should().Be(form.Version);
        unchanged.FindField(AllowCommands)!.EditText.Should().Be(localOnly.EditText);
        unchanged.FindField(WebServerLogLevel)!.EditText.Should().Be(ordinary.EditText);

        // The control: the same two changes, each sent with the scope the request needs.
        using var configuring = NativeClient(native, exchange, operation => operation);
        native.Operations.Clear();
        await configuring.Settings.UpdateAsync(RoofSettingsContract.LoggingGroup, logging.ToRequest());
        var afterFirst = RoofSettingsForm.Create(await configuring.Settings.GetCatalogueAsync(), await configuring.Settings.GetAsync());
        var alpacaAgain = afterFirst.Edit(RoofSettingsContract.AlpacaGroup);
        alpacaAgain.TrySet(AllowCommands, "CloseOnly", out problem).Should().BeTrue(problem);
        await configuring.Settings.UpdateAsync(RoofSettingsContract.AlpacaGroup, alpacaAgain.ToRequest(true));

        native.Operations.Should().Contain("configure").And.OnlyContain(operation => operation == "configure" || operation == "read");
        var changed = RoofSettingsForm.Create(await configuring.Settings.GetCatalogueAsync(), await configuring.Settings.GetAsync());
        changed.FindField(WebServerLogLevel)!.EditText.Should().Be(nameof(LogLevel.Error));
        changed.FindField(AllowCommands)!.EditText.Should().Be("CloseOnly");
        native.Logs.Entries.Should().Contain(entry => entry.Message.Contains("AUDIT settings") && entry.Message.Contains(WebServerLogLevel))
            .And.Contain(entry => entry.Message.Contains("AUDIT settings") && entry.Message.Contains(AllowCommands));
    }

    // A client whose every protected request signs in natively with the scope <paramref name="scope"/> gives for the
    // operation the request needs.
    private static RoofControllerClient NativeClient(NativeSettingsRig native, RoofControllerClient exchange, Func<string, string> scope)
        => new(new RoofConnectionOptions
        {
            BaseAddress = ClientTestSupport.BaseAddress,
            CreateHandler = () => ClientTestSupport.CreateHandler(() => native.Controller.Server),
            LocalAuthorization = async (operation, cancellationToken) => await exchange.Auth.SignInLocallyAsync(
                await native.Cli.ObtainLocalAssertion!(scope(operation), cancellationToken), cancellationToken)
        });

    private static Button? FindButton(View view, string label)
    {
        foreach (var child in view.SubViews)
        {
            if (child is Button button && button.Text == label) return button;
            if (FindButton(child, label) is { } nested) return nested;
        }
        return null;
    }
}

/// <summary>Real native signature verification, role/scope exchange, revalidation and API authorization; no PAM or sudo.</summary>
internal sealed class NativeSettingsRig : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly WebApplication _worker;
    private readonly byte[] _assertionKey = RandomNumberGenerator.GetBytes(32);

    public NativeSettingsRig(string role)
    {
        if (!Socket.OSSupportsUnixDomainSockets) Assert.Inconclusive("Native sessions require Unix sockets.");
        var enrollment = new RoofPrincipalEnrollRequest
        {
            Provider = "linux", Subject = Guid.NewGuid().ToString("N"), Name = "fixture",
            EnrollmentStamp = Guid.NewGuid().ToString("N"), Role = role
        };
        string Sign(string purpose, string operation)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = new RoofNativeAssertion(1, purpose, "linux", enrollment.Subject, enrollment.Name,
                enrollment.EnrollmentStamp, "native-settings-tests", operation, now, now + 30,
                Encode(RandomNumberGenerator.GetBytes(32)));
            var payload = JsonSerializer.SerializeToUtf8Bytes(claims, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Encode(payload) + "." + Encode(HMACSHA256.HashData(_assertionKey, payload));
        }
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
        _worker.MapPost("/v1/password", () => Microsoft.AspNetCore.Http.Results.Json(new { assertion = Sign("linux-password", "login") }));
        _worker.StartAsync().GetAwaiter().GetResult();
        var roof = new SettingsApiTests.RoofDouble();
        Controller = new RoofApiTestHost(roof.Mock,
            configureServices: services => services.AddSingleton<ILoggerProvider>(Logs),
            settings: new Dictionary<string, string?>
            {
                ["RoofControllerSecurity:Identity:StorePath"] = _directory.File("identity.json"),
                ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true",
                ["RoofControllerSecurity:Identity:LinuxAuth:SocketPath"] = socketPath,
                ["RoofControllerSecurity:Identity:LinuxAuth:AssertionKeyPath"] = keyPath,
                ["RoofControllerSecurity:Identity:LinuxAuth:Audience"] = "native-settings-tests"
            });
        roof.StartFrom(Controller);
        Controller.Services.GetRequiredService<RoofIdentityStore>().EnrollLinux(enrollment).Succeeded.Should().BeTrue();
        Cli = new CliRig(Controller);
        RoofCredentialStore.Save(Cli.CredentialsPath, new RoofStoredCredentials
        {
            Controller = new Uri("http://localhost/"), AuthMode = "local"
        });
        Cli.ObtainLocalAssertion = (operation, _) =>
        {
            Operations.Enqueue(operation);
            return Task.FromResult(Sign("local-admin", operation));
        };
    }

    public RoofApiTestHost Controller { get; }
    public CliRig Cli { get; }
    public ConcurrentQueue<string> Operations { get; } = new();
    public RecordingLoggerProvider Logs { get; } = new();

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
