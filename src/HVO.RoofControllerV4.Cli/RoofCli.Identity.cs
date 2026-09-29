using System.CommandLine;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Cli;

public static partial class RoofCli
{
    internal sealed partial class CommandBuilder
    {
        // ---- People -------------------------------------------------------------------------------------------------

        private Command CreateUsersCommand()
        {
            var command = new Command(
                "users",
                "Manage the people who sign in (admin; not with a PIN session). Passwords and PINs are read from the terminal or standard input, never from the command line.");
            command.Add(CreateUsersListCommand());
            command.Add(CreateUsersShowCommand());
            command.Add(CreateUsersAddCommand());
            command.Add(CreateUsersSetCommand());
            command.Add(CreateUsersRemoveCommand());
            return command;
        }

        private Command CreateUsersListCommand()
        {
            var command = new Command("list", "List the people, their roles, and whether each has a password and a PIN.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var users = await client.Identity.GetUsersAsync(cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(users);
                }
                else if (users.Length == 0)
                {
                    context.Out.WriteLine($"No people yet. Add one with '{CommandName} users add NAME --role admin'.");
                }
                else
                {
                    RoofCliFormat.WriteTable(context.Out, UserHeaders, users.Select(UserRow));
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private Command CreateUsersShowCommand()
        {
            var name = NameArgument("The person.");
            var command = new Command("show", "Show one person.") { name };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                var user = await client.Identity.GetUserAsync(parseResult.GetRequiredValue(name), cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(user);
                }
                else
                {
                    RoofCliFormat.WriteRows(context.Out, DescribeUser(user));
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        internal static readonly string[] UserHeaders = ["NAME", "ROLE", "PASSWORD", "PIN", "SESSIONS", "UPDATED"];

        internal static IReadOnlyList<string> UserRow(RoofUserResponse user) =>
        [
            user.Name,
            RoofCliFormat.Role(user.Role),
            user.HasPassword ? "yes" : "no",
            user.HasPin ? "yes" : "no",
            user.ActiveSessions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            RoofCliFormat.Time(user.UpdatedUtc)
        ];

        internal static readonly string[] KeyHeaders = ["NAME", "ROLE", "KIOSK", "SOURCE", "UPDATED"];

        internal static IReadOnlyList<string> KeyRow(RoofApiKeyResponse key) =>
        [
            key.Name,
            RoofCliFormat.Role(key.Role),
            key.Kiosk ? "yes" : "no",
            key.Source == RoofApiKeySource.Configuration ? "configuration (read-only)" : "managed",
            key.UpdatedUtc is { } updated ? RoofCliFormat.Time(updated) : string.Empty
        ];

        internal static readonly string[] SessionHeaders = ["ID", "NAME", "ROLE", "KIND", "DEVICE", "CREATED", "EXPIRES"];

        internal static IReadOnlyList<string> SessionRow(RoofSessionInfoResponse session) =>
        [
            session.Id,
            session.Name,
            RoofCliFormat.Role(session.Role),
            RoofIdentityText.DescribeKind(session.Kind),
            session.Device ?? string.Empty,
            RoofCliFormat.Time(session.CreatedUtc),
            RoofCliFormat.Time(session.ExpiresUtc)
        ];

        internal static IEnumerable<(string, string)> DescribeUser(RoofUserResponse user)
        {
            yield return ("Name", user.Name);
            yield return ("Role", RoofCliFormat.Role(user.Role));
            yield return ("Password", user.HasPassword ? "set" : "not set");
            yield return ("PIN", user.HasPin ? "set" : "not set");
            yield return ("Sessions", user.ActiveSessions.ToString(System.Globalization.CultureInfo.InvariantCulture));
            yield return ("Created", RoofCliFormat.Time(user.CreatedUtc));
            yield return ("Updated", RoofCliFormat.Time(user.UpdatedUtc));
        }

        private Command CreateUsersAddCommand()
        {
            var name = NameArgument("The new person's name: letters, digits, '.', '_', '@' or '-', starting with a letter or digit.");
            var role = RoleOption(required: true);
            var password = new Option<bool>("--password") { Description = "Give the person a password, for the web UI and the CLI (the default)." };
            var pin = new Option<bool>("--pin") { Description = "Give the person a PIN, for a kiosk (operators and admins)." };
            var command = new Command("add", "Add a person. Asks for the password, the PIN, or both.") { name, role, password, pin };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var person = RequireName(parseResult.GetRequiredValue(name));
                var withPin = parseResult.GetValue(pin);
                var withPassword = parseResult.GetValue(password) || !withPin;
                var request = new RoofUserCreateRequest
                {
                    Name = person,
                    Role = parseResult.GetRequiredValue(role),
                    Password = withPassword ? ReadNewPassword(context, $"Password for {person}: ") : null,
                    Pin = withPin ? ReadNewPin(context, $"PIN for {person}: ") : null
                };

                using var client = context.Connect();
                var user = await client.Identity.AddUserAsync(request, cancellationToken).ConfigureAwait(false);
                return ReportUser(context, user, $"Added {user.Name} ({RoofCliFormat.Role(user.Role)}).");
            });
            return command;
        }

        private Command CreateUsersSetCommand()
        {
            var name = NameArgument("The person.");
            var role = RoleOption(required: false);
            var password = new Option<bool>("--password") { Description = "Set a new password (asked for). Ends all the person's sessions." };
            var pin = new Option<bool>("--pin") { Description = "Set a new PIN (asked for). Ends the person's PIN sessions." };
            var removePassword = new Option<bool>("--remove-password") { Description = "Remove the password. The person keeps a PIN." };
            var removePin = new Option<bool>("--remove-pin") { Description = "Remove the PIN. Ends the person's PIN sessions." };
            var command = new Command(
                "set",
                "Change a person's role, password or PIN. Changing the role or the password ends all their sessions.")
            {
                name,
                role,
                password,
                pin,
                removePassword,
                removePin
            };
            command.Validators.Add(result =>
            {
                if (result.GetValue(password) && result.GetValue(removePassword))
                {
                    result.AddError("--password and --remove-password cannot be used together.");
                }

                if (result.GetValue(pin) && result.GetValue(removePin))
                {
                    result.AddError("--pin and --remove-pin cannot be used together.");
                }

                if (result.GetResult(role) is null && !result.GetValue(password) && !result.GetValue(pin)
                    && !result.GetValue(removePassword) && !result.GetValue(removePin))
                {
                    result.AddError("Say what to change: --role, --password, --pin, --remove-password or --remove-pin.");
                }
            });
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var person = parseResult.GetRequiredValue(name);
                var newPassword = parseResult.GetValue(password) ? ReadNewPassword(context, $"New password for {person}: ") : null;
                var newPin = parseResult.GetValue(pin) ? ReadNewPin(context, $"New PIN for {person}: ") : null;

                using var client = context.Connect();
                // The update always carries the role, so an unchanged role is read first.
                var current = parseResult.GetValue(role) is { } changed
                    ? changed
                    : (await client.Identity.GetUserAsync(person, cancellationToken).ConfigureAwait(false)).Role;
                var user = await client.Identity.UpdateUserAsync(
                    person,
                    new RoofUserUpdateRequest
                    {
                        Role = current,
                        Password = newPassword,
                        Pin = newPin,
                        RemovePassword = parseResult.GetValue(removePassword),
                        RemovePin = parseResult.GetValue(removePin)
                    },
                    cancellationToken).ConfigureAwait(false);
                return ReportUser(context, user, $"Updated {user.Name}.");
            });
            return command;
        }

