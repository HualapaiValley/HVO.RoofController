using System.Text;
using System.Text.Json;

namespace HVO.RoofControllerV4.Client;

/// <summary>A saved session: the bearer token and what the controller said about it.</summary>
public sealed record RoofStoredSession(string Token, string? Name, string? Role, string? SessionId, DateTimeOffset? ExpiresUtc)
{
    // The token is left out, so the record can be logged.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Role = {Role}, SessionId = {SessionId}, ExpiresUtc = {ExpiresUtc:O}");
        return true;
    }
}

/// <summary>What a command-line client keeps between runs. Secrets: saved only by <see cref="RoofCredentialStore"/>.</summary>
public sealed record RoofStoredCredentials
{
    /// <summary>The controller's base address.</summary>
    public Uri? Controller { get; init; }

    public string? ApiKey { get; init; }

    public string? OnBehalfOf { get; init; }

    public RoofStoredSession? Session { get; init; }

    /// <summary>The pinned SHA-256 of a controller's self-signed certificate (see <see cref="RoofConnectionOptions.ServerCertificateSha256"/>).</summary>
    public string? CertificateSha256 { get; init; }

    // The API key is left out, so the record can be logged.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Controller = {Controller}, ApiKey = {(ApiKey is null ? "(none)" : "(set)")}, OnBehalfOf = {OnBehalfOf}, ")
            .Append($"Session = {Session}, CertificateSha256 = {CertificateSha256}");
        return true;
    }

    /// <summary>The credential to use: a saved session first, else the API key; null when neither is saved.</summary>
    /// <exception cref="RoofCredentialFileException">The key, the token or the on-behalf-of name cannot be sent.</exception>
    public RoofCredential? ToCredential()
    {
        try
        {
            if (Session is { } session && !string.IsNullOrWhiteSpace(session.Token))
            {
                return new RoofSessionCredential(session.Token, session.Name, session.Role, session.SessionId, session.ExpiresUtc);
            }

            return string.IsNullOrWhiteSpace(ApiKey) ? null : new RoofApiKeyCredential(ApiKey, OnBehalfOf);
        }
        catch (ArgumentException ex)
        {
            throw new RoofCredentialFileException(
                ex.ParamName == "onBehalfOf" ? RoofApiKeyCredential.InvalidOnBehalfOf : RoofCredential.InvalidHeaderValue, ex);
        }
    }
}

/// <summary>The credentials file could not be used: it is readable by other users, or it is not valid.</summary>
public sealed class RoofCredentialFileException : Exception
{
    public RoofCredentialFileException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Credentials for command-line use: from the environment (<c>HVO_ROOF_API_KEY</c> or <c>HVO_ROOF_SESSION</c>), or from
/// a JSON file that only its owner may read (<c>0600</c>, in a <c>0700</c> directory). A file that other users can read,
/// or one in a directory they can change, is refused rather than used, because it holds a key or a session token.
/// </summary>
public static class RoofCredentialStore
{
    public const string ControllerVariable = "HVO_ROOF_URL";
    public const string ApiKeyVariable = "HVO_ROOF_API_KEY";
    public const string OnBehalfOfVariable = "HVO_ROOF_ON_BEHALF_OF";
    public const string SessionVariable = "HVO_ROOF_SESSION";
    public const string CertificateVariable = "HVO_ROOF_CERT_SHA256";

    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OthersWriteMask = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private const UnixFileMode OthersMask = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// <c>$XDG_CONFIG_HOME/hvo-roof/credentials.json</c>, or <c>~/.config/hvo-roof/credentials.json</c>.
    /// </summary>
    public static string GetDefaultPath(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var configHome = getEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome) || !Path.IsPathRooted(configHome))
        {
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configHome, "hvo-roof", "credentials.json");
    }

    /// <summary>Credentials from the environment, or null when none of the variables is set.</summary>
    /// <exception cref="RoofCredentialFileException">The controller address or the certificate pin is not valid.</exception>
    public static RoofStoredCredentials? FromEnvironment(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var controller = getEnvironmentVariable(ControllerVariable);
        var apiKey = getEnvironmentVariable(ApiKeyVariable);
        var session = getEnvironmentVariable(SessionVariable);
        var certificate = getEnvironmentVariable(CertificateVariable);
        if (string.IsNullOrWhiteSpace(controller)
            && string.IsNullOrWhiteSpace(apiKey)
            && string.IsNullOrWhiteSpace(session)
            && string.IsNullOrWhiteSpace(certificate))
        {
            return null;
        }

        Uri? address = null;
        if (!string.IsNullOrWhiteSpace(controller)
            && (!Uri.TryCreate(controller.Trim(), UriKind.Absolute, out address) || address.Scheme is not ("http" or "https")))
        {
            throw new RoofCredentialFileException($"{ControllerVariable} is not an absolute http or https URL.");
        }

        if (!string.IsNullOrWhiteSpace(certificate) && !RoofCertificatePin.IsValid(certificate))
        {
            throw new RoofCredentialFileException($"{CertificateVariable} is not a SHA-256 pin: 64 hex digits.");
        }

        return new RoofStoredCredentials
        {
            Controller = address,
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
            OnBehalfOf = getEnvironmentVariable(OnBehalfOfVariable),
            Session = string.IsNullOrWhiteSpace(session) ? null : new RoofStoredSession(session.Trim(), null, null, null, null),
            CertificateSha256 = string.IsNullOrWhiteSpace(certificate) ? null : certificate.Trim()
        };
    }

    /// <summary>Reads the file, or returns null when it does not exist.</summary>
    /// <exception cref="RoofCredentialFileException">
    /// Other users can read or write the file, or change its directory; or it is not valid JSON.
    /// </exception>
    public static RoofStoredCredentials? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            CheckDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var mode = File.GetUnixFileMode(path);
            if ((mode & OthersMask) != 0)
            {
                throw new RoofCredentialFileException(
                    $"{path} can be read or changed by other users. Run 'chmod 600 {path}', then try again.");
            }
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<RoofStoredCredentials>(stream, RoofClientJson.Options);
        }
        catch (JsonException ex)
        {
            throw new RoofCredentialFileException($"{path} is not a valid credentials file.", ex);
        }
    }

    /// <summary>
    /// Writes the file atomically with mode <c>0600</c>, creating its directory with mode <c>0700</c> when missing.
    /// </summary>
    /// <exception cref="RoofCredentialFileException">Other users can change the directory.</exception>
    public static void Save(string path, RoofStoredCredentials credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(credentials);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (Directory.Exists(directory))
        {
            CheckDirectory(directory);
        }
        else
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, OwnerDirectoryMode);
            }
        }

        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerFileMode;
            }

            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, credentials, RoofClientJson.Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // Another user who can change the directory can replace the file, whatever the file's own mode.
    private static void CheckDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(directory) & OthersWriteMask) != 0)
        {
            throw new RoofCredentialFileException(
                $"{directory} can be changed by other users, who could replace the credentials file. Run 'chmod 700 {directory}', then try again.");
        }
    }

    /// <summary>Deletes the file. Returns false when there was none.</summary>
    public static bool Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
