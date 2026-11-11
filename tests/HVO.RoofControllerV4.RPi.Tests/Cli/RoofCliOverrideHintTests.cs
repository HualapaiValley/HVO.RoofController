using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// The exit-7 safety override hint keeps the <c>--credentials-file</c> and the <c>--controller</c> its command line gave
/// (Refs #261), as it keeps <c>--local</c> and <c>--remote</c> (Refs #255): the command it prints, run as printed,
/// reads the same file and asks the same controller, so it does not change to the default file's mode or credential.
/// Each is printed as one word of a POSIX shell, which real shells read back here (<see cref="PosixShell"/>).
/// <para>
/// The real <c>hvo-roof</c> command line against the real controller in process, as in
/// <see cref="F2LocalRouteReproTests"/> and with its rigs: a mocked roof (nothing can move), the test's own native
/// helper, and credentials files in the rig's own directory. A command line that names no file reads the rig's default
/// file (<see cref="CliRig.DefaultCredentialsPath"/>), never the person's. Each test runs the printed command as a
/// shell splits it (<see cref="SafetyOverrideHintCommand.Words"/>) and adds only <c>--no-wait</c>, which the fixture
/// needs: the mocked roof never finishes a motion.
/// </para>
/// </summary>
[TestClass, DoNotParallelize]
public sealed class RoofCliOverrideHintTests
{
    private const string OpenRoute = "/api/v4.0/RoofControl/Open";
    private const string OpenWithOverride = "/api/v4.0/RoofControl/Open?overrideSafety=true";
    private const string LocalSession = "/api/v4.0/Auth/LocalSession";

    /// <summary>The controller a credentials file names, where the command line's <c>--controller</c> names another.</summary>
    private static readonly Uri Saved = new("http://roof-a.test:5001/");

    /// <summary>The controller the command line names, as it is typed (no closing slash) and as a request shows it.</summary>
    private const string Given = "http://roof-b.test:5001";

    public TestContext TestContext { get; set; } = null!;

