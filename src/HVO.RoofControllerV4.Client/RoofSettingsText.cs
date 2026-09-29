using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// The words every client uses for remote settings: a setting's notes, the changes an edit makes, and a hand edit of the
/// settings file, so the web UI, the CLI and the terminal UI describe them the same way.
/// </summary>
public static class RoofSettingsText
{
    public const string HandEditPending = "The settings file was edited by hand. Until it is applied or discarded, changes through the controller are refused.";

    /// <summary>Shown to anyone who sees a hand edit is pending but may not review it.</summary>
    public const string HandEditSeenByOthers = "The settings file was edited by hand: changes here are refused until an admin applies or discards it.";

    public const string HandEditNeedsAdminText = "Reviewing, applying or discarding a hand edit of the settings file needs the admin role.";

    public const string DiscardHandEditQuestion = "Discard the hand edit? The edited file is overwritten with the settings the controller uses.";

    /// <summary>Shown with a safety-critical change, before it is confirmed.</summary>
    public const string SafetyCriticalChange = "This change is safety-critical. Send it only if the roof is safe with it.";

    /// <summary>Shown when an edit would send what the controller already has.</summary>
    public const string NothingToChange = "Nothing to change: the setting already has this value.";

    public const string InMemoryOnly = "Kept in memory only: changes are lost when the controller restarts.";

    /// <summary>What a secret editor says when a value is left empty: a secret is removed with Clear secret.</summary>
    public const string TypeTheSecret = "Type the new value; to remove the secret, use Clear secret.";

    /// <summary>Says which of a group's secrets are set and cleared together, or null for a group with one.</summary>
    public static string? SecretsSetTogether(RoofSettingsFormGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Secrets is { Count: > 1 } secrets
            ? $"{string.Join(", ", secrets.Take(secrets.Count - 1).Select(secret => secret.Label))} and {secrets[^1].Label} are set and cleared together."
            : null;
    }

    /// <summary>
    /// Why the secrets typed for a group cannot be sent, or null: each of its <see cref="RoofSettingsFormGroup.Secrets"/>
    /// must be typed, and typed alike twice. <paramref name="typed"/> holds each one's value and its repeat, in that order.
    /// </summary>
    public static string? CheckTypedSecrets(RoofSettingsFormGroup group, IReadOnlyList<(string? Value, string? Again)> typed)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(typed);
        var secrets = group.Secrets;
        var together = SecretsSetTogether(group);
        for (var i = 0; i < secrets.Count && i < typed.Count; i++)
        {
            var (value, again) = typed[i];
            if (string.IsNullOrWhiteSpace(value))
            {
                return together is null ? TypeTheSecret : $"Type the new {secrets[i].Label}: {together} To remove them, use Clear secret.";
            }

            if (!string.Equals(value, again, StringComparison.Ordinal))
            {
                return together is null ? "The two values differ." : $"The two {secrets[i].Label} values differ.";
            }
        }

        return null;
    }

    /// <summary>True when a field says a hand edit is pending, which anyone can see.</summary>
    public static bool HandEditSeen(RoofSettingsForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.PendingHandEdit is not null
            || form.Fields.Any(field => field.ReadOnlyCode == RoofControllerErrorCode.SettingsHandEditPending);
    }

    /// <summary>
    /// Said to anyone without the admin role, who is not shown a pending hand edit: the fields still say when one is
    /// pending.
    /// </summary>
    public static string HandEditNeedsAdmin(RoofSettingsForm form)
        => HandEditNeedsAdminText + (HandEditSeen(form) ? " A hand edit is pending: ask an admin to review it." : string.Empty);

    /// <summary>A pending hand edit, one line each: what changed, what is wrong with it, and what applying it needs.</summary>
    public static IEnumerable<string> DescribeHandEdit(RoofSettingsForm form, RoofSettingsHandEdit pending)
    {
        yield return HandEditPending;
        foreach (var change in DescribeHandEditChanges(form, pending))
        {
            yield return "  " + change;
        }

        foreach (var note in DescribeHandEditNotes(pending))
        {
            yield return note;
        }
    }

    /// <summary>What a pending hand edit changes, one line per setting: <c>key: from -> to</c>.</summary>
    public static IReadOnlyList<string> DescribeHandEditChanges(RoofSettingsForm form, RoofSettingsHandEdit pending)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(pending);
        return pending.Changes.Select(change =>
        {
            if (change.Secret)
            {
                return $"{change.Key}: secret changed";
            }

            var field = form.FindField(change.Key);
            string Show(JsonElement? value) => field is null ? value?.GetRawText() ?? "(none)" : RoofSettingValues.Describe(field.Setting, value);
            var safety = field is not null && change.To is { } to && field.NeedsConfirmation(to) ? "  [SAFETY-CRITICAL]" : string.Empty;
            return $"{change.Key}: {Show(change.From)} -> {Show(change.To)}{safety}";
        }).ToList();
    }

    /// <summary>What is wrong with a pending hand edit, and what applying it needs.</summary>
    public static IReadOnlyList<string> DescribeHandEditNotes(RoofSettingsHandEdit pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var notes = new List<string>();
        if (pending.FileProblem is { } fileProblem)
        {
            notes.Add($"The file cannot be used: {fileProblem}");
        }

        notes.AddRange(pending.Problems.Select(problem => $"Problem: {problem.Key}: {problem.Message}"));
        if (pending.RequiresConfirmation)
        {
            notes.Add("Applying it is safety-critical and needs confirming.");
        }

        if (pending.RequiresLocalCredential)
        {
            notes.Add("Applying it needs a local credential.");
        }

        return notes;
    }

    /// <summary>A setting's notes, comma-separated: read-only, safety-critical, after restart and so on; empty for none.</summary>
    public static string DescribeNotes(RoofSettingsFormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return string.Join(", ", new[]
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
    }

    /// <summary>
    /// What an edit changes, one line per setting: <c>Label: from -> to  [SAFETY-CRITICAL, applies after a restart]</c>.
    /// A setting set to the value it has is left out, unless it is a secret (never shown, so always a change). Empty when
    /// the edit changes nothing.
    /// </summary>
    public static IReadOnlyList<string> DescribeChanges(RoofSettingsEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return edit.Group.Fields
            .Where(field => edit.Changes.ContainsKey(field.Key))
            .Select(field =>
            {
                var value = edit.Changes[field.Key];
                var to = field.Setting.Secret
                    ? value.ValueKind == JsonValueKind.Null ? RoofSettingValues.SecretNotSet : RoofSettingValues.SecretNewValue
                    : RoofSettingValues.Describe(field.Setting, value);
                var notes = string.Join(", ", new[]
                {
                    field.NeedsConfirmation(value) ? "SAFETY-CRITICAL" : null,
                    field.Setting.AppliesAfterRestart ? "applies after a restart" : null,
                    field.NeedsLocalCredential ? "needs a local credential" : null
                }.OfType<string>());
                var changed = field.Setting.Secret || field.DisplayValue != to || field.NeedsConfirmation(value);
                return (Text: $"{field.Label}: {field.DisplayValue} -> {to}{(notes.Length > 0 ? $"  [{notes}]" : string.Empty)}", Changed: changed);
            })
            .Where(change => change.Changed)
            .Select(change => change.Text)
            .ToList();
    }
}
