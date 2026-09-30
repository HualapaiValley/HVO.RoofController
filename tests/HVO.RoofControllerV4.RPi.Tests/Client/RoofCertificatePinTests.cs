using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// A controller with a self-signed certificate, reached over real HTTPS on a loopback port (#44): the pin is what lets
/// a request, Stop and the status hub's WebSocket through, and without it (or with another certificate's) all three
/// are refused at the TLS handshake. Every other client test but <see cref="RoofCertificateAuthorityTests"/> goes
/// through the in-memory server, which has no TLS.
/// </summary>
[TestClass]
public sealed class RoofCertificatePinTests
{
    private static X509Certificate2 _controller = null!;
    private static X509Certificate2 _other = null!;
    private static TlsTestController _host = null!;

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _controller = TestCertificates.CreateSelfSigned("CN=roof.local");
        _other = TestCertificates.CreateSelfSigned("CN=other.local");
        _host = await TlsTestController.StartAsync(_controller);
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _host.DisposeAsync();
        _controller.Dispose();
        _other.Dispose();
    }

    [TestMethod]
    public async Task WithThePin_ARequest_Stop_AndTheStatusHub_AllConnect()
    {
        using var client = CreatePinnedClient(RoofCertificatePin.GetSha256(_controller));

        var caller = await client.Auth.GetCallerAsync();
        caller.Name.Should().Be(TlsTestController.CallerName);

        var stop = await client.StopAsync();
        stop.Outcome.Should().Be(RoofStopOutcome.Acknowledged);
        stop.Message.Should().Be(RoofStopText.AcknowledgedVerified);

        await using var feed = client.CreateStatusFeed();
        feed.Start();
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && feed.Current is not null, "the first snapshot over the pinned WebSocket");
        feed.Current!.InstanceId.Should().Be(TlsTestController.InstanceId);
        feed.LastError.Should().BeNull();
        _host.HubKeys.Should().Contain(TestApiKeys.Viewer, "the key reaches the hub over the platform's WebSocket, not only over HTTP");
    }

    [TestMethod]
    [DataRow(true, DisplayName = "another certificate's pin")]
    [DataRow(false, DisplayName = "no pin")]
    public async Task WithoutTheRightPin_ARequest_Stop_AndTheStatusHub_AreAllRefusedAtTheHandshake(bool otherPin)
    {
        using var client = CreatePinnedClient(otherPin ? RoofCertificatePin.GetSha256(_other) : null);

        var request = await FluentActions.Awaiting(() => client.Auth.GetCallerAsync()).Should().ThrowAsync<HttpRequestException>();
        IsCertificateRefusal(request.Which).Should().BeTrue("the request fails on the certificate, not on the network");

        // With a pin, the refusal says why; without one, the platform refused it, and it is described as unreachable.
        var refusal = RoofCertificateRefusedException.Find(request.Which);
        if (otherPin)
        {
            refusal.Should().NotBeNull();
            refusal!.Reason.Should().Be(RoofCertificateRefusal.NotPinned);
            refusal.Message.Should().Be("The controller's certificate is not the pinned one, and this computer does not trust it.");
        }
        else
        {
            refusal.Should().BeNull();
        }

        var stop = await client.StopAsync();
        stop.Outcome.Should().Be(RoofStopOutcome.Failed);
        stop.Message.Should().Be(RoofStopText.Failed(refusal?.Message ?? RoofText.Unreachable));
        IsCertificateRefusal(stop.Error).Should().BeTrue();

        await using var feed = client.CreateStatusFeed();
        feed.Start();
        await WaitUntilAsync(() => feed.LastError is not null, "the hub's refusal");
        IsCertificateRefusal(feed.LastError).Should().BeTrue();
        feed.State.Should().NotBe(RoofStatusFeedState.Connected);
        feed.ConnectionCount.Should().Be(0);
        feed.Current.Should().BeNull();
    }

    [TestMethod]
    public void ThePin_IsTheCertificatesSha256_AndAcceptsColons()
    {
        var pin = RoofCertificatePin.GetSha256(_controller);
        pin.Should().Be(Convert.ToHexString(SHA256.HashData(_controller.RawData)));
        var withColons = string.Join(':', Enumerable.Range(0, pin.Length / 2).Select(i => pin.Substring(i * 2, 2)));
        FluentActions.Invoking(() => CreatePinnedClient(withColons).Dispose()).Should().NotThrow();
    }

    [TestMethod]
    public void APin_AndACaCertificate_TogetherAreRefused()
    {
        using var authority = TestCertificates.CreateAuthority("HVO Roof Test CA");
        var creating = () => new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = _host.BaseAddress,
            ServerCertificateSha256 = RoofCertificatePin.GetSha256(_controller),
            ServerCaCertificate = authority
        });

        creating.Should().Throw<ArgumentException>().WithMessage("Set a certificate pin or a CA certificate, not both.*");
    }

    private static RoofControllerClient CreatePinnedClient(string? pin) => new(new RoofConnectionOptions
    {
        BaseAddress = _host.BaseAddress,
        Credential = new RoofApiKeyCredential(TestApiKeys.Viewer),
        ServerCertificateSha256 = pin,
        StatusFeed = FastFeed
    });

    internal static bool IsCertificateRefusal(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsCertificateRefusal))
            {
                return true;
            }
        }

        return false;
    }
}
