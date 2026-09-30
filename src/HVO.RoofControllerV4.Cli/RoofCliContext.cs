using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Cli;

/// <summary>
/// The controller address and credential a command uses, and where they came from, with how the controller's
/// certificate is checked: a pin, a private CA, or neither (the system's trust store). The environment wins over the
/// credentials file, and <c>--controller</c> wins over both for the address.
/// </summary>
internal sealed record RoofCliConnection(
    Uri Controller,
    RoofCredential? Credential,
    string? CertificateSha256,
    X509Certificate2? CaCertificate,
    string Source);

/// <summary>There is no controller address, so no command can reach the controller.</summary>
internal sealed class RoofCliNotConfiguredException(string message) : Exception(message);

/// <summary>A value on the command line or typed at a prompt was not usable; the command sent nothing.</summary>
internal sealed class RoofCliUsageException(string message) : Exception(message);

/// <summary>
/// The command cannot be done, for a reason the command line words itself: <see cref="Code"/> is the exit code, as the
/// controller's own refusal would give it.
/// </summary>
internal sealed class RoofCliRefusedException(string message, RoofExitCode code = RoofExitCode.Refused, Exception? innerException = null)
    : Exception(message, innerException)
{
    public RoofExitCode Code { get; } = code;
}

/// <summary>One run of a command: its global options, its output, and the connection to the controller.</summary>
internal sealed class RoofCliContext
{
    public RoofCliContext(RoofCliHost host, bool json, Uri? controller, string? credentialsFile)
    {
        Host = host;
        Json = json;
        ControllerOverride = controller;
        CredentialsPath = string.IsNullOrWhiteSpace(credentialsFile)
            ? RoofCredentialStore.GetDefaultPath(host.GetEnvironmentVariable)
            : Path.GetFullPath(credentialsFile);
    }

    public RoofCliHost Host { get; }

    /// <summary>True with <c>--json</c>: results are written as JSON to standard output.</summary>
    public bool Json { get; }

    public Uri? ControllerOverride { get; }

    public string CredentialsPath { get; }

    public TextWriter Out => Host.Out;

    /// <summary>The environment's credentials, then the file's; throws when the file cannot be used.</summary>
    public RoofCliConnection ResolveConnection()
    {
        var environment = RoofCredentialStore.FromEnvironment(Host.GetEnvironmentVariable);
        var file = RoofCredentialStore.Load(CredentialsPath);
        var controller = ControllerOverride ?? environment?.Controller ?? file?.Controller
            ?? throw new RoofCliNotConfiguredException(
                $"No controller address. Run 'hvo-roof setup', set {RoofCredentialStore.ControllerVariable}, or pass --controller.");

        var fromEnvironment = environment?.ToCredential();
        var credential = fromEnvironment ?? file?.ToCredential();
        var source = fromEnvironment is not null ? "environment" : credential is not null ? CredentialsPath : "none";

        // The pin and the CA go together: when the environment sets either, the file's are not used, so a pin in one
        // and a CA in the other are never both in force.
        var trust = environment is { CertificateSha256: not null } or { CaCertificate: not null } ? environment : file;
        var certificate = trust?.CertificateSha256;

        // Checked here, so a pin or a CA that cannot be used is a configuration problem (exit 3), not a controller
        // failure. The environment's were checked as they were read.
        if (certificate is not null && !RoofCertificatePin.IsValid(certificate))
        {
            throw new RoofCredentialFileException(
                $"The certificate pin in {CredentialsPath} is not a SHA-256 pin: 64 hex digits. Save it again with '{RoofCli.CommandName} setup'.");
        }

        if (certificate is not null && trust?.CaCertificate is not null)
        {
            throw new RoofCredentialFileException(
                $"{CredentialsPath} has both a certificate pin and a CA certificate. Save one with '{RoofCli.CommandName} setup'.");
        }

        return new RoofCliConnection(controller, credential, certificate, ReadCaCertificate(trust?.CaCertificate), source);
    }

    /// <summary>The CA certificate saved as PEM, or null; throws when it cannot be used.</summary>
    internal X509Certificate2? ReadCaCertificate(string? pem)
    {
        try
        {
            return pem is null ? null : RoofCertificateAuthority.FromPem(pem);
        }
        catch (ArgumentException ex)
        {
            throw new RoofCredentialFileException(
                $"The CA certificate in {CredentialsPath} is not a CA's certificate in PEM form. Save it again with '{RoofCli.CommandName} setup --ca-certificate FILE'.",
                ex);
        }
    }

    public RoofControllerClient CreateClient(RoofCliConnection connection) => new(new RoofConnectionOptions
    {
        BaseAddress = connection.Controller,
        Credential = connection.Credential,
        ServerCertificateSha256 = connection.CertificateSha256,
        ServerCaCertificate = connection.CaCertificate,
        CreateHandler = Host.CreateHandler,
        WebSocketFactory = Host.WebSocketFactory,
        StatusFeed = StatusFeed,
        TimeProvider = Host.Time
    });