    // ---------------------------------------------------------------------------------------------------------------
    // --credentials-file: the issue's reproduction, and the same the other way round.
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(true, DisplayName = "File named says authMode local; the default file holds a remote operator key")]
    [DataRow(false, DisplayName = "File named says authMode local; there is no default file")]
    public async Task TheDisplayedCommand_AfterAFileThatSaysLocal_ReadsThatFile_AndStaysLocal(bool defaultFileHoldsARemoteKey)
    {
        using var native = new F2LocalRig("Warn", installedDefault: true);
        var named = native.Cli.CredentialsPath;
        var byDefault = native.Cli.DefaultCredentialsPath;
        if (defaultFileHoldsARemoteKey)
        {
            RoofCredentialStore.Save(byDefault, new RoofStoredCredentials { Controller = ClientTestSupport.BaseAddress, ApiKey = TestApiKeys.Operator });
        }

        string[] first = ["--credentials-file", named, "open", "--no-wait"];
        var refused = await native.Cli.RunAsIsAsync(first);
        Evidence(native, refused, first);
        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain("[SafetyOverrideRequired]");
        native.Operations.Should().Equal(["operate"], "the file named on the command line says authMode local");
        native.ClientCredentials.Should().Equal($"POST {LocalSession}: none", $"POST {OpenRoute}: session");
        native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0);

        var displayed = Displayed(refused);
        native.Reset();

        string[] followed = [.. displayed[1..], "--no-wait"];
        var opened = await native.Cli.RunAsIsAsync(followed);
        Evidence(native, opened, followed);

        using (new AssertionScope())
        {
            displayed.Should().Equal([RoofCli.CommandName, "--credentials-file", named, "open", "--override-safety"],
                "the printed command names the file the refused one named");
            opened.ExitCode.Should().Be(0, "the printed command reads the same file, so it is authorized the same way");
            native.Operations.Should().Equal(["operate"], "the native helper is asked; the default file's key does not substitute");
            native.ClientCredentials.Should().Equal([$"POST {LocalSession}: none", $"POST {OpenWithOverride}: session"],
                "the Open carries the fresh native session, never the default file's API key");
            native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
            File.Exists(byDefault).Should().Be(defaultFileHoldsARemoteKey, "neither run writes the default file");
        }
    }

    [TestMethod]
    [DataRow(true, DisplayName = "File named holds an operator key; the default file says authMode local")]
    [DataRow(false, DisplayName = "File named holds an operator key; the default file holds a viewer key")]
    public async Task TheDisplayedCommand_AfterAFileWithAnOperatorKey_ReadsThatFile_AndStaysRemote(bool defaultFileSaysLocal)
    {
        // The other way round: the default file must not turn the printed command to local mode, nor give it a key
        // that may not open.
        using var native = new F2LocalRig("Warn", installedDefault: false, savedApiKey: TestApiKeys.Operator);
        var named = native.Cli.CredentialsPath;
        RoofCredentialStore.Save(native.Cli.DefaultCredentialsPath, defaultFileSaysLocal
            ? new RoofStoredCredentials { Controller = ClientTestSupport.BaseAddress, AuthMode = "local" }
            : new RoofStoredCredentials { Controller = ClientTestSupport.BaseAddress, ApiKey = TestApiKeys.Viewer });

        string[] first = ["--credentials-file", named, "open", "--no-wait"];
        var refused = await native.Cli.RunAsIsAsync(first);
        Evidence(native, refused, first);
        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain("[SafetyOverrideRequired]");
        native.Operations.Should().BeEmpty("the file named on the command line selects no local mode");
        native.ClientCredentials.Should().Equal($"POST {OpenRoute}: api-key");

        var displayed = Displayed(refused);
        native.Reset();

        string[] followed = [.. displayed[1..], "--no-wait"];
        var opened = await native.Cli.RunAsIsAsync(followed);
        Evidence(native, opened, followed);

        using (new AssertionScope())
        {
            displayed.Should().Equal(RoofCli.CommandName, "--credentials-file", named, "open", "--override-safety");
            opened.ExitCode.Should().Be(0, "the operator key in the file named opens with the override");
            native.Operations.Should().BeEmpty("the default file's authMode does not apply to the printed command");
            native.ClientCredentials.Should().Equal([$"POST {OpenWithOverride}: api-key"], "the key is the named file's");
            native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
        }
    }

    [TestMethod]
    public async Task TheDisplayedCommand_QuotesAFileTheShellWouldNotTakeAsItIs()
    {
        // A space, an apostrophe, and characters a shell expands or ends a command at.
        using var native = new F2LocalRig("Warn", installedDefault: false);
        var named = Path.Combine(native.Cli.Directory, "roy's roof $HOME & (2);", "the *credentials*.json");
        RoofCredentialStore.Save(named, new RoofStoredCredentials { Controller = ClientTestSupport.BaseAddress, AuthMode = "local" });

        string[] first = ["--credentials-file", named, "open", "--no-wait"];
        var refused = await native.Cli.RunAsIsAsync(first);
        Evidence(native, refused, first);
        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        native.Operations.Should().Equal("operate");

        var printed = SafetyOverrideHintCommand.Text(refused.Error);
        TestContext.WriteLine($"[evidence] printed command: {printed}");
        foreach (var shell in PosixShell.Installed)
        {
            PosixShell.Split(printed, shell).Should().Equal([RoofCli.CommandName, "--credentials-file", named, "open", "--override-safety"],
                $"{shell} reads the printed command as these words");
        }

        native.Reset();
        string[] followed = [.. PosixShell.Split(printed)[1..], "--no-wait"];
        var opened = await native.Cli.RunAsIsAsync(followed);
        Evidence(native, opened, followed);

        using (new AssertionScope())
        {
            opened.ExitCode.Should().Be(0, opened.ToString());
            native.Operations.Should().Equal("operate");
            native.ClientCredentials.Should().Equal($"POST {LocalSession}: none", $"POST {OpenWithOverride}: session");
            native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // --controller.
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheDisplayedCommand_KeepsAControllerGivenOnTheCommandLine()
    {
        // The default file names one controller and --controller another: the override goes to the one that refused.
        using var keyed = new F2KeyRig("Warn");
        var asked = RecordControllers(keyed.Cli);
        RoofCredentialStore.Save(keyed.Cli.DefaultCredentialsPath, new RoofStoredCredentials { Controller = Saved, ApiKey = TestApiKeys.Operator });

        string[] first = ["--controller", Given, "open", "--no-wait"];
        var refused = await keyed.Cli.RunAsIsAsync(first);
        Evidence(keyed, refused, first);
        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain("[SafetyOverrideRequired]");
        asked.Should().Equal(Given);

        var displayed = Displayed(refused);
        keyed.Reset();
        asked.Clear();

        string[] followed = [.. displayed[1..], "--no-wait"];
        var opened = await keyed.Cli.RunAsIsAsync(followed);
        Evidence(keyed, opened, followed);

        using (new AssertionScope())
        {
            displayed.Should().Equal([RoofCli.CommandName, "--controller", Given + "/", "open", "--override-safety"],
                "the printed command names the controller the refused one named, and no file: none was named");
            opened.ExitCode.Should().Be(0, opened.ToString());
            asked.Should().Equal([Given], "the override is sent to the controller that refused, not the one the file names");
            keyed.ClientCredentials.Should().Equal($"POST {OpenWithOverride}: api-key");
            keyed.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
        }
    }

    [TestMethod]
    public async Task TheDisplayedCommand_KeepsTheModeTheControllerAndTheFileTogether()
    {
        // Every choice at once, against a file and a default file that each say otherwise: the file named says local and
        // names another controller, and the default file holds a viewer key.
        using var native = new F2LocalRig("Warn", installedDefault: true, savedApiKey: TestApiKeys.Operator);
        var asked = RecordControllers(native.Cli);
        var named = native.Cli.CredentialsPath;
        RoofCredentialStore.Save(named, native.Cli.Stored! with { Controller = Saved });
        RoofCredentialStore.Save(native.Cli.DefaultCredentialsPath, new RoofStoredCredentials { Controller = Saved, ApiKey = TestApiKeys.Viewer });

        string[] first = ["--remote", "--controller", Given, "--credentials-file", named, "open", "--no-wait"];
        var refused = await native.Cli.RunAsIsAsync(first);
        Evidence(native, refused, first);
        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        native.Operations.Should().BeEmpty();

        var displayed = Displayed(refused);
        native.Reset();
        asked.Clear();

        string[] followed = [.. displayed[1..], "--no-wait"];
        var opened = await native.Cli.RunAsIsAsync(followed);
        Evidence(native, opened, followed);

        using (new AssertionScope())
        {
            displayed.Should().Equal(RoofCli.CommandName, "--remote", "--controller", Given + "/", "--credentials-file", named, "open", "--override-safety");
            opened.ExitCode.Should().Be(0, opened.ToString());
            native.Operations.Should().BeEmpty("--remote is kept: the file's authMode local does not apply");
            asked.Should().Equal(Given);
            native.ClientCredentials.Should().Equal([$"POST {OpenWithOverride}: api-key"], "the operator key of the file named");
            native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Controls: what the command line did not give is not added, and JSON prints no hint.
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(true, DisplayName = "No option given; the default file says authMode local")]
    [DataRow(false, DisplayName = "No option given; the default file holds an operator key")]
    public async Task TheDisplayedCommand_AfterACommandThatGaveNoOption_IsThePlainOne_AndReadsTheDefaultFile(bool defaultFileSaysLocal)
    {
        using var native = new F2LocalRig("Warn", installedDefault: false);
        RoofCredentialStore.Save(native.Cli.DefaultCredentialsPath, defaultFileSaysLocal
            ? new RoofStoredCredentials { Controller = ClientTestSupport.BaseAddress, AuthMode = "local" }
            : new RoofStoredCredentials { Controller = ClientTestSupport.BaseAddress, ApiKey = TestApiKeys.Operator });

        string[] first = ["open", "--no-wait"];
        var refused = await native.Cli.RunAsIsAsync(first);
        Evidence(native, refused, first);
        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain(RoofCliContext.SafetyOverrideHint, "nothing was chosen on the command line, so nothing is added");

        var displayed = Displayed(refused);
        native.Reset();

        string[] followed = [.. displayed[1..], "--no-wait"];
        var opened = await native.Cli.RunAsIsAsync(followed);
        Evidence(native, opened, followed);

        using (new AssertionScope())
        {
            displayed.Should().Equal(RoofCli.CommandName, "open", "--override-safety");
            opened.ExitCode.Should().Be(0, "the default file applies to the printed command as it did to the refused one");
            native.Operations.Should().Equal(defaultFileSaysLocal ? ["operate"] : Array.Empty<string>());
            native.ClientCredentials.Should().Equal(defaultFileSaysLocal
                ? [$"POST {LocalSession}: none", $"POST {OpenWithOverride}: session"]
                : new[] { $"POST {OpenWithOverride}: api-key" });
            native.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
        }
    }

    [TestMethod]
    public async Task TheDisplayedCommand_NeverPrintsTheEnvironment()
    {
        // The address and the key come from the environment, as a container or a script sets them: neither is printed,
        // and the printed command, run in the same environment, uses them as the refused one did.
        using var keyed = new F2KeyRig("Warn");
        keyed.Cli.Environment[RoofCredentialStore.ControllerVariable] = ClientTestSupport.BaseAddress.ToString();
        keyed.Cli.Environment[RoofCredentialStore.ApiKeyVariable] = TestApiKeys.Operator;

        string[] first = ["open", "--no-wait"];
        var refused = await keyed.Cli.RunAsIsAsync(first);
        refused.Code.Should().Be(RoofExitCode.Refused, refused.ToString());
        refused.Error.Should().Contain(RoofCliContext.SafetyOverrideHint).And.NotContain(TestApiKeys.Operator).And.NotContain("--controller");
        File.Exists(keyed.Cli.DefaultCredentialsPath).Should().BeFalse("the credential is the environment's alone");

        var displayed = Displayed(refused);
        keyed.Reset();

        string[] followed = [.. displayed[1..], "--no-wait"];
        var opened = await keyed.Cli.RunAsIsAsync(followed);
        Evidence(keyed, opened, followed);

        using (new AssertionScope())
        {
            displayed.Should().Equal(RoofCli.CommandName, "open", "--override-safety");
            opened.ExitCode.Should().Be(0, opened.ToString());
            keyed.ClientCredentials.Should().Equal($"POST {OpenWithOverride}: api-key");
            keyed.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(1);
        }
    }

    [TestMethod]
    [DataRow(RoofControllerErrorCode.SafetyInterlockActive, DisplayName = "Refused by the interlock in Block mode")]
    [DataRow(RoofControllerErrorCode.FaultLatched, DisplayName = "Refused for a latched fault")]
    public void ARefusalTheOverrideDoesNotLift_PrintsNoHint_WhateverTheCommandLineGave(RoofControllerErrorCode code)
    {
        using var rig = new CliRig();
        using var error = new StringWriter();
        var file = Path.Combine(Path.GetTempPath(), "credentials.json");
        var context = new RoofCliContext(rig.CreateHost(TextWriter.Null, error), json: false, new Uri(Given + "/"), file, local: true);

        context.Fail(new RoofApiException(HttpStatusCode.Conflict, code)).Should().Be((int)RoofExitCode.Refused);

        error.ToString().Should().NotContain("To open anyway").And.NotContain("--override-safety").And.NotContain(file);
    }

    [TestMethod]
    public void TheHint_NamesTheFullPathOfACredentialsFileGivenRelativeToTheDirectory()
    {
        // The printed command may be run from another directory: it reads the same file there.
        var relative = Path.Combine("hvo-roof-261", "credentials.json");

        var displayed = SafetyOverrideHintCommand.Words(Refuse(credentialsFile: relative).Error);

        Path.IsPathRooted(relative).Should().BeFalse();
        displayed.Should().Equal(RoofCli.CommandName, "--credentials-file", Path.GetFullPath(relative), "open", "--override-safety");
    }

    [TestMethod]
    [DataRow(null, DisplayName = "No --credentials-file")]
    [DataRow("", DisplayName = "An empty --credentials-file")]
    [DataRow("  ", DisplayName = "A blank --credentials-file")]
    public void TheHint_NamesNoFile_WhenTheCommandLineNamedNone(string? credentialsFile)
    {
        // An empty or blank value reads the default file, as no option does, so the printed command names none.
        var refused = Refuse(credentialsFile: credentialsFile);

        refused.Code.Should().Be(RoofExitCode.Refused);
        refused.Error.Should().Contain(RoofCliContext.SafetyOverrideHint);
        SafetyOverrideHintCommand.Words(refused.Error).Should().Equal(RoofCli.CommandName, "open", "--override-safety");
    }

    [TestMethod]
    public void Json_PrintsNoHint_WhateverTheCommandLineGave()
    {
        var refused = Refuse(json: true, controller: new Uri(Given + "/"), credentialsFile: Path.Combine(Path.GetTempPath(), "credentials.json"));

        refused.Code.Should().Be(RoofExitCode.Refused);
        refused.Error.Should().BeEmpty();
        refused.Out.Should().NotContain("To open anyway");
        refused.Json.GetProperty("error").GetProperty("code").GetString().Should().Be("SafetyOverrideRequired");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The hint's words, and how a value is made one word of the shell's.
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheHint_PrintsWhatTheCommandLineChose_InOneOrder()
    {
        const string after = " The controller logs the override with your name; Close and Stop are never affected.";
        const string file = "/srv/roof/credentials.json";
        var controller = new Uri("https://roof.local:5001/");

        using (new AssertionScope())
        {
            RoofCliContext.DescribeSafetyOverrideHint(null).Should().Be("To open anyway: 'hvo-roof open --override-safety'." + after)
                .And.Be(RoofCliContext.SafetyOverrideHint);
            RoofCliContext.DescribeSafetyOverrideHint(null, controller).Should()
                .Be("To open anyway: 'hvo-roof --controller https://roof.local:5001/ open --override-safety'." + after);
            RoofCliContext.DescribeSafetyOverrideHint(null, credentialsFile: file).Should()
                .Be("To open anyway: 'hvo-roof --credentials-file /srv/roof/credentials.json open --override-safety'." + after, "docs/cli.md shows this one");
            RoofCliContext.DescribeSafetyOverrideHint(true, controller, file).Should()
                .Be("To open anyway: 'hvo-roof --local --controller https://roof.local:5001/ --credentials-file /srv/roof/credentials.json open --override-safety'." + after);
            RoofCliContext.DescribeSafetyOverrideHint(false, controller, file).Should()
                .Be("To open anyway: 'hvo-roof --remote --controller https://roof.local:5001/ --credentials-file /srv/roof/credentials.json open --override-safety'." + after);

            // Values a shell would not take as they stand: in single quotes, a quote of their own as '\''.
            RoofCliContext.DescribeSafetyOverrideHint(null, credentialsFile: "/srv/roof files/credentials.json").Should()
                .Be("To open anyway: 'hvo-roof --credentials-file '/srv/roof files/credentials.json' open --override-safety'." + after, "docs/cli.md shows this one");
            RoofCliContext.DescribeSafetyOverrideHint(null, new Uri("https://[2001:db8::1]:5001/"), "/home/roy's roof/credentials.json").Should()
                .Be("To open anyway: 'hvo-roof --controller 'https://[2001:db8::1]:5001/' --credentials-file '/home/roy'\\''s roof/credentials.json' open --override-safety'." + after);
        }
    }

    [TestMethod]
    [DataRow(null, DisplayName = "No mode")]
    [DataRow(true, DisplayName = "--local")]
    [DataRow(false, DisplayName = "--remote")]
    public void ThePrintedCommand_IsACommandLineHvoRoofReads_WithTheSameChoices(bool? local)
    {
        // The words a shell gives hvo-roof, read by its own parser: Open with the override, and each choice as it was.
        using var rig = new CliRig();
        var controller = new Uri("https://[2001:db8::1]:5001/roof (east)/");
        var file = Path.Combine(Path.GetTempPath(), "roy's roof $HOME", "the *credentials*.json");

        var displayed = SafetyOverrideHintCommand.Words(RoofCliContext.DescribeSafetyOverrideHint(local, controller, file));
        var read = RoofCli.CreateRootCommand(rig.CreateHost(TextWriter.Null, TextWriter.Null)).Parse(displayed[1..]);

        using (new AssertionScope())
        {
            displayed[0].Should().Be(RoofCli.CommandName);
            read.Errors.Should().BeEmpty();
            read.CommandResult.Command.Name.Should().Be("open");
            read.GetValue<bool>("--override-safety").Should().BeTrue();
            read.GetValue<bool>("--local").Should().Be(local == true);
            read.GetValue<bool>("--remote").Should().Be(local == false);
            read.GetValue<Uri?>("--controller").Should().Be(controller);
            read.GetValue<string?>("--credentials-file").Should().Be(file);
        }
    }

    [TestMethod]
    [DataRow("https://roof.local:5001", DisplayName = "As typed, with no closing slash")]
    [DataRow("http://192.0.2.10:5195/", DisplayName = "An IPv4 address")]
    [DataRow("https://[2001:db8::1]:8443/roof", DisplayName = "An IPv6 address: brackets, which a shell matches names with")]
    [DataRow("https://roof.local/east roof/it's (1)/", DisplayName = "A path with a space, an apostrophe and parentheses")]
    [DataRow("https://ROOF.Local:443/", DisplayName = "Upper case, and the scheme's own port")]
    public void TheHint_PrintsAController_ThatReadsBackAsTheSameAddress(string typed)
    {
        RoofCli.TryParseControllerAddress(typed, out var address, out _).Should().BeTrue();

        var displayed = SafetyOverrideHintCommand.Words(RoofCliContext.DescribeSafetyOverrideHint(null, address));

        displayed.Should().HaveCount(5);
        displayed.Should().StartWith([RoofCli.CommandName, "--controller"]).And.EndWith(["open", "--override-safety"]);
        RoofCli.TryParseControllerAddress(displayed[2], out var read, out _).Should().BeTrue($"the printed address is one hvo-roof takes: {displayed[2]}");
        read.Should().Be(address);
    }

    [TestMethod]
    public void QuoteForShell_MakesOneWord_ThatEveryShellReadsBackAsTheValue()
    {
        // Every ASCII character but NUL (no argument holds one): alone, first in a word and within one. Then words a
        // shell would expand, split, or end the command at, and characters outside ASCII.
        var values = new List<string>
        {
            string.Empty, " ", "  two  spaces  ", "/srv/roof/credentials.json", "/srv/roof files/credentials.json",
            "'", "''", "it's", "'quoted'", "a'b'c", "\"double\"", "back\\slash", "trailing\\", "\\'", "'\\''",
            "$HOME", "${HOME}", "$(printf expanded)", "`printf expanded`", "~", "~/credentials.json", "~root",
            "*", "/tmp/*", "?", "[a-z]", "{a,b}", "!!", "!$", "#comment", "a #b", "=ls", "a=b",
            "a;b", "a&b", "a|b", "a<b", "a>b", "(a)", "a\nb", "a\tb", "a\rb", "-", "--", "--credentials-file", "@file",
            "\u00fc", "caf\u00e9 roof", "\u89b3\u6e2c\u6240", "\ud83d\udd2d"
        };
        for (var character = (char)1; character < 128; character++)
        {
            values.AddRange([character.ToString(), $"{character}x", $"/a{character}b"]);
        }

        var line = string.Join(' ', values.Select(RoofCliContext.QuoteForShell));

        foreach (var shell in PosixShell.Installed)
        {
            TestContext.WriteLine($"[evidence] {values.Count} values read back by {shell}");
            PosixShell.Split(line, shell).Should().Equal(values, $"{shell} reads each quoted value as one word, unchanged");
        }
    }

    [TestMethod]
    public void QuoteForShell_LeavesOnlyPlainWordsAsTheyAre()
    {
        const string plain = "_@%+:,./-";

        using (new AssertionScope())
        {
            for (var character = (char)1; character < 128; character++)
            {
                var value = $"/a{character}b";
                var asItIs = char.IsAsciiLetterOrDigit(character) || plain.Contains(character);
                RoofCliContext.QuoteForShell(value).Should().Be(asItIs ? value : character == '\'' ? "'/a'\\''b'" : $"'{value}'",
                    $"character {(int)character} is {(asItIs ? "plain" : "not plain")}");
            }

            RoofCliContext.QuoteForShell(string.Empty).Should().Be("''", "an empty value is still one word");
            RoofCliContext.QuoteForShell("\u00fc").Should().Be("'\u00fc'", "only ASCII letters are plain");
            RoofCliContext.QuoteForShell("/srv/roof/credentials.json").Should().Be("/srv/roof/credentials.json");
            RoofCliContext.QuoteForShell("https://roof.local:5001/").Should().Be("https://roof.local:5001/");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What a command line with these global options writes when the controller refuses its Open for want of the
    /// override. Nothing is read or sent: the refusal is given to the command's own report (<c>Fail</c>).
    /// </summary>
    private static CliResult Refuse(bool json = false, Uri? controller = null, string? credentialsFile = null)
    {
        using var rig = new CliRig();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var context = new RoofCliContext(rig.CreateHost(output, error), json, controller, credentialsFile);

        var code = context.Fail(new RoofApiException(HttpStatusCode.Conflict, RoofControllerErrorCode.SafetyOverrideRequired));

        return new CliResult(code, output.ToString(), error.ToString());
    }

    /// <summary>The printed command's words, as a shell splits them.</summary>
    private string[] Displayed(CliResult refused)
    {
        TestContext.WriteLine($"[evidence] printed command: {SafetyOverrideHintCommand.Text(refused.Error)}");
        var displayed = SafetyOverrideHintCommand.Words(refused.Error);
        displayed[0].Should().Be(RoofCli.CommandName);
        return displayed;
    }

    /// <summary>Records the controller (scheme, host and port) each request of <paramref name="rig"/> is sent to.</summary>
    private static ConcurrentQueue<string> RecordControllers(CliRig rig)
    {
        var asked = new ConcurrentQueue<string>();
        var wrap = rig.WrapHandler ?? (handler => handler);
        rig.WrapHandler = inner => new ControllerRecorder(wrap(inner), asked);
        return asked;
    }

    private void Evidence(F2Recorders rig, CliResult result, string[] arguments)
    {
        TestContext.WriteLine($"[evidence] hvo-roof {string.Join(' ', arguments)}");
        TestContext.WriteLine($"[evidence]   exit code: {result.ExitCode} ({result.Code})");
        TestContext.WriteLine($"[evidence]   stderr: {(result.Error.Trim().Length == 0 ? "(empty)" : result.Error.Trim().ReplaceLineEndings(" | "))}");
        TestContext.WriteLine($"[evidence]   native helper asked for: {Lines(rig.Operations)}");
        TestContext.WriteLine($"[evidence]   requests sent by the client: {Lines(rig.ClientCredentials)}");
        TestContext.WriteLine($"[evidence]   roof service calls: Open={rig.Calls(nameof(IRoofControllerServiceV4.Open))}");
    }

    private static string Lines(IEnumerable<string> lines) => lines.ToArray() is { Length: > 0 } all ? string.Join(", ", all) : "(none)";

    private sealed class ControllerRecorder(HttpMessageHandler inner, ConcurrentQueue<string> asked) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            asked.Enqueue(request.RequestUri!.GetLeftPart(UriPartial.Authority));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
