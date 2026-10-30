using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>
/// The connection, as <c>hvo-roof setup</c>, <c>login</c> and <c>logout</c> keep it: the controller's address, the
/// certificate pin or CA, and an API key in the credentials file, a check of the connection, signing in and out, the
/// signed-in person's own display and short name (#209), and adding the first admin person with an admin API key.
/// </summary>
internal sealed class RoofUiSetupPage : RoofUiPage
{
    private readonly RoofCliSetup _setup;
    private readonly TerminalReadingView _lines;
    private RoofCliSetup.Check? _check;
    private RoofCliConnection? _checked;

    public RoofUiSetupPage(RoofTerminalUi ui)
        : base(ui, "Setup")
    {
        _setup = new RoofCliSetup(ui.Context);
        _lines = new TerminalReadingView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true };
        var connectionPanel = new FrameView { Title = "CONNECTION / TRUST", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
        Ui.Theme.SetFrame(connectionPanel);
        _lines.Height = Dim.Fill();
        connectionPanel.Add(_lines);
        Add(connectionPanel);
        var connection = AddButton("Connection", null, EditConnection);
        var check = AddButton("Check", connection, () => CheckConnection());
        var signIn = AddButton("Sign in", check, SignIn);
        var signOut = AddButton("Sign out", signIn, SignOut);
        var names = AddButton("Names", signOut, ChangeNames);
        AddButton("First admin", names, AddFirstAdmin);
    }

    /// <summary>The last check of the connection, while it is still the one in use.</summary>
    public RoofCliSetup.Check? LastCheck => _check;

    public override void Shown() => Show();

    public override void ConnectionChanged()
    {
        // The caller told for the connection it checked keeps the check; a new connection has not been checked.
        if (!ReferenceEquals(_checked, Ui.Connection))
        {
            _check = null;
            _checked = null;
        }

        Show();
    }

    public override string Describe() => string.Join('\n', LinesOf(_lines));

    private void Show()
    {
        var lines = new List<string>();
        var context = Ui.Context;
        if (Ui.Connection is { } connection)
        {
            var from = context.ControllerOverride is not null ? "--controller"
                : Environment(context)?.Controller is not null ? RoofCredentialStore.ControllerVariable
                : "the credentials file";
            lines.Add($"Controller:  {connection.Controller} (from {from})");
            lines.Add($"Certificate: {DescribeTrust(connection)}");
            lines.Add(context.Local ? "Credential:  local Linux authorization, checked for each request; no saved web session."
                : connection.Credential is null
                ? "Credential:  none. Sign in, or save an admin's API key under Connection."
                : $"Credential:  {connection.Credential} (from {(connection.Source == "environment" ? "the environment" : connection.Source)})");
        }
        else
        {
            lines.Add(Ui.ConnectionProblem ?? "No controller is configured.");
            lines.Add("Connection saves the controller's address, and an API key when you have one.");
        }

        lines.Add($"Credentials file: {context.CredentialsPath}");
        if (!context.Local && Environment(context)?.ToCredential() is not null)
        {
            lines.Add($"{RoofCredentialStore.ApiKeyVariable} or {RoofCredentialStore.SessionVariable} is set, and this interface uses it instead of the saved credential.");
        }

        if (_check is { } check)
        {
            lines.Add(string.Empty);
            lines.Add("Check:");
            lines.AddRange(check.Lines.Select(line => "  " + line));
        }

        SetLines(_lines, lines);
    }

