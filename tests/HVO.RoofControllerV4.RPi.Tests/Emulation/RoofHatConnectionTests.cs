using System;
using System.Linq;
using FluentAssertions;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

[TestClass]
public sealed class RoofHatConnectionTests
{
    [TestMethod]
    [DataRow("Development")]
    [DataRow("Production")]
    public void Disabled_IsTheHardwareConnection_AndIgnoresTheOtherSettings(string environment)
    {
        var connection = HatConnections.Create(new HatEmulatorOptions { Enabled = false, Host = " ", Port = 0 }, environment);

        connection.IsEmulated.Should().BeFalse();
        connection.EmulatorEndpoint.Should().BeNull();
        connection.Options.Port.Should().Be(0);
    }

    [TestMethod]
    public void Enabled_InDevelopment_IsEmulated_WithTheEndpoint()
    {
        var connection = HatConnections.Emulated("hat-emulator", 5391);

        connection.IsEmulated.Should().BeTrue();
        connection.EmulatorEndpoint.Should().Be("hat-emulator:5391");
    }

    [TestMethod]
    [DataRow("Production")]
    [DataRow("Staging")]
    public void Enabled_OutsideDevelopment_WithoutConsent_IsRefused(string environment)
    {
        var act = () => HatConnections.Create(new HatEmulatorOptions { Enabled = true }, environment);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"HAT emulator mode (HatEmulator:Enabled) is refused in the {environment} environment.*")
            .Which.Message.Should().Contain("HatEmulator:AllowOutsideDevelopment").And.Contain("ALLOW_EMULATED_HAT=true")
            .And.Contain("does not operate the roof");
    }

    [TestMethod]
    public void Enabled_OutsideDevelopment_WithConsent_IsEmulated()
    {
        var connection = HatConnections.Create(new HatEmulatorOptions { Enabled = true, AllowOutsideDevelopment = true }, "Production");

        connection.IsEmulated.Should().BeTrue();
        connection.EmulatorEndpoint.Should().Be($"127.0.0.1:{HatEmulatorOptions.DefaultPort}");
    }

    [TestMethod]
    public void Enabled_WithInvalidSettings_IsRefused_BeforeTheEnvironmentCheck()
    {
        var act = () => HatConnections.Create(new HatEmulatorOptions { Enabled = true, Port = 0, Host = "" }, "Production");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("The HAT emulator settings are invalid: *")
            .Which.Message.Should().Contain("HatEmulator:Host must not be empty.").And.Contain("HatEmulator:Port must be between 1 and 65535.");
    }

    [TestMethod]
    public void Constructor_RefusesNullArguments()
    {
        FluentActions.Invoking(() => new RoofHatConnection(null!, new TestHostEnvironment())).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new RoofHatConnection(Options.Create(new HatEmulatorOptions()), null!)).Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void CreateClient_InEmulatorMode_IsASocketClient_ForTheHatsBusAndStackAddress_ThatHasNotConnected()
    {
        var connection = HatConnections.Emulated();
        var hat = new FourRelayFourInputHatOptions { I2cBusId = 1, Stack = 2 };

        using var client = connection.CreateClient(hat, NullLoggerFactory.Instance);

        var socket = client.Should().BeOfType<SocketI2cRegisterClient>().Subject;
        socket.ConnectionSettings.BusId.Should().Be(1);
        socket.ConnectionSettings.DeviceAddress.Should().Be(hat.BaseAddress + 2);
        socket.ConnectCount.Should().Be(0, "the client connects on its first access");
    }

    [TestMethod]
    public void CreateClient_WithTheEmulatorOff_IsNotASocketClient()
    {
        var connection = HatConnections.Hardware();

        using var client = connection.CreateClient(new FourRelayFourInputHatOptions(), NullLoggerFactory.Instance);

        client.Should().NotBeOfType<SocketI2cRegisterClient>();
    }

    [TestMethod]
    public void CreateClient_RefusesNullArguments()
    {
        var connection = HatConnections.Emulated();

        FluentActions.Invoking(() => connection.CreateClient(null!, NullLoggerFactory.Instance)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => connection.CreateClient(new FourRelayFourInputHatOptions(), null!)).Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void ModeFor_IsEmulated_WhateverTheClient_AndOtherwiseFollowsTheHardwareFlag()
    {
        HatConnections.Emulated().ModeFor(hardwareBacked: true).Should().Be(RoofHatMode.Emulated);
        HatConnections.Emulated().ModeFor(hardwareBacked: false).Should().Be(RoofHatMode.Emulated);
        HatConnections.Hardware().ModeFor(hardwareBacked: true).Should().Be(RoofHatMode.Physical);
        HatConnections.Hardware().ModeFor(hardwareBacked: false).Should().Be(RoofHatMode.Simulation);
    }

    [TestMethod]
    public void ReportHatMode_InEmulatorMode_WarnsWithTheEndpoint()
    {
        var logs = new RecordingLoggerProvider();
        using var services = Services(HatConnections.Emulated("hat-emulator", 5391), logs);

        HatEmulatorStartup.ReportHatMode(services);

        var entry = logs.Entries.Should().ContainSingle().Subject;
        entry.Category.Should().Be(HatEmulatorStartup.LoggerCategory);
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().StartWith("EMULATED HAT: the roof controller is connected to the HAT emulator at hat-emulator:5391, not the physical HAT.")
            .And.Contain("the observatory roof does not move");
    }

    [TestMethod]
    public void ReportHatMode_WithTheEmulatorOff_LogsNothing()
    {
        var logs = new RecordingLoggerProvider();
        using var services = Services(HatConnections.Hardware(), logs);

        HatEmulatorStartup.ReportHatMode(services);

        logs.Entries.Should().BeEmpty();
    }

    [TestMethod]
    public void ReportHatMode_WithARefusedConfiguration_StopsTheStart()
    {
        var logs = new RecordingLoggerProvider();
        var collection = new ServiceCollection();
        collection.AddLogging(builder => builder.AddProvider(logs));
        collection.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Production"));
        collection.AddSingleton(Options.Create(new HatEmulatorOptions { Enabled = true }));
        collection.AddSingleton<RoofHatConnection>();
        using var services = collection.BuildServiceProvider();

        FluentActions.Invoking(() => HatEmulatorStartup.ReportHatMode(services)).Should().Throw<InvalidOperationException>()
            .WithMessage("*refused in the Production environment*");
        FluentActions.Invoking(() => HatEmulatorStartup.ReportHatMode(null!)).Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void TheResourceDetector_MarksAnEmulatorRun_WithTheEndpoint()
    {
        var attributes = new RoofHatModeResourceDetector(HatConnections.Emulated("hat-emulator", 5391)).Detect().Attributes.ToDictionary();

        attributes.Should().HaveCount(2);
        attributes.Should().ContainKey("hvo.roof.hat.mode").WhoseValue.Should().Be("emulated");
        attributes.Should().ContainKey("hvo.roof.hat.emulator.endpoint").WhoseValue.Should().Be("hat-emulator:5391");
    }

    [TestMethod]
    public void TheResourceDetector_MarksAHardwareRun_WithoutAnEndpoint()
    {
        var attributes = new RoofHatModeResourceDetector(HatConnections.Hardware()).Detect().Attributes.ToDictionary();

        attributes.Should().ContainSingle().Which.Should().Be(new System.Collections.Generic.KeyValuePair<string, object>("hvo.roof.hat.mode", "hardware"));
    }

    private static ServiceProvider Services(RoofHatConnection connection, RecordingLoggerProvider logs)
    {
        var collection = new ServiceCollection();
        collection.AddLogging(builder => builder.AddProvider(logs));
        collection.AddSingleton(connection);
        return collection.BuildServiceProvider();
    }
}
