using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>
/// Settings, by group, as the controller's catalogue describes them. Only the settings the caller may change can be
/// changed; a safety-critical change is shown and needs confirming before it is sent; a secret is typed twice and never
/// shown. A hand edit of the settings file is shown with its changes and can be applied or discarded (admin).
/// </summary>
internal sealed class RoofUiSettingsPage : RoofUiPage
{
    private readonly Label _header;
    private readonly ListView _groups;
    private readonly ListView _fields;
    private readonly Label _detail;
    private RoofSettingsForm? _form;
    private bool _loading;

    public RoofUiSettingsPage(RoofTerminalUi ui)
        : base(ui, "Settings")
    {
        _header = new Label { X = 0, Y = 0, Width = Dim.Fill() };
        _groups = new ListView { X = 0, Y = 1, Width = 22, Height = Dim.Fill(6), CanFocus = true };
        _fields = new ListView { X = 23, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(6), CanFocus = true };
        _detail = TextBlock(Pos.AnchorEnd(6), 5);
        Add(_header, _groups, _fields, _detail);
        _groups.ValueChanged += (_, _) => ShowFields();
        _fields.ValueChanged += (_, _) => ShowDetail();
        _fields.Accepting += (_, e) =>
        {
            e.Handled = true;
            Change();
        };

        var change = AddButton("Change", null, Change);
        var clear = AddButton("Clear secret", change, ClearSecret);
        var reload = AddButton("Reload", clear, Reload);
        AddButton("Hand edit", reload, HandEdit);
    }

    public RoofSettingsForm? Form => _form;

    public RoofSettingsFormGroup? SelectedGroup => _form is { } form && _groups.SelectedItem is { } index && index < form.Groups.Count
        ? form.Groups[index]
        : null;

    public RoofSettingsFormField? SelectedField => SelectedGroup is { } group && _fields.SelectedItem is { } index && index < group.Fields.Count
        ? group.Fields[index]
        : null;

    public override void Shown()
    {
        if (_form is null && !_loading)
        {
            Reload();
        }
    }

    public override void ConnectionChanged()
    {
        _form = null;
        Show();
        if (ReferenceEquals(Ui.CurrentPage, this))
        {
            Reload();
        }
    }

    public override string Describe()
        => string.Join('\n', new[] { _header.Text, string.Join('\n', LinesOf(_fields)), _detail.Text });

    /// <summary>Selects a setting by key, as picking it in the lists would.</summary>
    public void Select(string key)
    {
        if (_form?.FindField(key) is not { } field)
        {
            throw new InvalidOperationException($"There is no setting '{key}'.");
        }

        var groupIndex = _form.Groups.ToList().FindIndex(group => group.Name == field.Setting.Group);
        _groups.SelectedItem = groupIndex;
        ShowFields();
        _fields.SelectedItem = _form.Groups[groupIndex].Fields.ToList().FindIndex(candidate => candidate.Key == key);
        ShowDetail();
    }

    public void Reload()
    {
        _loading = Ui.Run("Reading the settings…", async (client, cancellationToken) =>
        {
            try
            {
                var catalogue = await client.Settings.GetCatalogueAsync(cancellationToken).ConfigureAwait(false);
                var settings = await client.Settings.GetAsync(cancellationToken).ConfigureAwait(false);
                var form = RoofSettingsForm.Create(catalogue, settings);
                Ui.Post(() =>
                {
                    _form = form;
                    Show();
                    Ui.Say($"Read the settings (version {form.Version}).");
                });
            }
            finally
            {
                Ui.Post(() => _loading = false);
            }
        });
    }

    private void Show()
    {
        if (_form is not { } form)
        {
            _header.Text = Ui.Connection is null ? "No controller is configured: use Setup (F5)." : "Settings are not read yet.";
            SetLines(_groups, []);
            SetLines(_fields, []);
            _detail.Text = string.Empty;
            return;
        }

        _header.Text = form.PendingHandEdit is { } pending
            ? $"Version {form.Version}. The settings file was edited by hand ({pending.Changes.Count} change(s)): changes here are refused until it is applied or discarded (Hand edit)."
            : RoofSettingsText.HandEditSeen(form)
                ? $"Version {form.Version}. {RoofSettingsText.HandEditSeenByOthers}"
                : $"Version {form.Version}.{(form.Settings.FileBacked ? string.Empty : $" {RoofSettingsText.InMemoryOnly}")}";
        SetLines(_groups, form.Groups.Select(group => group.Title).ToArray());
        ShowFields();
    }