    private void EditConnection()
    {
        if (Ui.Context.Local)
        {
            Ui.Say("Local authorization uses the installed connection. Change remote credentials with --remote setup.", error: true);
            return;
        }

        RoofStoredCredentials stored;
        try
        {
            stored = _setup.Load();
        }
        catch (RoofCredentialFileException error)
        {
            Ui.Say(error.Message, error: true);
            return;
        }

        Ui.Ask(new RoofUiPrompt(
            "Connection",
            "The controller's address, for example https://roof.local:5001/. The CA certificate file is for a certificate "
                + $"a private CA such as the installer's issued (PEM or DER; blank keeps {(stored.CaCertificate is null ? "none" : "the saved one")}, "
                + "'none' removes it). The certificate SHA-256 is for a self-signed certificate (64 hex digits; blank or 'none' "
                + "removes it). Give one or the other. A blank API key keeps the saved credential.",
            [
                new RoofUiField("Controller address", Initial: stored.Controller?.ToString() ?? string.Empty),
                new RoofUiField("CA certificate file"),
                new RoofUiField("Certificate SHA-256", Initial: stored.CertificateSha256 ?? string.Empty),
                new RoofUiField("API key", Secret: true)
            ],
            [new RoofUiAction("Save and check", values =>
            {
                var controller = RoofCliSetup.ParseController(values[0].Trim());
                var certificate = string.IsNullOrWhiteSpace(values[2]) ? null : RoofCliSetup.ParsePin(values[2]);
                var authority = string.IsNullOrWhiteSpace(values[1])
                    ? certificate is null ? stored.CaCertificate : null
                    : RoofCliSetup.ParseCaCertificate(values[1]);

                // A new CA replaces the saved pin, as 'setup --ca-certificate' does, while its field still shows that pin.
                if (authority is not null && certificate is not null && string.Equals(certificate, stored.CertificateSha256, StringComparison.OrdinalIgnoreCase))
                {
                    certificate = null;
                }

                var apiKey = values[3].Trim();
                RoofCliSetupResult saved;
                try
                {
                    saved = _setup.Save(controller, certificate, authority, apiKey.Length == 0 ? null : apiKey);
                }
                catch (Exception error) when (error is RoofCredentialFileException or IOException or UnauthorizedAccessException)
                {
                    return error.Message;
                }

                Ui.Reconnect();
                CheckConnection(string.Join(' ', saved.Notes));
                return null;
            })]));
    }

    /// <summary>Checks the connection; <paramref name="done"/>, when given, is said before the verdict.</summary>
    private void CheckConnection(string? done = null)
    {
        if (Ui.Connection is not { } connection)
        {
            Ui.Say(Ui.ConnectionProblem ?? "No controller is configured.", error: true);
            return;
        }

        _ = Ui.Run(Join(done, "Checking the connection…"), async (_, cancellationToken) =>
        {
            var check = await _setup.CheckAsync(connection, cancellationToken).ConfigureAwait(false);
            Ui.Post(() =>
            {
                if (!ReferenceEquals(connection, Ui.Connection))
                {
                    return;
                }

                _check = check;
                _checked = connection;
                Show();
                Ui.Say(
                    Join(
                        done,
                        check.NeedsFirstAdmin ? "Connected with an admin API key, and nobody can sign in yet: add the first admin person."
                            : check.ExitCode == RoofExitCode.Success ? "The connection works."
                            : "The connection does not work yet: see the check."),
                    error: check.ExitCode != RoofExitCode.Success);
            });
        });
    }

    internal void SignIn()
    {
        if (Ui.Context.Local)
        {
            Ui.Say("Local Linux authorization is checked for each request. Use --remote login for a remote password session.");
            return;
        }

        _ = Ui.Run("Checking account types…", async (client, cancellationToken) =>
        {
            var enabled = false;
            try
            {
                enabled = (await client.Auth.GetProvidersAsync(cancellationToken).ConfigureAwait(false)).LinuxEnabled;
            }
            catch (Exception error) when (error is RoofApiException or RoofProtocolException or HttpRequestException or TimeoutException)
            {
                // Older controllers keep their original form.
            }

            Ui.Post(() => ShowSignIn(enabled));
        });
    }

