using System.CommandLine;
using System.Net;
using System.Text.Json;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Cli;

public static partial class RoofCli
{
    internal sealed partial class CommandBuilder
    {
        private Command CreateConfigCommand()
        {
            var command = new Command(
                "config",
                "Read and change the controller's settings (kept in its appsettings.Local.json). Changes to one group at a time.");
            command.Add(CreateConfigShowCommand());
            command.Add(CreateConfigGetCommand());
            command.Add(CreateConfigSetCommand());
            command.Add(CreateConfigSetSecretCommand());
            command.Add(CreateConfigDiffCommand());
            command.Add(CreateConfigApplyCommand());
            command.Add(CreateConfigDiscardCommand());
            return command;
        }

        private static Option<bool> ConfirmSafetyCriticalOption() => new("--confirm-safety-critical")
        {
            Description = "Confirm a safety-critical change after reviewing it. Without it, such a change is shown and not sent (exit 10)."
        };

        private static async Task<RoofSettingsForm> LoadFormAsync(RoofControllerClient client, CancellationToken cancellationToken)
        {
            var catalogue = await client.Settings.GetCatalogueAsync(cancellationToken).ConfigureAwait(false);
            var settings = await client.Settings.GetAsync(cancellationToken).ConfigureAwait(false);
            return RoofSettingsForm.Create(catalogue, settings);
        }

        private Command CreateConfigShowCommand()
        {
            var group = new Argument<string?>("group") { Description = "Show only this group.", Arity = ArgumentArity.ZeroOrOne };
            var command = new Command("show", "Show the settings you may read, by group.") { group };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                var groups = form.Groups;
                if (parseResult.GetValue(group) is { } name)
                {
                    groups = [form.FindGroup(name) ?? throw new RoofCliUsageException(
                        $"There is no settings group '{name}' that you may read. Groups: {string.Join(", ", form.Groups.Select(candidate => candidate.Name))}.")];
                }

                if (context.Json)
                {
                    context.WriteJson(new
                    {
                        form.Version,
                        form.Settings.SavedAtUtc,
                        form.Settings.SavedBy,
                        form.Settings.FileBacked,
                        form.Settings.FilePath,
                        form.Settings.Warnings,
                        pendingHandEdit = form.PendingHandEdit,
                        groups = groups.Select(candidate => new
                        {
                            candidate.Name,
                            candidate.Title,
                            candidate.Description,
                            settings = candidate.Fields.Select(DescribeFieldJson)
                        })
                    });
                    return (int)RoofExitCode.Success;
                }

                WriteSettingsHeader(context, form);
                foreach (var shown in groups)
                {
                    context.Out.WriteLine();
                    context.Out.WriteLine($"[{shown.Name}] {shown.Title}");
                    RoofCliFormat.WriteTable(
                        context.Out,
                        ["SETTING", "VALUE", "DEFAULT", "NOTES"],
                        shown.Fields.Select(field => (IReadOnlyList<string>)[field.Key, field.DisplayValue, field.DefaultValue, DescribeNotes(field)]));
                }

                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private Command CreateConfigGetCommand()
        {
            var key = new Argument<string>("key") { Description = "The setting: its full key, or the last part of it when that is unique." };
            var command = new Command("get", "Show one setting: its value, default, and what changing it involves.") { key };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                var field = FindField(form, parseResult.GetRequiredValue(key));
                if (context.Json)
                {
                    context.WriteJson(DescribeFieldJson(field));
                    return (int)RoofExitCode.Success;
                }

                var rows = new List<(string, string)>
                {
                    ("Setting", $"{field.Label} ({field.Key})"),
                    ("Group", field.Setting.Group),
                    ("Value", field.DisplayValue),
                    ("Default", field.Setting.Secret ? "(none)" : field.DefaultValue),
                    ("Source", field.State?.Source ?? "(not returned)"),
                    ("Change", field.CanWrite ? "you may change it" : $"read-only: {field.ReadOnlyReason}")
                };
                if (DescribeNotes(field) is { Length: > 0 } notes)
                {
                    rows.Add(("Notes", notes));
                }

                if (field.State?.Problem is { } problem)
                {
                    rows.Add(("Problem", problem));
                }

                rows.Add(("About", field.Description));
                RoofCliFormat.WriteRows(context.Out, rows);
                return (int)RoofExitCode.Success;
            });
            return command;
        }