    private void ShowFields()
    {
        if (SelectedGroup is not { } group)
        {
            SetLines(_fields, []);
            _detail.Text = string.Empty;
            return;
        }

        var width = group.Fields.Max(field => field.Label.Length);
        SetLines(_fields, group.Fields.Select(field =>
            $"{field.Label.PadRight(width)}  {field.DisplayValue}{(field.CanWrite ? string.Empty : "  (read-only)")}").ToArray());
        ShowDetail();
    }

    private void ShowDetail()
    {
        if (SelectedField is not { } field)
        {
            _detail.Text = string.Empty;
            return;
        }

        var notes = RoofSettingsText.DescribeNotes(field);
        var lines = new List<string>
        {
            $"{field.Label} ({field.Key}): {field.DisplayValue}{(field.Setting.Secret ? string.Empty : $"   default {field.DefaultValue}")}",
            field.CanWrite ? "You may change it." : $"Read-only: {field.ReadOnlyReason}",
        };
        if (notes.Length > 0)
        {
            lines.Add($"Notes: {notes}");
        }

        if (field.State?.Problem is { } problem)
        {
            lines.Add($"Problem: {problem}");
        }

        lines.Add(field.Description);
        _detail.Text = string.Join('\n', lines);
    }

    private void Change()
    {
        if (SelectedField is not { } field || _form is not { } form)
        {
            Ui.Say("Pick a setting first.", error: true);
            return;
        }

        if (!field.CanWrite)
        {
            Ui.Say($"{field.Label} cannot be changed: {field.ReadOnlyReason}", error: true);
            return;
        }

        if (field.Setting.Secret && form.FindGroup(field.Setting.Group) is { } group)
        {
            // A secret is sent with the group's other secrets, which the controller may take only together.
            var secrets = group.Secrets;
            var together = RoofSettingsText.SecretsSetTogether(group);
            Ui.Ask(new RoofUiPrompt(
                together is null ? $"Change {field.Label}" : $"Change {string.Join(" and ", secrets.Select(secret => secret.Label))}",
                together is null
                    ? $"{field.Description}\nThe secret is never shown. Type the new value twice."
                    : $"{together}\nThe secrets are never shown. Type each new value twice.",
                [.. secrets.SelectMany(secret => together is null
                    ? new[] { new RoofUiField("New value", Secret: true), new RoofUiField("Again", Secret: true) }
                    : [new RoofUiField($"New {secret.Label}", Secret: true), new RoofUiField($"{secret.Label} again", Secret: true)])],
                [new RoofUiAction("Save", values =>
                {
                    var edit = form.Edit(group.Name);
                    var typed = secrets.Select((_, i) => ((string?)values[2 * i], (string?)values[2 * i + 1])).ToList();
                    return edit.TrySetSecrets(typed, out var error) ? Review(form, edit) : error;
                })]));
            return;
        }

        Ui.Ask(new RoofUiPrompt(
            $"Change {field.Label}",
            $"{field.Description}\nNow {field.DisplayValue}; default {field.DefaultValue}.{(RoofSettingsText.DescribeNotes(field) is { Length: > 0 } notes ? $" ({notes})" : string.Empty)}",
            [new RoofUiField("New value", Initial: field.EditText)],
            [new RoofUiAction("Save", values =>
            {
                var edit = form.Edit(field.Setting.Group);
                return edit.TrySet(field.Key, values[0], out var error) ? Review(form, edit) : error;
            })]));
    }

    private void ClearSecret()
    {
        if (SelectedField is not { } field || _form is not { } form)
        {
            Ui.Say("Pick a setting first.", error: true);
            return;
        }

        if (!field.Setting.Secret)
        {
            Ui.Say($"{field.Label} is not a secret.", error: true);
            return;
        }

        // The group's secrets are cleared together, as they are set.
        var edit = form.Edit(field.Setting.Group);
        try
        {
            edit.ClearSecrets();
        }
        catch (ArgumentException error)
        {
            Ui.Say(error.Message.Split(" (Parameter")[0], error: true);
            return;
        }

        var cleared = edit.Group.Fields.Where(candidate => edit.Changes.ContainsKey(candidate.Key)).ToList();
        if (cleared.Count == 0)
        {
            Ui.Say($"Nothing to clear: {field.Label} is not set.");
            return;
        }

        var names = string.Join(" and ", cleared.Select(secret => secret.Label));
        Ui.Ask(new RoofUiPrompt(
            $"Clear {names}",
            cleared.Count == 1 ? $"Remove {names}? {cleared[0].Description}" : $"Remove {names}? {RoofSettingsText.SecretsSetTogether(edit.Group)}",
            [],
            [new RoofUiAction(cleared.Count == 1 ? "Clear it" : "Clear them", _ => Review(form, edit))]));
    }