    private void ShowSignIn(bool providersEnabled, string? retryName = null, string? retryProvider = null, string? failure = null)
    {
        if (Ui.Connection is not { } connection)
        {
            Ui.Say("Save the controller's address under Connection first.", error: true);
            return;
        }

        Ui.Ask(new RoofUiPrompt(
            "Sign in",
            $"{failure} Sign in to {connection.Controller} with a password. The session is saved in {Ui.Context.CredentialsPath}. Cancel leaves credentials unchanged.",
            [
                new RoofUiField("Name", Initial: retryName ?? (Ui.Caller?.Kind == RoofCredentialKind.Session ? Ui.Caller.Name : Ui.Context.Host.GetUserName())),
                new RoofUiField("Password", Secret: true),
                .. providersEnabled ? new[] { new RoofUiField("Account type (virtual or linux)", Initial: retryProvider ?? "virtual") } : []
            ],
            [new RoofUiAction("Sign in", values =>
            {
                var provider = providersEnabled ? values[2].Trim() : null;
                if (provider is not (null or "virtual" or "linux"))
                {
                    return "Account type must be virtual or linux.";
                }

                var name = values[0].Trim();
                if (name.Length == 0 || values[1].Length == 0)
                {
                    return "Give a name and a password.";
                }

                _ = Ui.Run($"Signing in as {name}…", async (_, cancellationToken) =>
                {
                    try
                    {
                    var session = await _setup.SignInAsync(connection, name, values[1], cancellationToken, provider).ConfigureAwait(false);
                    Ui.Post(() =>
                    {
                        Ui.Reconnect();
                        var expires = session.ExpiresUtc is { } until ? $" until {RoofCliFormat.Time(until)}" : string.Empty;
                        var note = Environment(Ui.Context)?.ToCredential() is null ? string.Empty : " The credential in the environment is still used instead.";
                        Ui.Say($"Signed in as {session.Name} ({(session.Role is { } role ? RoofCliFormat.Role(role) : "role not given")}){expires}.{note}");
                    });
                    }
                    catch (Exception error) when (error is RoofApiException or RoofProtocolException or HttpRequestException or TimeoutException)
                    {
                        // Retry is a new explicit action with an empty password. No rejected operation is replayed.
                        Ui.Post(() =>
                        {
                            if (ReferenceEquals(connection, Ui.Connection))
                                ShowSignIn(providersEnabled, name, provider, RoofCliContext.Classify(error).Message + " Try again or cancel.");
                        });
                    }
                });
                return null;
            })]));
    }

    private void SignOut()
    {
        if (Ui.Context.Local)
        {
            Ui.Say("Local mode has no saved session to sign out.");
            return;
        }

        _ = Ui.Run("Signing out…", async (_, cancellationToken) =>
    {
        var message = await _setup.SignOutAsync(cancellationToken).ConfigureAwait(false);
        Ui.Post(() =>
        {
            if (message is null)
            {
                Ui.Say("No session is saved.");
                return;
            }

            Ui.Reconnect();
            Ui.Say($"{message} The session was removed from {Ui.Context.CredentialsPath}.");
        });
    });
    }

    /// <summary>
    /// The signed-in person's own display and short name (#209), changed here because the terminal has a keyboard (the
    /// kiosk does not change them). A Linux account's names come from the operating system and are only shown; an API key
    /// is not a person.
    /// </summary>
    internal void ChangeNames()
    {
        if (Ui.Context.Local)
        {
            Ui.Say(RoofIdentityText.LinuxNamesReadOnly);
            return;
        }

        _ = Ui.Run("Reading your names…", async (client, cancellationToken) =>
        {
            var me = await client.Auth.GetCallerAsync(cancellationToken).ConfigureAwait(false);
            Ui.Post(() => ShowNames(me));
        });
    }