        private Command CreateConfigSetCommand()
        {
            var assignments = new Argument<string[]>("key=value")
            {
                Description = "One or more settings of one group, as key=value. An empty value clears a setting that may be empty. Durations take 90, 90s, 5m or 01:30:00.",
                Arity = ArgumentArity.OneOrMore
            };
            RejectOptions(assignments);
            var confirm = ConfirmSafetyCriticalOption();
            var dryRun = new Option<bool>("--dry-run") { Description = "Show the change without sending it." };
            var command = new Command(
                "set",
                $"Change settings of one group. Secrets are refused here: use '{CommandName} config set-secret' so they stay off the command line.")
            {
                assignments,
                confirm,
                dryRun
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                var parsed = parseResult.GetRequiredValue(assignments).Select(ParseAssignment).ToArray();
                var fields = parsed.Select(assignment => FindField(form, assignment.Key)).ToArray();
                if (fields.FirstOrDefault(field => field.Setting.Secret) is { } secret)
                {
                    throw new RoofCliUsageException(
                        $"{secret.Key} is a secret. Use '{CommandName} config set-secret {secret.Key}', which reads it from the terminal or standard input.");
                }

                var edit = StartEdit(form, fields);
                for (var index = 0; index < parsed.Length; index++)
                {
                    if (!edit.TrySet(fields[index].Key, parsed[index].Value, out var error))
                    {
                        throw new RoofCliUsageException($"{fields[index].Key}: {error}");
                    }
                }

                return await SendEditAsync(context, client, form, edit, parseResult.GetValue(confirm), parseResult.GetValue(dryRun), cancellationToken).ConfigureAwait(false);
            });
            return command;
        }

        private Command CreateConfigSetSecretCommand()
        {
            var keys = new Argument<string[]>("key")
            {
                Description = "The secret settings, in one group. Settings that are valid only together, such as a user and its password, are set in one change.",
                Arity = ArgumentArity.OneOrMore
            };
            RejectOptions(keys);
            var clear = new Option<bool>("--clear") { Description = "Remove the secrets instead of setting them." };
            var confirm = ConfirmSafetyCriticalOption();
            var command = new Command(
                "set-secret",
                "Set secret settings. Each value is read from the terminal without echo, or as one line of standard input, in the order given; never from the command line.")
            {
                keys,
                clear,
                confirm
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                var fields = parseResult.GetRequiredValue(keys).Select(key => FindField(form, key)).DistinctBy(field => field.Key).ToArray();
                if (fields.FirstOrDefault(field => !field.Setting.Secret) is { } plain)
                {
                    throw new RoofCliUsageException($"{plain.Key} is not a secret. Use '{CommandName} config set {plain.Key}=VALUE'.");
                }

                var edit = StartEdit(form, fields);
                foreach (var field in fields)
                {
                    if (parseResult.GetValue(clear))
                    {
                        edit.ClearSecret(field.Key);
                    }
                    else if (!edit.TrySet(field.Key, context.ReadNewSecret($"{field.Label}: "), out var error))
                    {
                        throw new RoofCliUsageException($"{field.Key}: {error}");
                    }
                }

                return await SendEditAsync(context, client, form, edit, parseResult.GetValue(confirm), dryRun: false, cancellationToken).ConfigureAwait(false);
            });
            return command;
        }

        /// <summary>
        /// A list argument takes every token that no option claims, so an unknown option would be read as a setting:
        /// it is refused as the command line refuses any unknown option.
        /// </summary>
        private static void RejectOptions(Argument<string[]> argument) => argument.Validators.Add(result =>
        {
            if (result.Tokens.FirstOrDefault(token => token.Value.StartsWith('-')) is { } option)
            {
                result.AddError($"Unrecognized command or argument '{option.Value}'.");
            }
        });

        private static RoofSettingsEdit StartEdit(RoofSettingsForm form, IReadOnlyList<RoofSettingsFormField> fields)
        {
            var groups = fields.Select(field => field.Setting.Group).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (groups.Length != 1)
            {
                throw new RoofCliUsageException($"Change one group at a time. These settings are in: {string.Join(", ", groups)}.");
            }

            if (fields.FirstOrDefault(field => !field.CanWrite) is { } readOnly)
            {
                // The controller's reason usually names the setting already.
                var reason = readOnly.ReadOnlyReason ?? string.Empty;
                var message = reason.StartsWith(readOnly.Key, StringComparison.OrdinalIgnoreCase) ? reason : $"{readOnly.Key} cannot be changed: {reason}";

                // Refused as the controller would refuse the change, so the exit code is the same as if it had been sent.
                throw readOnly.ReadOnlyCode is { } code
                    ? new RoofApiException((HttpStatusCode)RoofControllerApiContract.HttpStatusFor(code), code, detail: message)
                    : new RoofCliUsageException(message);
            }

            return form.Edit(groups[0]);
        }

        private static async Task<int> SendEditAsync(
            RoofCliContext context,
            RoofControllerClient client,
            RoofSettingsForm form,
            RoofSettingsEdit edit,
            bool confirm,
            bool dryRun,
            CancellationToken cancellationToken)
        {
            var changes = edit.Group.Fields
                .Where(field => edit.Changes.ContainsKey(field.Key))
                .Select(field => new
                {
                    field.Key,
                    field.Label,
                    From = field.DisplayValue,
                    To = field.Setting.Secret
                        ? (edit.Changes[field.Key].ValueKind == JsonValueKind.Null ? RoofSettingValues.SecretNotSet : RoofSettingValues.SecretNewValue)
                        : RoofSettingValues.Describe(field.Setting, edit.Changes[field.Key]),
                    SafetyCritical = field.NeedsConfirmation(edit.Changes[field.Key]),
                    field.Setting.AppliesAfterRestart,
                    LocalOnly = field.NeedsLocalCredential,
                    field.Setting.Secret
                })

                // A secret sent is always a change, as the controller counts it (its value is never shown, so the text
                // cannot tell), unless it clears a secret that is not set.
                .Where(change => change.Secret
                    ? change.From != RoofSettingValues.SecretNotSet || change.To != RoofSettingValues.SecretNotSet
                    : change.From != change.To || change.SafetyCritical)
                .ToArray();

            if (!edit.HasChanges || changes.Length == 0)
            {
                if (context.Json)
                {
                    context.WriteJson(new { sent = false, changes });
                }
                else
                {
                    context.Out.WriteLine("Nothing to change: the settings already have these values.");
                }

                return (int)RoofExitCode.Success;
            }

            var blocked = edit.NeedsConfirmation && !confirm;
            if (!context.Json)
            {
                context.Out.WriteLine($"[{edit.Group.Name}] {edit.Group.Title}:");
                foreach (var change in changes)
                {
                    var notes = string.Join(", ", new[]
                    {
                        change.SafetyCritical ? "SAFETY-CRITICAL" : null,
                        change.AppliesAfterRestart ? "applies after a restart" : null,
                        change.LocalOnly ? "needs a local credential" : null
                    }.OfType<string>());
                    context.Out.WriteLine($"  {change.Label} ({change.Key}): {change.From} -> {change.To}{(notes.Length > 0 ? $"  [{notes}]" : string.Empty)}");
                }
            }

            if (blocked || dryRun)
            {
                if (context.Json)
                {
                    context.WriteJson(new { sent = false, confirmationRequired = blocked, changes });
                }
                else if (blocked)
                {
                    context.Host.Error.WriteLine("Not sent: the change is safety-critical. Review it, then run the command again with --confirm-safety-critical.");
                }
                else
                {
                    context.Out.WriteLine("Not sent (--dry-run).");
                }

                return (int)(blocked ? RoofExitCode.ConfirmationRequired : RoofExitCode.Success);
            }

            var saved = await client.Settings.UpdateAsync(edit.Group.Name, edit.ToRequest(confirm), cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                context.WriteJson(new { sent = true, saved.Version, needsRestart = edit.NeedsRestart, changes, saved.Warnings });
                return (int)RoofExitCode.Success;
            }

            context.Out.WriteLine($"Saved (settings version {saved.Version}).");
            if (!saved.FileBacked)
            {
                context.Out.WriteLine("The controller keeps settings in memory only: this change is lost when it restarts.");
            }

            if (edit.NeedsRestart)
            {
                context.Out.WriteLine($"Part of the change applies after a restart: '{CommandName} restart'.");
            }

            foreach (var warning in saved.Warnings)
            {
                context.Out.WriteLine($"Warning: {warning}");
            }

            return (int)RoofExitCode.Success;
        }

        private Command CreateConfigDiffCommand()
        {
            var command = new Command("diff", "Show an edit made to the settings file by hand that is not in effect yet.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                var pending = await ReadHandEditAsync(client, form, cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(new { pending = pending is not null, handEdit = pending });
                    return (int)RoofExitCode.Success;
                }

                if (pending is null)
                {
                    context.Out.WriteLine("No hand edit is pending: the settings file matches what the controller uses.");
                    return (int)RoofExitCode.Success;
                }

                WriteHandEdit(context, form, pending, "config apply");
                return (int)RoofExitCode.Success;
            });
            return command;
        }

        /// <summary>Writes a pending hand edit, and the command that confirms it (<paramref name="command"/>).</summary>
        private static void WriteHandEdit(RoofCliContext context, RoofSettingsForm form, RoofSettingsHandEdit pending, string command)
        {
            foreach (var line in DescribeHandEdit(form, pending))
            {
                context.Out.WriteLine(line);
            }

            if (pending.RequiresConfirmation)
            {
                context.Out.WriteLine($"To confirm it: '{CommandName} {command} --confirm-safety-critical'.");
            }
        }

        /// <summary>A pending hand edit, one line each: what changed, what is wrong with it, and what applying it needs.</summary>
        internal static IEnumerable<string> DescribeHandEdit(RoofSettingsForm form, RoofSettingsHandEdit pending)
        {
            yield return "The settings file was edited by hand. Until it is applied or discarded, changes through the controller are refused.";
            foreach (var change in pending.Changes)
            {
                var field = form.FindField(change.Key);
                string Show(JsonElement? value) => change.Secret ? "(secret)" : field is null ? value?.GetRawText() ?? "(none)" : RoofSettingValues.Describe(field.Setting, value);
                var safety = field is not null && change.To is { } to && field.NeedsConfirmation(to) ? "  [SAFETY-CRITICAL]" : string.Empty;
                yield return change.Secret
                    ? $"  {change.Key}: secret changed"
                    : $"  {change.Key}: {Show(change.From)} -> {Show(change.To)}{safety}";
            }

            if (pending.FileProblem is { } fileProblem)
            {
                yield return $"The file cannot be used: {fileProblem}";
            }

            foreach (var problem in pending.Problems)
            {
                yield return $"Problem: {problem.Key}: {problem.Message}";
            }

            if (pending.RequiresConfirmation)
            {
                yield return "Applying it is safety-critical and needs confirming.";
            }

            if (pending.RequiresLocalCredential)
            {
                yield return "Applying it needs a local credential.";
            }
        }

        private Command CreateConfigApplyCommand()
        {
            var confirm = ConfirmSafetyCriticalOption();
            var command = new Command("apply", "Apply the pending hand edit of the settings file.") { confirm };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                if (await ReadHandEditAsync(client, form, cancellationToken).ConfigureAwait(false) is not { } pending)
                {
                    return NoHandEdit(context);
                }

                var confirmed = parseResult.GetValue(confirm);
                if (pending.RequiresConfirmation && !confirmed)
                {
                    if (context.Json)
                    {
                        context.WriteJson(new { sent = false, confirmationRequired = true, handEdit = pending });
                    }
                    else
                    {
                        WriteHandEdit(context, form, pending, "config apply");
                        context.Host.Error.WriteLine("Not applied: the edit is safety-critical. Review it, then run the command again with --confirm-safety-critical.");
                    }

                    return (int)RoofExitCode.ConfirmationRequired;
                }

                var saved = await client.Settings.ApplyHandEditAsync(
                    new RoofSettingsHandEditRequest { Token = pending.Token, ConfirmSafetyCriticalChange = confirmed },
                    cancellationToken).ConfigureAwait(false);
                return ReportHandEdit(context, saved, "Applied the hand edit");
            });
            return command;
        }

        private Command CreateConfigDiscardCommand()
        {
            var force = new Option<bool>("--force") { Description = "Discard without asking." };
            var command = new Command("discard", "Discard the pending hand edit: the settings file is written back as the controller has it.") { force };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                using var client = context.Connect();
                var form = await LoadFormAsync(client, cancellationToken).ConfigureAwait(false);
                if (await ReadHandEditAsync(client, form, cancellationToken).ConfigureAwait(false) is not { } pending)
                {
                    return NoHandEdit(context);
                }

                if (!parseResult.GetValue(force) && !Confirm(context, "Discard the hand edit? The edited file is overwritten."))
                {
                    return (int)RoofExitCode.ConfirmationRequired;
                }

                var saved = await client.Settings.DiscardHandEditAsync(new RoofSettingsHandEditRequest { Token = pending.Token }, cancellationToken).ConfigureAwait(false);
                return ReportHandEdit(context, saved, "Discarded the hand edit");
            });
            return command;
        }

        /// <summary>
        /// The pending hand edit, or null when there is none. The controller shows a hand edit only to admins, so for
        /// anyone else "none" would be a guess: that is refused as the controller refuses applying or discarding one.
        /// </summary>
        private static async Task<RoofSettingsHandEdit?> ReadHandEditAsync(RoofControllerClient client, RoofSettingsForm form, CancellationToken cancellationToken)
        {
            if (form.PendingHandEdit is { } pending)
            {
                return pending;
            }

            var caller = await client.Auth.GetCallerAsync(cancellationToken).ConfigureAwait(false);
            if (caller.Role == RoofControllerApiContract.AdminRole)
            {
                return null;
            }

            throw new RoofCliRefusedException(HandEditNeedsAdmin(form), RoofExitCode.Forbidden);
        }

        /// <summary>
        /// Said to anyone without the admin role, who is not shown a pending hand edit: the fields still say when one is
        /// pending.
        /// </summary>
        internal static string HandEditNeedsAdmin(RoofSettingsForm form)
            => "Reviewing, applying or discarding a hand edit of the settings file needs the admin role."
                + (HandEditSeen(form) ? " A hand edit is pending: ask an admin to review it." : string.Empty);

        /// <summary>True when a field says a hand edit is pending, which anyone can see.</summary>
        internal static bool HandEditSeen(RoofSettingsForm form)
            => form.PendingHandEdit is not null
                || form.Fields.Any(field => field.ReadOnlyCode == RoofControllerErrorCode.SettingsHandEditPending);

        private static int NoHandEdit(RoofCliContext context)
        {
            if (context.Json)
            {
                context.WriteJson(new { sent = false, pending = false });
            }
            else
            {
                context.Out.WriteLine("No hand edit is pending.");
            }

            return (int)RoofExitCode.Success;
        }

        private static int ReportHandEdit(RoofCliContext context, RoofSettingsResponse saved, string done)
        {
            if (context.Json)
            {
                context.WriteJson(new { sent = true, saved.Version, saved.Warnings });
            }
            else
            {
                context.Out.WriteLine($"{done} (settings version {saved.Version}).");
                foreach (var warning in saved.Warnings)
                {
                    context.Out.WriteLine($"Warning: {warning}");
                }
            }

            return (int)RoofExitCode.Success;
        }

        /// <summary>
        /// Asks a yes/no question. Without a terminal, or with <c>--json</c>, the answer is no and the caller is told which
        /// option confirms; with <c>--json</c>, standard output says so too, so a script always has a document to read.
        /// </summary>
        internal static bool Confirm(RoofCliContext context, string question)
        {
            if (!context.Host.IsInteractive || context.Json)
            {
                var message = $"{question} Not confirmed: run the command again with --force.";
                context.Host.Error.WriteLine(message);
                if (context.Json)
                {
                    context.WriteJson(new { sent = false, confirmationRequired = true, message });
                }

                return false;
            }

            var answer = context.Host.ReadLine($"{question} [y/N] ", false)?.Trim();
            if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            context.Host.Error.WriteLine("Not confirmed; nothing was sent.");
            return false;
        }

        private static void WriteSettingsHeader(RoofCliContext context, RoofSettingsForm form)
        {
            var saved = form.Settings.SavedAtUtc is { } at ? $", saved {RoofCliFormat.Time(at)}{(form.Settings.SavedBy is { } by ? $" by {by}" : string.Empty)}" : string.Empty;
            context.Out.WriteLine($"Settings version {form.Version}{saved}{(form.Settings.FileBacked ? string.Empty : " (in memory only: lost at a restart)")}.");
            if (form.Settings.FilePath is { } path)
            {
                context.Out.WriteLine($"File: {path}");
            }

            if (form.PendingHandEdit is not null)
            {
                context.Out.WriteLine($"A hand edit of the settings file is pending: '{CommandName} config diff'.");
            }

            foreach (var warning in form.Settings.Warnings)
            {
                context.Out.WriteLine($"Warning: {warning}");
            }
        }

        internal static string DescribeNotes(RoofSettingsFormField field) => string.Join(", ", new[]
        {
            field.CanWrite ? null : "read-only",
            field.Setting.Safety switch
            {
                RoofSettingSafety.Always => "safety-critical",
                RoofSettingSafety.WhenTurnedOff => "safety-critical to turn off",
                _ => null
            },
            field.Setting.AppliesAfterRestart ? "after restart" : null,
            field.State?.RestartPending == true ? "RESTART PENDING" : null,
            field.NeedsLocalCredential ? "local credential" : null,
            field.Setting.Secret ? "secret" : null,
            field.State?.Problem is null ? null : "PROBLEM"
        }.OfType<string>());

        private static object DescribeFieldJson(RoofSettingsFormField field) => new
        {
            field.Key,
            field.Setting.Group,
            field.Label,
            field.Description,
            value = field.Setting.Secret ? null : field.Value,
            field.DisplayValue,
            @default = field.Setting.Secret ? null : field.Setting.Default,
            field.Setting.Type,
            field.Setting.Unit,
            field.Setting.Secret,
            isSet = field.State?.IsSet,
            source = field.State?.Source,
            field.CanWrite,
            field.ReadOnlyReason,
            field.Setting.Safety,
            field.Setting.AppliesAfterRestart,
            restartPending = field.State?.RestartPending,
            localOnly = field.NeedsLocalCredential,
            problem = field.State?.Problem
        };

        /// <summary>Finds a setting by its full key, or by the last part of its key when only one setting has it.</summary>
        internal static RoofSettingsFormField FindField(RoofSettingsForm form, string key)
        {
            if (form.FindField(key) is { } exact)
            {
                return exact;
            }

            var matches = form.Fields
                .Where(field => string.Equals(field.Key[(field.Key.LastIndexOf(':') + 1)..], key, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return matches.Length switch
            {
                1 => matches[0],
                0 => throw new RoofCliUsageException($"There is no setting '{key}' that you may read. '{CommandName} config show' lists them."),
                _ => throw new RoofCliUsageException($"'{key}' matches more than one setting: {string.Join(", ", matches.Select(field => field.Key))}. Give the full key.")
            };
        }

        private static (string Key, string Value) ParseAssignment(string text)
        {
            var separator = text.IndexOf('=');
            if (separator <= 0)
            {
                throw new RoofCliUsageException($"'{text}' is not key=value.");
            }

            return (text[..separator].Trim(), text[(separator + 1)..]);
        }
    }
}
