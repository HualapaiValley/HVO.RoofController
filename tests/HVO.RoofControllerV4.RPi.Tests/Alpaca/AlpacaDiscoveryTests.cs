using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Alpaca;
using HVO.RoofControllerV4.Alpaca.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The Alpaca discovery responder (#220): it answers <c>alpacadiscovery1</c> with the server's port, to allowed clients
/// only, ignores anything else, and shares its port with other responders on the same computer. The tests use a port
/// the system picks, on loopback, so they never answer the LAN.
/// </summary>
[TestClass]
public sealed class AlpacaDiscoveryTests
{
    [TestMethod]
    public async Task ADiscoveryRequest_IsAnsweredWithTheServersPort()
    {
        await using var responder = await Responder.StartAsync(new AlpacaOptions { Port = 11111, ListenAddresses = ["127.0.0.1"] });

        var answer = await responder.AskAsync("alpacadiscovery1");

        answer.Should().Be("{\"AlpacaPort\":11111}");
    }

    [TestMethod]
    public async Task TheAdvertisedPort_IsTheOneClientsAreTold()
    {
        var options = new AlpacaOptions { Port = 11111, ListenAddresses = ["0.0.0.0"] };
        options.Discovery.AdvertisedPort = 47123;
        await using var responder = await Responder.StartAsync(options);

        (await responder.AskAsync("alpacadiscovery1")).Should().Be("{\"AlpacaPort\":47123}");
    }

    [TestMethod]
    [DataRow("alpacadiscovery2")]
    [DataRow("alpacadiscovery")]
    [DataRow("hello")]
    [DataRow("ALPACADISCOVERY1")]
    public async Task AnythingElse_IsIgnored(string message)
    {
        await using var responder = await Responder.StartAsync(new AlpacaOptions { ListenAddresses = ["127.0.0.1"] });

        (await responder.AskAsync(message)).Should().BeNull();
    }

    [TestMethod]
    public async Task AClientOutsideTheAllowedNetworks_IsNotAnswered()
    {
        var options = new AlpacaOptions { AllowedNetworks = ["192.168.7.0/24"] };
        await using var responder = await Responder.StartAsync(options, discoveryOnly: true);

        (await responder.AskAsync("alpacadiscovery1")).Should().BeNull("loopback is not among the allowed networks");
    }

    [TestMethod]
    public async Task AServerOnLoopbackAlone_StillAnswersOnlyTheAllowedNetworks()
    {
        var options = new AlpacaOptions { ListenAddresses = ["127.0.0.1"], AllowedNetworks = ["10.0.0.0/8"] };
        await using var responder = await Responder.StartAsync(options);

        (await responder.AskAsync("alpacadiscovery1")).Should().BeNull("loopback is outside the allowed networks");
    }

    [TestMethod]
    public async Task AServerOnLoopbackAlone_AnswersLoopback_WhenItIsAllowed()
    {
        var options = new AlpacaOptions { ListenAddresses = ["127.0.0.1"], AllowedNetworks = ["127.0.0.0/8", "10.0.0.0/8"] };
        await using var responder = await Responder.StartAsync(options);

        (await responder.AskAsync("alpacadiscovery1")).Should().NotBeNull();
    }

    [TestMethod]
    public async Task TheDiscoveryPort_IsShared()
    {
        await using var first = await Responder.StartAsync(new AlpacaOptions { ListenAddresses = ["127.0.0.1"] });
        var options = new AlpacaOptions { ListenAddresses = ["127.0.0.1"] };
        options.Discovery.Port = first.Port;

        await using var second = await Responder.StartAsync(options);

        second.Port.Should().Be(first.Port, "another Alpaca server on this computer can answer discovery too");
    }

    [TestMethod]
    public void OnlyVersionOne_IsADiscoveryRequest()
    {
        AlpacaDiscoveryResponder.IsDiscovery(Encoding.ASCII.GetBytes("alpacadiscovery1")).Should().BeTrue();
        AlpacaDiscoveryResponder.IsDiscovery(Encoding.ASCII.GetBytes("alpacadiscovery1 and more")).Should().BeTrue();
        AlpacaDiscoveryResponder.IsDiscovery(Encoding.ASCII.GetBytes("alpacadiscovery3")).Should().BeFalse();
        AlpacaDiscoveryResponder.IsDiscovery([]).Should().BeFalse();
    }

    private sealed class Responder : IAsyncDisposable
    {
        private readonly AlpacaDiscoveryResponder _responder;

        private Responder(AlpacaDiscoveryResponder responder, int port)
        {
            _responder = responder;
            Port = port;
        }

        public int Port { get; }

        public static async Task<Responder> StartAsync(AlpacaOptions options, bool discoveryOnly = false)
        {
            if (options.Discovery.Port == AlpacaDiscoveryOptions.DefaultPort)
            {
                options.Discovery.Port = 0;
            }

            var responder = new AlpacaDiscoveryResponder(options, NullLogger<AlpacaDiscoveryResponder>.Instance, discoveryOnly);
            await responder.StartAsync(CancellationToken.None);
            var port = await responder.Bound.WaitAsync(TimeSpan.FromSeconds(5));
            return new Responder(responder, port);
        }

        /// <summary>Sends <paramref name="message"/> from loopback and returns the answer, or null when none comes in 500 ms.</summary>
        public async Task<string?> AskAsync(string message)
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await client.SendAsync(Encoding.ASCII.GetBytes(message), new IPEndPoint(IPAddress.Loopback, Port));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                var answer = await client.ReceiveAsync(timeout.Token);
                return Encoding.ASCII.GetString(answer.Buffer);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _responder.StopAsync(CancellationToken.None);
            _responder.Dispose();
        }
    }
}