        private Command CreateUsersRemoveCommand()
        {
            var name = NameArgument("The person.");
            var force = ForceOption("Remove without asking.");
            var command = new Command("remove", "Remove a person and end all their sessions.") { name, force };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var person = parseResult.GetRequiredValue(name);
                if (!parseResult.GetValue(force) && !Confirm(context, $"Remove {person} and end all their sessions?"))
                {
                    return (int)RoofExitCode.ConfirmationRequired;
                }

                using var client = context.Connect();
                await client.Identity.RemoveUserAsync(person, cancellationToken).ConfigureAwait(false);
                return ReportDone(context, new { removed = person }, $"Removed {person}.");
            });
            return command;
        }

        private static int ReportUser(RoofCliContext context, RoofUserResponse user, string message)
        {
            if (context.Json)
            {
                context.WriteJson(user);
            }
            else
            {
                context.Out.WriteLine(message);
                RoofCliFormat.WriteRows(context.Out, DescribeUser(user));
            }

            return (int)RoofExitCode.Success;
        }

        // ---- PINs ---------------------------------------------------------------------------------------------------

        private Command CreatePinsCommand()
        {
            var command = new Command(
                "pins",
                "Manage the PINs people use at a kiosk (admin; not with a PIN session). A PIN is read from the terminal or standard input.");

            var list = new Command("list", "List the people who have a PIN.");
            SetAction(list, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var users = (await client.Identity.GetUsersAsync(cancellationToken).ConfigureAwait(false)).Where(user => user.HasPin).ToArray();
                if (context.Json)
                {
                    context.WriteJson(users.Select(user => new { user.Name, user.Role }));
                }
                else if (users.Length == 0)
                {
                    context.Out.WriteLine($"No one has a PIN. Give one with '{CommandName} pins set NAME'.");
                }
                else
                {
                    RoofCliFormat.WriteTable(
                        context.Out,
                        ["NAME", "ROLE"],
                        users.Select(user => (IReadOnlyList<string>)[user.Name, RoofCliFormat.Role(user.Role)]));
                }

                return (int)RoofExitCode.Success;
            });

            var setName = NameArgument("The person (an operator or an admin).");
            var set = new Command("set", "Set or change a person's PIN (6 to 12 digits). Ends their PIN sessions.") { setName };
            SetAction(set, async (context, parseResult, cancellationToken) =>
            {
                var person = parseResult.GetRequiredValue(setName);
                var newPin = ReadNewPin(context, $"PIN for {person}: ");
                using var client = context.Connect();
                var current = await client.Identity.GetUserAsync(person, cancellationToken).ConfigureAwait(false);
                var user = await client.Identity.UpdateUserAsync(
                    person,
                    new RoofUserUpdateRequest { Role = current.Role, Pin = newPin },
                    cancellationToken).ConfigureAwait(false);
                return ReportDone(context, user, $"PIN set for {user.Name}. Their PIN sessions were ended.");
            });

            var removeName = NameArgument("The person.");
            var remove = new Command("remove", "Remove a person's PIN. Ends their PIN sessions.") { removeName };
            SetAction(remove, async (context, parseResult, cancellationToken) =>
            {
                var person = parseResult.GetRequiredValue(removeName);
                using var client = context.Connect();
                var current = await client.Identity.GetUserAsync(person, cancellationToken).ConfigureAwait(false);
                var user = await client.Identity.UpdateUserAsync(
                    person,
                    new RoofUserUpdateRequest { Role = current.Role, RemovePin = true },
                    cancellationToken).ConfigureAwait(false);
                return ReportDone(context, user, $"PIN removed for {user.Name}.");
            });

            command.Add(list);
            command.Add(set);
            command.Add(remove);
            return command;
        }

        // ---- API keys -----------------------------------------------------------------------------------------------

        private Command CreateKeysCommand()
        {
            var command = new Command(
                "keys",
                "Manage API keys (admin; not with a PIN session). A new key's value is shown once, when it is added or rotated.");
            command.Add(CreateKeysListCommand());
            command.Add(CreateKeysAddCommand());
            command.Add(CreateKeysSetCommand());
            command.Add(CreateKeysRotateCommand());
            command.Add(CreateKeysRemoveCommand());
            return command;
        }

        private Command CreateKeysListCommand()
        {
            var command = new Command("list", "List the API keys (never their values). Keys from the controller's configuration are read-only here.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var keys = await client.Identity.GetApiKeysAsync(cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(keys);
                }
                else
                {
                    RoofCliFormat.WriteTable(context.Out, KeyHeaders, keys.Select(KeyRow));
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private Command CreateKeysAddCommand()
        {
            var name = NameArgument("The key's name, for example the device or script that uses it.");
            var role = RoleOption(required: true);
            var kiosk = new Option<bool>("--kiosk") { Description = "A kiosk's key: people sign in at it with a PIN. Must have the viewer role." };
            var command = new Command("add", "Add an API key. The controller generates it; it is shown once.") { name, role, kiosk };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                var created = await client.Identity.AddApiKeyAsync(
                    new RoofApiKeyCreateRequest
                    {
                        Name = RequireName(parseResult.GetRequiredValue(name)),
                        Role = parseResult.GetRequiredValue(role),
                        Kiosk = parseResult.GetValue(kiosk)
                    },
                    cancellationToken).ConfigureAwait(false);
                return ReportKeySecret(context, created, "Added");
            });
            return command;
        }

        private Command CreateKeysSetCommand()
        {
            var name = NameArgument("The key.");
            var role = RoleOption(required: false);
            var kiosk = new Option<bool?>("--kiosk") { Description = "true to make it a kiosk's key, false to make it an ordinary key." };
            var command = new Command("set", "Change a managed key's role, or whether it is a kiosk's key. Requests with its old role are refused at once.")
            {
                name,
                role,
                kiosk
            };
            command.Validators.Add(result =>
            {
                if (result.GetResult(role) is null && result.GetResult(kiosk) is null)
                {
                    result.AddError("Say what to change: --role or --kiosk.");
                }
            });
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var keyName = parseResult.GetRequiredValue(name);
                using var client = context.Connect();
                var newRole = parseResult.GetValue(role);
                var newKiosk = parseResult.GetValue(kiosk);
                if (newRole is null || newKiosk is null)
                {
                    // The update carries both, so the one left out is read first.
                    var current = (await client.Identity.GetApiKeysAsync(cancellationToken).ConfigureAwait(false))
                        .FirstOrDefault(key => string.Equals(key.Name, keyName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new RoofApiException(System.Net.HttpStatusCode.NotFound, RoofControllerErrorCode.IdentityNotFound, detail: $"There is no API key '{keyName}'.");
                    newRole ??= current.Role;
                    newKiosk ??= current.Kiosk;
                }

                var key = await client.Identity.UpdateApiKeyAsync(
                    keyName,
                    new RoofApiKeyUpdateRequest { Role = newRole, Kiosk = newKiosk },
                    cancellationToken).ConfigureAwait(false);
                return ReportDone(context, key, $"Updated {key.Name}: {RoofCliFormat.Role(key.Role)}{(key.Kiosk ? ", kiosk" : string.Empty)}.");
            });
            return command;
        }

        private Command CreateKeysRotateCommand()
        {
            var name = NameArgument("The key.");
            var force = ForceOption("Rotate without asking.");
            var command = new Command("rotate", "Replace a managed key's value. The old value is refused at once; the new one is shown once.") { name, force };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var keyName = parseResult.GetRequiredValue(name);
                if (!parseResult.GetValue(force) && !Confirm(context, $"Rotate {keyName}? Everything using its current value stops working."))
                {
                    return (int)RoofExitCode.ConfirmationRequired;
                }

                using var client = context.Connect();
                var rotated = await client.Identity.RotateApiKeyAsync(keyName, cancellationToken).ConfigureAwait(false);
                return ReportKeySecret(context, rotated, "Rotated");
            });
            return command;
        }

        private Command CreateKeysRemoveCommand()
        {
            var name = NameArgument("The key.");
            var force = ForceOption("Remove without asking.");
            var command = new Command("remove", "Remove a managed key. Requests with it are refused at once.") { name, force };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var keyName = parseResult.GetRequiredValue(name);
                if (!parseResult.GetValue(force) && !Confirm(context, $"Remove {keyName}? Everything using it stops working."))
                {
                    return (int)RoofExitCode.ConfirmationRequired;
                }

                using var client = context.Connect();
                await client.Identity.RemoveApiKeyAsync(keyName, cancellationToken).ConfigureAwait(false);
                return ReportDone(context, new { removed = keyName }, $"Removed {keyName}.");
            });
            return command;
        }

        /// <summary>
        /// Writes a key that was just made. The value goes to standard output alone on its line (or in the JSON), so a
        /// script can take it; the notes go to standard error.
        /// </summary>
        private static int ReportKeySecret(RoofCliContext context, RoofApiKeySecretResponse created, string done)
        {
            if (context.Json)
            {
                context.WriteJson(new { key = created.Key, secret = created.Secret });
            }
            else
            {
                context.Host.Error.WriteLine(
                    $"{done} {created.Key.Name} ({RoofCliFormat.Role(created.Key.Role)}{(created.Key.Kiosk ? ", kiosk" : string.Empty)}). "
                    + "Its value follows. It is shown only now: store it where only its device or script can read it.");
                context.Out.WriteLine(created.Secret);
            }

            return (int)RoofExitCode.Success;
        }

        // ---- Sessions -----------------------------------------------------------------------------------------------

        private Command CreateSessionsCommand()
        {
            var command = new Command("sessions", "List and end people's sessions (admin; not with a PIN session). Tokens are never shown.");

            var list = new Command("list", "List the open sessions.");
            SetAction(list, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var sessions = await client.Identity.GetSessionsAsync(cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(sessions);
                }
                else if (sessions.Length == 0)
                {
                    context.Out.WriteLine("No sessions are open.");
                }
                else
                {
                    RoofCliFormat.WriteTable(context.Out, SessionHeaders, sessions.Select(SessionRow));
                }

                return (int)RoofExitCode.Success;
            });

            var id = new Argument<string>("id") { Description = "The session's ID, from 'sessions list'." };
            var end = new Command("end", "End a session. Its holder is signed out at once.") { id };
            SetAction(end, async (context, parseResult, cancellationToken) =>
            {
                var sessionId = parseResult.GetRequiredValue(id);
                using var client = context.Connect();
                await client.Identity.EndSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return ReportDone(context, new { ended = sessionId }, $"Ended session {sessionId}.");
            });

            command.Add(list);
            command.Add(end);
            return command;
        }

        // ---- Shared -------------------------------------------------------------------------------------------------

        private static Argument<string> NameArgument(string description) => new("name") { Description = description };

        private static Option<bool> ForceOption(string description) => new("--force") { Description = description };

        /// <summary><c>--role</c>: viewer, operator or admin (or the controller's own role names).</summary>
        private static Option<string?> RoleOption(bool required)
        {
            var option = new Option<string?>("--role")
            {
                Description = "viewer (status and Stop), operator (also open, close, clear fault) or admin (also settings, people, keys, restart).",
                Required = required,
                CustomParser = result =>
                {
                    var text = result.Tokens.Count == 0 ? string.Empty : result.Tokens[0].Value;
                    if (RoofCliFormat.ParseRole(text) is { } role)
                    {
                        return role;
                    }

                    result.AddError($"'{text}' is not a role. Use viewer, operator or admin.");
                    return null;
                }
            };
            option.CompletionSources.Add("viewer", "operator", "admin");
            return option;
        }

        private static string RequireName(string name) => RoofIdentityContract.IsValidName(name)
            ? name
            : throw new RoofCliUsageException($"'{name}' is not a valid name: {RoofIdentityText.NameRule}");

        private static string ReadNewPassword(RoofCliContext context, string prompt)
        {
            var password = context.ReadNewSecret(prompt);
            return RoofIdentityContract.IsValidPassword(password) ? password : throw new RoofCliUsageException(RoofIdentityText.PasswordRule);
        }

        private static string ReadNewPin(RoofCliContext context, string prompt)
        {
            var pin = context.ReadNewSecret(prompt);
            return RoofIdentityContract.IsValidPin(pin) ? pin : throw new RoofCliUsageException(RoofIdentityText.PinRule);
        }

        private static int ReportDone<T>(RoofCliContext context, T value, string message)
        {
            if (context.Json)
            {
                context.WriteJson(value);
            }
            else
            {
                context.Out.WriteLine(message);
            }

            return (int)RoofExitCode.Success;
        }
    }
}
