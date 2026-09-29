using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>
/// People, API keys and sessions (admin). A new or rotated key's secret is shown once. Keys from the controller's
/// configuration are read-only here.
/// </summary>
internal sealed class RoofUiPeoplePage : RoofUiPage
{
    internal enum Mode
    {
        Users,
        Keys,
        Sessions
    }

    private readonly Label _header;
    private readonly ListView _rows;
    private readonly Dictionary<Mode, List<Button>> _actions = [];
    private RoofUserResponse[] _users = [];
    private RoofApiKeyResponse[] _keys = [];
    private RoofSessionInfoResponse[] _sessions = [];
    private bool _loaded;

    public RoofUiPeoplePage(RoofTerminalUi ui)
        : base(ui, "People")
    {
        var users = AddButton("Users", null, () => Switch(Mode.Users), y: 0);
        var keys = AddButton("API keys", users, () => Switch(Mode.Keys), y: 0);
        AddButton("Sessions", keys, () => Switch(Mode.Sessions), y: 0);
        _header = new Label { X = 0, Y = 2, Width = Dim.Fill() };
        _rows = new ListView { X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true };
        Add(_header, _rows);

        _actions[Mode.Users] = Buttons(
            ("Add", AddUser),
            ("Role", ChangeRole),
            ("Password", SetPassword),
            ("PIN", SetPin),
            ("Remove PIN", RemovePin),
            ("Remove", RemoveUser));
        _actions[Mode.Keys] = Buttons(
            ("Add", AddKey),
            ("Change", ChangeKey),
            ("Rotate", RotateKey),
            ("Remove", RemoveKey));
        _actions[Mode.Sessions] = Buttons(("End", EndSession));
        Switch(Mode.Users);
    }

    public Mode Showing { get; private set; }

    public override void Shown()
    {
        if (!_loaded)
        {
            Reload();
        }
    }

    public override void ConnectionChanged()
    {
        _loaded = false;
        _users = [];
        _keys = [];
        _sessions = [];
        Show();
        if (ReferenceEquals(Ui.CurrentPage, this))
        {
            Reload();
        }
    }

    public override string Describe() => string.Join('\n', LinesOf(_rows).Prepend(_header.Text));

    public void Switch(Mode mode)
    {
        Showing = mode;
        foreach (var (candidate, buttons) in _actions)
        {
            foreach (var button in buttons)
            {
                button.Visible = candidate == mode;
            }
        }

        Show();
    }

    /// <summary>Selects the row of <paramref name="name"/> (a user or key name, or a session ID).</summary>
    public void Select(string name)
    {
        var index = Showing switch
        {
            Mode.Users => Array.FindIndex(_users, user => user.Name == name),
            Mode.Keys => Array.FindIndex(_keys, key => key.Name == name),
            _ => Array.FindIndex(_sessions, session => session.Id == name)
        };
        if (index < 0)
        {
            throw new InvalidOperationException($"There is no '{name}' in {Showing}.");
        }

        _rows.SelectedItem = index + 1;
    }

    public void Reload() => Reload(done: null);

    /// <summary>Reads the lists again; <paramref name="done"/>, the result of a change, is what the message line then says.</summary>
    private void Reload(string? done)
    {
        if (Ui.Caller is { } caller && caller.Role != RoofControllerApiContract.AdminRole)
        {
            Show();
            return;
        }

        _ = Ui.Run(done ?? "Reading people, keys and sessions…", async (client, cancellationToken) =>
        {
            var users = await client.Identity.GetUsersAsync(cancellationToken).ConfigureAwait(false);
            var keys = await client.Identity.GetApiKeysAsync(cancellationToken).ConfigureAwait(false);
            var sessions = await client.Identity.GetSessionsAsync(cancellationToken).ConfigureAwait(false);
            Ui.Post(() =>
            {
                _users = users;
                _keys = keys;
                _sessions = sessions;
                _loaded = true;
                Show();
                Ui.Say(done ?? $"{Count(users.Length, "person", "people")}, {Count(keys.Length, "API key", "API keys")}, {Count(sessions.Length, "session", "sessions")}.");
            });
        });
    }

