using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The words the clients share for people, the system and settings (<see cref="RoofIdentityText"/>,
/// <see cref="RoofSystemText"/>, <see cref="RoofSettingsText"/>), which the web UI, the CLI and the terminal UI all say.
/// </summary>
[TestClass]
public sealed class RoofClientTextTests
{
    private const string Password = "test-password-not-real-01";

    [TestMethod]
    [DataRow(null, null, null)]
    [DataRow("", "", null)]
    [DataRow(Password, Password, null)]
    [DataRow("", Password, "The two passwords differ.")]
    [DataRow(Password, Password + "!", "The two passwords differ.")]
    [DataRow("too-short", "too-short", "A password must be 12 to 256 characters.")]
    public void ANewPassword_IsCheckedTwice(string? value, string? again, string? expected)
        => RoofIdentityText.CheckNewPassword(value, again).Should().Be(expected);

    [TestMethod]
    [DataRow("", "", null)]
    [DataRow("246810", "246810", null)]
    [DataRow("246810", "135790", "The two PINs differ.")]
    [DataRow("", "246810", "The two PINs differ.")]
    [DataRow("12345", "12345", "A PIN must be 6 to 12 digits.")]
    [DataRow("24681a", "24681a", "A PIN must be 6 to 12 digits.")]
    public void ANewPin_IsCheckedTwice(string? value, string? again, string? expected)
        => RoofIdentityText.CheckNewPin(value, again).Should().Be(expected);

    [TestMethod]
    public void TheRules_NameTheContractsLimits()
    {
        RoofIdentityText.NameRule.Should().Be("use 1 to 64 letters, digits, '.', '_', '@' or '-', starting with a letter or digit.");
        RoofIdentityText.DescribeKind(RoofCredentialKind.Pin).Should().Be("PIN");
        RoofIdentityText.DescribeKind(RoofCredentialKind.Session).Should().Be("password");
    }

    [TestMethod]
    [DataRow(0, "0m 0s")]
    [DataRow(59, "0m 59s")]
    [DataRow(3_599, "59m 59s")]
    [DataRow(3_600, "1h 0m")]
    [DataRow(86_399, "23h 59m")]
    [DataRow(90_061, "1d 1h 1m")]
    public void ADuration_IsShortest(int seconds, string expected)
        => RoofSystemText.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [TestMethod]
    [DataRow(0L, "0.0 KiB")]
    [DataRow(1_536L, "1.5 KiB")]
    [DataRow(1_048_575L, "1024.0 KiB")]
    [DataRow(1_048_576L, "1.0 MiB")]
    [DataRow(157_286_400L, "150.0 MiB")]
    public void Bytes_AreKiBBelowAMiB(long bytes, string expected)
        => RoofSystemText.Bytes(bytes).Should().Be(expected);

    [TestMethod]
    public void AHandEditsNotes_SayWhatIsWrong_AndWhatApplyingItNeeds()
    {
        var pending = new RoofSettingsHandEdit(
            "token",
            [],
            [new RoofSettingProblem("RoofControllerOptionsV4:OpenRelayId", "must differ from CloseRelayId")],
            "line 3: unexpected '}'",
            RequiresConfirmation: true,
            RequiresLocalCredential: true);

        RoofSettingsText.DescribeHandEditNotes(pending).Should().Equal(
            "The file cannot be used: line 3: unexpected '}'",
            "Problem: RoofControllerOptionsV4:OpenRelayId: must differ from CloseRelayId",
            "Applying it is safety-critical and needs confirming.",
            "Applying it needs a local credential.");
        RoofSettingsText.DescribeHandEditNotes(pending with { Problems = [], FileProblem = null, RequiresConfirmation = false, RequiresLocalCredential = false })
            .Should().BeEmpty();
    }
}
