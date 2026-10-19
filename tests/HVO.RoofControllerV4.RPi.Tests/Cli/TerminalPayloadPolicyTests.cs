using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.RPi.Tests.Installer;
using HVO.RoofControllerV4.TerminalUi;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>Compiled feature boundary and offline notices while the highlighting closure is excluded (#172).</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class TerminalPayloadPolicyTests
{
    [TestMethod]
    public void TerminalPrograms_DoNotReferenceHighlightingViewsOrAssemblies()
    {
        foreach (var assembly in new[] { typeof(RoofCli).Assembly, typeof(InstallerProgram).Assembly, typeof(RoofUiTheme).Assembly })
        {
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                name.Should().NotContain("TextMate", assembly.FullName).And.NotContain("Onigwrap", assembly.FullName);
            }

            foreach (var handle in metadata.TypeReferences)
            {
                var reference = metadata.GetTypeReference(handle);
                var ns = metadata.GetString(reference.Namespace);
                var name = metadata.GetString(reference.Name);
                if (ns.StartsWith("Terminal.Gui", StringComparison.Ordinal))
                    new[] { "Code", "Markdown", "Wizard", "WizardStep", "TextMateSyntaxHighlighter" }.Should().NotContain(name, assembly.FullName);
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Notices_TerminateBeforeCliConfigurationAuthorizationOrMotion(bool command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new RoofCliHost
        {
            Out = output, Error = error, IsInteractive = false,
            GetEnvironmentVariable = _ => throw new AssertFailedException("Notices read no configuration."),
            ReadLine = (_, _) => throw new AssertFailedException("Notices ask for no input."),
            CreateHandler = () => throw new AssertFailedException("Notices make no HTTP requests."),
            ObtainLocalAssertion = (_, _) => throw new AssertFailedException("Notices authorize nothing.")
        };
        var code = await RoofCli.RunAsync(command ? ["open", "--third-party-notices"] : ["--third-party-notices"], host);
        code.Should().Be(0);
        AssertNotices(output.ToString());
        error.ToString().Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Notices_TerminateBeforeInstallerSurveyOrUninstall(bool command)
    {
        using var machine = new FakeMachine();
        var before = machine.Snapshot();
        var run = await machine.RunAsync(command ? ["uninstall", "--third-party-notices"] : ["--third-party-notices"]);
        run.ExitCode.Should().Be(0, run.ToString());
        AssertNotices(run.Output);
        run.Error.Should().BeEmpty();
        machine.Snapshot().Should().Equal(before);
        machine.Ran.Should().BeEmpty();
    }

    internal static void AssertNotices(string text)
    {
        using var expected = new StringWriter();
        ThirdPartyNotices.WriteTo(expected);
        text.Should().Be(expected.ToString()).And.Contain("Copyright 2007-2011 Novell Inc")
            .And.Contain("Copyright Patrik Svensson. Phil Scott")
            .And.Contain("Copyright (c) 2018-2019, Alexandre Mutel")
            .And.Contain("Redistributions in binary form must reproduce")
            .And.Contain("Artyom Gritsuk").And.Contain("Tatham Oddie").And.Contain("Valentin Breuß");
    }
}
