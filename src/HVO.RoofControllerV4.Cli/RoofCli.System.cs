using System.CommandLine;
using System.Security.Cryptography.X509Certificates;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Cli;

public static partial class RoofCli
{
    internal sealed partial class CommandBuilder
    {
        private Command CreateInfoCommand()
        {
            var command = new Command("info", "Show hvo-roof's own version, then the controller's version, host and resource use (admin).");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var information = await client.System.GetInformationAsync(cancellationToken).ConfigureAwait(false);
                var metrics = await client.System.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    // hvo-roof's full version (with its commit), as the controller's applicationVersion is given.
                    var self = new { name = CommandName, version = RoofProductVersion.Of(typeof(RoofCli).Assembly) };
                    context.WriteJson(new { client = self, information, metrics });
                }
                else
                {
                    RoofCliFormat.WriteRows(
                        context.Out,
                        RoofSystemText.DescribeInformation(information, metrics).Prepend(RoofSystemText.DescribeClient(CommandName, typeof(RoofCli).Assembly)));
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private Command CreateRestartCommand()
        {
            var force = ForceOption("Restart without asking.");
            var confirm = ConfirmSafetyCriticalOption();
            var command = new Command(
                "restart",
                "Restart the controller (admin), so settings read at startup take effect. It stops the roof and verifies the stop first; it refuses when the stop cannot be verified.")
            {
                force,
                confirm
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                if (!parseResult.GetValue(force)
                    && !Confirm(context, RoofSystemText.RestartQuestion))
                {
                    return (int)RoofExitCode.ConfirmationRequired;
                }

                using var client = context.Connect();
                var confirmed = parseResult.GetValue(confirm);
                if (!confirmed && await PendingSafetyCriticalEditAsync(client, cancellationToken).ConfigureAwait(false) is { } pending)
                {
                    if (context.Json)
                    {
                        context.WriteJson(new { sent = false, confirmationRequired = true, handEdit = pending.Edit });
                    }
                    else
                    {
                        WriteHandEdit(context, pending.Form, pending.Edit, "restart");
                        context.Host.Error.WriteLine(
                            "Not restarted: the restart would load a safety-critical hand edit. Review it, then run the command again with --confirm-safety-critical.");
                    }

                    return (int)RoofExitCode.ConfirmationRequired;
                }

                var restart = await client.System.RestartAsync(confirmed, cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(restart);
                }
                else
                {
                    context.Out.WriteLine(restart.Message);
                    context.Out.WriteLine($"'{CommandName} health --probe ready' says when it is back.");
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        /// <summary>
        /// The pending hand edit a restart would load, when it is safety-critical (only admins are shown one). The
        /// controller refuses such a restart anyway; this shows the edit first. When the settings cannot be read, the
        /// restart is left to the controller.
        /// </summary>
        private static async Task<(RoofSettingsForm Form, RoofSettingsHandEdit Edit)?> PendingSafetyCriticalEditAsync(
            RoofControllerClient client,
            CancellationToken cancellationToken)
        {
            try
            {
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                return form.PendingHandEdit is { RequiresConfirmation: true } pending ? (form, pending) : null;
            }
            catch (RoofApiException)
            {
                return null;
            }
        }

        private Command CreateSetupCommand()
        {
            var pin = new Option<string?>("--certificate-sha256")
            {
                Description = "The SHA-256 of the controller's self-signed certificate, 64 hex digits (colons allowed); 'none' removes a saved one. Replaces a saved CA certificate."
            };
            var authority = new Option<string?>("--ca-certificate")
            {
                Description = "A file holding the private CA that issued the controller's certificate, such as the installer's (PEM or DER): only that CA is trusted, and the certificate can be reissued under it with no change here. 'none' removes a saved one. Replaces a saved pin.",
                HelpName = "file"
            };
            var authoritySha = new Option<string?>("--ca-sha256")
            {
                Description = "Fetch the private CA from the controller and save it when its SHA-256 is this one, as the installer's Done page and 'hvo-roof-install cert show' give it (64 hex digits, colons allowed). Instead of --ca-certificate; replaces a saved pin.",
                HelpName = "fingerprint"
            };
            var apiKey = new Option<bool>("--api-key")
            {
                Description = "Save an API key, read from the terminal without echo or as one line of standard input."
            };
            var createAdmin = new Option<string?>("--create-admin")
            {
                Description = "With an admin API key: add this person as an admin, with a password (asked for)."
            };
            var command = new Command(
                "setup",
                "Save the controller's address, an API key and how its certificate is checked (a pin or a CA) in the credentials file, check the connection, and add the first admin person. Asks for what the options leave out when run in a terminal.")
            {
                pin,
                authority,
                authoritySha,
                apiKey,
                createAdmin
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                // A broken HVO_ROOF_* variable would override what setup saves: it is refused (exit 3) before anything
                // is asked or written.
                _ = RoofCredentialStore.FromEnvironment(context.Host.GetEnvironmentVariable);
                var setup = new RoofCliSetup(context);
                var adminName = parseResult.GetValue(createAdmin) is { } requested ? RequireName(requested) : null;
                var saved = await setup.PromptAsync(
                    parseResult.GetValue(pin), parseResult.GetValue(authority), parseResult.GetValue(authoritySha), parseResult.GetValue(apiKey), cancellationToken).ConfigureAwait(false);
                WriteSetupLines(context, saved.Notes);
                var check = await setup.CheckAsync(saved.Connection, cancellationToken).ConfigureAwait(false);
                WriteSetupLines(context, check.Lines);

                RoofUserResponse? created = null;
                adminName ??= check.ExitCode == RoofExitCode.Success ? setup.OfferFirstAdmin(check) : null;
                if (adminName is not null && check.ExitCode == RoofExitCode.Success)
                {
                    if (!check.IsAdminKey)
                    {
                        throw new RoofCliUsageException(
                            "Adding a person needs an admin API key, and the saved credential is not one. Save one with --api-key.");
                    }

                    var password = context.ReadNewSecret($"Password for {adminName}: ");
                    created = await setup.CreateAdminAsync(saved.Connection, adminName, password, cancellationToken).ConfigureAwait(false);
                }

                if (context.Json)
                {
                    context.WriteJson(new
                    {
                        controller = saved.Connection.Controller,
                        credentialsFile = context.CredentialsPath,
                        certificatePinned = saved.Connection.CertificateSha256 is not null,
                        caCertificate = saved.Connection.CaCertificate is { } trusted ? RoofCertificateAuthority.Describe(trusted) : null,
                        caCertificateSha256 = saved.Connection.CaCertificate is { } fingerprinted ? RoofCertificateAuthority.Fingerprint(fingerprinted) : null,
                        credential = saved.Connection.Credential?.ToString(),
                        reachable = check.Reachable,
                        live = check.Live,
                        caller = check.Caller,
                        error = check.Error is null ? null : RoofCliContext.Classify(check.Error).Message,
                        exitCode = (int)check.ExitCode,
                        createdAdmin = created?.Name
                    });
                }
                else if (created is not null)
                {
                    context.Out.WriteLine($"Added {created.Name} (admin). Sign in with '{CommandName} login {created.Name}'.");
                }

                WarnIfEnvironmentWins(context);
                return (int)check.ExitCode;
            });
            return command;
        }

        private static void WriteSetupLines(RoofCliContext context, IEnumerable<string> lines)
        {
            if (context.Json)
            {
                return;
            }

            foreach (var line in lines)
            {
                context.Out.WriteLine(line);
            }
        }
    }
}

/// <summary>The credentials file after setup wrote it, and what to tell the person about it.</summary>
internal sealed record RoofCliSetupResult(RoofCliConnection Connection, IReadOnlyList<string> Notes);

/// <summary>
/// What <c>hvo-roof setup</c> and the terminal interface's Setup page share: saving the address, key, and pin or CA,
/// checking the connection, and adding the first admin person.
/// </summary>
internal sealed class RoofCliSetup(RoofCliContext context)
{
    /// <summary>The outcome of a connection check. <see cref="ExitCode"/> is what the command exits with.</summary>
    public sealed record Check(
        bool Reachable,
        string? Live,
        RoofCallerResponse? Caller,
        Exception? Error,
        RoofExitCode ExitCode,
        bool HasPeople,
        IReadOnlyList<string> Lines)
    {
        /// <summary>The controller accepted the saved credential as an admin API key, which may add people.</summary>
        public bool IsAdminKey => Caller is { Kind: RoofCredentialKind.ApiKey, Role: RoofControllerApiContract.AdminRole };

        /// <summary>An admin key, and nobody can sign in yet: the first admin person is still to be added.</summary>
        public bool NeedsFirstAdmin => IsAdminKey && !HasPeople;
    }

    /// <summary>What the credentials file holds now (nothing when there is no file).</summary>
    public RoofStoredCredentials Load() => RoofCredentialStore.Load(context.CredentialsPath) ?? new RoofStoredCredentials();

    /// <summary>How long fetching the CA may take.</summary>
    internal static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The command line's setup: asks for what the options leave out (in a terminal), then writes the credentials file.
    /// <paramref name="authorityShaOption"/> fetches the CA from the controller and saves it when its SHA-256 is that one;
    /// in a terminal, 'fetch' at the CA prompt fetches it and asks the person to compare its SHA-256.
    /// </summary>
    public async Task<RoofCliSetupResult> PromptAsync(
        string? pinOption, string? authorityOption, string? authorityShaOption, bool readApiKey, CancellationToken cancellationToken)
    {
        var stored = Load();
        var interactive = context.Host.IsInteractive && !context.Json;

        var controller = context.ControllerOverride;
        if (controller is null && interactive)
        {
            var current = stored.Controller?.ToString();
            var text = context.Host.ReadLine($"Controller address{(current is null ? " (for example https://roof.local:5001/)" : $" [{current}]")}: ", false)?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                controller = ParseController(text);
            }
        }

        controller ??= stored.Controller ?? throw new RoofCliNotConfiguredException(
            $"No controller address. Give it with --controller, for example '{RoofCli.CommandName} setup --controller https://roof.local:5001/'.");

        // A pin and a CA are never both saved: the one given replaces the other.
        var certificate = stored.CertificateSha256;
        var authority = stored.CaCertificate;
        var notes = new List<string>();
        if (authorityShaOption is not null)
        {
            if (pinOption is not null || authorityOption is not null)
            {
                throw new RoofCliUsageException(FetchAlone);
            }

            var expected = ParseFingerprint(authorityShaOption);
            using var fetched = await FetchCaAsync(controller, cancellationToken).ConfigureAwait(false);
            if (!RoofCertificateAuthority.HasFingerprint(fetched, expected))
            {
                throw new RoofCliRefusedException(
                    $"The CA the controller serves ({RoofCertificateAuthority.Describe(fetched)}, SHA-256 {RoofCertificateAuthority.Fingerprint(fetched)}) is not the one given, so it is not saved. "
                    + "Compare it with 'hvo-roof-install cert show' on the controller.");
            }

            (authority, certificate) = (RoofCertificateAuthority.ToPem(fetched), null);
            notes.Add($"Fetched the CA {RoofCertificateAuthority.Describe(fetched)} from the controller: its SHA-256 is the one given.");
        }
        else if (pinOption is not null || authorityOption is not null)
        {
            var givenPin = pinOption is null ? null : ParsePin(pinOption);
            var givenAuthority = authorityOption is null ? null : ParseCaCertificate(authorityOption);
            if (givenPin is not null && givenAuthority is not null)
            {
                throw new RoofCliUsageException(BothRefused);
            }

            certificate = pinOption is not null ? givenPin : (givenAuthority is null ? certificate : null);
            authority = authorityOption is not null ? givenAuthority : (givenPin is null ? authority : null);
        }
        else if (interactive && controller.Scheme == Uri.UriSchemeHttps)
        {
            var text = context.Host.ReadLine(
                $"CA certificate file, or 'fetch' to get it from the controller, when a private CA such as the installer's issued the controller's certificate (Enter keeps {(authority is null ? "none" : "the saved one")}; 'none' removes it): ",
                false)?.Trim();
            if (string.Equals(text, "fetch", StringComparison.OrdinalIgnoreCase))
            {
                authority = await ConfirmFetchedCaAsync(controller, cancellationToken).ConfigureAwait(false);
                certificate = null;
            }
            else if (!string.IsNullOrEmpty(text))
            {
                authority = ParseCaCertificate(text);
                certificate = authority is null ? certificate : null;
            }

            if (authority is null)
            {
                text = context.Host.ReadLine(
                    $"Certificate SHA-256, for a self-signed certificate (Enter keeps {(certificate is null ? "none" : "the saved one")}; 'none' removes it): ",
                    false)?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    certificate = ParsePin(text);
                }
            }
        }

        string? apiKey = null;
        if (readApiKey)
        {
            apiKey = context.ReadSecret("API key: ");
        }
        else if (interactive)
        {
            apiKey = context.Host.ReadLine(
                stored.ApiKey is null && stored.Session is null
                    ? $"API key (Enter to skip, and sign in later with '{RoofCli.CommandName} login NAME'): "
                    : "API key (Enter keeps the saved credential): ",
                true)?.Trim();
        }

        var result = Save(controller, certificate, authority, string.IsNullOrEmpty(apiKey) ? null : apiKey);
        return result with { Notes = [.. notes, .. result.Notes] };
    }

    /// <summary>
    /// Fetches the CA from the controller, shows its SHA-256, and returns it as PEM once the person says it is the one
    /// the controller's installer showed.
    /// </summary>
    private async Task<string> ConfirmFetchedCaAsync(Uri controller, CancellationToken cancellationToken)
    {
        using var fetched = await FetchCaAsync(controller, cancellationToken).ConfigureAwait(false);
        context.Out.WriteLine($"The controller serves the CA {RoofCertificateAuthority.Describe(fetched)}, SHA-256:");
        context.Out.WriteLine($"  {RoofCertificateAuthority.Fingerprint(fetched)}");
        var answer = context.Host.ReadLine(
            "Is that the SHA-256 on the installer's Done page, or from 'hvo-roof-install cert show' on the controller? [y/N] ", false)?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
            ? RoofCertificateAuthority.ToPem(fetched)
            : throw new RoofCliRefusedException(
                "The CA was not saved: its SHA-256 was not confirmed. If it differs from the controller's, something between here and the controller may be answering for it.");
    }

    /// <summary>The CA the controller serves, with its refusals as the command line's.</summary>
    private static async Task<X509Certificate2> FetchCaAsync(Uri controller, CancellationToken cancellationToken)
    {
        if (controller.Scheme != Uri.UriSchemeHttps)
        {
            throw new RoofCliUsageException($"The CA is fetched over HTTPS, and {controller} is not an https address.");
        }

        try
        {
            return await RoofCertificateAuthority.FetchAsync(controller, FetchTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (RoofCaFetchException ex)
        {
            throw new RoofCliRefusedException(ex.Message, RoofExitCode.Refused, ex);
        }
        catch (RoofCertificateRefusedException ex)
        {
            throw new RoofCliRefusedException(ex.Message, RoofExitCode.Refused, ex);
        }
    }

    /// <summary>
    /// Writes the credentials file: the address, the certificate pin or the CA certificate as PEM (null removes each;
    /// not both) and, when <paramref name="apiKey"/> is given, a new API key. A saved session is kept.
    /// </summary>
    public RoofCliSetupResult Save(Uri controller, string? certificateSha256, string? caCertificate, string? apiKey)
    {
        if (certificateSha256 is not null && caCertificate is not null)
        {
            throw new RoofCliUsageException(BothRefused);
        }

        if (apiKey is not null)
        {
            try
            {
                _ = new RoofApiKeyCredential(apiKey);
            }
            catch (ArgumentException)
            {
                throw new RoofCliUsageException(RoofCredential.InvalidHeaderValue);
            }
        }

        var authority = context.ReadCaCertificate(caCertificate);
        var stored = Load();
        var saved = stored with
        {
            Controller = controller,
            CertificateSha256 = certificateSha256,
            CaCertificate = caCertificate,
            ApiKey = apiKey ?? stored.ApiKey
        };
        RoofCredentialStore.Save(context.CredentialsPath, saved);

        var trust = certificateSha256 is not null ? ", certificate pinned"
            : authority is not null ? $", CA {RoofCertificateAuthority.Describe(authority)} trusted"
            : string.Empty;
        var notes = new List<string>
        {
            $"Saved in {context.CredentialsPath}: {controller}{trust}{(saved.ApiKey is null ? string.Empty : ", API key")}."
        };
        if (authority is not null && stored.CertificateSha256 is not null)
        {
            notes.Add("The saved certificate pin was removed: the CA is trusted instead.");
        }
        else if (certificateSha256 is not null && stored.CaCertificate is not null)
        {
            notes.Add("The saved CA certificate was removed: the certificate is pinned instead.");
        }

        if (apiKey is not null && stored.Session is { } session)
        {
            notes.Add($"The saved session{(session.Name is null ? string.Empty : $" for {session.Name}")} is used before the key; sign out to use the key.");
        }

        var credential = saved.ToCredential();
        return new RoofCliSetupResult(
            new RoofCliConnection(controller, credential, certificateSha256, authority, credential is null ? "none" : context.CredentialsPath),
            notes);
    }

    /// <summary>
    /// Checks that the controller answers (the anonymous liveness probe), then who it says the credential is, and
    /// whether any people exist yet (for an admin key).
    /// </summary>
    public async Task<Check> CheckAsync(RoofCliConnection connection, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        using var client = context.CreateClient(connection);
        string live;
        try
        {
            live = (await client.Health.GetLivenessAsync(cancellationToken).ConfigureAwait(false)).Status;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var (code, message) = RoofCliContext.Classify(error);
            lines.Add($"Not reachable: {message}{PinHint(error)}");
            return new Check(false, null, null, error, code, false, lines);
        }

        lines.Add($"Reachable: the controller says it is {live.ToLowerInvariant()}.");
        if (connection.Credential is null)
        {
            lines.Add($"No credential is saved: sign in with '{RoofCli.CommandName} login NAME', or save an API key with '{RoofCli.CommandName} setup --api-key'.");
            return new Check(true, live, null, null, RoofExitCode.Success, false, lines);
        }

        RoofCallerResponse caller;
        var hasPeople = true;
        try
        {
            caller = await client.Auth.GetCallerAsync(cancellationToken).ConfigureAwait(false);
            if (caller is { Kind: RoofCredentialKind.ApiKey, Role: RoofControllerApiContract.AdminRole })
            {
                hasPeople = (await client.Identity.GetUsersAsync(cancellationToken).ConfigureAwait(false)).Length > 0;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var (code, message) = RoofCliContext.Classify(error);
            lines.Add($"The credential was not accepted: {message}");
            return new Check(true, live, null, error, code, false, lines);
        }

        lines.Add($"Signed in as {caller.Name} ({RoofCliFormat.Role(caller.Role)}, {(caller.Kind == RoofCredentialKind.ApiKey ? "API key" : "session")}).");
        if (!hasPeople)
        {
            lines.Add($"Nobody can sign in yet. Add the first admin person with '{RoofCli.CommandName} setup --create-admin NAME'.");
        }

        return new Check(true, live, caller, null, RoofExitCode.Success, hasPeople, lines);
    }

    /// <summary>In a terminal, with an admin key and no people yet, offers to add the first admin; returns the name.</summary>
    public string? OfferFirstAdmin(Check check)
    {
        if (!check.NeedsFirstAdmin || !context.Host.IsInteractive || context.Json)
        {
            return null;
        }

        var answer = context.Host.ReadLine("Add the first admin person now? [y/N] ", false)?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = context.Host.ReadLine("Name: ", false)?.Trim();
        return RoofIdentityContract.IsValidName(name)
            ? name
            : throw new RoofCliUsageException($"'{name}' is not a valid name: {RoofIdentityText.NameRule}");
    }

    /// <summary>Adds <paramref name="name"/> as an admin person with <paramref name="password"/>.</summary>
    public async Task<RoofUserResponse> CreateAdminAsync(RoofCliConnection connection, string name, string password, CancellationToken cancellationToken)
    {
        if (!RoofIdentityContract.IsValidPassword(password))
        {
            throw new RoofCliUsageException(RoofIdentityText.PasswordRule);
        }

        using var client = context.CreateClient(connection);
        return await client.Identity.AddUserAsync(
            new RoofUserCreateRequest { Name = name, Role = RoofControllerApiContract.AdminRole, Password = password },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs in with a password over <paramref name="connection"/>'s address and pin or CA, and saves the session in
    /// the credentials file beside them.
    /// </summary>
    public async Task<RoofSessionCredential> SignInAsync(RoofCliConnection connection, string name, string password, CancellationToken cancellationToken)
    {
        var stored = RoofCredentialStore.Load(context.CredentialsPath);
        using var client = context.CreateClient(connection with { Credential = null });
        var session = await client.Auth.SignInAsync(name, password, cancellationToken).ConfigureAwait(false);
        RoofCredentialStore.Save(context.CredentialsPath, (stored ?? new RoofStoredCredentials()) with
        {
            Controller = connection.Controller,
            CertificateSha256 = connection.CertificateSha256,
            CaCertificate = connection.CaCertificate is { } authority ? RoofCertificateAuthority.ToPem(authority) : null,
            Session = new RoofStoredSession(session.Token, session.Name, session.Role, session.SessionId, session.ExpiresUtc)
        });
        return session;
    }

    /// <summary>
    /// Signs out the saved session and removes it from the credentials file (a saved API key is kept). Returns what
    /// happened, or null when no session is saved.
    /// </summary>
    public async Task<string?> SignOutAsync(CancellationToken cancellationToken)
    {
        var stored = RoofCredentialStore.Load(context.CredentialsPath);
        if (stored?.Session is null)
        {
            return null;
        }

        var message = "Signed out.";
        try
        {
            var connection = context.ResolveConnection();
            using var client = context.CreateClient(connection with { Credential = stored.ToCredential() });
            await client.Auth.SignOutAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RoofApiException refusal) when (refusal.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            message = "The session had already ended.";
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            message = $"The controller was not told ({RoofText.DescribeFailure(error)}); the session stays valid there until it expires.";
        }

        RoofCredentialStore.Save(context.CredentialsPath, stored with { Session = null });
        return message;
    }

    /// <summary>Says so when a credential in the environment is used instead of the saved one; null otherwise.</summary>
    public static string? EnvironmentNote(RoofCliContext context)
    {
        RoofStoredCredentials? environment;
        try
        {
            environment = RoofCredentialStore.FromEnvironment(context.Host.GetEnvironmentVariable);
        }
        catch (RoofCredentialFileException)
        {
            // Reported by the command that uses the environment; a note never fails the command.
            return null;
        }

        return environment?.ToCredential() is not null
            ? $"Note: {RoofCredentialStore.ApiKeyVariable} or {RoofCredentialStore.SessionVariable} is set, and commands use it instead of the saved credential."
            : null;
    }

    /// <summary>An http or https address, as <c>--controller</c> takes it.</summary>
    internal static Uri ParseController(string text)
        => RoofCli.TryParseControllerAddress(text, out var address, out var error) ? address! : throw new RoofCliUsageException(error!);

    /// <summary>64 hex digits (colons, spaces or dashes allowed between them) as uppercase hex; 'none' is null.</summary>
    internal static string? ParsePin(string text)
    {
        if (string.Equals(text.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // The client library accepts the same forms.
        var hex = new string(text.Where(c => c is not (':' or ' ' or '-')).ToArray());
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigit)
            ? hex.ToUpperInvariant()
            : throw new RoofCliUsageException("The certificate SHA-256 must be 64 hex digits (colons allowed), or 'none'.");
    }

    /// <summary>A CA's SHA-256: 64 hex digits, colons, spaces or dashes allowed between them.</summary>
    internal static string ParseFingerprint(string text)
    {
        var hex = new string(text.Where(c => c is not (':' or ' ' or '-')).ToArray());
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigit)
            ? hex.ToUpperInvariant()
            : throw new RoofCliUsageException("The CA's SHA-256 must be 64 hex digits (colons allowed).");
    }

    /// <summary>
    /// The CA certificate in the file at <paramref name="text"/>, as PEM, checked to be a CA's; 'none' is null.
    /// </summary>
    internal static string? ParseCaCertificate(string text)
    {
        var path = text.Trim();
        if (string.Equals(path, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var authority = RoofCertificateAuthority.Load(path);
            return RoofCertificateAuthority.ToPem(authority);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new RoofCliUsageException($"The CA certificate cannot be used: {ex.Message}");
        }
    }

    private const string FetchAlone
        = "--ca-sha256 fetches the CA to save, so give it without --ca-certificate or --certificate-sha256.";

    private const string BothRefused
        = "Give a certificate SHA-256 or a CA certificate, not both: the pin is for a self-signed certificate, the CA for one it issued.";

    // What to save so the controller's certificate is accepted, when that is what went wrong. A certificate that has
    // expired or does not name the address needs the controller's certificate reissued, which the refusal says.
    private static string PinHint(Exception error)
    {
        switch (RoofCertificateRefusedException.Find(error)?.Reason)
        {
            case RoofCertificateRefusal.NotPinned:
                return " If the controller's certificate was replaced on purpose, save its SHA-256 with --certificate-sha256, or the CA that issued it with --ca-sha256 or --ca-certificate.";
            case RoofCertificateRefusal.OtherAuthority:
                return " If the controller's CA was replaced on purpose, save the new one with --ca-sha256 or --ca-certificate.";
            case null:
                break;
            default:
                return string.Empty;
        }

        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is System.Security.Authentication.AuthenticationException)
            {
                return " The certificate was not accepted: save the CA that issued it with --ca-sha256 or --ca-certificate, or, for a self-signed certificate, its SHA-256 with --certificate-sha256.";
            }
        }

        return string.Empty;
    }
}
