using System.CommandLine;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Cli;

public static partial class RoofCli
{
    internal sealed partial class CommandBuilder
    {
        private Command CreateLoginCommand()
        {
            var name = new Argument<string>("name") { Description = "The person to sign in as." };
            var command = new Command(
                "login",
                "Sign in with a password and save the session in the credentials file. The password is read from the terminal, or from standard input.")
            {
                name
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var person = parseResult.GetRequiredValue(name);
                var connection = context.ResolveConnection();
                var password = context.ReadSecret($"Password for {person}: ");
                var session = await new RoofCliSetup(context).SignInAsync(connection, person, password, cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(new { name = session.Name, role = session.Role, session.ExpiresUtc, credentialsFile = context.CredentialsPath });
                }
                else
                {
                    var role = session.Role is { } known ? $" ({RoofCliFormat.Role(known)})" : string.Empty;
                    context.Out.WriteLine($"Signed in as {session.Name}{role} until {FormatExpiry(session.ExpiresUtc)}. Saved in {context.CredentialsPath}.");
                }

                WarnIfEnvironmentWins(context);
                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private Command CreateLogoutCommand()
        {
            var command = new Command("logout", "Sign out the saved session, and remove it from the credentials file. A saved API key is kept.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                var message = await new RoofCliSetup(context).SignOutAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    if (context.Json)
                    {
                        context.WriteJson(new { signedOut = false, message = "No session is saved." });
                    }
                    else
                    {
                        context.Out.WriteLine("No session is saved.");
                    }

                    return (int)RoofExitCode.Success;
                }

                if (context.Json)
                {
                    context.WriteJson(new { signedOut = true, message });
                }
                else
                {
                    context.Out.WriteLine($"{message} The session was removed from {context.CredentialsPath}.");
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private Command CreatePasswordCommand()
        {
            var command = new Command("passwd", "Change the signed-in person's password (needs a session from 'login'). Other sessions are ended.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var current = context.ReadSecret("Current password: ");
                var replacement = context.ReadNewSecret("New password: ");
                try
                {
                    await client.Auth.ChangePasswordAsync(current, replacement, cancellationToken).ConfigureAwait(false);
                }
                catch (RoofApiException wrong) when (wrong.Code == RoofControllerErrorCode.SignInFailed)
                {
                    // Only the current password was wrong: the session is still valid, so this is not a sign-in problem.
                    throw new RoofCliRefusedException("The current password is not correct. The password was not changed.", innerException: wrong);
                }

                if (context.Json)
                {
                    context.WriteJson(new { changed = true });
                }
                else
                {
                    context.Out.WriteLine("Password changed. Your other sessions were ended.");
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private static void WarnIfEnvironmentWins(RoofCliContext context)
        {
            if (RoofCliSetup.EnvironmentNote(context) is { } note)
            {
                context.Host.Error.WriteLine(note);
            }
        }

        private static string FormatExpiry(DateTimeOffset? expires) => expires is { } at ? RoofCliFormat.Time(at) : "it is signed out";
    }
}
