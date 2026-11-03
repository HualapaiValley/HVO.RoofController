using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>The real CLI and settings API with native helper issuance replaced by an enrolled test principal.</summary>
[TestClass]
public sealed class RoofCliLocalSettingsTests
{
    private const string AllowCommands = "RoofController:Alpaca:AllowCommands";

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LocalAdmin_SettingsFormUsesConfigure_AndSetStillRequiresSafetyConfirmation(bool show)
    {
        if (!Socket.OSSupportsUnixDomainSockets) Assert.Inconclusive("Native sessions require Unix sockets.");
        using var directory = new TemporaryDirectory();
        var socketPath = directory.File("worker.sock");
        var enrollment = new RoofPrincipalEnrollRequest
        {
            Provider = "linux", Subject = Guid.NewGuid().ToString("N"), Name = "fixture",
            EnrollmentStamp = Guid.NewGuid().ToString("N"), Role = RoofControllerApiContract.AdminRole
        };
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socketPath));
        await using var worker = builder.Build();
        worker.MapPost("/v1/revalidate", () => Results.Json(new { valid = true, userName = enrollment.Name }));
        await worker.StartAsync();
        var logs = new RecordingLoggerProvider();
        await using var controller = new RoofApiTestHost(configureServices: services => services.AddSingleton<ILoggerProvider>(logs), settings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"] = directory.File("identity.json"),
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true",
            ["RoofControllerSecurity:Identity:LinuxAuth:SocketPath"] = socketPath
        });
        using var rig = new CliRig(controller);
        rig.UseApiKey(null);
        var store = controller.Services.GetRequiredService<RoofIdentityStore>();
        var principal = store.EnrollLinux(enrollment).Value!;
        var linux = store.FindLinux(enrollment.Subject)!;
        var operations = new List<string>();
        rig.ObtainLocalAssertion = (operation, _) =>
        {
            operations.Add(operation);
            var session = store.CreateProviderSession(principal.Id, linux.Stamp, operation,
                new StoredAssertionNonce(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddSeconds(30))).Value!;
            return Task.FromResult(session.Token);
        };
        rig.WrapHandler = inner => new LocalExchangeHandler(inner);

        if (show)
        {
            var shown = await rig.RunAsync("--local", "config", "show", "alpaca");
            shown.Code.Should().Be(RoofExitCode.Success, shown.ToString());
            shown.Out.Split('\n').Single(line => line.Contains(AllowCommands)).Should().NotContain("read-only");
            operations.Should().Equal("read", "configure");
            return;
        }

        operations.Clear();
        var refused = await rig.RunAsync("--local", "config", "set", AllowCommands + "=CloseOnly");
        refused.Code.Should().Be(RoofExitCode.ConfirmationRequired, refused.ToString());
        operations.Should().Equal("read", "configure");

        operations.Clear();
        var changed = await rig.RunAsync("--local", "config", "set", AllowCommands + "=CloseOnly", "--confirm-safety-critical");
        changed.Code.Should().Be(RoofExitCode.Success, changed.ToString());
        operations.Should().Equal("read", "configure", "configure");
        var reread = await rig.RunAsync("--local", "config", "get", AllowCommands);
        reread.Out.Should().Contain("CloseOnly");
        rig.Stored!.Session.Should().BeNull();
        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("AUDIT settings") && entry.Message.Contains("fixture")
            && entry.Message.Contains(AllowCommands) && entry.Message.Contains("CloseOnly"));
    }

    private sealed class LocalExchangeHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/api/v4.0/Auth/LocalSession")
                return await base.SendAsync(request, cancellationToken);
            var body = await request.Content!.ReadFromJsonAsync<RoofLocalSignInRequest>(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RoofSessionResponse(body!.Assertion, "fixture", "fixture", "RoofAdmin",
                    RoofCredentialKind.Session, DateTimeOffset.UtcNow.AddSeconds(30), null), options: RoofClientJson.Options)
            };
        }
    }
}
