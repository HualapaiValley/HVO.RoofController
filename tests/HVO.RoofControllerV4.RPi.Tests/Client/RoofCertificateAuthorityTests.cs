using System.Net;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// A controller whose certificate a private CA issued, reached over real HTTPS on a loopback port (#65). With the CA
/// set, a request, Stop and the status hub's WebSocket are accepted when the CA issued the certificate for the host
/// being reached, and a certificate reissued under the same CA needs no change. Anything else is refused at the
/// handshake, and the refusal says why: another CA, expiry, a name outside the CA's name constraints, or a name
/// mismatch. The CAs and certificates are made at run time.
/// </summary>
[TestClass]
public sealed class RoofCertificateAuthorityTests
{
    private const string AuthorityName = "HVO Roof Test CA";

    private static X509Certificate2 _authority = null!;

    [ClassInitialize]
    public static void CreateAuthority(TestContext _) => _authority = TestCertificates.CreateAuthority(AuthorityName);

    [ClassCleanup]
    public static void DisposeAuthority() => _authority.Dispose();

    [TestMethod]
    [DataRow(false, DisplayName = "by address")]
    [DataRow(true, DisplayName = "by name")]
    public async Task UnderTheCa_ARequest_Stop_AndTheStatusHub_AllConnect(bool byName)
    {
        using var certificate = TestCertificates.Issue(_authority);
        await using var host = await TlsTestController.StartAsync(certificate);
        using var client = CreateClient(byName ? host.LocalhostAddress : host.BaseAddress);

        await ExpectAcceptedAsync(client, host);
    }

