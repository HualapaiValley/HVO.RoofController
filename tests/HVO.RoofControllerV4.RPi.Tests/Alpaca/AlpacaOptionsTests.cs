using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Alpaca;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Alpaca.Security;
using Microsoft.Extensions.Configuration;
using AlpacaProgram = HVO.RoofControllerV4.Alpaca.Program;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The Alpaca server's settings (#220): off by default, listening only where it is told and never on a public address,
/// answering only its LAN, with its key in a file only its user can read and its device identities kept for good.
/// </summary>
[TestClass]
public sealed class AlpacaOptionsTests
{
    private static AlpacaOptions Valid() => new()
    {
        Enabled = true,
        ListenAddresses = ["192.168.1.20"],
        ControllerApiKeyFile = "/run/secrets/alpaca-key",
    };

    [TestMethod]
    public void TheServer_IsOffByDefault()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        AlpacaProgram.ReadOptions(configuration, discoveryOnly: false).Should().BeNull();
        AlpacaProgram.ReadOptions(configuration, discoveryOnly: true).Should().BeNull();
        AlpacaProgram.BuildApp([]).Should().BeNull("an Alpaca server that is off builds nothing and listens nowhere");
        new AlpacaOptions().Port.Should().Be(11111);
        new AlpacaOptions().Discovery.Port.Should().Be(32227);
    }

    [TestMethod]
    public void ValidSettings_HaveNoProblems() => Valid().Validate().Should().BeEmpty();

    [TestMethod]
    public void TheListenAddress_MustBeGiven()
    {
        var options = Valid();
        options.ListenAddresses = [];

        options.Validate().Should().ContainSingle().Which.Should().Contain("ListenAddresses");
    }

    [TestMethod]
    [DataRow("127.0.0.1", true)]
    [DataRow("0.0.0.0", true)]
    [DataRow("::", true)]
    [DataRow("10.1.2.3", true)]
    [DataRow("fd00::5", true)]
    [DataRow("8.8.8.8", false)]
    [DataRow("2001:db8::1", false)]
    [DataRow("roof.local", false)]
    public void TheListenAddress_IsALanAddress(string address, bool valid)
    {
        var options = Valid();
        options.ListenAddresses = [address];

        options.Validate().Should().HaveCount(valid ? 0 : 1);
    }

    [TestMethod]
    [DataRow("192.168.1.0/24", true)]
    [DataRow("10.0.0.0/8", true)]
    [DataRow("127.0.0.1/32", true)]
    [DataRow("fd12:3456::/48", true)]
    [DataRow("0.0.0.0/0", false)]
    [DataRow("8.8.8.0/24", false)]
    [DataRow("100.64.0.0/10", false)]
    [DataRow("192.168.0.0/15", false)]
    [DataRow("not a network", false)]
    public void AllowedNetworks_StayWithinTheLan(string network, bool valid)
    {
        var options = Valid();
        options.AllowedNetworks = [network];

        options.Validate().Should().HaveCount(valid ? 0 : 1);
    }

    [TestMethod]
    public void TheDefaultAllowedNetworks_AreLoopbackAndThePrivateRanges()
    {
        var networks = new AlpacaOptions().EffectiveAllowedNetworks;

        AlpacaNetworks.Contains(networks, IPAddress.Parse("192.168.1.50")).Should().BeTrue();
        AlpacaNetworks.Contains(networks, IPAddress.Parse("10.9.8.7")).Should().BeTrue();
        AlpacaNetworks.Contains(networks, IPAddress.Parse("172.17.0.1")).Should().BeTrue("Docker's gateway");
        AlpacaNetworks.Contains(networks, IPAddress.Loopback).Should().BeTrue();
        AlpacaNetworks.Contains(networks, IPAddress.IPv6Loopback).Should().BeTrue();
        AlpacaNetworks.Contains(networks, IPAddress.Parse("::ffff:192.168.1.50")).Should().BeTrue();
        AlpacaNetworks.Contains(networks, IPAddress.Parse("203.0.113.9")).Should().BeFalse();
        AlpacaNetworks.Contains(networks, IPAddress.Parse("100.100.1.1")).Should().BeFalse();
        AlpacaNetworks.Contains(networks, IPAddress.Parse("2001:db8::1")).Should().BeFalse();
    }

    [TestMethod]
    public void TheKeyFile_MustBeNamed()
    {
        var options = Valid();
        options.ControllerApiKeyFile = null;

        options.Validate().Should().ContainSingle().Which.Should().Contain("ControllerApiKeyFile");
        options.Validate(discoveryOnly: true).Should().BeEmpty("the discovery responder alone needs no key");
    }

    [TestMethod]
    public void TheControllersCertificate_IsPinnedOrItsCaGiven_NotBoth()
    {
        var options = Valid();
        options.ControllerCertificateSha256 = new string('a', 64);
        options.Validate().Should().BeEmpty();

        options.ControllerCaCertificatePath = "/etc/hvo-roof/ca.pem";
        options.Validate().Should().ContainSingle().Which.Should().Contain("not both");

        options.ControllerCaCertificatePath = null;
        options.ControllerCertificateSha256 = "not-a-hash";
        options.Validate().Should().ContainSingle().Which.Should().Contain("SHA-256");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(65536)]
    public void APortOutOfRange_IsRefused(int port)
    {
        var options = Valid();
        options.Port = port;
        options.Discovery.AdvertisedPort = port;

        options.Validate().Should().HaveCount(2);
    }

    [TestMethod]
    public void AHostNameThatIsNotOne_IsRefused()
    {
        var options = Valid();
        options.AllowedHosts = ["roof.example.org", "not a host"];

        options.Validate().Should().ContainSingle().Which.Should().Contain("not a host");
    }

    [TestMethod]
    public void BadSettings_StopTheServer_WithEveryProblem()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Alpaca:Enabled"] = "true",
            ["Alpaca:Port"] = "0",
        }).Build();

        var read = () => AlpacaProgram.ReadOptions(configuration, discoveryOnly: false);

        read.Should().Throw<AlpacaSettingsException>().Which.Message.Should()
            .Contain("Port").And.Contain("ListenAddresses").And.Contain("ControllerApiKeyFile");
    }

    [TestMethod]
    public void TheKeyFile_IsReadAsOneKey_AndItsValueIsNeverInAMessage()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("key", "an-alpaca-key-for-tests-0123456789\n", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        AlpacaProgram.LoadKey(path).Should().Be("an-alpaca-key-for-tests-0123456789");

        var twoLines = directory.Write("two", "secret-one-0123456789\nsecret-two\n", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var load = () => AlpacaProgram.LoadKey(twoLines);
        load.Should().Throw<AlpacaSettingsException>().Which.Message.Should().NotContain("secret-one").And.NotContain("secret-two");
    }

    [TestMethod]
    [DataRow(UnixFileMode.OtherRead)]
    [DataRow(UnixFileMode.GroupWrite)]
    public void AKeyFileOthersCanReadOrChange_IsRefused(UnixFileMode extra)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes only.");
        }

        using var directory = new TempDirectory();
        var path = directory.Write("key", "an-alpaca-key-for-tests-0123456789", UnixFileMode.UserRead | UnixFileMode.UserWrite | extra);

        var load = () => AlpacaProgram.LoadKey(path);

        load.Should().Throw<AlpacaSettingsException>().Which.Message.Should().Contain("chmod 600");
    }

    [TestMethod]
    public void AMissingKeyFile_IsRefused()
    {
        var load = () => AlpacaProgram.LoadKey("/nonexistent/alpaca-key");

        load.Should().Throw<AlpacaSettingsException>().Which.Message.Should().Contain("could not be read");
    }

    [TestMethod]
    public void TheDevicesIdentity_IsMadeOnce_AndKept()
    {
        using var directory = new TempDirectory();
        var data = Path.Combine(directory.Path, "nested", "data");

        var first = AlpacaDeviceIds.Load(data);
        var second = AlpacaDeviceIds.Load(data);

        Guid.TryParse(first.Dome, out _).Should().BeTrue();
        second.Dome.Should().Be(first.Dome);
        File.ReadAllText(Path.Combine(data, AlpacaDeviceIds.FileName)).Should().Contain(first.Dome);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("{}")]
    [DataRow("{\"dome/0\":\"not-a-guid\"}")]
    public void AnIdentityFileThatCannotBeRead_StopsTheServer_RatherThanBeingReplaced(string content)
    {
        using var directory = new TempDirectory();
        var path = directory.Write(AlpacaDeviceIds.FileName, content, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var load = () => AlpacaDeviceIds.Load(directory.Path);

        load.Should().Throw<AlpacaSettingsException>();
        File.ReadAllText(path).Should().Be(content, "the file is left for someone to restore");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Path = Directory.CreateTempSubdirectory("hvo-alpaca-options-").FullName;

        public string Path { get; }

        public string Write(string name, string content, UnixFileMode mode)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, mode);
            }

            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