    private void Show()
    {
        if (Ui.Connection is null)
        {
            _header.Text = "No controller is configured: use Setup (F5).";
            SetLines(_rows, []);
            return;
        }

        if (Ui.Caller is { } caller && caller.Role != RoofControllerApiContract.AdminRole)
        {
            _header.Text = $"People, API keys and sessions need the Admin role; {caller.Name} is {RoofCliFormat.Role(caller.Role)}.";
            SetLines(_rows, []);
            return;
        }

        switch (Showing)
        {
            case Mode.Users:
                _header.Text = _users.Length == 0 && _loaded ? "No people yet: Add one. The first admin can also be made on Setup (F5)." : "People";
                SetLines(_rows, Table(RoofCli.CommandBuilder.UserHeaders, _users.Select(RoofCli.CommandBuilder.UserRow)));
                break;
            case Mode.Keys:
                _header.Text = "API keys (a configuration key is read-only here)";
                SetLines(_rows, Table(RoofCli.CommandBuilder.KeyHeaders, _keys.Select(RoofCli.CommandBuilder.KeyRow)));
                break;
            default:
                _header.Text = "Sessions (ending one signs its holder out at once)";
                SetLines(_rows, Table(RoofCli.CommandBuilder.SessionHeaders, _sessions.Select(RoofCli.CommandBuilder.SessionRow)));
                break;
        }
    }

    // Row 0 is the table's header.
    private int Selected => (_rows.SelectedItem ?? 0) - 1;

    private RoofUserResponse? SelectedUser => Selected >= 0 && Selected < _users.Length ? _users[Selected] : null;

    private RoofApiKeyResponse? SelectedKey => Selected >= 0 && Selected < _keys.Length ? _keys[Selected] : null;

    private RoofSessionInfoResponse? SelectedSession => Selected >= 0 && Selected < _sessions.Length ? _sessions[Selected] : null;

    private List<Button> Buttons(params (string Label, Action Run)[] actions)
    {
        var buttons = new List<Button>();
        View? left = null;
        foreach (var (label, run) in actions)
        {
            var button = AddButton(label, left, run);
            buttons.Add(button);
            left = button;
        }

        return buttons;
    }

    private void Send(string busy, Func<RoofControllerClient, CancellationToken, Task<string>> send)
        => _ = Ui.Run(busy, async (client, cancellationToken) =>
        {
            var done = await send(client, cancellationToken).ConfigureAwait(false);
            Ui.Post(() => Reload(done));
        });

    private void AddUser() => Ui.Ask(new RoofUiPrompt(
        "Add a person",
        $"Name: {RoofCli.NameRule}\nRole: viewer, operator or admin. Give a password (web UI and CLI), a PIN (kiosk; operators and admins), or both.",
        [
            new RoofUiField("Name"),
            new RoofUiField("Role", Initial: "viewer"),
            new RoofUiField("Password", Secret: true),
            new RoofUiField("Password again", Secret: true),
            new RoofUiField("PIN", Secret: true),
            new RoofUiField("PIN again", Secret: true)
        ],
        [new RoofUiAction("Add", values =>
        {
            var name = values[0].Trim();
            if (!RoofIdentityContract.IsValidName(name))
            {
                return $"'{name}' is not a valid name: {RoofCli.NameRule}";
            }

            if (RoofCliFormat.ParseRole(values[1]) is not { } role)
            {
                return "The role must be viewer, operator or admin.";
            }

            if (values[2].Length == 0 && values[4].Length == 0)
            {
                return "Give a password, a PIN, or both.";
            }

            if (NewSecret(values[2], values[3], RoofIdentityContract.IsValidPassword, RoofCli.PasswordRule, "passwords") is { } passwordError)
            {
                return passwordError;
            }

            if (NewSecret(values[4], values[5], RoofIdentityContract.IsValidPin, RoofCli.PinRule, "PINs") is { } pinError)
            {
                return pinError;
            }

            var request = new RoofUserCreateRequest
            {
                Name = name,
                Role = role,
                Password = values[2].Length > 0 ? values[2] : null,
                Pin = values[4].Length > 0 ? values[4] : null
            };
            Send($"Adding {name}…", async (client, cancellationToken) =>
            {
                var user = await client.Identity.AddUserAsync(request, cancellationToken).ConfigureAwait(false);
                return $"Added {user.Name} ({RoofCliFormat.Role(user.Role)}).";
            });
            return null;
        })]));

    private void ChangeRole()
    {
        if (RequireUser() is not { } user)
        {
            return;
        }

        Ui.Ask(new RoofUiPrompt(
            $"Role of {user.Name}",
            "viewer, operator or admin. Changing the role ends every session of the person.",
            [new RoofUiField("Role", Initial: RoofCliFormat.Role(user.Role))],
            [new RoofUiAction("Change", values =>
            {
                if (RoofCliFormat.ParseRole(values[0]) is not { } role)
                {
                    return "The role must be viewer, operator or admin.";
                }

                Update(user, new RoofUserUpdateRequest { Role = role }, $"{user.Name} is now {RoofCliFormat.Role(role)}.");
                return null;
            })]));
    }

