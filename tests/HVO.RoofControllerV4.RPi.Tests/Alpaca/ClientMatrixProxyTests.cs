using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using HVO.RoofControllerV4.AlpacaClientMatrix;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>The matrix's real forwarding proxy, with loopback HTTP only and no roof commands.</summary>
[TestClass]
public sealed class ClientMatrixProxyTests
{
    [TestMethod]
    public async Task OsAllocatedProxyPorts_RemainUsable_WhenAnOutgoingConnectionOwnsAnEarlierCandidate()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var upstream = builder.Build();
        upstream.Run(context => context.Response.WriteAsync("emulated proxy target"));
        await upstream.StartAsync();
        var target = new Uri(upstream.Urls.Single());

        // An earlier profile's outbound connection can acquire a port that a later fixed-port proxy plans to use.
        // This is a real connected socket, not another listener: preflight connection checks cannot reserve it.
        using var outbound = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await outbound.ConnectAsync(IPAddress.Loopback, target.Port);
        var candidate = ((IPEndPoint)outbound.LocalEndPoint!).Port;
        candidate.Should().BeGreaterThan(0);
        using var competingListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Action bindCandidate = () =>
        {
            competingListener.Bind(new IPEndPoint(IPAddress.Loopback, candidate));
            competingListener.Listen(1);
        };
        bindCandidate.Should().Throw<SocketException>().Which.SocketErrorCode.Should().Be(SocketError.AddressAlreadyInUse);

        await using var first = await CapturingProxy.StartAsync(0, target, "test-matrix-key-not-a-real-secret");
        await using var second = await CapturingProxy.StartAsync(0, target, "test-matrix-key-not-a-real-secret");
        first.Port.Should().BeGreaterThan(0, "clients must use the actual OS-bound port, not the requested zero");
        second.Port.Should().BeGreaterThan(0).And.NotBe(first.Port).And.NotBe(candidate);
        first.Port.Should().NotBe(candidate);

        using var http = new HttpClient();
        (await http.GetStringAsync($"http://127.0.0.1:{first.Port}/probe")).Should().Be("emulated proxy target");
        (await http.GetStringAsync($"http://127.0.0.1:{second.Port}/probe")).Should().Be("emulated proxy target");
        first.Requests.Should().ContainSingle().Which.Path.Should().Be("/probe");
        second.Requests.Should().ContainSingle().Which.Path.Should().Be("/probe");
    }
}
