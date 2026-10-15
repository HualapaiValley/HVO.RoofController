using System.CommandLine;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Cli;

public static partial class RoofCli
{
    internal sealed partial class CommandBuilder
    {
        internal static readonly string[] PrincipalHeaders = ["ID", "PROVIDER", "NAME", "ROLE", "ENABLED"];
        internal static IReadOnlyList<string> PrincipalRow(RoofPrincipalResponse person)
            => [person.Id, person.Provider, person.Name, RoofCliFormat.Role(person.Role), person.Enabled ? "yes" : "no"];

        private Command CreatePrincipalsCommand()
        {
            var command = new Command("principals", "Manage enrolled Linux users' controller roles and access (admin). Native accounts and passwords remain managed by Linux.");
            var list = new Command("list", "List enrolled provider identities, without credentials.");
            SetAction(list, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var people = await client.Identity.GetPrincipalsAsync(cancellationToken).ConfigureAwait(false);
                if (context.Json) context.WriteJson(people);
                else RoofCliFormat.WriteTable(context.Out, PrincipalHeaders, people.Select(PrincipalRow));
                return (int)RoofExitCode.Success;
            });
            command.Add(list);
            var id = new Argument<string>("id");
            var role = new Option<string>("--role") { Required = true, Description = "viewer, operator or admin" };
            var enabled = new Option<bool>("--enabled") { Required = true, Description = "true to allow access, false to disable it and revoke sessions and personal keys" };
            var set = new Command("set", "Set a Linux principal's controller role and enabled state by immutable ID.") { id, role, enabled };
            SetAction(set, async (context, parsed, cancellationToken) =>
            {
                var normalized = RoofCliFormat.ParseRole(parsed.GetRequiredValue(role))
                    ?? throw new RoofCliUsageException("The role must be viewer, operator or admin.");
                using var client = context.Connect();
                var person = await client.Identity.UpdatePrincipalAsync(parsed.GetRequiredValue(id),
                    new RoofPrincipalUpdateRequest { Role = normalized, Enabled = parsed.GetValue(enabled) }, cancellationToken).ConfigureAwait(false);
                return ReportDone(context, person, $"Updated {person.Provider}:{person.Name}: {RoofCliFormat.Role(person.Role)}, enabled {person.Enabled}.");
            });
            command.Add(set);
            return command;
        }
    }
}
