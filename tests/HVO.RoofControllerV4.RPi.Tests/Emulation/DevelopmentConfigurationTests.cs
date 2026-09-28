using System.IO;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Emulator;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

/// <summary>
/// The Development settings (<c>appsettings.json</c> overlaid with <c>appsettings.Development.json</c>, as the
/// Development environment loads them): Development runs against the HAT emulator, which supplies the limit switches,
/// so they are in force with the production wiring. The in-memory register simulation is kept for unit tests, and as the
/// fallback with emulator mode off and no I2C bus.
/// </summary>
[TestClass]
public class DevelopmentConfigurationTests
{
    [TestMethod]
    public void Development_UsesTheHatEmulator_AtTheEmulatorsDefaultRegisterPort()
    {
        var configuration = LoadDevelopment();

        var hat = new HatEmulatorOptions();
        configuration.GetSection(HatEmulatorOptions.SectionName).Bind(hat);

        hat.Enabled.Should().BeTrue("Development runs the controller against the HAT emulator");
        hat.Validate().Should().BeEmpty();
        hat.Host.Should().Be("127.0.0.1");
        hat.Port.Should().Be(new EmulatorHostOptions().RegisterPort, "dotnet run of the emulator listens there by default");
        hat.AllowOutsideDevelopment.Should().BeFalse();
    }

    [TestMethod]
    public void Development_KeepsTheLimitSwitchesInForce_WithTheProductionWiring()
    {
        var configuration = LoadDevelopment();

        var options = new RoofControllerOptionsV4();
        configuration.GetSection(nameof(RoofControllerOptionsV4)).Bind(options);
        var production = ProductionOptions.Load();

        options.IgnorePhysicalLimitSwitches.Should().BeFalse("the emulator supplies the limit switches");
        options.AllowIgnoringLimitSwitchesOnPhysicalHardware.Should().BeFalse();
        options.UseNormallyClosedLimitSwitches.Should().Be(production.UseNormallyClosedLimitSwitches, "the emulator has the documented wiring");
        options.FaultInputActiveHigh.Should().Be(production.FaultInputActiveHigh);
        options.AtSpeedConfirmationTimeout.Should().Be(production.AtSpeedConfirmationTimeout);
        new RoofControllerOptionsV4Validator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    private static IConfigurationRoot LoadDevelopment()
    {
        var directory = Path.GetDirectoryName(ProductionOptions.AppSettingsPath)!;
        return new ConfigurationBuilder()
            .AddJsonFile(ProductionOptions.AppSettingsPath, optional: false, reloadOnChange: false)
            .AddJsonFile(Path.Combine(directory, "appsettings.Development.json"), optional: false, reloadOnChange: false)
            .Build();
    }
}