    private void SetPassword()
    {
        if (RequireUser() is not { } user)
        {
            return;
        }

        Ui.Ask(new RoofUiPrompt(
            $"Password of {user.Name}",
            $"{RoofCli.PasswordRule} Setting it ends every session of the person.",
            [new RoofUiField("Password", Secret: true), new RoofUiField("Again", Secret: true)],
            [new RoofUiAction("Set", values =>
            {
                if (values[0].Length == 0)
                {
                    return RoofCli.PasswordRule;
                }

                if (NewSecret(values[0], values[1], RoofIdentityContract.IsValidPassword, RoofCli.PasswordRule, "passwords") is { } error)
                {
                    return error;
                }

                Update(user, new RoofUserUpdateRequest { Role = user.Role, Password = values[0] }, $"Set the password of {user.Name}.");
                return null;
            })]));
    }

    private void SetPin()
    {
        if (RequireUser() is not { } user)
        {
            return;
        }

        Ui.Ask(new RoofUiPrompt(
            $"PIN of {user.Name}",
            $"{RoofCli.PinRule} For a kiosk; operators and admins only. Setting it ends the person's PIN sessions.",
            [new RoofUiField("PIN", Secret: true), new RoofUiField("Again", Secret: true)],
            [new RoofUiAction("Set", values =>
            {
                if (values[0].Length == 0)
                {
                    return RoofCli.PinRule;
                }

                if (NewSecret(values[0], values[1], RoofIdentityContract.IsValidPin, RoofCli.PinRule, "PINs") is { } error)
                {
                    return error;
                }

                Update(user, new RoofUserUpdateRequest { Role = user.Role, Pin = values[0] }, $"Set the PIN of {user.Name}.");
                return null;
            })]));
    }

    private void RemovePin()
    {
        if (RequireUser() is not { } user)
        {
            return;
        }

        Confirm($"Remove the PIN of {user.Name}? Their PIN sessions end.", "Remove PIN", ()
            => Update(user, new RoofUserUpdateRequest { Role = user.Role, RemovePin = true }, $"Removed the PIN of {user.Name}."));
    }

    private void RemoveUser()
    {
        if (RequireUser() is not { } user)
        {
            return;
        }

        Confirm($"Remove {user.Name}? Their sessions end at once.", "Remove", () => Send($"Removing {user.Name}…", async (client, cancellationToken) =>
        {
            await client.Identity.RemoveUserAsync(user.Name, cancellationToken).ConfigureAwait(false);
            return $"Removed {user.Name}.";
        }));
    }

    private void Update(RoofUserResponse user, RoofUserUpdateRequest request, string done)
        => Send($"Changing {user.Name}…", async (client, cancellationToken) =>
        {
            await client.Identity.UpdateUserAsync(user.Name, request, cancellationToken).ConfigureAwait(false);
            return done;
        });

    private void AddKey() => Ui.Ask(new RoofUiPrompt(
        "Add an API key",
        $"Name: {RoofCli.NameRule}\nRole: viewer, operator or admin. Kiosk: yes for a kiosk's device key.",
        [new RoofUiField("Name"), new RoofUiField("Role", Initial: "viewer"), new RoofUiField("Kiosk", Initial: "no")],
        [new RoofUiAction("Add", values =>
        {
            var name = values[0].Trim();
            if (!RoofIdentityContract.IsValidName(name))
            {
                return $"'{name}' is not a valid name: {RoofCli.NameRule}";
            }

            if (RoofCliFormat.ParseRole(values[1]) is not { } role)
            {
                return "The role must be viewer, operator or admin.";
            }

            if (ParseYesNo(values[2]) is not { } kiosk)
            {
                return "Kiosk must be yes or no.";
            }

            var request = new RoofApiKeyCreateRequest { Name = name, Role = role, Kiosk = kiosk };
            ShowSecret($"Adding the key {name}…", "New API key", (client, cancellationToken) => client.Identity.AddApiKeyAsync(request, cancellationToken));
            return null;
        })]));