    private void ShowNames(RoofCallerResponse me)
    {
        if (me.Kind != RoofCredentialKind.Session)
        {
            Ui.Say($"Names are a person's: sign in with your name and password first (Sign in). {RoofIdentityText.OwnNamesWhere}", error: true);
            return;
        }

        if (me.LoginType == RoofLoginTypes.Linux)
        {
            Ui.Say($"{RoofIdentityText.LinuxNamesReadOnly} Yours: {RoofIdentityText.DescribePerson(me.ShortName ?? me.Name, me.DisplayName)}.");
            return;
        }

        Ui.Ask(new RoofUiPrompt(
            $"Your names ({me.Name})",
            $"{RoofIdentityText.DisplayNameRule}\n{RoofIdentityText.ShortNameRule}\n{RoofIdentityText.NamesChangeEndsNothing}",
            [new RoofUiField("Display name", Initial: me.DisplayName ?? string.Empty), new RoofUiField("Short name", Initial: me.ShortName ?? string.Empty)],
            [new RoofUiAction("Save", values =>
            {
                if (RoofIdentityText.CheckNames(values[0], values[1]) is { } error)
                {
                    return error;
                }

                // An empty name removes it.
                var request = new RoofNamesChangeRequest
                {
                    DisplayName = RoofIdentityContract.NormalizeLabel(values[0]) ?? string.Empty,
                    ShortName = RoofIdentityContract.NormalizeLabel(values[1]) ?? string.Empty
                };
                _ = Ui.Run("Saving your names…", async (client, cancellationToken) =>
                {
                    var after = await client.Auth.ChangeNamesAsync(request, cancellationToken).ConfigureAwait(false);
                    Ui.Post(() => Ui.Say(
                        $"Saved your names: {RoofIdentityText.DescribePerson(after.Name, after.DisplayName)}"
                        + (after.ShortName is { } shortName ? $", short name {shortName}." : ", no short name.")));
                });
                return null;
            })]));
    }

    private void AddFirstAdmin()
    {
        if (Ui.Connection is not { } connection)
        {
            Ui.Say("Save the controller's address and an admin API key under Connection first.", error: true);
            return;
        }

        if (Ui.Caller is { } caller && caller.Role != RoofControllerApiContract.AdminRole)
        {
            Ui.Say($"Adding a person needs the Admin role; {caller.Name} is {RoofCliFormat.Role(caller.Role)}.", error: true);
            return;
        }

        Ui.Ask(new RoofUiPrompt(
            "First admin",
            $"Adds an admin person, who can then sign in with the password. Name: {RoofIdentityText.NameRule} Password: {RoofIdentityText.PasswordRule}",
            [new RoofUiField("Name"), new RoofUiField("Password", Secret: true), new RoofUiField("Password again", Secret: true)],
            [new RoofUiAction("Add", values =>
            {
                var name = values[0].Trim();
                if (!RoofIdentityContract.IsValidName(name))
                {
                    return $"'{name}' is not a valid name: {RoofIdentityText.NameRule}";
                }

                if (!RoofIdentityContract.IsValidPassword(values[1]))
                {
                    return RoofIdentityText.PasswordRule;
                }

                if (values[1] != values[2])
                {
                    return "The two passwords do not match.";
                }

                _ = Ui.Run($"Adding {name}…", async (_, cancellationToken) =>
                {
                    var user = await _setup.CreateAdminAsync(connection, name, values[1], cancellationToken).ConfigureAwait(false);
                    Ui.Post(() => CheckConnection($"Added {user.Name} (admin). Sign in as {user.Name} here, with Sign in."));
                });
                return null;
            })]));
    }

    private static string Join(string? first, string then) => string.IsNullOrEmpty(first) ? then : $"{first} {then}";

    private static string DescribeTrust(RoofCliConnection connection)
        => connection.CaCertificate is { } authority ? $"issued by the CA {RoofCertificateAuthority.Describe(authority)} (only that CA is trusted)"
            : connection.CertificateSha256 is not null ? "pinned by SHA-256"
            : "not pinned (the system's trust store checks it)";

    private static RoofStoredCredentials? Environment(RoofCliContext context)
    {
        try
        {
            return RoofCredentialStore.FromEnvironment(context.Host.GetEnvironmentVariable);
        }
        catch (RoofCredentialFileException)
        {
            return null;
        }
    }
}
