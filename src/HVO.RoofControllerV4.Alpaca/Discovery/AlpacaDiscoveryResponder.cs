using System.Net;
using System.Net.Sockets;
using System.Text;
using HVO.RoofControllerV4.Alpaca.Security;
using IPNetwork = System.Net.IPNetwork;

namespace HVO.RoofControllerV4.Alpaca.Discovery;

/// <summary>
/// Answers Alpaca discovery: a UDP datagram <c>alpacadiscovery1</c> to <see cref="AlpacaDiscoveryOptions.Port"/> (32227),
/// broadcast on IPv4 or, with <see cref="AlpacaDiscoveryOptions.IPv6"/>, sent to the multicast group ff12::a1:9aca, is
/// answered to its sender with <c>{"AlpacaPort":11111}</c> (the advertised port). Only clients within
/// <see cref="AlpacaOptions.AllowedNetworks"/> are answered, and only loopback ones when the server listens on loopback
/// alone. The sockets share the port (SO_REUSEADDR), so another Alpaca server on the same computer can answer too.
/// </summary>
/// <remarks>
/// A container on Docker's bridge network never receives the LAN's broadcasts. There the responder runs by itself on
/// the host's network (<c>--discovery-only</c>), advertising the port the container publishes the server on
/// (docs/alpaca.md).
/// </remarks>
public sealed class AlpacaDiscoveryResponder : BackgroundService
{
    /// <summary>The discovery message's prefix; the character after it is the protocol version.</summary>
    public const string MessagePrefix = "alpacadiscovery";

    /// <summary>The discovery versions this responder answers.</summary>
    public const char SupportedVersion = '1';

    /// <summary>The IPv6 multicast group Alpaca discovery uses.</summary>
    public static readonly IPAddress IPv6Group = IPAddress.Parse("ff12::a1:9aca");

    private readonly AlpacaOptions _options;
    private readonly IReadOnlyList<IPNetwork> _networks;
    private readonly byte[] _answer;
    private readonly ILogger<AlpacaDiscoveryResponder> _logger;
    private readonly TaskCompletionSource<int> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AlpacaDiscoveryResponder(AlpacaOptions options, ILogger<AlpacaDiscoveryResponder> logger, bool discoveryOnly = false)
    {
        _options = options;
        _logger = logger;
        // The server listening on loopback alone can be reached from this computer only, so only it is told of the server.
        _networks = !discoveryOnly && options.ListenAddresses.Length > 0 && AlpacaNetworks.AllLoopback(options.ParsedListenAddresses)
            ? [IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("::1/128")]
            : options.EffectiveAllowedNetworks;
        _answer = Encoding.ASCII.GetBytes($"{{\"AlpacaPort\":{options.AdvertisedPort}}}");
    }

    /// <summary>The IPv4 port the responder listens on, once it is listening (for the tests, which use port 0).</summary>
    public Task<int> Bound => _bound.Task;

    /// <summary>True when <paramref name="datagram"/> is a discovery request this responder answers.</summary>
    public static bool IsDiscovery(ReadOnlySpan<byte> datagram)
        => datagram.Length >= MessagePrefix.Length + 1
            && datagram[..MessagePrefix.Length].SequenceEqual(Encoding.ASCII.GetBytes(MessagePrefix))
            && datagram[MessagePrefix.Length] == (byte)SupportedVersion;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Socket? v4 = null;
        Socket? v6 = null;
        try
        {
            v4 = Open(AddressFamily.InterNetwork, IPAddress.Any);
            _bound.TrySetResult(((IPEndPoint)v4.LocalEndPoint!).Port);
            if (_options.Discovery.IPv6)
            {
                v6 = OpenIPv6();
            }

            _logger.LogInformation(
                "Answering Alpaca discovery on UDP port {DiscoveryPort}{IPv6} with port {AdvertisedPort}",
                ((IPEndPoint)v4.LocalEndPoint!).Port, v6 is null ? string.Empty : " (IPv4 and IPv6)", _options.AdvertisedPort);
            await Task.WhenAll(
                AnswerAsync(v4, new IPEndPoint(IPAddress.Any, 0), stoppingToken),
                v6 is null ? Task.CompletedTask : AnswerAsync(v6, new IPEndPoint(IPAddress.IPv6Any, 0), stoppingToken)).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            _bound.TrySetException(ex);
            _logger.LogError(ex, "Could not answer Alpaca discovery on UDP port {DiscoveryPort}; clients must be given the server's address", _options.Discovery.Port);
        }
        finally
        {
            _bound.TrySetCanceled(CancellationToken.None);
            v4?.Dispose();
            v6?.Dispose();
        }
    }

    private Socket Open(AddressFamily family, IPAddress any)
    {
        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (family == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = false;
            }
            else
            {
                socket.EnableBroadcast = true;
            }

            socket.Bind(new IPEndPoint(any, _options.Discovery.Port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private Socket OpenIPv6()
    {
        var socket = Open(AddressFamily.InterNetworkV6, IPAddress.IPv6Any);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(IPv6Group));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task AnswerAsync(Socket socket, EndPoint any, CancellationToken stoppingToken)
    {
        var buffer = new byte[256];
        while (!stoppingToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                // A datagram too large, or an ICMP error from an earlier answer: carry on.
                _logger.LogDebug(ex, "An Alpaca discovery datagram could not be received");
                continue;
            }

            if (received.RemoteEndPoint is not IPEndPoint sender
                || !IsDiscovery(buffer.AsSpan(0, received.ReceivedBytes))
                || !AlpacaNetworks.Contains(_networks, sender.Address))
            {
                continue;
            }

            try
            {
                await socket.SendToAsync(_answer, SocketFlags.None, sender, stoppingToken).ConfigureAwait(false);
                _logger.LogDebug("Answered Alpaca discovery from {Sender}", sender);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Could not answer Alpaca discovery from {Sender}", sender);
            }
        }
    }
}