    private void ChangeKey()
    {
        if (RequireManagedKey() is not { } key)
        {
            return;
        }

        Ui.Ask(new RoofUiPrompt(
            $"Key {key.Name}",
            "Role: viewer, operator or admin. Kiosk: yes for a kiosk's device key.",
            [new RoofUiField("Role", Initial: RoofCliFormat.Role(key.Role)), new RoofUiField("Kiosk", Initial: key.Kiosk ? "yes" : "no")],
            [new RoofUiAction("Change", values =>
            {
                if (RoofCliFormat.ParseRole(values[0]) is not { } role)
                {
                    return "The role must be viewer, operator or admin.";
                }

                if (ParseYesNo(values[1]) is not { } kiosk)
                {
                    return "Kiosk must be yes or no.";
                }

                var request = new RoofApiKeyUpdateRequest { Role = role, Kiosk = kiosk };
                Send($"Changing the key {key.Name}…", async (client, cancellationToken) =>
                {
                    var changed = await client.Identity.UpdateApiKeyAsync(key.Name, request, cancellationToken).ConfigureAwait(false);
                    return $"The key {changed.Name} is now {RoofCliFormat.Role(changed.Role)}{(changed.Kiosk ? ", kiosk" : string.Empty)}.";
                });
                return null;
            })]));
    }

    private void RotateKey()
    {
        if (RequireManagedKey() is not { } key)
        {
            return;
        }

        Confirm($"Rotate the key {key.Name}? The old secret stops working at once.", "Rotate", ()
            => ShowSecret($"Rotating the key {key.Name}…", "Rotated API key", (client, cancellationToken) => client.Identity.RotateApiKeyAsync(key.Name, cancellationToken)));
    }

    private void RemoveKey()
    {
        if (RequireManagedKey() is not { } key)
        {
            return;
        }

        Confirm($"Remove the key {key.Name}? It stops working at once.", "Remove", () => Send($"Removing the key {key.Name}…", async (client, cancellationToken) =>
        {
            await client.Identity.RemoveApiKeyAsync(key.Name, cancellationToken).ConfigureAwait(false);
            return $"Removed the key {key.Name}.";
        }));
    }

    /// <summary>Sends a request that returns a key's secret, and shows the secret once.</summary>
    private void ShowSecret(string busy, string title, Func<RoofControllerClient, CancellationToken, Task<RoofApiKeySecretResponse>> send)
        => _ = Ui.Run(busy, async (client, cancellationToken) =>
        {
            var created = await send(client, cancellationToken).ConfigureAwait(false);
            Ui.Post(() =>
            {
                Reload($"The key {created.Key.Name} is ready ({RoofCliFormat.Role(created.Key.Role)}{(created.Key.Kiosk ? ", kiosk" : string.Empty)}).");
                Ui.Ask(new RoofUiPrompt(
                    title,
                    $"The secret of {created.Key.Name}. Copy it now: it is not shown again.",
                    [new RoofUiField("Secret", Initial: created.Secret, ReadOnly: true)],
                    [],
                    CloseLabel: "Done"));
            });
        });

    private void EndSession()
    {
        if (SelectedSession is not { } session)
        {
            Ui.Say("Pick a session first.", error: true);
            return;
        }

        var own = Ui.Caller?.SessionId == session.Id ? " It is this interface's own session: you will need to sign in again." : string.Empty;
        Confirm($"End the session of {session.Name} ({session.Id})?{own}", "End", () => Send("Ending the session…", async (client, cancellationToken) =>
        {
            await client.Identity.EndSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
            return $"Ended the session of {session.Name}.";
        }));
    }

    private RoofUserResponse? RequireUser()
    {
        if (SelectedUser is { } user)
        {
            return user;
        }

        Ui.Say("Pick a person first.", error: true);
        return null;
    }

    private RoofApiKeyResponse? RequireManagedKey()
    {
        if (SelectedKey is not { } key)
        {
            Ui.Say("Pick a key first.", error: true);
            return null;
        }

        if (key.Source == RoofApiKeySource.Configuration)
        {
            Ui.Say($"The key {key.Name} comes from the controller's configuration and is read-only here.", error: true);
            return null;
        }

        return key;
    }

    private void Confirm(string question, string action, Action run) => Ui.Ask(new RoofUiPrompt(
        "Confirm",
        question,
        [],
        [new RoofUiAction(action, _ =>
        {
            run();
            return null;
        })]));

    /// <summary>Checks a new password or PIN typed twice; empty means none. Returns an error, or null.</summary>
    private static string? NewSecret(string value, string again, Func<string?, bool> isValid, string rule, string plural)
        => value.Length == 0 ? again.Length == 0 ? null : $"The two {plural} differ."
            : !isValid(value) ? rule
            : value != again ? $"The two {plural} differ."
            : null;

    private static bool? ParseYesNo(string text) => text.Trim().ToLowerInvariant() switch
    {
        "yes" or "y" or "true" => true,
        "no" or "n" or "false" or "" => false,
        _ => null
    };

    private static string Count(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
