using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// <c>hvo-roof users</c>, <c>pins</c>, <c>keys</c> and <c>sessions</c> against the real identity API, in process (#45):
/// the text and JSON each writes, secrets read only from prompts (twice in a terminal), a new key's value shown once,
/// the confirmation a destructive change needs, and the exit code for each refusal (usage 2, signed out 5, role 6,
/// the controller's own refusals 7, not confirmed 10).
/// </summary>
[TestClass]
public sealed class RoofCliIdentityCommandTests
{
    private const string NotConfirmed = "Not confirmed: run the command again with --force.";

    // ---- Who may manage people ----------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Users_WithAnOperatorKey_IsForbidden()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("users", "list");

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        result.Out.Should().BeEmpty();
        result.Error.Should().Contain("Your role does not permit this.");
    }

    [TestMethod]
    public async Task Keys_WithAViewerKey_IsForbidden_AsJson()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("keys", "list", "--json");

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        var error = result.Json.GetProperty("error");
        error.GetProperty("exitCode").GetInt32().Should().Be((int)RoofExitCode.Forbidden);
        error.GetProperty("kind").GetString().Should().Be(nameof(RoofExitCode.Forbidden));
        error.GetProperty("status").GetInt32().Should().Be(403);
    }

    [TestMethod]
    public async Task Sessions_WithAWrongKey_IsSignedOut()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey("wrong-key-not-a-real-secret-000000");

        var result = await rig.RunAsync("sessions", "list");

        result.Code.Should().Be(RoofExitCode.SignedOut, result.ToString());
        result.Error.Should().Contain("Not signed in, or the credential is no longer valid. Sign in again.");
    }

    [TestMethod]
    public async Task Pins_WithoutAController_IsNotConfigured()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);

        var result = await rig.RunAsync("pins", "list");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().Contain("No controller address.");
    }

    // ---- users list and show ------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task UsersList_WithNobody_SaysHowToAddTheFirst()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("users", "list");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be("No people yet. Add one with 'hvo-roof users add NAME --role admin'." + Environment.NewLine);
    }

    [TestMethod]
    public async Task UsersList_WithPeople_IsATable_AndJson()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.AdminRole, TestSecrets.Pin);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("users", "list");
        var json = await rig.RunAsync("users", "list", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().MatchRegex(@"(?m)^NAME\s+ROLE\s+PASSWORD\s+PIN\s+SESSIONS\s+UPDATED\s*$")
            .And.MatchRegex(@"(?m)^alice\s+operator\s+yes\s+no\s+0\s+\S")
            .And.MatchRegex(@"(?m)^olive\s+admin\s+yes\s+yes\s+0\s+\S");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        var olive = json.Json.EnumerateArray().Single(user => user.GetProperty("name").GetString() == "olive");
        olive.GetProperty("role").GetString().Should().Be(RoofControllerApiContract.AdminRole);
        olive.GetProperty("hasPassword").GetBoolean().Should().BeTrue();
        olive.GetProperty("hasPin").GetBoolean().Should().BeTrue();
        olive.GetProperty("activeSessions").GetInt32().Should().Be(0);
        json.Out.Should().NotContain(TestSecrets.Password).And.NotContain(TestSecrets.Pin);
    }

    [TestMethod]
    public async Task UsersShow_DescribesThePerson_AndJson()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("users", "show", "olive");
        var json = await rig.RunAsync("users", "show", "olive", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().MatchRegex(Row("Name", "olive"))
            .And.MatchRegex(Row("Role", "operator"))
            .And.MatchRegex(Row("Password", "set"))
            .And.MatchRegex(Row("PIN", "set"))
            .And.MatchRegex(Row("Sessions", "0"))
            .And.Contain("Created:").And.Contain("Updated:");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("name").GetString().Should().Be("olive");
        json.Json.GetProperty("role").GetString().Should().Be(RoofControllerApiContract.OperatorRole);
    }

    [TestMethod]
    public async Task UsersShow_ForNobody_IsRefusedByTheController()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("users", "show", "nobody");
        var json = await rig.RunAsync("users", "show", "nobody", "--json");

        text.Code.Should().Be(RoofExitCode.Refused, text.ToString());
        text.Error.Should().Contain("The user, key or session was not found. [IdentityNotFound]")
            .And.Contain("No person has that name.");
        json.Code.Should().Be(RoofExitCode.Refused, json.ToString());
        var error = json.Json.GetProperty("error");
        error.GetProperty("status").GetInt32().Should().Be(404);
        error.GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.IdentityNotFound));
        error.GetProperty("detail").GetString().Should().Be("No person has that name.");
    }

    // ---- users add ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task UsersAdd_ReadsThePasswordAsASecret_AndAddsThePerson()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(TestSecrets.Password);

        var result = await rig.RunAsync("users", "add", "alice", "--role", "operator");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal(("Password for alice: ", true));
        result.Out.Should().StartWith("Added alice (operator).")
            .And.MatchRegex(Row("Password", "set"))
            .And.MatchRegex(Row("PIN", "not set"))
            .And.NotContain(TestSecrets.Password);
        using var signIn = ClientTestSupport.CreateClient(host);
        (await signIn.Auth.SignInAsync("alice", TestSecrets.Password)).Role.Should().Be(RoofControllerApiContract.OperatorRole);
    }

    [TestMethod]
    public async Task UsersAdd_WithOnlyAPin_AsksOnlyForThePin_AndJson()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(TestSecrets.Pin);

        var result = await rig.RunAsync("users", "add", "olive", "--role", "admin", "--pin", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal(("PIN for olive: ", true));
        result.Json.GetProperty("name").GetString().Should().Be("olive");
        result.Json.GetProperty("role").GetString().Should().Be(RoofControllerApiContract.AdminRole);
        result.Json.GetProperty("hasPassword").GetBoolean().Should().BeFalse();
        result.Json.GetProperty("hasPin").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task UsersAdd_InATerminal_AsksForEachSecretTwice()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        foreach (var answer in new[] { TestSecrets.Password, TestSecrets.Password, TestSecrets.Pin, TestSecrets.Pin })
        {
            rig.Input.Enqueue(answer);
        }

        var result = await rig.RunAsync("users", "add", "olive", "--role", "operator", "--password", "--pin");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal(
            ("Password for olive: ", true),
            ("Again: ", true),
            ("PIN for olive: ", true),
            ("Again: ", true));
        result.Out.Should().MatchRegex(Row("Password", "set")).And.MatchRegex(Row("PIN", "set"));
    }

    [TestMethod]
    public async Task UsersAdd_WhenTheTwoEntriesDiffer_SendsNothing()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(TestSecrets.Password);
        rig.Input.Enqueue(TestSecrets.OtherPassword);

        var result = await rig.RunAsync("users", "add", "alice", "--role", "operator");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("The two entries do not match.");
        (await UsersAsync(host)).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("short-pass", false, "A password must be 12 to 256 characters.")]
    [DataRow("12ab", true, "A PIN must be 6 to 12 digits.")]
    [DataRow(null, false, "No value was given for: Password for alice.")]
    public async Task UsersAdd_WithAnUnusableSecret_IsAUsageError(string? answer, bool pinOnly, string expected)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(answer);

        var result = await rig.RunAsync(["users", "add", "alice", "--role", "operator", .. pinOnly ? new[] { "--pin" } : []]);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain(expected);
        (await UsersAsync(host)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task UsersAdd_WithAnInvalidName_AsksForNothing()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("users", "add", "-bad name", "--role", "operator");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("'-bad name' is not a valid name: use 1 to 64 letters, digits, '.', '_', '@' or '-', starting with a letter or digit.");
        rig.Prompts.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(new[] { "users", "add", "alice" }, "--role")]
    [DataRow(new[] { "users", "add", "alice", "--role", "superuser" }, "'superuser' is not a role. Use viewer, operator or admin.")]
    public async Task UsersAdd_WithoutAUsableRole_IsAUsageError(string[] args, string expected)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync(args);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain(expected).And.Contain("Run 'hvo-roof users add --help' for usage.");
        rig.Prompts.Should().BeEmpty();
    }

    [TestMethod]
    public async Task UsersAdd_ATakenName_IsRefusedByTheController()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(TestSecrets.OtherPassword);

        var result = await rig.RunAsync("users", "add", "alice", "--role", "admin");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("That name is already in use. [IdentityNameConflict]")
            .And.Contain("A person named 'alice' already exists.");
    }

    // ---- users set ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task UsersSet_Role_ChangesIt_AndJson()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("users", "set", "alice", "--role", "admin");
        var json = await rig.RunAsync("users", "set", "alice", "--role", "viewer", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().StartWith("Updated alice.").And.MatchRegex(Row("Role", "admin"));
        rig.Prompts.Should().BeEmpty();
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("role").GetString().Should().Be(RoofControllerApiContract.ViewerRole);
    }

    [TestMethod]
    public async Task UsersSet_Password_KeepsTheRole_AndTheNewPasswordWorks()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(TestSecrets.OtherPassword);

        var result = await rig.RunAsync("users", "set", "alice", "--password");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal(("New password for alice: ", true));
        result.Out.Should().MatchRegex(Row("Role", "operator"));
        using var signIn = ClientTestSupport.CreateClient(host);
        (await RoofClientApiTests.RefusedAsync(() => signIn.Auth.SignInAsync("alice", TestSecrets.Password)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await signIn.Auth.SignInAsync("alice", TestSecrets.OtherPassword)).Name.Should().Be("alice");
    }

    [TestMethod]
    public async Task UsersSet_PinThenRemovePin_GivesAndTakesAPin()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(TestSecrets.Pin);

        var given = await rig.RunAsync("users", "set", "alice", "--pin");
        var taken = await rig.RunAsync("users", "set", "alice", "--remove-pin");

        given.Code.Should().Be(RoofExitCode.Success, given.ToString());
        given.Out.Should().MatchRegex(Row("PIN", "set"));
        rig.Prompts.Should().Equal(("New PIN for alice: ", true));
        taken.Code.Should().Be(RoofExitCode.Success, taken.ToString());
        taken.Out.Should().MatchRegex(Row("PIN", "not set"));
    }

    [TestMethod]
    [DataRow(new[] { "--password", "--remove-password" }, "--password and --remove-password cannot be used together.")]
    [DataRow(new[] { "--pin", "--remove-pin" }, "--pin and --remove-pin cannot be used together.")]
    [DataRow(new string[0], "Say what to change: --role, --password, --pin, --remove-password or --remove-pin.")]
    public async Task UsersSet_WithConflictingOrNoChanges_IsAUsageError(string[] options, string expected)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync(["users", "set", "alice", .. options]);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain(expected).And.Contain("Run 'hvo-roof users set --help' for usage.");
        rig.Prompts.Should().BeEmpty();
    }

    [TestMethod]
    public async Task UsersSet_ForNobody_IsRefusedByTheController()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("users", "set", "nobody", "--remove-pin");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("[IdentityNotFound]").And.Contain("No person has that name.");
    }

    // ---- users remove -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task UsersRemove_WithoutForce_AndNoTerminal_IsNotConfirmed()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("users", "remove", "alice");
        rig.Interactive = true;
        var json = await rig.RunAsync("users", "remove", "alice", "--json");

        text.Code.Should().Be(RoofExitCode.ConfirmationRequired, text.ToString());
        text.Error.Should().Contain($"Remove alice and end all their sessions? {NotConfirmed}");
        json.Code.Should().Be(RoofExitCode.ConfirmationRequired, json.ToString());
        json.Error.Should().Contain(NotConfirmed, "--json never prompts");
        rig.Prompts.Should().BeEmpty();
        (await UsersAsync(host)).Should().ContainSingle(user => user.Name == "alice");
    }

    [TestMethod]
    public async Task UsersRemove_InATerminal_Yes_RemovesThePerson()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue("y");

        var result = await rig.RunAsync("users", "remove", "alice");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal(("Remove alice and end all their sessions? [y/N] ", false));
        result.Out.Should().Be("Removed alice." + Environment.NewLine);
        (await UsersAsync(host)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task UsersRemove_InATerminal_No_SendsNothing()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue("n");

        var result = await rig.RunAsync("users", "remove", "alice");

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        result.Error.Should().Contain("Not confirmed; nothing was sent.");
        (await UsersAsync(host)).Should().ContainSingle(user => user.Name == "alice");
    }

    [TestMethod]
    public async Task UsersRemove_WithForce_Json_SaysWhoWasRemoved()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("users", "remove", "alice", "--force", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("removed").GetString().Should().Be("alice");
        (await UsersAsync(host)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task UsersRemoveOrDemote_TheLastAdmin_IsRefusedByTheController()
    {
        using var host = new RoofApiTestHost(
            settings: new Dictionary<string, string?>
            {
                ["RoofControllerSecurity:ApiKeys:0:Name"] = "viewer",
                ["RoofControllerSecurity:ApiKeys:0:Role"] = RoofControllerApiContract.ViewerRole,
                ["RoofControllerSecurity:ApiKeys:0:Key"] = TestApiKeys.Viewer
            },
            includeDefaultKeys: false,
            configureServices: services => services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000));
        var store = host.Services.GetRequiredService<RoofIdentityStore>();
        var hasher = host.Services.GetRequiredService<RoofSecretHasher>();
        store.AddUser("root", RoofControllerApiContract.AdminRole, await hasher.HashAsync(TestSecrets.Password, CancellationToken.None), null)
            .Succeeded.Should().BeTrue();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        rig.Input.Enqueue(TestSecrets.Password);
        var login = await rig.RunAsync("login", "root");
        login.Code.Should().Be(RoofExitCode.Success, login.ToString());

        var removed = await rig.RunAsync("users", "remove", "root", "--force");
        var demoted = await rig.RunAsync("users", "set", "root", "--role", "operator", "--json");

        removed.Code.Should().Be(RoofExitCode.Refused, removed.ToString());
        removed.Error.Should().Contain("The last administrator credential cannot be removed or demoted. [LastAdministrator]")
            .And.Contain("This would leave no admin credential (an admin API key, or an admin with a password). Add another admin first.");
        demoted.Code.Should().Be(RoofExitCode.Refused, demoted.ToString());
        demoted.Json.GetProperty("error").GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.LastAdministrator));
        var still = await rig.RunAsync("users", "show", "root", "--json");
        still.Code.Should().Be(RoofExitCode.Success, still.ToString());
        still.Json.GetProperty("role").GetString().Should().Be(RoofControllerApiContract.AdminRole);
    }

    // ---- pins ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task PinsList_WithNoPins_SaysHowToGiveOne()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("pins", "list");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be("No one has a PIN. Give one with 'hvo-roof pins set NAME'." + Environment.NewLine);
    }

    [TestMethod]
    public async Task PinsList_ShowsOnlyThePeopleWithAPin_AndJson()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("pins", "list");
        var json = await rig.RunAsync("pins", "list", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().MatchRegex(@"(?m)^NAME\s+ROLE\s*$").And.MatchRegex(@"(?m)^olive\s+operator\s*$").And.NotContain("alice");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        var only = json.Json.EnumerateArray().Should().ContainSingle().Which;
        only.GetProperty("name").GetString().Should().Be("olive");
        only.GetProperty("role").GetString().Should().Be(RoofControllerApiContract.OperatorRole);
        json.Out.Should().NotContain(TestSecrets.Pin);
    }

    [TestMethod]
    public async Task PinsSet_ReadsThePin_KeepsTheRole_AndJson()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.AdminRole);
        await RoofClientApiTests.AddUserAsync(host, "bob", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(TestSecrets.Pin);
        rig.Input.Enqueue(TestSecrets.OtherPin);

        var text = await rig.RunAsync("pins", "set", "alice");
        var json = await rig.RunAsync("pins", "set", "bob", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().Be("PIN set for alice. Their PIN sessions were ended." + Environment.NewLine);
        rig.Prompts.Should().Equal(("PIN for alice: ", true), ("PIN for bob: ", true));
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("hasPin").GetBoolean().Should().BeTrue();
        json.Json.GetProperty("role").GetString().Should().Be(RoofControllerApiContract.OperatorRole);
        var users = await UsersAsync(host);
        users.Single(user => user.Name == "alice").Should().Match<RoofUserResponse>(user => user.HasPin && user.Role == RoofControllerApiContract.AdminRole);
    }

    [TestMethod]
    public async Task PinsSet_WithABadPin_IsAUsageError()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue("12345");

        var result = await rig.RunAsync("pins", "set", "alice");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("A PIN must be 6 to 12 digits.");
        (await UsersAsync(host)).Single().HasPin.Should().BeFalse();
    }

    [TestMethod]
    public async Task PinsSet_ForNobody_IsRefusedByTheController()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(TestSecrets.Pin);

        var result = await rig.RunAsync("pins", "set", "nobody");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("[IdentityNotFound]");
    }

    [TestMethod]
    public async Task PinsRemove_TakesThePin_AndJson()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
        await RoofClientApiTests.AddUserAsync(host, "oscar", RoofControllerApiContract.OperatorRole, TestSecrets.OtherPin);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("pins", "remove", "olive");
        var json = await rig.RunAsync("pins", "remove", "oscar", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().Be("PIN removed for olive." + Environment.NewLine);
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("hasPin").GetBoolean().Should().BeFalse();
        (await UsersAsync(host)).Should().OnlyContain(user => !user.HasPin && user.HasPassword);
    }

    // ---- keys list and add --------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task KeysList_ShowsTheConfiguredKeys_ButNeverTheirValues()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("keys", "list");
        var json = await rig.RunAsync("keys", "list", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().MatchRegex(@"(?m)^NAME\s+ROLE\s+KIOSK\s+SOURCE\s+UPDATED\s*$")
            .And.MatchRegex(@"(?m)^test-admin\s+admin\s+no\s+configuration \(read-only\)\s*$")
            .And.MatchRegex(@"(?m)^test-kiosk\s+viewer\s+yes\s+configuration \(read-only\)\s*$");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.EnumerateArray().Select(key => key.GetProperty("name").GetString()).Should()
            .Contain(["test-viewer", "test-operator", "test-admin", "test-hashed-viewer", "test-kiosk"]);
        json.Json.EnumerateArray().Single(key => key.GetProperty("name").GetString() == "test-kiosk")
            .GetProperty("kiosk").GetBoolean().Should().BeTrue();
        foreach (var output in new[] { text.Out, json.Out })
        {
            output.Should().NotContain(TestApiKeys.Admin).And.NotContain(TestApiKeys.Viewer).And.NotContain(RoofClientApiTests.KioskKey);
        }
    }

    [TestMethod]
    public async Task KeysAdd_PrintsTheValueOnceAlone_WithTheNoteOnStandardError()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("keys", "add", "script", "--role", "operator");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Error.Should().Be(
            "Added script (operator). Its value follows. It is shown only now: store it where only its device or script can read it."
            + Environment.NewLine);
        var secret = result.Out.TrimEnd('\r', '\n');
        secret.Should().NotBeNullOrWhiteSpace().And.NotContain(Environment.NewLine);
        using var script = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(secret));
        var caller = await script.Auth.GetCallerAsync();
        caller.Name.Should().Be("script");
        caller.Role.Should().Be(RoofControllerApiContract.OperatorRole);

        var listed = await rig.RunAsync("keys", "list", "--json");
        listed.Out.Should().NotContain(secret, "the value is shown only when it is made");
        listed.Json.EnumerateArray().Single(key => key.GetProperty("name").GetString() == "script")
            .GetProperty("source").GetString().Should().Be(nameof(RoofApiKeySource.Managed));
    }

    [TestMethod]
    public async Task KeysAdd_Json_HoldsTheKeyAndItsValue()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("keys", "add", "lobby", "--role", "viewer", "--kiosk", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Error.Should().BeEmpty();
        var key = result.Json.GetProperty("key");
        key.GetProperty("name").GetString().Should().Be("lobby");
        key.GetProperty("role").GetString().Should().Be(RoofControllerApiContract.ViewerRole);
        key.GetProperty("kiosk").GetBoolean().Should().BeTrue();
        var secret = result.Json.GetProperty("secret").GetString();
        secret.Should().NotBeNullOrWhiteSpace();
        using var lobby = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(secret!));
        (await lobby.Auth.GetCallerAsync()).Name.Should().Be("lobby");
    }

    [TestMethod]
    public async Task KeysAdd_AKioskKey_SaysSo()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("keys", "add", "lobby", "--role", "viewer", "--kiosk");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Error.Should().StartWith("Added lobby (viewer, kiosk). Its value follows.");
    }

    [TestMethod]
    public async Task KeysAdd_AKioskKeyThatIsNotAViewer_IsRefusedByTheController()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("keys", "add", "lobby", "--role", "admin", "--kiosk");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Out.Should().BeEmpty();
        result.Error.Should().Contain("The request was invalid. [InvalidRequest]")
            .And.Contain("A kiosk key must have the RoofViewer role: a PIN unlocks more.");
    }

    [TestMethod]
    public async Task KeysAdd_ATakenName_IsRefused_AndAnInvalidName_IsAUsageError()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        (await rig.RunAsync("keys", "add", "script", "--role", "viewer")).Code.Should().Be(RoofExitCode.Success);

        var taken = await rig.RunAsync("keys", "add", "script", "--role", "viewer");
        var invalid = await rig.RunAsync("keys", "add", "my script", "--role", "viewer");

        taken.Code.Should().Be(RoofExitCode.Refused, taken.ToString());
        taken.Error.Should().Contain("That name is already in use. [IdentityNameConflict]")
            .And.Contain("An API key named 'script' already exists.");
        taken.Out.Should().BeEmpty();
        invalid.Code.Should().Be(RoofExitCode.Usage, invalid.ToString());
        invalid.Error.Should().Contain("'my script' is not a valid name:");
    }

    // ---- keys set -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task KeysSet_ChangesTheRoleOrTheKioskFlag_KeepingTheOther()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        (await rig.RunAsync("keys", "add", "lobby", "--role", "viewer", "--kiosk")).Code.Should().Be(RoofExitCode.Success);
        (await rig.RunAsync("keys", "add", "script", "--role", "viewer")).Code.Should().Be(RoofExitCode.Success);

        var unkiosked = await rig.RunAsync("keys", "set", "lobby", "--kiosk", "false");
        var promoted = await rig.RunAsync("keys", "set", "script", "--role", "operator");
        var json = await rig.RunAsync("keys", "set", "lobby", "--kiosk", "true", "--role", "viewer", "--json");

        unkiosked.Code.Should().Be(RoofExitCode.Success, unkiosked.ToString());
        unkiosked.Out.Should().Be("Updated lobby: viewer." + Environment.NewLine);
        promoted.Code.Should().Be(RoofExitCode.Success, promoted.ToString());
        promoted.Out.Should().Be("Updated script: operator." + Environment.NewLine);
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("name").GetString().Should().Be("lobby");
        json.Json.GetProperty("kiosk").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task KeysSet_AKioskKeyThatIsNotAViewer_IsRefusedByTheController()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        (await rig.RunAsync("keys", "add", "script", "--role", "operator")).Code.Should().Be(RoofExitCode.Success);

        var result = await rig.RunAsync("keys", "set", "script", "--kiosk", "true");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("[InvalidRequest]").And.Contain("A kiosk key must have the RoofViewer role");
    }

    [TestMethod]
    public async Task KeysSet_AConfiguredKey_IsReadOnly()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("keys", "set", "test-viewer", "--role", "operator");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("This entry is defined in the configuration file and cannot be changed here. [IdentityReadOnly]");
    }

    [TestMethod]
    public async Task KeysSet_AMissingKey_IsNotFound()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var readFirst = await rig.RunAsync("keys", "set", "missing", "--role", "viewer");
        var sentAtOnce = await rig.RunAsync("keys", "set", "missing", "--role", "viewer", "--kiosk", "false", "--json");

        readFirst.Code.Should().Be(RoofExitCode.Refused, readFirst.ToString());
        readFirst.Error.Should().Contain("The user, key or session was not found. [IdentityNotFound]")
            .And.Contain("There is no API key 'missing'.");
        sentAtOnce.Code.Should().Be(RoofExitCode.Refused, sentAtOnce.ToString());
        var error = sentAtOnce.Json.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.IdentityNotFound));
        error.GetProperty("detail").GetString().Should().Be("No API key is named 'missing'.");
    }

    [TestMethod]
    [DataRow(new string[0], "Say what to change: --role or --kiosk.")]
    [DataRow(new[] { "--kiosk", "maybe" }, "maybe")]
    public async Task KeysSet_WithoutAUsableChange_IsAUsageError(string[] options, string expected)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync(["keys", "set", "script", .. options]);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain(expected).And.Contain("Run 'hvo-roof keys set --help' for usage.");
    }

    [TestMethod]
    [DataRow("users", "alice")]
    [DataRow("keys", "script")]
    public async Task Set_WithAnUnknownRole_IsAUsageError(string group, string name)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync(group, "set", name, "--role", "root");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("'root' is not a role. Use viewer, operator or admin.")
            .And.Contain($"Run 'hvo-roof {group} set --help' for usage.");
    }

    // ---- keys rotate --------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task KeysRotate_WithoutForce_AndNoTerminal_IsNotConfirmed()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        var secret = (await rig.RunAsync("keys", "add", "script", "--role", "viewer")).Out.TrimEnd('\r', '\n');

        var result = await rig.RunAsync("keys", "rotate", "script");

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        result.Out.Should().BeEmpty();
        result.Error.Should().Contain($"Rotate script? Everything using its current value stops working. {NotConfirmed}");
        using var script = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(secret));
        (await script.Auth.GetCallerAsync()).Name.Should().Be("script", "nothing was sent");
    }

    [TestMethod]
    public async Task KeysRotate_WithForce_ReplacesTheValue_AndShowsTheNewOneOnce()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        var old = (await rig.RunAsync("keys", "add", "script", "--role", "viewer")).Out.TrimEnd('\r', '\n');

        var result = await rig.RunAsync("keys", "rotate", "script", "--force");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Error.Should().StartWith("Rotated script (viewer). Its value follows. It is shown only now:");
        var rotated = result.Out.TrimEnd('\r', '\n');
        rotated.Should().NotBeNullOrWhiteSpace().And.NotBe(old);
        using var before = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(old));
        using var after = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(rotated));
        (await RoofClientApiTests.RefusedAsync(() => before.Auth.GetCallerAsync())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await after.Auth.GetCallerAsync()).Name.Should().Be("script");
    }

    [TestMethod]
    public async Task KeysRotate_InATerminal_Yes_Rotates_AndJsonNeverPrompts()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        (await rig.RunAsync("keys", "add", "script", "--role", "viewer")).Code.Should().Be(RoofExitCode.Success);
        rig.Input.Enqueue("YES");

        var asked = await rig.RunAsync("keys", "rotate", "script");
        var json = await rig.RunAsync("keys", "rotate", "script", "--force", "--json");

        asked.Code.Should().Be(RoofExitCode.Success, asked.ToString());
        rig.Prompts.Should().Equal(("Rotate script? Everything using its current value stops working. [y/N] ", false));
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("key").GetProperty("name").GetString().Should().Be("script");
        json.Json.GetProperty("secret").GetString().Should().NotBe(asked.Out.TrimEnd('\r', '\n'));
    }

    [TestMethod]
    public async Task KeysRotate_AConfiguredKey_IsReadOnly()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("keys", "rotate", "test-operator", "--force");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Out.Should().BeEmpty();
        result.Error.Should().Contain("[IdentityReadOnly]");
    }

    // ---- keys remove --------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task KeysRemove_WithoutForce_IsNotConfirmed_AndInATerminal_No_SendsNothing()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        (await rig.RunAsync("keys", "add", "script", "--role", "viewer")).Code.Should().Be(RoofExitCode.Success);

        var redirected = await rig.RunAsync("keys", "remove", "script");
        rig.Interactive = true;
        rig.Input.Enqueue("n");
        var declined = await rig.RunAsync("keys", "remove", "script");

        redirected.Code.Should().Be(RoofExitCode.ConfirmationRequired, redirected.ToString());
        redirected.Error.Should().Contain($"Remove script? Everything using it stops working. {NotConfirmed}");
        declined.Code.Should().Be(RoofExitCode.ConfirmationRequired, declined.ToString());
        declined.Error.Should().Contain("Not confirmed; nothing was sent.");
        rig.Prompts.Should().Equal(("Remove script? Everything using it stops working. [y/N] ", false));
        (await KeysAsync(host)).Should().Contain(key => key.Name == "script");
    }

    [TestMethod]
    public async Task KeysRemove_WithForce_RemovesIt_AndItsValueIsRefused()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        var secret = (await rig.RunAsync("keys", "add", "script", "--role", "viewer")).Out.TrimEnd('\r', '\n');
        (await rig.RunAsync("keys", "add", "other", "--role", "viewer")).Code.Should().Be(RoofExitCode.Success);

        var text = await rig.RunAsync("keys", "remove", "script", "--force");
        var json = await rig.RunAsync("keys", "remove", "other", "--force", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().Be("Removed script." + Environment.NewLine);
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("removed").GetString().Should().Be("other");
        using var script = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(secret));
        (await RoofClientApiTests.RefusedAsync(() => script.Auth.GetCallerAsync())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await KeysAsync(host)).Should().NotContain(key => key.Name == "script" || key.Name == "other");
    }

    // ---- sessions -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task SessionsList_WithNone_SaysSo()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("sessions", "list");
        var json = await rig.RunAsync("sessions", "list", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().Be("No sessions are open." + Environment.NewLine);
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetArrayLength().Should().Be(0);
    }

    [TestMethod]
    public async Task SessionsList_ShowsAnOpenSession_ButNeverItsToken()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var anonymous = ClientTestSupport.CreateClient(host);
        var session = await anonymous.Auth.SignInAsync("alice", TestSecrets.Password);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("sessions", "list");
        var json = await rig.RunAsync("sessions", "list", "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().MatchRegex(@"(?m)^ID\s+NAME\s+ROLE\s+KIND\s+DEVICE\s+CREATED\s+EXPIRES\s*$")
            .And.MatchRegex($@"(?m)^{Regex.Escape(session.SessionId!)}\s+alice\s+operator\s+password\s");
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        var listed = json.Json.EnumerateArray().Should().ContainSingle().Which;
        listed.GetProperty("id").GetString().Should().Be(session.SessionId);
        listed.GetProperty("name").GetString().Should().Be("alice");
        listed.GetProperty("kind").GetString().Should().Be(nameof(RoofCredentialKind.Session));
        foreach (var output in new[] { text.Out, json.Out })
        {
            output.Should().NotContain(session.Token);
        }
    }

    [TestMethod]
    public async Task SessionsEnd_SignsTheHolderOut_AndJson()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var anonymous = ClientTestSupport.CreateClient(host);
        var first = await anonymous.Auth.SignInAsync("alice", TestSecrets.Password);
        var second = await anonymous.Auth.SignInAsync("alice", TestSecrets.Password);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("sessions", "end", first.SessionId!);
        var json = await rig.RunAsync("sessions", "end", second.SessionId!, "--json");

        text.Code.Should().Be(RoofExitCode.Success, text.ToString());
        text.Out.Should().Be($"Ended session {first.SessionId}." + Environment.NewLine);
        json.Code.Should().Be(RoofExitCode.Success, json.ToString());
        json.Json.GetProperty("ended").GetString().Should().Be(second.SessionId);
        using var alice = ClientTestSupport.CreateClient(host, first);
        (await RoofClientApiTests.RefusedAsync(() => alice.Auth.GetCallerAsync())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var left = await rig.RunAsync("sessions", "list", "--json");
        left.Json.GetArrayLength().Should().Be(0);
    }

    [TestMethod]
    public async Task SessionsEnd_AnUnknownSession_IsRefusedByTheController()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("sessions", "end", "no-such-session");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("The user, key or session was not found. [IdentityNotFound]")
            .And.Contain("No open session has that identifier.");
    }

    [TestMethod]
    public async Task SessionsEnd_WithoutAnId_IsAUsageError()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);

        var result = await rig.RunAsync("sessions", "end");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("Run 'hvo-roof sessions end --help' for usage.");
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A <c>Label: value</c> line as <see cref="RoofCliFormat"/> writes it, whatever the label padding.</summary>
    private static string Row(string label, string value) => $@"(?m)^{Regex.Escape(label)}:\s+{Regex.Escape(value)}\s*$";

    private static async Task<RoofUserResponse[]> UsersAsync(RoofApiTestHost host)
    {
        using var admin = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        return await admin.Identity.GetUsersAsync();
    }

    private static async Task<RoofApiKeyResponse[]> KeysAsync(RoofApiTestHost host)
    {
        using var admin = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        return await admin.Identity.GetApiKeysAsync();
    }
}
