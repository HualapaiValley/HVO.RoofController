using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The client library's parts that need no controller (#44): the command-line credentials file and environment, error
/// answers that are not the controller's own, the certificate pin, the connection options and the reconnect delay.
/// </summary>
[TestClass]
public sealed class RoofClientUnitTests
{
    private const string ApiKey = "test-stored-key-not-a-real-secret-13";
    private const string Token = "test-stored-token-not-a-real-secret-14";

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "hvo-client-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
        {
            // Whatever the umask, the credentials file's directory must be the owner's alone.
            File.SetUnixFileMode(_directory, OwnerDirectory);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- The credentials file -------------------------------------------------------------------------------------

    [TestMethod]
    public void TheCredentialsFile_IsWrittenForItsOwnerOnly_AndReadBack()
    {
        var path = Path.Combine(_directory, "hvo-roof", "credentials.json");
        var credentials = new RoofStoredCredentials
        {
            Controller = new Uri("https://roof.local:5001/"),
            ApiKey = ApiKey,
            OnBehalfOf = "alice",
            Session = new RoofStoredSession(Token, "alice", RoofControllerApiContract.OperatorRole, "session-1", new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)),
            CertificateSha256 = new string('A', 64)
        };

        RoofCredentialStore.Save(path, credentials);

        RoofCredentialStore.Load(path).Should().Be(credentials);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(OwnerOnly);
            File.GetUnixFileMode(Path.GetDirectoryName(path)!).Should().Be(OwnerDirectory);
        }
    }

    [TestMethod]
    public void SavingOverAFileOthersCouldRead_LeavesItForItsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_directory, "credentials.json");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        RoofCredentialStore.Save(path, new RoofStoredCredentials { ApiKey = ApiKey });

        File.GetUnixFileMode(path).Should().Be(OwnerOnly);
        Directory.GetFiles(_directory).Should().Equal([path], "the temporary file is renamed over the file");
        RoofCredentialStore.Load(path)!.ApiKey.Should().Be(ApiKey);
    }

    [TestMethod]
    [DataRow(UnixFileMode.GroupRead)]
    [DataRow(UnixFileMode.GroupWrite)]
    [DataRow(UnixFileMode.OtherRead)]
    [DataRow(UnixFileMode.OtherWrite)]
    public void AFileOtherUsersCanReadOrChange_IsRefused(UnixFileMode extra)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_directory, "credentials.json");
        File.WriteAllText(path, $$"""{ "apiKey": "{{ApiKey}}" }""");
        File.SetUnixFileMode(path, OwnerOnly | extra);

        var refused = FluentActions.Invoking(() => RoofCredentialStore.Load(path)).Should().Throw<RoofCredentialFileException>().Which;

        refused.Message.Should().Be($"{path} can be read or changed by other users. Run 'chmod 600 {path}', then try again.");
        refused.Message.Should().NotContain(ApiKey);
    }

    [TestMethod]
    [DataRow(UnixFileMode.GroupWrite)]
    [DataRow(UnixFileMode.OtherWrite)]
    public void AFileInADirectoryOtherUsersCanChange_IsRefused_ForReadingAndWriting(UnixFileMode extra)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_directory, "credentials.json");
        RoofCredentialStore.Save(path, new RoofStoredCredentials { ApiKey = ApiKey });
        File.SetUnixFileMode(_directory, OwnerDirectory | extra);
        var expected = $"{_directory} can be changed by other users, who could replace the credentials file. Run 'chmod 700 {_directory}', then try again.";

        FluentActions.Invoking(() => RoofCredentialStore.Load(path)).Should().Throw<RoofCredentialFileException>()
            .Which.Message.Should().Be(expected);
        FluentActions.Invoking(() => RoofCredentialStore.Save(path, new RoofStoredCredentials { ApiKey = ApiKey }))
            .Should().Throw<RoofCredentialFileException>().Which.Message.Should().Be(expected);

        File.SetUnixFileMode(_directory, OwnerDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        RoofCredentialStore.Load(path)!.ApiKey.Should().Be(ApiKey, "others may list the directory, as long as they cannot change it");
    }

    [TestMethod]
    public void AMissingFile_IsNoCredentials_AndAnInvalidOne_IsRefused()
    {
        var path = Path.Combine(_directory, "credentials.json");
        RoofCredentialStore.Load(path).Should().BeNull();
        RoofCredentialStore.Delete(path).Should().BeFalse();

        File.WriteAllText(path, $"{{ \"apiKey\": \"{ApiKey}\"");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnerOnly);
        }

        var refused = FluentActions.Invoking(() => RoofCredentialStore.Load(path)).Should().Throw<RoofCredentialFileException>().Which;
        refused.Message.Should().Be($"{path} is not a valid credentials file.");
        refused.Message.Should().NotContain(ApiKey);
        refused.InnerException.Should().BeAssignableTo<System.Text.Json.JsonException>();

        RoofCredentialStore.Delete(path).Should().BeTrue();
        File.Exists(path).Should().BeFalse();
    }

    [TestMethod]
    public void TheDefaultPath_FollowsXdgConfigHome()
    {
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "hvo-roof", "credentials.json");
        var configHome = Path.Combine(_directory, "config");

        RoofCredentialStore.GetDefaultPath(name => name == "XDG_CONFIG_HOME" ? configHome : null)
            .Should().Be(Path.Combine(configHome, "hvo-roof", "credentials.json"));
        RoofCredentialStore.GetDefaultPath(_ => null).Should().Be(home);
        RoofCredentialStore.GetDefaultPath(name => name == "XDG_CONFIG_HOME" ? "relative/config" : null)
            .Should().Be(home, "a relative XDG_CONFIG_HOME is ignored, as the specification requires");
    }

    [TestMethod]
    public void TheEnvironment_GivesCredentialsWithoutAFile()
    {
        var environment = new Dictionary<string, string?>();
        string? Get(string name) => environment.GetValueOrDefault(name);

        RoofCredentialStore.FromEnvironment(Get).Should().BeNull("no variable is set");
        environment[RoofCredentialStore.CertificateVariable] = new string('a', 64);
        RoofCredentialStore.FromEnvironment(Get).Should().BeNull("a certificate pin alone is not a credential");

        environment[RoofCredentialStore.ControllerVariable] = " https://roof.local:5001 ";
        environment[RoofCredentialStore.ApiKeyVariable] = $" {ApiKey} ";
        environment[RoofCredentialStore.OnBehalfOfVariable] = "alice";
        var fromKey = RoofCredentialStore.FromEnvironment(Get)!;
        fromKey.Controller.Should().Be(new Uri("https://roof.local:5001/"));
        fromKey.ApiKey.Should().Be(ApiKey);
        fromKey.CertificateSha256.Should().Be(new string('a', 64));
        fromKey.ToCredential().Should().BeOfType<RoofApiKeyCredential>().Which.OnBehalfOf.Should().Be("alice");

        environment[RoofCredentialStore.SessionVariable] = Token;
        RoofCredentialStore.FromEnvironment(Get)!.ToCredential().Should().BeOfType<RoofSessionCredential>()
            .Which.Token.Should().Be(Token, "a session is used before an API key");

        environment.Remove(RoofCredentialStore.SessionVariable);
        environment[RoofCredentialStore.OnBehalfOfVariable] = "Zoë Smith";
        FluentActions.Invoking(() => RoofCredentialStore.FromEnvironment(Get)!.ToCredential()).Should().Throw<RoofCredentialFileException>()
            .WithMessage(RoofApiKeyCredential.InvalidOnBehalfOf, "a name that is not a user name would break every request");
        environment[RoofCredentialStore.OnBehalfOfVariable] = "alice";

        foreach (var address in new[] { "roof.local:5001", "/roof", "ftp://roof.local/" })
        {
            environment[RoofCredentialStore.ControllerVariable] = address;
            FluentActions.Invoking(() => RoofCredentialStore.FromEnvironment(Get)).Should().Throw<RoofCredentialFileException>()
                .WithMessage("HVO_ROOF_URL is not an absolute http or https URL.", address);
        }
    }

    [TestMethod]
    public void TheStoredCredential_PrefersTheSession()
    {
        var session = new RoofStoredSession(Token, "alice", RoofControllerApiContract.OperatorRole, "session-1", null);

        new RoofStoredCredentials().ToCredential().Should().BeNull();
        new RoofStoredCredentials { ApiKey = "  " }.ToCredential().Should().BeNull();
        new RoofStoredCredentials { ApiKey = ApiKey }.ToCredential().Should().BeOfType<RoofApiKeyCredential>();
        new RoofStoredCredentials { ApiKey = ApiKey, Session = session with { Token = " " } }.ToCredential()
            .Should().BeOfType<RoofApiKeyCredential>("a blank token is no session");
        var credential = new RoofStoredCredentials { ApiKey = ApiKey, Session = session }.ToCredential()
            .Should().BeOfType<RoofSessionCredential>().Which;
        credential.Token.Should().Be(Token);
        credential.Name.Should().Be("alice");
        credential.SessionId.Should().Be("session-1");
        FluentActions.Invoking(() => new RoofStoredCredentials { ApiKey = ApiKey, OnBehalfOf = "alice\r\nX-Other: 1" }.ToCredential())
            .Should().Throw<RoofCredentialFileException>().WithMessage(RoofApiKeyCredential.InvalidOnBehalfOf);
        new RoofStoredCredentials { ApiKey = ApiKey, OnBehalfOf = " alice " }.ToCredential()
            .Should().BeOfType<RoofApiKeyCredential>().Which.OnBehalfOf.Should().Be("alice");
        FluentActions.Invoking(() => new RoofStoredCredentials { ApiKey = ApiKey + "\nX-Other: 1" }.ToCredential())
            .Should().Throw<RoofCredentialFileException>().WithMessage(RoofCredential.InvalidHeaderValue);
        FluentActions.Invoking(() => new RoofStoredCredentials { Session = session with { Token = Token + "ö" } }.ToCredential())
            .Should().Throw<RoofCredentialFileException>().WithMessage(RoofCredential.InvalidHeaderValue);
    }

    [TestMethod]
    [DataRow("test-key-not-a-real-secret\r\nX-Other: 1")]
    [DataRow("test-key-not-a-real-secret\t1")]
    [DataRow("test-kéy-not-a-real-secret")]
    public void AKeyOrTokenAHeaderCannotCarry_IsRefusedWhenTheCredentialIsMade(string value)
    {
        // Otherwise every request, Stop included, would fail as it is sent, and read as an unreachable controller.
        var session = new RoofSessionResponse(value, "session-1", "olive", RoofControllerApiContract.OperatorRole, RoofCredentialKind.Pin, DateTimeOffset.UtcNow, 300);
        var kiosk = new RoofKioskCredential(ApiKey);
        foreach (var (make, parameter) in new (Action Make, string Parameter)[]
        {
            (() => _ = new RoofApiKeyCredential(value), "apiKey"),
            (() => _ = new RoofSessionCredential(value), "token"),
            (() => _ = new RoofKioskCredential(value), "deviceKey"),
            (() => kiosk.UsePinSession(session), "session")
        })
        {
            FluentActions.Invoking(make).Should().Throw<ArgumentException>().WithMessage(RoofCredential.InvalidHeaderValue + "*")
                .Which.ParamName.Should().Be(parameter);
        }

        kiosk.PinSession.Should().BeNull();
        new RoofApiKeyCredential("test key with spaces, not a secret").GetHeaders(RoofCredentialUse.Request).Should().ContainSingle();
    }

    [TestMethod]
    public void NoCredential_ShowsItsSecretWhenPrinted()
    {
        const string secret = "test-printed-secret-not-a-real-secret-15";
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var session = new RoofSessionResponse(Token, "session-1", "olive", RoofControllerApiContract.OperatorRole, RoofCredentialKind.Pin, now, 300);
        var kiosk = new RoofKioskCredential(secret);
        var printed = new List<string>
        {
            new RoofApiKeyCredential(ApiKey).ToString(),
            new RoofApiKeyCredential(ApiKey, "alice").ToString(),
            new RoofSessionCredential(Token).ToString(),
            new RoofSessionCredential(Token, "alice").ToString(),
            kiosk.ToString(),
            session.ToString(),
            new RoofStoredSession(Token, "alice", null, null, now).ToString(),
            new RoofStoredCredentials { ApiKey = ApiKey, Session = new RoofStoredSession(Token, "alice", null, null, null) }.ToString(),
            new RoofApiKeySecretResponse(new RoofApiKeyResponse("kiosk-1", RoofControllerApiContract.ViewerRole, true, RoofApiKeySource.Managed, null, null), secret).ToString(),
            new RoofSignInRequest { Name = "alice", Password = secret }.ToString(),
            new RoofPinSignInRequest { Name = "olive", Pin = secret }.ToString(),
            new RoofPasswordChangeRequest { CurrentPassword = secret, NewPassword = secret + "-new" }.ToString(),
            new RoofUserCreateRequest { Name = "bob", Role = RoofControllerApiContract.ViewerRole, Password = secret }.ToString(),
            new RoofUserUpdateRequest { Pin = secret, RemovePassword = true }.ToString()
        };
        kiosk.UsePinSession(session);
        printed.Add(kiosk.ToString());

        printed.Should().OnlyContain(text => !text.Contains(ApiKey) && !text.Contains(Token) && !text.Contains(secret));
        printed.Should().Equal(
            "API key",
            "API key on behalf of alice",
            "session",
            "session for alice",
            "kiosk (locked)",
            "RoofSessionResponse { SessionId = session-1, Name = olive, Role = RoofOperator, Kind = Pin, ExpiresUtc = 2026-03-01T12:00:00.0000000+00:00, IdleTimeoutSeconds = 300 }",
            "RoofStoredSession { Name = alice, Role = , SessionId = , ExpiresUtc = 2026-03-01T12:00:00.0000000+00:00 }",
            "RoofStoredCredentials { Controller = , ApiKey = (set), OnBehalfOf = , Session = RoofStoredSession { Name = alice, Role = , SessionId = , ExpiresUtc =  }, CertificateSha256 =  }",
            "RoofApiKeySecretResponse { Key = RoofApiKeyResponse { Name = kiosk-1, Role = RoofViewer, Kiosk = True, Source = Managed, CreatedUtc = , UpdatedUtc =  } }",
            "RoofSignInRequest { Name = alice, Password = (set) }",
            "RoofPinSignInRequest { Name = olive, Pin = (set) }",
            "RoofPasswordChangeRequest { CurrentPassword = (set), NewPassword = (set) }",
            "RoofUserCreateRequest { Name = bob, Role = RoofViewer, Password = (set), Pin = (none) }",
            "RoofUserUpdateRequest { Role = , Password = (none), Pin = (set), RemovePassword = True, RemovePin = False }",
            "kiosk unlocked by olive");
    }

    // ---- Error answers ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnAnswerThatIsNotProblemDetails_IsDescribedByItsStatus()
    {
        using var page = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>Bad gateway</html>", Encoding.UTF8, "text/html") };
        using var empty = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        using var array = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("[1, 2]") };

        var fromPage = await RoofApiException.FromResponseAsync(page);
        var fromEmpty = await RoofApiException.FromResponseAsync(empty);
        var fromArray = await RoofApiException.FromResponseAsync(array);

        fromPage.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        fromPage.Code.Should().BeNull();
        fromPage.CodeText.Should().BeNull();
        fromPage.Title.Should().BeNull();
        fromPage.Detail.Should().BeNull();
        fromPage.Message.Should().Be("The controller reported an unexpected error.");
        fromPage.Errors.Should().BeEmpty();
        fromEmpty.Message.Should().Be("Too many requests. Wait, then try again.");
        fromEmpty.RetryAfter.Should().BeNull();
        fromArray.Message.Should().Be("The request was invalid.");
    }

    [TestMethod]
    public async Task ACodeThisClientDoesNotKnow_IsKeptAsText_AndDescribedByTheStatus()
    {
        var future = await FromProblemAsync(HttpStatusCode.Conflict, """
            { "title": "Refused", "status": 409, "detail": "A newer rule.", "code": "SomeFutureCode", "traceId": "00-abc-01",
              "errors": { "pulseMs": ["Too long."] } }
            """);
        var numeric = await FromProblemAsync(HttpStatusCode.Conflict, """{ "code": "3" }""");
        var wrongCase = await FromProblemAsync(HttpStatusCode.Conflict, """{ "code": "faultLatched" }""");
        var notText = await FromProblemAsync(HttpStatusCode.Conflict, """{ "code": 3, "roofStatus": "open" }""");

        future.Code.Should().BeNull();
        future.CodeText.Should().Be("SomeFutureCode");
        future.Title.Should().Be("Refused");
        future.Detail.Should().Be("A newer rule.");
        future.TraceId.Should().Be("00-abc-01");
        future.Errors.Should().ContainKey("pulseMs").WhoseValue.Should().Equal("Too long.");
        future.Message.Should().Be("The controller refused the request (HTTP 409).");
        numeric.Code.Should().BeNull("a number is not read as an error code");
        numeric.CodeText.Should().Be("3");
        wrongCase.Code.Should().BeNull("codes are matched exactly");
        notText.CodeText.Should().BeNull();
        notText.RoofStatus.Should().BeNull();
    }

    [TestMethod]
    public async Task ARefusalBeforeTheApi_KeepsItsOwnCode()
    {
        var refused = await FromProblemAsync(HttpStatusCode.Forbidden, """{ "title": "HTTPS required", "code": "https_required" }""");

        refused.Code.Should().BeNull();
        refused.CodeText.Should().Be("https_required");
        refused.Message.Should().Be("The controller requires HTTPS from this network. [https_required]");
    }

    [TestMethod]
    public async Task RetryAfter_IsReadAsADelayOrADate()
    {
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        using var delay = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        delay.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
        using var date = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        date.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(30));
        using var past = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        past.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(-30));

        (await RoofApiException.FromResponseAsync(delay, clock)).RetryAfter.Should().Be(TimeSpan.FromSeconds(5));
        (await RoofApiException.FromResponseAsync(date, clock)).RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
        (await RoofApiException.FromResponseAsync(past, clock)).RetryAfter.Should().Be(TimeSpan.Zero);
    }

    // ---- The certificate pin ---------------------------------------------------------------------------------------

    [TestMethod]
    public void APinnedCertificate_IsAccepted_EvenWhenSelfSigned()
    {
        using var pinned = SelfSigned("CN=roof.local");
        using var other = SelfSigned("CN=roof.local");
        var hash = RoofCertificatePin.GetSha256(pinned);
        var written = string.Join(":", Enumerable.Range(0, 32).Select(i => hash.Substring(i * 2, 2))).ToLowerInvariant();

        hash.Should().MatchRegex("^[0-9A-F]{64}$");
        var validator = RoofCertificatePin.CreateValidator(written);

        validator(this, pinned, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue("the pin matches");
        validator(this, pinned, null, SslPolicyErrors.RemoteCertificateNameMismatch).Should().BeTrue("the pin matches");
        validator(this, other, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse("another certificate is not trusted");
        validator(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable).Should().BeFalse();
        validator(this, other, null, SslPolicyErrors.None).Should().BeTrue("a trusted certificate is still accepted");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("ABCD")]
    [DataRow("ZZ00000000000000000000000000000000000000000000000000000000000000")]
    [DataRow("00000000000000000000000000000000000000000000000000000000000000000")]
    public void APinThatIsNotASha256Hash_IsRefused(string pin)
        => FluentActions.Invoking(() => RoofCertificatePin.Parse(pin)).Should().Throw<ArgumentException>()
            .WithMessage("The certificate pin must be a SHA-256 hash: 64 hex digits.*");

    [TestMethod]
    public void APin_MayBeWrittenWithColonsSpacesOrDashes()
        => RoofCertificatePin.Parse(string.Join(" ", Enumerable.Repeat("ab:cd-ef01", 8))).Should().HaveCount(32);

    // ---- Options and the reconnect delay --------------------------------------------------------------------------

    [TestMethod]
    public void TheOptions_RefuseValuesTheClientCannotUse()
    {
        Refusal(new RoofConnectionOptions { BaseAddress = new Uri("ftp://roof.local/") })
            .Should().StartWith("The base address must be an absolute http or https URL.");
        Refusal(new RoofConnectionOptions { BaseAddress = new Uri("/api", UriKind.Relative) })
            .Should().StartWith("The base address must be an absolute http or https URL.");
        Refusal(Options(requestTimeout: TimeSpan.Zero)).Should().Be("Timeouts must be positive.");
        Refusal(Options(stopTimeout: TimeSpan.FromSeconds(-1))).Should().Be("Timeouts must be positive.");
        Refusal(Options(pin: "not-a-pin")).Should().StartWith("The certificate pin must be a SHA-256 hash: 64 hex digits.");
        Refusal(Options(feed: new RoofStatusFeedOptions { InitialReconnectDelay = TimeSpan.Zero }))
            .Should().Be("Reconnect delays must be positive, and the maximum at least the initial delay.");
        Refusal(Options(feed: new RoofStatusFeedOptions { InitialReconnectDelay = TimeSpan.FromSeconds(10), MaxReconnectDelay = TimeSpan.FromSeconds(5) }))
            .Should().Be("Reconnect delays must be positive, and the maximum at least the initial delay.");
        foreach (var jitter in new[] { -0.1, 1, double.NaN })
        {
            Refusal(Options(feed: new RoofStatusFeedOptions { ReconnectJitter = jitter })).Should().Be("Reconnect jitter must be at least 0 and less than 1.");
        }

        Refusal(Options(feed: new RoofStatusFeedOptions { StaleAfter = TimeSpan.Zero })).Should().Be("StaleAfter and ConnectTimeout must be positive.");
        Refusal(Options(feed: new RoofStatusFeedOptions { ConnectTimeout = TimeSpan.Zero })).Should().Be("StaleAfter and ConnectTimeout must be positive.");

        FluentActions.Invoking(() => new RoofControllerClient(Options(requestTimeout: TimeSpan.Zero))).Should().Throw<ArgumentException>(
            "the client checks its options when it is made");
    }

    [TestMethod]
    public void TheDefaults_AreValid_AndTheBaseAddressGainsATrailingSlash()
    {
        var options = new RoofConnectionOptions { BaseAddress = new Uri("https://roof.local:5001/controller") };

        options.Invoking(o => o.Validate()).Should().NotThrow();
        options.NormalizedBaseAddress.Should().Be(new Uri("https://roof.local:5001/controller/"));
        new RoofConnectionOptions { BaseAddress = new Uri("https://roof.local/") }.NormalizedBaseAddress.AbsoluteUri.Should().Be("https://roof.local/");
        options.StatusFeed.StaleAfter.Should().Be(TimeSpan.FromSeconds(3), "three heartbeat intervals");
    }

    [TestMethod]
    [DataRow(0, 1d)]
    [DataRow(1, 2d)]
    [DataRow(2, 4d)]
    [DataRow(4, 16d)]
    [DataRow(5, 30d)]
    [DataRow(1000, 30d)]
    [DataRow(int.MaxValue, 30d)]
    public void TheReconnectDelay_DoublesUpToTheMaximum(int attempt, double seconds)
        => RoofReconnectDelay.For(attempt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 0, 0.5).Should().Be(TimeSpan.FromSeconds(seconds));

    [TestMethod]
    public void TheReconnectDelay_IsSpreadByTheJitter_ButNeverPastTheMaximum()
    {
        TimeSpan Delay(int attempt, double random) => RoofReconnectDelay.For(attempt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 0.2, random);

        Delay(0, 0).Should().Be(TimeSpan.FromSeconds(1), "the jitter never shortens the first delay below the initial one");
        Delay(0, 1).Should().Be(TimeSpan.FromSeconds(1.2));
        Delay(1, 0).Should().Be(TimeSpan.FromSeconds(1.6));
        Delay(1, 0.5).Should().Be(TimeSpan.FromSeconds(2));
        Delay(1, 1).Should().Be(TimeSpan.FromSeconds(2.4));
        Delay(1, 7).Should().Be(TimeSpan.FromSeconds(2.4), "the random value is clamped");
        Delay(1, -7).Should().Be(TimeSpan.FromSeconds(1.6));
        Delay(10, 0).Should().Be(TimeSpan.FromSeconds(24));
        Delay(10, 1).Should().Be(TimeSpan.FromSeconds(30), "the jitter never goes past the maximum");
        FluentActions.Invoking(() => Delay(-1, 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void ALeaseWithNoEnd_IsRenewedAtTheLongestDelay()
        => RoofStatusRules.GetLeaseRenewalDelay(double.PositiveInfinity).Should().Be(RoofStatusRules.MaximumLeaseRenewalDelay);

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static async Task<RoofApiException> FromProblemAsync(HttpStatusCode status, string json)
    {
        using var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/problem+json") };
        return await RoofApiException.FromResponseAsync(response);
    }

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static RoofConnectionOptions Options(
        TimeSpan? requestTimeout = null,
        TimeSpan? stopTimeout = null,
        string? pin = null,
        RoofStatusFeedOptions? feed = null)
        => new()
        {
            BaseAddress = new Uri("https://roof.local:5001/"),
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30),
            StopTimeout = stopTimeout ?? TimeSpan.FromSeconds(10),
            ServerCertificateSha256 = pin,
            StatusFeed = feed ?? new RoofStatusFeedOptions()
        };

    private static string Refusal(RoofConnectionOptions options)
        => options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>().Which.Message;
}
