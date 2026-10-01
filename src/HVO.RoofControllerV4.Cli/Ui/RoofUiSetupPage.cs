using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>
/// The connection, as <c>hvo-roof setup</c>, <c>login</c> and <c>logout</c> keep it: the controller's address, the
/// certificate pin or CA, and an API key in the credentials file, a check of the connection, signing in and out, and
/// adding the first admin person with an admin API key.
/// </summary>
internal sealed class RoofUiSetupPage : RoofUiPage
{
    private readonly RoofCliSetup _setup;
    private readonly ListView _lines;
    private RoofCliSetup.Check? _check;
    private RoofCliConnection? _checked;

    public RoofUiSetupPage(RoofTerminalUi ui)
        : base(ui, "Setup")
    {
        _setup = new RoofCliSetup(ui.Context);
        _lines = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true };
        Add(_lines);
        var connection = AddButton("Connection", null, EditConnection);
        var check = AddButton("Check", connection, () => CheckConnection());
        var signIn = AddButton("Sign in", check, SignIn);
        var signOut = AddButton("Sign out", signIn, SignOut);
        AddButton("First admin", signOut, AddFirstAdmin);
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
            lines.Add(connection.Credential is null
                ? "Credential:  none. Sign in, or save an admin's API key under Connection."
                : $"Credential:  {connection.Credential} (from {(connection.Source == "environment" ? "the environment" : connection.Source)})");
        }
        else
        {
            lines.Add(Ui.ConnectionProblem ?? "No controller is configured.");
            lines.Add("Connection saves the controller's address, and an API key when you have one.");
        }

        lines.Add($"Credentials file: {context.CredentialsPath}");
        if (Environment(context)?.ToCredential() is not null)
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

    private void SignIn()
    {
        if (Ui.Connection is not { } connection)
        {
            Ui.Say("Save the controller's address under Connection first.", error: true);
            return;
        }

        Ui.Ask(new RoofUiPrompt(
            "Sign in",
            $"Sign in to {connection.Controller} with a password. The session is saved in {Ui.Context.CredentialsPath}.",
            [new RoofUiField("Name", Initial: Ui.Caller?.Kind == RoofCredentialKind.Session ? Ui.Caller.Name : string.Empty), new RoofUiField("Password", Secret: true)],
            [new RoofUiAction("Sign in", values =>
            {
                var name = values[0].Trim();
                if (name.Length == 0 || values[1].Length == 0)
                {
                    return "Give a name and a password.";
                }

                _ = Ui.Run($"Signing in as {name}…", async (_, cancellationToken) =>
                {
                    var session = await _setup.SignInAsync(connection, name, values[1], cancellationToken).ConfigureAwait(false);
                    Ui.Post(() =>
                    {
                        Ui.Reconnect();
                        var expires = session.ExpiresUtc is { } until ? $" until {RoofCliFormat.Time(until)}" : string.Empty;
                        var note = Environment(Ui.Context)?.ToCredential() is null ? string.Empty : " The credential in the environment is still used instead.";
                        Ui.Say($"Signed in as {session.Name} ({(session.Role is { } role ? RoofCliFormat.Role(role) : "role not given")}){expires}.{note}");
                    });
                });
                return null;
            })]));
    }

    private void SignOut() => _ = Ui.Run("Signing out…", async (_, cancellationToken) =>
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
