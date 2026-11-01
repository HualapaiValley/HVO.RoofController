using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Settings;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The controller's Alpaca command settings in phase 4 (#220): <c>AllowCommands</c> accepts <c>OpenAndClose</c>, and
/// <c>AllowOpenWithoutBlockingScopes</c> (off by default) is a safety-critical setting changed only with the local
/// credential, both in the settings catalogue and validated at start.
/// </summary>
[TestClass]
public sealed class RoofAlpacaSettingsTests
{
    [TestMethod]
    public void AllowCommands_AcceptsOff_CloseOnly_AndOpenAndClose_AndNothingElse()
    {
        foreach (var mode in Enum.GetValues<RoofAlpacaCommandMode>())
        {
            RoofAlpacaCommandOptionsValidator.Problems(new RoofAlpacaCommandOptions { AllowCommands = mode }, []).Should().BeEmpty(mode.ToString());
        }

        RoofAlpacaCommandOptionsValidator.Problems(new RoofAlpacaCommandOptions { AllowCommands = (RoofAlpacaCommandMode)3 }, [])
            .Should().ContainSingle().Which.Should().Contain("Off, CloseOnly or OpenAndClose");

        var setting = RoofSettingsCatalogue.Find("RoofController:Alpaca:AllowCommands")!;
        setting.AllowedValues.Should().Equal("Off", "CloseOnly", "OpenAndClose");
        setting.CodeDefault.Should().Be("Off");
        setting.Safety.Should().Be(RoofSettingSafety.Always);
        setting.LocalOnly.Should().BeTrue();
    }

    [TestMethod]
    public void AllowOpenWithoutBlockingScopes_IsOffByDefault_SafetyCritical_AndLocalOnly()
    {
        new RoofAlpacaCommandOptions().AllowOpenWithoutBlockingScopes.Should().BeFalse();

        var setting = RoofSettingsCatalogue.Find("RoofController:Alpaca:AllowOpenWithoutBlockingScopes")!;
        setting.Should().NotBeNull();
        setting.Type.Should().Be(RoofSettingType.Boolean);
        setting.CodeDefault.Should().Be(false);
        setting.Safety.Should().Be(RoofSettingSafety.Always);
        setting.LocalOnly.Should().BeTrue();
        setting.Group.Should().Be(RoofSettingsContract.AlpacaGroup);
    }
}
