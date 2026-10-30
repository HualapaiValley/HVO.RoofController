using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// A Linux account's names come from the operating system (#209): its full name (the GECOS field, from the native
/// worker's <c>/v1/profile</c>) and its user name, refreshed at each sign-in and read-only in the controller. The worker
/// here is a fake on a Unix socket; no native PAM, account or sudo is involved.
/// </summary>
[TestClass]
public sealed class LinuxAccountNamesTests
{
    private const string FullName = "Fixture Owner";

    [TestMethod]
    [DataRow("Fixture Owner,Room 1,555-0100,,", "Fixture Owner")]
    [DataRow("  Fixture Owner  ", "Fixture Owner")]
    [DataRow(",Room 1", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void TheFullName_IsTheGecosFieldsFirstPart(string? gecos, string? expected)
        => HVO.RoofControllerV4.LinuxAuth.Native.GecosFullName(gecos).Should().Be(expected);

    [TestMethod]
    public void TheStore_KeepsTheOsFullName_AndKeepsTheLastOneWhenTheWorkerCannotSay()
    {
        using var directory = new TemporaryDirectory();
        using var rig = new IdentityRig(directory.File("identity.json"), new RoofIdentityOptions { LinuxAuth = new() { Enabled = true } });
        var (principal, linux) = Enroll(rig.Store);

        Session(rig.Store, principal, linux, FullName);
        rig.Store.FindLinux(linux.Subject)!.DisplayName.Should().Be(FullName);
        Session(rig.Store, principal, linux, null);
        rig.Store.FindLinux(linux.Subject)!.DisplayName.Should().Be(FullName, "an older or busy worker leaves the last full name");
        Session(rig.Store, principal, linux, "Bad\u202EName");
        rig.Store.FindLinux(linux.Subject)!.DisplayName.Should().Be(FullName, "a full name that is not a valid display name is not kept");

        var listed = rig.Store.ProviderPrincipals.Single(candidate => candidate.Id == principal.Id);
        listed.DisplayName.Should().Be(FullName);
        listed.ShortName.Should().Be("fixture", "a Linux account's short name is its user name");
        rig.Store.FindLinux(linux.Subject)!.Stamp.Should().Be(linux.Stamp, "a name from the OS changes no stamp");
    }

    [TestMethod]
    public async Task ALinuxSession_SeesItsOsNames_AndCannotChangeThem()
    {
        if (!Socket.OSSupportsUnixDomainSockets) Assert.Inconclusive("The native worker needs Unix sockets.");
        using var directory = new TemporaryDirectory();
        var socketPath = directory.File("worker.sock");
        await using var worker = await StartWorkerAsync(socketPath, profile: true);
        await using var host = Host(directory, socketPath);
        var store = host.Services.GetRequiredService<RoofIdentityStore>();
        var native = host.Services.GetRequiredService<RoofNativeAuthClient>();
        var (principal, linux) = Enroll(store);

        native.GetFullName(linux).Should().Be(FullName);
        var issued = Session(store, principal, linux, native.GetFullName(linux));
        using var client = host.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, issued.Token);

        var me = await ApiJson.ReadAsync<RoofCallerResponse>(await client.GetAsync("/api/v4.0/Auth/Me"));
        var refused = await client.PostAsJsonAsync("/api/v4.0/Auth/Names", new RoofNamesChangeRequest { DisplayName = "Someone Else" });

        me.DisplayName.Should().Be(FullName);
        me.ShortName.Should().Be("fixture");
        me.LoginType.Should().Be(RoofLoginTypes.Linux);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ApiJson.ReadElementAsync(refused);
        problem.GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.IdentityReadOnly));
        store.FindLinux(linux.Subject)!.DisplayName.Should().Be(FullName);
    }

    [TestMethod]
    public async Task AWorkerOlderThanProfiles_GivesNoFullName_AndTheSignInStillWorks()
    {
        if (!Socket.OSSupportsUnixDomainSockets) Assert.Inconclusive("The native worker needs Unix sockets.");
        using var directory = new TemporaryDirectory();
        var socketPath = directory.File("worker.sock");
        await using var worker = await StartWorkerAsync(socketPath, profile: false);
        await using var host = Host(directory, socketPath);
        var store = host.Services.GetRequiredService<RoofIdentityStore>();
        var native = host.Services.GetRequiredService<RoofNativeAuthClient>();
        var (principal, linux) = Enroll(store);

        native.GetFullName(linux).Should().BeNull("an older worker answers 404 for /v1/profile");
        var issued = Session(store, principal, linux, native.GetFullName(linux));
        using var client = host.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, issued.Token);

        var me = await ApiJson.ReadAsync<RoofCallerResponse>(await client.GetAsync("/api/v4.0/Auth/Me"));
        me.DisplayName.Should().BeNull();
        me.ShortName.Should().Be("fixture");
    }

    private static (RoofPrincipalResponse Principal, StoredLinuxPrincipal Linux) Enroll(RoofIdentityStore store)
    {
        var request = new RoofPrincipalEnrollRequest
        {
            Provider = "linux",
            Subject = Guid.NewGuid().ToString("N"),
            Name = "fixture",
            EnrollmentStamp = Guid.NewGuid().ToString("N"),
            Role = RoofControllerApiContract.OperatorRole
        };
        var principal = store.EnrollLinux(request).Value!;
        return (principal, store.FindLinux(request.Subject)!);
    }

    private static RoofIssuedSession Session(RoofIdentityStore store, RoofPrincipalResponse principal, StoredLinuxPrincipal linux, string? fullName)
    {
        var issued = store.CreateProviderSession(principal.Id, linux.Stamp, assertion:
            new StoredAssertionNonce(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddSeconds(30)),
            osDisplayName: fullName);
        issued.Succeeded.Should().BeTrue(issued.Detail);
        return issued.Value!;
    }

    private static RoofApiTestHost Host(TemporaryDirectory directory, string socketPath)
        => new(settings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"] = directory.File("identity.json"),
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true",
            ["RoofControllerSecurity:Identity:LinuxAuth:SocketPath"] = socketPath
        });

    /// <summary>A fake native worker: revalidation always current, and <c>/v1/profile</c> only when <paramref name="profile"/>.</summary>
    private static async Task<WebApplication> StartWorkerAsync(string socketPath, bool profile)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socketPath));
        var worker = builder.Build();
        worker.MapPost("/v1/revalidate", () => HVO.RoofControllerV4.LinuxAuth.Program.RevalidationResult(new(true, "fixture")));
        if (profile)
        {
            worker.MapPost("/v1/profile", () => Results.Json(new { fullName = FullName }));
        }

        await worker.StartAsync();
        return worker;
    }
}
