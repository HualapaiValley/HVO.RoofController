using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>How a setting's value is typed on the touchscreen.</summary>
public enum KioskEditorKind
{
    /// <summary>Digits, a point, a minus and a colon: whole numbers, numbers and durations.</summary>
    Keypad,

    /// <summary>Letters, digits and the symbols a name or an address needs.</summary>
    Keyboard,

    /// <summary>One button per value: true or false, or an enum's values.</summary>
    Choices
}

/// <summary>
/// The kiosk's settings page: the controller's settings by group, one setting's details, and the editor that changes it.
/// It says and asks what the CLI and the web UI do; the controller decides what the unlocking person may change. Secrets
/// are not typed on the kiosk.
/// </summary>
/// <remarks>Used on the UI thread only; <see cref="Changed"/> is raised there.</remarks>
public sealed class KioskSettingsPanel
{
    /// <summary>Said instead of an editor for a secret: secrets are not typed on a screen others can watch.</summary>
    public string SecretsElsewhere => _console.Wording.SecretsElsewhere;

    /// <summary>The value of the "no value" choice of a nullable setting.</summary>
    public const string NoValue = "(none)";

    public const string ReadAgainAfterRefusal = "The settings were read again: check them, then try again.";

    public const string NotReadYet = "The settings are not read yet.";

    private readonly KioskConsole _console;
    private CancellationTokenSource _reset = new();

