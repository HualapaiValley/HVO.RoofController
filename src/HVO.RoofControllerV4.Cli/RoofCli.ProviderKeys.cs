using System.CommandLine;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Cli;

public static partial class RoofCli
{
    internal sealed partial class CommandBuilder
    {
        private Command CreateMyKeysCommand()
        {
            var command = new Command("my-keys", "Manage your provider account's expiring, scoped automation keys (remote password session required).");
            var list = new Command("list", "List your keys without their secrets.");
            SetAction(list, async (context, _, cancellationToken) =>
            {
                RequireRemoteKeys(context);
                using var client = context.Connect();
                var keys = await client.Auth.GetOwnedKeysAsync(cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(keys);
                }
                else
                {
                    RoofCliFormat.WriteTable(context.Out, ["ID", "NAME", "SCOPES", "EXPIRES", "REVOKED"],
                        keys.Select(key => (IReadOnlyList<string>)[key.Id, key.Name, string.Join(",", key.Scopes), RoofCliFormat.Time(key.ExpiresUtc), key.Revoked ? "yes" : "no"]));
                }

                return (int)RoofExitCode.Success;
            });
            command.Add(list);

            var name = new Argument<string>("name");
            var scopes = new Option<string>("--scopes") { Required = true, Description = "Comma-separated read, operate or configure; your role still applies." };
            var hours = new Option<int>("--hours") { DefaultValueFactory = _ => 24, Description = "Lifetime in hours, 1 to 720 (default 24)." };
            var create = new Command("create", "Create a scoped key. Its secret is returned once on standard output; redirect it to protected storage.") { name, scopes, hours };
            SetAction(create, async (context, parsed, cancellationToken) =>
            {
                RequireRemoteKeys(context);
                var requestedScopes = parsed.GetRequiredValue(scopes).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
                var lifetime = parsed.GetValue(hours);
                if (requestedScopes.Length == 0 || requestedScopes.Any(scope => scope is not ("read" or "operate" or "configure")) || lifetime is < 1 or > 720)
                {
                    throw new RoofCliUsageException("Give valid scopes and --hours from 1 to 720.");
                }

                using var client = context.Connect();
                var key = await client.Auth.CreateOwnedKeyAsync(new RoofOwnedKeyCreateRequest
                {
                    Name = parsed.GetRequiredValue(name), Scopes = requestedScopes,
                    ExpiresUtc = context.Host.Time.GetUtcNow().AddHours(lifetime)
                }, cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(new { key = key.Key, secret = key.Secret });
                }
                else
                {
                    context.Host.Error.WriteLine("Store this key where only its intended application can read it. It is returned only once.");
                    context.Out.WriteLine(key.Secret);
                }

                return (int)RoofExitCode.Success;
            });
            command.Add(create);

            var id = new Argument<string>("id");
            var revoke = new Command("revoke", "Revoke your key by ID. Requests with it are refused immediately.") { id };
            SetAction(revoke, async (context, parsed, cancellationToken) =>
            {
                RequireRemoteKeys(context);
                using var client = context.Connect();
                var target = parsed.GetRequiredValue(id);
                await client.Auth.RevokeOwnedKeyAsync(target, cancellationToken).ConfigureAwait(false);
                return ReportDone(context, new { revoked = target }, $"Revoked {target}.");
            });
            command.Add(revoke);
            return command;
        }

        private static void RequireRemoteKeys(RoofCliContext context)
        {
            if (context.Local)
            {
                throw new RoofCliUsageException("Personal keys require an explicit remote password session. Local authorization cannot mint a reusable key.");
            }
        }
    }
}