    /// <summary>Sends the edit, or first asks to confirm it when it is safety-critical. Returns an error to show, or null.</summary>
    private string? Review(RoofSettingsForm form, RoofSettingsEdit edit)
    {
        var changes = RoofSettingsText.DescribeChanges(edit).Select(change => "  " + change).ToList();
        if (changes.Count == 0)
        {
            Ui.Say(RoofSettingsText.NothingToChange);
            return null;
        }

        if (edit.NeedsConfirmation)
        {
            Ui.Ask(new RoofUiPrompt(
                "Safety-critical change",
                $"[{edit.Group.Title}]\n{string.Join('\n', changes)}\n{RoofSettingsText.SafetyCriticalChange}",
                [],
                [new RoofUiAction("Confirm and send", _ =>
                {
                    Send(form, edit, confirm: true);
                    return null;
                })]));
            return null;
        }

        Send(form, edit, confirm: false);
        return null;
    }

    private void Send(RoofSettingsForm form, RoofSettingsEdit edit, bool confirm)
    {
        var request = edit.ToRequest(confirm);
        var group = edit.Group.Name;
        var needsRestart = edit.NeedsRestart;
        _ = Ui.Run("Saving…", async (client, cancellationToken) =>
        {
            var saved = await client.Settings.UpdateAsync(group, request, cancellationToken).ConfigureAwait(false);
            var catalogue = await client.Settings.GetCatalogueAsync(cancellationToken).ConfigureAwait(false);
            var reloaded = RoofSettingsForm.Create(catalogue, await client.Settings.GetAsync(cancellationToken).ConfigureAwait(false));
            Ui.Post(() =>
            {
                _form = reloaded;
                Show();
                var notes = new List<string> { $"Saved (settings version {saved.Version})." };
                // The controller's warnings say why the settings are in memory; this note is for when it gives no reason.
                if (!saved.FileBacked && saved.Warnings.Count == 0)
                {
                    notes.Add("Kept in memory only: it is lost when the controller restarts.");
                }

                if (needsRestart)
                {
                    notes.Add("Part of it applies after a restart (System, F4).");
                }

                notes.AddRange(saved.Warnings.Select(warning => $"Warning: {warning}"));
                Ui.Say(string.Join(' ', notes));
            });
        });
    }

    private void HandEdit()
    {
        if (_form is not { } form)
        {
            Ui.Say("The settings are not read yet.", error: true);
            return;
        }

        if (form.PendingHandEdit is not { } pending)
        {
            // The controller shows a hand edit only to admins: for anyone else, "none is pending" would be a guess.
            if (Ui.Caller is not { } caller)
            {
                Ui.Say("The controller has not said who this credential is yet. Try again shortly.", error: true);
            }
            else if (caller.Role != RoofControllerApiContract.AdminRole)
            {
                Ui.Say(RoofSettingsText.HandEditNeedsAdmin(form), error: true);
            }
            else
            {
                Ui.Say("No hand edit is pending: the settings file matches what the controller uses.");
            }

            return;
        }

        var lines = string.Join('\n', RoofSettingsText.DescribeHandEdit(form, pending));
        var request = new RoofSettingsHandEditRequest { Token = pending.Token };
        Ui.Ask(new RoofUiPrompt(
            "Hand edit",
            lines,
            [],
            [
                new RoofUiAction(pending.RequiresConfirmation ? "Confirm and apply" : "Apply", _ =>
                {
                    HandEditRequest("Applying the hand edit…", "Applied the hand edit", (client, cancellationToken)
                        => client.Settings.ApplyHandEditAsync(request with { ConfirmSafetyCriticalChange = pending.RequiresConfirmation }, cancellationToken));
                    return null;
                }),
                new RoofUiAction("Discard", _ =>
                {
                    Ui.Ask(new RoofUiPrompt(
                        "Discard the hand edit",
                        RoofSettingsText.DiscardHandEditQuestion,
                        [],
                        [new RoofUiAction("Discard it", _ =>
                        {
                            HandEditRequest("Discarding the hand edit…", "Discarded the hand edit", (client, cancellationToken)
                                => client.Settings.DiscardHandEditAsync(request, cancellationToken));
                            return null;
                        })]));
                    return null;
                })
            ]));
    }

    private void HandEditRequest(string busy, string done, Func<RoofControllerClient, CancellationToken, Task<RoofSettingsResponse>> send)
        => _ = Ui.Run(busy, async (client, cancellationToken) =>
        {
            var saved = await send(client, cancellationToken).ConfigureAwait(false);
            var catalogue = await client.Settings.GetCatalogueAsync(cancellationToken).ConfigureAwait(false);
            var form = RoofSettingsForm.Create(catalogue, saved);
            Ui.Post(() =>
            {
                _form = form;
                Show();
                Ui.Say(string.Join(' ', saved.Warnings.Select(warning => $"Warning: {warning}").Prepend($"{done} (settings version {saved.Version}).")));
            });
        });
}