    public KioskSettingsPanel(KioskConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    /// <summary>The settings as last read, or null before they are.</summary>
    public RoofSettingsForm? Form { get; private set; }

    /// <summary>What is on its way to the controller ("Saving…"), or null.</summary>
    public string? Busy { get; private set; }

    /// <summary>What the last read or change came to, or null.</summary>
    public KioskNotice? Message { get; private set; }

    /// <summary>The group shown, or null for the list of groups.</summary>
    public RoofSettingsFormGroup? Group { get; private set; }

    /// <summary>The setting shown, or null.</summary>
    public RoofSettingsFormField? Field { get; private set; }

    /// <summary>The setting being changed, or null when no editor is open.</summary>
    public RoofSettingsFormField? Editing { get; private set; }

    /// <summary>How <see cref="Editing"/> is typed.</summary>
    public KioskEditorKind EditorKind { get; private set; }

    /// <summary>The value typed so far.</summary>
    public string EditText { get; private set; } = string.Empty;

    /// <summary>True while the keyboard types capitals.</summary>
    public bool Shift { get; private set; }

    /// <summary>Why the value typed cannot be sent, or null.</summary>
    public string? EditError { get; private set; }

    /// <summary>The values offered by a <see cref="KioskEditorKind.Choices"/> editor.</summary>
    public IReadOnlyList<string> Choices { get; private set; } = [];

    /// <summary>A question to answer before something is sent, or null.</summary>
    public KioskQuestion? Question { get; private set; }

    /// <summary>The line above the groups: the version, and whether the settings file waits for a hand edit's review.</summary>
    public string Header
    {
        get
        {
            if (Form is not { } form)
            {
                return Busy is null ? NotReadYet : string.Empty;
            }

            if (form.PendingHandEdit is { } pending)
            {
                return $"Version {form.Version}. The settings file was edited by hand ({pending.Changes.Count} change(s)): changes here are refused until it is applied or discarded (Hand edit).";
            }

            return RoofSettingsText.HandEditSeen(form)
                ? $"Version {form.Version}. {RoofSettingsText.HandEditSeenByOthers}"
                : $"Version {form.Version}.{(form.Settings.FileBacked ? string.Empty : $" {RoofSettingsText.InMemoryOnly}")}";
        }
    }

    /// <summary>Raised after anything here changed.</summary>
    public event Action? Changed;

    /// <summary>A setting's line in its group's list: <c>label  value</c>, and whether it is read-only.</summary>
    public static string DescribeLine(RoofSettingsFormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.CanWrite ? field.DisplayValue : $"{field.DisplayValue}  (read-only)";
    }

    /// <summary>A setting's details, one line each: value and default, whether it may be changed, notes, problem, description.</summary>
    public static IReadOnlyList<string> Describe(RoofSettingsFormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var lines = new List<string>
        {
            field.Setting.Secret
                ? $"{field.Label} ({field.Key}): {field.DisplayValue}"
                : $"{field.Label} ({field.Key}): {field.DisplayValue}   default {field.DefaultValue}",
            field.CanWrite ? "You may change it." : $"Read-only: {field.ReadOnlyReason}"
        };

        if (RoofSettingsText.DescribeNotes(field) is { Length: > 0 } notes)
        {
            lines.Add($"Notes: {notes}");
        }

        if (field.State?.Problem is { } problem)
        {
            lines.Add($"Problem: {problem}");
        }

        lines.Add(field.Description);
        return lines;
    }

    /// <summary>Forgets everything read and typed: the kiosk was locked, or unlocked by someone.</summary>
    public void Reset()
    {
        // What is still on its way was asked for by the person before: its reads are cancelled, and no answer is shown
        // to the next person or ends their Busy. A change already sent is not cancelled; only its answer is dropped. The
        // old source is not disposed: those requests still hold its token, and a source without a timer holds nothing to
        // free.
        _reset.Cancel();
        _reset = new CancellationTokenSource();
        Form = null;
        Busy = null;
        Message = null;
        Group = null;
        Field = null;
        Question = null;
        CloseEditor();
        Raise();
    }

    /// <summary>Reads the catalogue and the settings. The group and setting shown stay shown while they still exist.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Busy is not null)
        {
            return;
        }

        var reset = _reset.Token;
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, reset);
        Busy = "Reading the settings…";
        Raise();
        try
        {
            var catalogue = await _console.Client.Settings.GetCatalogueAsync(reading.Token);
            var settings = await _console.Client.Settings.GetAsync(reading.Token);
            if (reset.IsCancellationRequested)
            {
                return;
            }

            Show(RoofSettingsForm.Create(catalogue, settings));
            Message = null;
        }
        catch (Exception) when (reset.IsCancellationRequested)
        {
            // Reset: the page is someone else's now.
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            Message = new KioskNotice($"The settings could not be read. {RoofText.DescribeFailure(error)}", KioskNoticeLevel.Danger, Now);
        }
        finally
        {
            Done(reset);
        }
    }

    /// <summary>Shows a group's settings, or the list of groups for null.</summary>
    public void SelectGroup(string? name)
    {
        Group = name is null ? null : Form?.FindGroup(name);
        Field = null;
        Question = null;
        CloseEditor();
        Raise();
    }

    /// <summary>Shows a setting's details, or none for null.</summary>
    public void SelectField(string? key)
    {
        Field = key is null ? null : Form?.FindField(key);
        if (Field is not null && Group?.Fields.Contains(Field) != true)
        {
            Group = Form!.Groups.First(group => group.Fields.Contains(Field));
        }

        Question = null;
        CloseEditor();
        Raise();
    }

    /// <summary>Opens the editor for the setting shown. A read-only setting, or a secret, says why instead.</summary>
    public void BeginEdit()
    {
        if (Field is not { } field || Busy is not null)
        {
            return;
        }

        if (field.Setting.Secret)
        {
            Message = new KioskNotice(SecretsElsewhere, KioskNoticeLevel.Info, Now);
        }
        else if (!field.CanWrite)
        {
            Message = new KioskNotice($"{field.Label} is read-only: {field.ReadOnlyReason}", KioskNoticeLevel.Warning, Now);
        }
        else
        {
            Editing = field;
            EditText = field.EditText;
            EditError = null;
            Shift = false;
            (EditorKind, Choices) = field.Setting.Type switch
            {
                RoofSettingType.Boolean => (KioskEditorKind.Choices, WithNoValue(field, ["true", "false"])),
                RoofSettingType.Enum => (KioskEditorKind.Choices, WithNoValue(field, field.Setting.AllowedValues ?? [])),
                RoofSettingType.String => (KioskEditorKind.Keyboard, (IReadOnlyList<string>)[]),
                _ => (KioskEditorKind.Keypad, [])
            };
        }

        Raise();
    }

    /// <summary>Types keys at the end of the value.</summary>
    public void Type(string keys)
    {
        if (Editing is null || Busy is not null || string.IsNullOrEmpty(keys))
        {
            return;
        }

        EditText += Shift ? keys.ToUpperInvariant() : keys;
        Shift = false;
        EditError = null;
        Raise();
    }

    /// <summary>
    /// Sets the whole value, as a text box does while the person types in it (<see cref="KioskWording.KeyboardTyping"/>).
    /// <see cref="Changed"/> is not raised: the box already shows the text, and a page rebuilt under the typing would lose
    /// the keyboard's place.
    /// </summary>
    public void SetText(string? text)
    {
        if (Editing is null || Busy is not null)
        {
            return;
        }

        EditText = text ?? string.Empty;
        Shift = false;
        EditError = null;
    }

    /// <summary>Removes the last key typed.</summary>
    public void Backspace()
    {
        if (Editing is null || Busy is not null || EditText.Length == 0)
        {
            return;
        }

        EditText = EditText[..^1];
        EditError = null;
        Raise();
    }

    /// <summary>Empties the value: no value for a nullable setting, an empty string for a string.</summary>
    public void ClearText()
    {
        if (Editing is null || Busy is not null)
        {
            return;
        }

        EditText = string.Empty;
        EditError = null;
        Raise();
    }

    /// <summary>Turns capitals on or off for the next key.</summary>
    public void ToggleShift()
    {
        Shift = !Shift;
        Raise();
    }

    /// <summary>Closes the editor without sending anything.</summary>
    public void CancelEdit()
    {
        CloseEditor();
        Question = null;
        Raise();
    }

    /// <summary>Chooses one of <see cref="Choices"/> and sends it, as <see cref="ReviewAsync"/>.</summary>
    public Task ChooseAsync(string choice)
    {
        if (Editing is null || !Choices.Contains(choice))
        {
            return Task.CompletedTask;
        }

        EditText = choice == NoValue ? string.Empty : choice;
        return ReviewAsync();
    }

    /// <summary>
    /// Checks the value typed and sends it: says when it changes nothing, and asks first when the change is
    /// safety-critical.
    /// </summary>
    public Task ReviewAsync()
    {
        if (Editing is not { } field || Form is not { } form || Busy is not null)
        {
            return Task.CompletedTask;
        }

        var group = form.Groups.First(candidate => candidate.Fields.Contains(field));
        var edit = form.Edit(group.Name);
        if (!edit.TrySet(field.Key, EditText, out var error))
        {
            EditError = error;
            Raise();
            return Task.CompletedTask;
        }

        var changes = RoofSettingsText.DescribeChanges(edit);
        if (changes.Count == 0)
        {
            CloseEditor();
            Message = new KioskNotice(RoofSettingsText.NothingToChange, KioskNoticeLevel.Info, Now);
            Raise();
            return Task.CompletedTask;
        }

        if (edit.NeedsConfirmation)
        {
            Question = new KioskQuestion(
                "Safety-critical change",
                [$"[{group.Title}]", .. changes, RoofSettingsText.SafetyCriticalChange],
                [new KioskAnswer("Confirm and send", () => SendAsync(edit, confirm: true))]);
            Raise();
            return Task.CompletedTask;
        }

        return SendAsync(edit, confirm: false);
    }

    /// <summary>Drops the question without sending anything.</summary>
    public void Dismiss()
    {
        Question = null;
        Raise();
    }

    /// <summary>
    /// Shows a pending hand edit of the settings file with what applying or discarding it asks, for an admin; anyone
    /// else is told it needs the admin role.
    /// </summary>
    public void ReviewHandEdit()
    {
        if (Form is not { } form || Busy is not null)
        {
            Message = new KioskNotice(NotReadYet, KioskNoticeLevel.Warning, Now);
            Raise();
            return;
        }

        CloseEditor();
        if (form.PendingHandEdit is not { } pending)
        {
            // The controller shows a hand edit only to admins: for anyone else, "none is pending" would be a guess.
            Message = _console.View.IsAdmin
                ? new KioskNotice("No hand edit is pending: the settings file matches what the controller uses.", KioskNoticeLevel.Info, Now)
                : new KioskNotice(RoofSettingsText.HandEditNeedsAdmin(form), KioskNoticeLevel.Warning, Now);
            Raise();
            return;
        }

        var request = new RoofSettingsHandEditRequest { Token = pending.Token };
        Question = new KioskQuestion(
            "Hand edit",
            [.. RoofSettingsText.DescribeHandEdit(form, pending)],
            [
                new KioskAnswer(pending.RequiresConfirmation ? "Confirm and apply" : "Apply", () => HandEditAsync(
                    "Applying the hand edit…",
                    "Applied the hand edit",
                    "The hand edit could not be applied.",
                    cancellationToken => _console.Client.Settings.ApplyHandEditAsync(
                        request with { ConfirmSafetyCriticalChange = pending.RequiresConfirmation },
                        cancellationToken))),
                new KioskAnswer("Discard", () =>
                {
                    Question = new KioskQuestion(
                        "Discard the hand edit",
                        [RoofSettingsText.DiscardHandEditQuestion],
                        [new KioskAnswer("Discard it", () => HandEditAsync(
                            "Discarding the hand edit…",
                            "Discarded the hand edit",
                            "The hand edit could not be discarded.",
                            cancellationToken => _console.Client.Settings.DiscardHandEditAsync(request, cancellationToken)))]);
                    Raise();
                    return Task.CompletedTask;
                })
            ]);
        Raise();
    }

    private DateTimeOffset Now => _console.Client.Options.TimeProvider.GetUtcNow();

    private static IReadOnlyList<string> WithNoValue(RoofSettingsFormField field, IReadOnlyList<string> values)
        => field.Setting.Nullable ? [.. values, NoValue] : values;

    private async Task SendAsync(RoofSettingsEdit edit, bool confirm)
    {
        var request = edit.ToRequest(confirm);
        var needsRestart = edit.NeedsRestart;
        var reset = _reset.Token;
        Question = null;
        Busy = "Saving…";
        Raise();
        try
        {
            var saved = await _console.Client.Settings.UpdateAsync(edit.Group.Name, request);
            // The screen timeout may be what changed, whoever is at the kiosk now.
            _console.RefreshScreenTimeout();
            if (reset.IsCancellationRequested)
            {
                return;
            }

            CloseEditor();
            var notes = new List<string> { $"Saved (settings version {saved.Version})." };
            // The controller's warnings say why the settings are in memory; this note is for when it gives no reason.
            if (!saved.FileBacked && saved.Warnings.Count == 0)
            {
                notes.Add(RoofSettingsText.InMemoryOnly);
            }

            if (needsRestart)
            {
                notes.Add("Part of it applies after a restart (System).");
            }

            notes.AddRange(saved.Warnings.Select(warning => $"Warning: {warning}"));
            var level = saved.Warnings.Count > 0 || !saved.FileBacked ? KioskNoticeLevel.Warning : KioskNoticeLevel.Info;
            Message = new KioskNotice(string.Join(' ', notes), level, Now);
            await ReadAgainAsync(null, saved, reset);
        }
        catch (Exception error)
        {
            if (!reset.IsCancellationRequested)
            {
                await RefusedAsync($"{edit.Group.Title} could not be saved.", error, alsoReadAgainFor: RoofControllerErrorCode.SettingsHandEditPending, reset);
            }
        }
        finally
        {
            Done(reset);
        }
    }

    private async Task HandEditAsync(string busy, string done, string failed, Func<CancellationToken, Task<RoofSettingsResponse>> send)
    {
        var reset = _reset.Token;
        Question = null;
        Busy = busy;
        Raise();
        try
        {
            var saved = await send(CancellationToken.None);
            // Applying a hand edit may change the screen timeout, whoever is at the kiosk now.
            _console.RefreshScreenTimeout();
            if (reset.IsCancellationRequested)
            {
                return;
            }

            Message = new KioskNotice(
                string.Join(' ', saved.Warnings.Select(warning => $"Warning: {warning}").Prepend($"{done} (settings version {saved.Version}).")),
                saved.Warnings.Count > 0 ? KioskNoticeLevel.Warning : KioskNoticeLevel.Info,
                Now);
            await ReadAgainAsync(saved, null, reset);
        }
        catch (Exception error)
        {
            if (!reset.IsCancellationRequested)
            {
                await RefusedAsync(failed, error, alsoReadAgainFor: null, reset);
            }
        }
        finally
        {
            Done(reset);
        }
    }

    /// <summary>
    /// Shows the settings after a change: <paramref name="settings"/> when the change answered with all of them, else
    /// read again. A failure to read keeps the change's message and says the page is from before it.
    /// </summary>
    private async Task ReadAgainAsync(RoofSettingsResponse? settings, RoofSettingsResponse? saved, CancellationToken reset)
    {
        try
        {
            var catalogue = await _console.Client.Settings.GetCatalogueAsync(reset);
            var form = RoofSettingsForm.Create(catalogue, settings ?? await _console.Client.Settings.GetAsync(reset));
            if (!reset.IsCancellationRequested)
            {
                Show(form);
            }
        }
        catch (Exception error)
        {
            if (reset.IsCancellationRequested)
            {
                return;
            }

            var done = Message?.Text ?? $"Saved (settings version {saved?.Version}).";
            Message = new KioskNotice(
                $"{done} The settings could not be read again. {RoofText.DescribeFailure(error)} They are shown as they were before the change.",
                KioskNoticeLevel.Warning,
                Now);
        }
    }

    /// <summary>Says why a change failed; after a version conflict (or a hand edit found pending) reads the settings again.</summary>
    private async Task RefusedAsync(string failed, Exception error, RoofControllerErrorCode? alsoReadAgainFor, CancellationToken reset)
    {
        var message = $"{failed} {RoofText.DescribeFailure(error)}";
        Message = new KioskNotice(message, KioskNoticeLevel.Danger, Now);
        if (error is RoofApiException { Code: { } code } && (code == RoofControllerErrorCode.ConfigurationVersionConflict || code == alsoReadAgainFor))
        {
            CloseEditor();
            try
            {
                var catalogue = await _console.Client.Settings.GetCatalogueAsync(reset);
                var form = RoofSettingsForm.Create(catalogue, await _console.Client.Settings.GetAsync(reset));
                if (!reset.IsCancellationRequested)
                {
                    Show(form);
                    Message = new KioskNotice($"{message} {ReadAgainAfterRefusal}", KioskNoticeLevel.Danger, Now);
                }
            }
            catch (Exception readError)
            {
                if (reset.IsCancellationRequested)
                {
                    return;
                }

                Message = new KioskNotice(
                    $"{message} The settings could not be read again. {RoofText.DescribeFailure(readError)}",
                    KioskNoticeLevel.Danger,
                    Now);
            }
        }
    }

    private void Show(RoofSettingsForm form)
    {
        Form = form;
        Group = Group is null ? null : form.FindGroup(Group.Name);
        Field = Field is null ? null : form.FindField(Field.Key);
        if (Editing is not null)
        {
            Editing = form.FindField(Editing.Key) is { CanWrite: true } editing ? editing : null;
        }
    }

    /// <summary>Ends what was on its way, unless <see cref="Reset"/> came first (the page is someone else's then).</summary>
    private void Done(CancellationToken reset)
    {
        if (!reset.IsCancellationRequested)
        {
            Busy = null;
            Raise();
        }
    }

    private void CloseEditor()
    {
        Editing = null;
        EditText = string.Empty;
        EditError = null;
        Choices = [];
        Shift = false;
    }

    private void Raise() => Changed?.Invoke();
}