    /// <summary>The status feed's timings: how soon a status that has stopped arriving is stale, among others.</summary>
    public RoofStatusFeedOptions StatusFeed => Host.StatusFeed ?? new RoofStatusFeedOptions();

    /// <summary>Resolves the connection and creates a client for it.</summary>
    public RoofControllerClient Connect() => CreateClient(ResolveConnection());

    /// <summary>Writes <paramref name="value"/> as indented JSON on one document.</summary>
    public void WriteJson<T>(T value) => Out.WriteLine(JsonSerializer.Serialize(value, RoofCliJson.Indented));

    /// <summary>Writes <paramref name="value"/> as one line of JSON (for <c>--watch</c>).</summary>
    public void WriteJsonLine<T>(T value) => Out.WriteLine(JsonSerializer.Serialize(value, RoofCliJson.Compact));

    /// <summary>
    /// Asks for a secret (a password, a PIN or a key): never from the command line. With input redirected, reads one line
    /// of it. Throws when there is no input.
    /// </summary>
    public string ReadSecret(string prompt)
        => Host.ReadLine(prompt, true) is { Length: > 0 } secret
            ? secret
            : throw new RoofCliUsageException($"No value was given for: {prompt.TrimEnd(' ', ':')}.");

    /// <summary>Asks for a secret twice and checks that both match (interactive only).</summary>
    public string ReadNewSecret(string prompt)
    {
        var secret = ReadSecret(prompt);
        if (Host.IsInteractive && ReadSecret("Again: ") != secret)
        {
            throw new RoofCliUsageException("The two entries do not match.");
        }

        return secret;
    }

    /// <summary>Reports <paramref name="error"/> and returns the exit code for it.</summary>
    public int Fail(Exception error)
    {
        var (code, message) = Classify(error);
        if (Json)
        {
            var refusal = error as RoofApiException;
            WriteJson(new
            {
                error = new
                {
                    exitCode = (int)code,
                    kind = code.ToString(),
                    message,
                    status = refusal is null ? (int?)null : (int)refusal.StatusCode,
                    code = refusal?.CodeText,
                    detail = refusal?.Detail,
                    retryAfterSeconds = refusal?.RetryAfter?.TotalSeconds,
                    errors = refusal?.Errors.Count > 0 ? refusal.Errors : null
                }
            });
        }
        else
        {
            Host.Error.WriteLine(message);
            if (error is RoofApiException { Detail: { Length: > 0 } detail } && detail != message)
            {
                Host.Error.WriteLine(detail);
            }

            if (error is RoofApiException { Errors.Count: > 0 } invalid)
            {
                foreach (var (field, problems) in invalid.Errors)
                {
                    foreach (var problem in problems)
                    {
                        Host.Error.WriteLine(field.Length > 0 ? $"  {field}: {problem}" : $"  {problem}");
                    }
                }
            }

            if (error is RoofApiException { RetryAfter: { } wait })
            {
                Host.Error.WriteLine($"Try again in {Math.Ceiling(wait.TotalSeconds)} s.");
            }
        }

        return (int)code;
    }

    internal static (RoofExitCode Code, string Message) Classify(Exception error) => error switch
    {
        RoofCliUsageException usage => (RoofExitCode.Usage, usage.Message),
        RoofCliRefusedException refused => (refused.Code, refused.Message),
        RoofCliNotConfiguredException missing => (RoofExitCode.NotConfigured, missing.Message),
        RoofCredentialFileException file => (RoofExitCode.NotConfigured, file.Message),
        RoofApiException { StatusCode: HttpStatusCode.Unauthorized } refusal => (RoofExitCode.SignedOut, refusal.Message),
        RoofApiException { StatusCode: HttpStatusCode.Forbidden } refusal => (RoofExitCode.Forbidden, refusal.Message),
        RoofApiException { StatusCode: HttpStatusCode.ServiceUnavailable } refusal => (RoofExitCode.Refused, refusal.Message),
        RoofApiException { StatusCode: >= HttpStatusCode.InternalServerError } refusal => (RoofExitCode.Failed, refusal.Message),
        RoofApiException refusal => (RoofExitCode.Refused, refusal.Message),
        RoofProtocolException => (RoofExitCode.Failed, RoofText.AnswerUnreadable),
        HttpRequestException or TimeoutException or TaskCanceledException { InnerException: TimeoutException }
            => (RoofExitCode.Unreachable, RoofText.DescribeFailure(error)),
        _ => (RoofExitCode.Failed, RoofText.DescribeFailure(error))
    };
}

/// <summary>JSON written by <c>--json</c>: the controller's own shapes (camelCase, enums as names).</summary>
internal static class RoofCliJson
{
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = RoofClientJson.Create();
        options.WriteIndented = indented;
        options.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
        return options;
    }
}