    [TestMethod]
    public async Task ACertificateReissuedUnderTheSameCa_IsAccepted_WithNoChangeToTheClient()
    {
        using var first = TestCertificates.Issue(_authority);
        using var reissued = TestCertificates.Issue(_authority, ["127.0.0.1", "localhost", "roof.example"]);
        await using var host = await TlsTestController.StartAsync(first);
        using var client = CreateClient(host.BaseAddress);
        (await client.Auth.GetCallerAsync()).Name.Should().Be(TlsTestController.CallerName);

        host.Present(reissued);

        // Stop and the status hub open their own connections, so they meet the reissued certificate.
        (await client.StopAsync()).Outcome.Should().Be(RoofStopOutcome.Acknowledged);
        await using var feed = client.CreateStatusFeed();
        feed.Start();
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && feed.Current is not null, "the first snapshot after the reissue");
        host.Presented.Should().Contain(first.Thumbprint).And.Contain(reissued.Thumbprint);
    }

    [TestMethod]
    public async Task ACertificateFromAnIntermediateCa_IsAccepted_WhenTheControllerSendsTheIntermediate()
    {
        using var intermediate = TestCertificates.CreateAuthority("HVO Roof Test Intermediate", issuer: _authority);
        using var certificate = TestCertificates.Issue(intermediate);
        await using var host = await TlsTestController.StartAsync(certificate, intermediate);
        using var client = CreateClient(host.BaseAddress);

        await ExpectAcceptedAsync(client, host);
    }

    [TestMethod]
    public async Task ANameInsideTheCasNameConstraints_IsAccepted()
    {
        using var constrained = TestCertificates.CreateAuthority(
            "HVO Roof Constrained CA",
            constraints: TestCertificates.PermitOnly(["localhost"], [(IPAddress.Parse("127.0.0.0"), 8)]));
        using var certificate = TestCertificates.Issue(constrained);
        await using var host = await TlsTestController.StartAsync(certificate);
        using var client = CreateClient(host.BaseAddress, constrained);

        await ExpectAcceptedAsync(client, host);
    }

    [TestMethod]
    public async Task ACertificateFromAnotherCaWithTheSameName_IsRefused()
    {
        // As after the CA was made again: the same name, another key.
        using var remade = TestCertificates.CreateAuthority(AuthorityName);
        using var certificate = TestCertificates.Issue(remade);

        await ExpectRefusedAsync(
            certificate,
            RoofCertificateRefusal.OtherAuthority,
            $"The controller's certificate was not issued by the CA this client trusts ({AuthorityName}).");
    }

    [TestMethod]
    public async Task ASelfSignedCertificate_IsRefused()
    {
        using var certificate = TestCertificates.CreateSelfSigned();

        await ExpectRefusedAsync(
            certificate,
            RoofCertificateRefusal.OtherAuthority,
            $"The controller's certificate was not issued by the CA this client trusts ({AuthorityName}).");
    }

    [TestMethod]
    public async Task ACertificateFromAnIntermediate_IsRefused_WhenTheControllerLeavesTheIntermediateOut()
    {
        using var intermediate = TestCertificates.CreateAuthority("HVO Roof Test Intermediate", issuer: _authority);
        using var certificate = TestCertificates.Issue(intermediate);

        await ExpectRefusedAsync(
            certificate,
            RoofCertificateRefusal.OtherAuthority,
            $"The controller's certificate was not issued by the CA this client trusts ({AuthorityName}).");
    }

    [TestMethod]
    public async Task ACertificateForAnotherName_IsRefused()
    {
        using var certificate = TestCertificates.Issue(_authority, ["roof.example", "10.0.0.5"]);

        await ExpectRefusedAsync(
            certificate,
            RoofCertificateRefusal.NameMismatch,
            "The controller's certificate is not for 127.0.0.1; it names roof.example, 10.0.0.5.");
    }

    [TestMethod]
    public async Task AnExpiredCertificate_IsRefused()
    {
        var expired = DateTimeOffset.UtcNow.AddHours(-1);
        using var certificate = TestCertificates.Issue(_authority, notBefore: expired.AddDays(-1), notAfter: expired);

        await ExpectRefusedAsync(
            certificate,
            RoofCertificateRefusal.Expired,
            $"The controller's certificate expired on {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.");
    }

    [TestMethod]
    public async Task ACertificateNotValidYet_IsRefused()
    {
        var from = DateTimeOffset.UtcNow.AddDays(1);
        using var certificate = TestCertificates.Issue(_authority, notBefore: from, notAfter: from.AddDays(30));

        await ExpectRefusedAsync(
            certificate,
            RoofCertificateRefusal.Expired,
            $"The controller's certificate is not valid until {certificate.NotBefore.ToUniversalTime():yyyy-MM-dd HH:mm} UTC. Check this computer's clock.");
    }

    [TestMethod]
    public async Task ACertificateUnderAnExpiredCa_IsRefused()
    {
        var until = DateTimeOffset.UtcNow.AddHours(-1);
        using var expired = TestCertificates.CreateAuthority("HVO Roof Expired CA", notBefore: until.AddDays(-30), notAfter: until);
        using var certificate = TestCertificates.Issue(expired);
        await using var host = await TlsTestController.StartAsync(certificate);
        using var client = CreateClient(host.BaseAddress, expired);

        var refusal = await RefusalOfAsync(client);

        refusal.Reason.Should().Be(RoofCertificateRefusal.Expired);
        refusal.Message.Should().Be($"The CA certificate (HVO Roof Expired CA) expired on {expired.NotAfter.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.");
    }

    [TestMethod]
    public async Task ANameOutsideTheCasNameConstraints_IsRefused()
    {
        using var constrained = TestCertificates.CreateAuthority(
            "HVO Roof Constrained CA",
            constraints: TestCertificates.PermitOnly(["roof.example"], [(IPAddress.Parse("10.0.0.0"), 8)]));
        using var certificate = TestCertificates.Issue(constrained);
        await using var host = await TlsTestController.StartAsync(certificate);
        using var client = CreateClient(host.BaseAddress, constrained);

        var refusal = await RefusalOfAsync(client);

        refusal.Reason.Should().Be(RoofCertificateRefusal.OutsideNameConstraints);
        refusal.Message.Should().Be(
            "The controller's certificate names a host that the CA (HVO Roof Constrained CA) may not issue for: it is outside the CA's name constraints.");
    }

    [TestMethod]
    public async Task ACertificateThatIsNotForAServer_IsRefused()
    {
        using var certificate = TestCertificates.Issue(_authority, usage: TestCertificates.ClientAuthentication);

        await ExpectRefusedAsync(
            certificate,
            RoofCertificateRefusal.Invalid,
            "The controller's certificate is not for a server: its extended key usage leaves out server authentication.");
    }

    [TestMethod]
    public void TheCaCertificate_LoadsFromPemOrDer_AndMustBeACas()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-roof-ca-");
        try
        {
            var pem = Path.Combine(directory.FullName, "ca.crt");
            var der = Path.Combine(directory.FullName, "ca.cer");
            var leaf = Path.Combine(directory.FullName, "leaf.crt");
            var text = Path.Combine(directory.FullName, "notes.txt");
            File.WriteAllText(pem, RoofCertificateAuthority.ToPem(_authority));
            File.WriteAllBytes(der, _authority.RawData);
            using (var issued = TestCertificates.Issue(_authority))
            {
                File.WriteAllText(leaf, issued.ExportCertificatePem());
            }

            File.WriteAllText(text, "not a certificate");

            using (var fromPem = RoofCertificateAuthority.Load(pem))
            using (var fromDer = RoofCertificateAuthority.Load(der))
            using (var fromText = RoofCertificateAuthority.FromPem(File.ReadAllText(pem)))
            {
                fromPem.Thumbprint.Should().Be(_authority.Thumbprint);
                fromDer.Thumbprint.Should().Be(_authority.Thumbprint);
                fromText.Thumbprint.Should().Be(_authority.Thumbprint);
                fromPem.HasPrivateKey.Should().BeFalse("only the certificate is read");
            }

            FluentActions.Invoking(() => RoofCertificateAuthority.Load(leaf)).Should().Throw<ArgumentException>()
                .WithMessage($"{leaf} is not a CA certificate: its basic constraints do not say CA.");
            FluentActions.Invoking(() => RoofCertificateAuthority.Load(text)).Should().Throw<ArgumentException>()
                .WithMessage($"{text} does not hold a certificate in PEM or DER form.");
            FluentActions.Invoking(() => RoofCertificateAuthority.Load(Path.Combine(directory.FullName, "missing.crt"))).Should().Throw<FileNotFoundException>();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void ACertificateThatIsNotACas_IsRefusedAsTheCa()
    {
        using var leaf = TestCertificates.Issue(_authority);
        var creating = () => new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("https://127.0.0.1/"),
            ServerCaCertificate = leaf
        });

        creating.Should().Throw<ArgumentException>().WithMessage("The CA certificate is not a CA's: its basic constraints do not say CA.*");
    }

    [TestMethod]
    public void ARefusal_IsDescribedByItsReason_AndSentNothing()
    {
        var refusal = new RoofCertificateRefusedException(RoofCertificateRefusal.OtherAuthority, "The controller's certificate was not issued by the CA this client trusts (X).");
        var failure = new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.", refusal);

        RoofText.DescribeFailure(failure).Should().Be(refusal.Message);
        RoofText.DescribeFailure(new AggregateException(new InvalidOperationException(), failure)).Should().Be(refusal.Message);
        RoofCertificateRefusedException.Find(new HttpRequestException("other")).Should().BeNull();
        RoofCommandRules.MayHaveReachedController(failure).Should().BeFalse("a refused handshake sent nothing");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "by address")]
    [DataRow(true, DisplayName = "by name")]
    public async Task TheCa_IsFetched_Anonymously_WhenItIssuedTheCertificateForTheHostReached(bool byName)
    {
        using var certificate = TestCertificates.Issue(_authority);
        await using var host = await TlsTestController.StartAsync(certificate);
        host.ServedAuthority = _authority;

        using var fetched = await RoofCertificateAuthority.FetchAsync(byName ? host.LocalhostAddress : host.BaseAddress, FetchTimeout);

        fetched.RawData.Should().Equal(_authority.RawData);
        RoofCertificateAuthority.Fingerprint(fetched).Should().Be(RoofCertificateAuthority.Fingerprint(_authority));
        host.CaRequestCredentials.Should().Equal([false], "nothing is trusted yet, so no key or session is sent");
    }

    [TestMethod]
    public async Task TheRoot_IsFetched_WhenAnIntermediateIssuedTheCertificate()
    {
        using var intermediate = TestCertificates.CreateAuthority("HVO Roof Test Intermediate", issuer: _authority);
        using var certificate = TestCertificates.Issue(intermediate);
        await using var host = await TlsTestController.StartAsync(certificate, intermediate);
        host.ServedAuthority = _authority;

        using var fetched = await RoofCertificateAuthority.FetchAsync(host.BaseAddress, FetchTimeout);

        fetched.RawData.Should().Equal(_authority.RawData);
    }

    [TestMethod]
    public async Task ACaThatDidNotIssueTheCertificate_IsNotFetched()
    {
        // Served by something in the way, or after the CA was made again: the same name, another key.
        using var remade = TestCertificates.CreateAuthority(AuthorityName);
        using var certificate = TestCertificates.Issue(_authority);
        await using var host = await TlsTestController.StartAsync(certificate);
        host.ServedAuthority = remade;

        var fetching = () => RoofCertificateAuthority.FetchAsync(host.BaseAddress, FetchTimeout);

        (await fetching.Should().ThrowAsync<RoofCaFetchException>()).WithMessage(
            $"The CA the controller serves ({AuthorityName}) did not issue the certificate it presents, so it is not saved.");
    }

    [TestMethod]
    public async Task AControllerWithASelfSignedCertificate_ServesNoCa()
    {
        using var certificate = TestCertificates.CreateSelfSigned();
        await using var host = await TlsTestController.StartAsync(certificate);

        var fetching = () => RoofCertificateAuthority.FetchAsync(host.BaseAddress, FetchTimeout);

        (await fetching.Should().ThrowAsync<RoofCaFetchException>()).WithMessage(
            $"The controller serves no CA at {host.BaseAddress}ca.crt: a private CA did not issue its certificate, *");
    }

    [TestMethod]
    [DataRow("leaf", "What the controller serves at *ca.crt is not a CA certificate: its basic constraints do not say CA.", DisplayName = "a leaf")]
    [DataRow("text", "What the controller serves at *ca.crt does not hold a certificate in PEM or DER form.", DisplayName = "not a certificate")]
    [DataRow("large", "What the controller serves as its CA is larger than 64 KiB, so it is not a CA certificate.", DisplayName = "too large")]
    public async Task WhatIsNotACaCertificate_IsNotFetched(string served, string message)
    {
        using var certificate = TestCertificates.Issue(_authority);
        await using var host = await TlsTestController.StartAsync(certificate);
        host.ServedCaText = served switch
        {
            "leaf" => certificate.ExportCertificatePem(),
            "text" => "<html>Sign in</html>",
            _ => new string('A', 65 * 1024)
        };

        var fetching = () => RoofCertificateAuthority.FetchAsync(host.BaseAddress, FetchTimeout);

        (await fetching.Should().ThrowAsync<RoofCaFetchException>()).WithMessage(message);
    }

    [TestMethod]
    public async Task TheCa_IsNotFetched_WhenItsCertificateIsForAnotherName()
    {
        using var certificate = TestCertificates.Issue(_authority, ["roof.example", "10.0.0.5"]);
        await using var host = await TlsTestController.StartAsync(certificate);
        host.ServedAuthority = _authority;

        var fetching = () => RoofCertificateAuthority.FetchAsync(host.BaseAddress, FetchTimeout);

        var refusal = await fetching.Should().ThrowAsync<RoofCertificateRefusedException>();
        refusal.Which.Reason.Should().Be(RoofCertificateRefusal.NameMismatch);
        refusal.Which.Message.Should().Be("The controller's certificate is not for 127.0.0.1; it names roof.example, 10.0.0.5.");
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:5000/")]
    [DataRow("ftp://roof.example/")]
    public async Task TheCa_IsOnlyFetchedOverHttps(string address)
    {
        var fetching = () => RoofCertificateAuthority.FetchAsync(new Uri(address), FetchTimeout);

        (await fetching.Should().ThrowAsync<ArgumentException>()).WithMessage("The CA is fetched over HTTPS, *");
    }

    [TestMethod]
    public void TheFingerprint_IsColonSeparatedHex_AndIsMatchedWithOrWithoutSeparators()
    {
        var fingerprint = RoofCertificateAuthority.Fingerprint(_authority);
        var hex = Convert.ToHexString(_authority.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256));
        using var other = TestCertificates.CreateAuthority(AuthorityName);

        fingerprint.Should().MatchRegex("^([0-9A-F]{2}:){31}[0-9A-F]{2}$").And.Be(string.Join(':', hex.Chunk(2).Select(pair => new string(pair))));
        RoofCertificateAuthority.HasFingerprint(_authority, fingerprint).Should().BeTrue();
        RoofCertificateAuthority.HasFingerprint(_authority, hex.ToLowerInvariant()).Should().BeTrue();
        RoofCertificateAuthority.HasFingerprint(_authority, fingerprint.Replace(':', ' ')).Should().BeTrue();
        RoofCertificateAuthority.HasFingerprint(_authority, fingerprint.Replace(':', '-')).Should().BeTrue();
        RoofCertificateAuthority.HasFingerprint(other, fingerprint).Should().BeFalse("another key under the same name");
        RoofCertificateAuthority.HasFingerprint(_authority, hex[..62]).Should().BeFalse("a fingerprint cut short");
        RoofCertificateAuthority.HasFingerprint(_authority, hex[..62] + "ZZ").Should().BeFalse("not hex");
        RoofCertificateAuthority.HasFingerprint(_authority, "").Should().BeFalse();
    }

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    private static RoofControllerClient CreateClient(Uri address, X509Certificate2? authority = null) => new(new RoofConnectionOptions
    {
        BaseAddress = address,
        Credential = new RoofApiKeyCredential(TestApiKeys.Viewer),
        ServerCaCertificate = authority ?? _authority,
        StatusFeed = FastFeed
    });

    private static async Task ExpectAcceptedAsync(RoofControllerClient client, TlsTestController host)
    {
        (await client.Auth.GetCallerAsync()).Name.Should().Be(TlsTestController.CallerName);

        var stop = await client.StopAsync();
        stop.Outcome.Should().Be(RoofStopOutcome.Acknowledged);
        stop.Message.Should().Be(RoofStopText.AcknowledgedVerified);

        await using var feed = client.CreateStatusFeed();
        feed.Start();
        await WaitUntilAsync(() => feed.State == RoofStatusFeedState.Connected && feed.Current is not null, "the first snapshot over the WebSocket");
        feed.Current!.InstanceId.Should().Be(TlsTestController.InstanceId);
        feed.LastError.Should().BeNull();
        host.HubKeys.Should().Contain(TestApiKeys.Viewer);
    }

    /// <summary>Starts a controller with <paramref name="certificate"/> and expects the default CA's client to refuse it.</summary>
    private static async Task ExpectRefusedAsync(X509Certificate2 certificate, RoofCertificateRefusal reason, string message)
    {
        await using var host = await TlsTestController.StartAsync(certificate);
        using var client = CreateClient(host.BaseAddress);

        var refusal = await RefusalOfAsync(client);

        refusal.Reason.Should().Be(reason);
        refusal.Message.Should().Be(message);
    }

    /// <summary>
    /// The refusal a request, Stop and the status hub all meet, which each reports: Stop and <see cref="RoofText"/> say
    /// why in the refusal's words.
    /// </summary>
    private static async Task<RoofCertificateRefusedException> RefusalOfAsync(RoofControllerClient client)
    {
        var request = await FluentActions.Awaiting(() => client.Auth.GetCallerAsync()).Should().ThrowAsync<HttpRequestException>();
        request.Which.HttpRequestError.Should().Be(HttpRequestError.SecureConnectionError);
        var refusal = RoofCertificateRefusedException.Find(request.Which);
        refusal.Should().NotBeNull("the request fails with the reason, not only with 'could not be reached'");
        RoofText.DescribeFailure(request.Which).Should().Be(refusal!.Message);

        var stop = await client.StopAsync();
        stop.Outcome.Should().Be(RoofStopOutcome.Failed);
        stop.Message.Should().Be(RoofStopText.Failed(refusal.Message));
        RoofCertificateRefusedException.Find(stop.Error)?.Reason.Should().Be(refusal.Reason);

        await using var feed = client.CreateStatusFeed();
        feed.Start();
        await WaitUntilAsync(() => feed.LastError is not null, "the hub's refusal");
        RoofCertificateRefusedException.Find(feed.LastError)?.Reason.Should().Be(refusal.Reason, "the hub is refused for the same reason");
        RoofCertificateRefusedException.Find(feed.LastError).Should().NotBeNull();
        feed.ConnectionCount.Should().Be(0);
        return refusal;
    }
}
