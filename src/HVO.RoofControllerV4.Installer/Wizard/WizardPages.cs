using System.Globalization;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Roles;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Installer.Wizard;

/// <summary>One page of the wizard.</summary>
internal abstract class WizardPage : View
{
    protected WizardPage(InstallerWizard wizard, string title)
    {
        Wizard = wizard;
        PageTitle = title;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
    }

    public InstallerWizard Wizard { get; }

    public InstallerSession Session => Wizard.Session;

    public string PageTitle { get; }

    public virtual string NextLabel => "Next";

    public virtual bool CanGoNext => true;

    public virtual bool CanGoBack => true;

    /// <summary>Called each time the page is shown: it gets ready for what the earlier pages chose.</summary>
    public virtual void Showing()
    {
    }

    /// <summary>Takes the page's answers when Next is pressed: null to go on, or why not.</summary>
    public virtual string? Leave() => null;

    /// <summary>The page as text, for tests and the log.</summary>
    public abstract IReadOnlyList<string> Describe();

    protected static Label Wrapping(Label label)
    {
        label.Width = Dim.Fill();
        label.Height = Dim.Auto(DimAutoStyle.Text, minimumContentDim: 1);
        label.TextFormatter.WordWrap = true;
        return label;
    }

    /// <summary>Clears what the wizard said about the page's answers once the person changes one: it no longer applies.</summary>
    protected void Edited() => Wizard.Say(string.Empty);

    /// <summary>Lines to read, wrapped at the page's width.</summary>
    protected static ReadingView Lines(Pos top, int bottom = 0)
        => new() { X = 0, Y = top, Width = Dim.Fill(), Height = Dim.Fill(bottom) };
}

/// <summary>Step 1: what the installer found on this machine.</summary>
internal sealed class MachinePage : WizardPage
{
    private readonly IReadOnlyList<string> _lines;
    private readonly string? _problem;

    public MachinePage(InstallerWizard wizard)
        : base(wizard, "This machine")
    {
        _problem = RoleGuards.PlatformProblem(Session.Survey);
        var intro = Wrapping(new Label
        {
            Text = _problem ?? "The installer looked at this machine and changed nothing. Next chooses what to install; nothing is installed before you review the plan.",
            X = 0,
            Y = 0
        });
        intro.SetScheme(_problem is null ? Wizard.Theme.Base : Wizard.Theme.Danger);
        _lines = InstallerSession.DescribeSurvey(Session.Survey, Session.Time.GetUtcNow());
        var text = Lines(Pos.Bottom(intro) + 1);
        text.Show(_lines);
        Add(intro, text);
    }

    public override bool CanGoNext => _problem is null;

    public override IReadOnlyList<string> Describe() => _problem is null ? _lines : [_problem, .. _lines];
}

/// <summary>Step 2: the roles to install, each offered only where this machine can have it.</summary>
internal sealed class RolesPage : WizardPage
{
    private readonly Dictionary<InstallRole, CheckBox> _choices = [];
    private readonly Label _confirmPrompt;
    private readonly TextField _confirmation;

    public RolesPage(InstallerWizard wizard)
        : base(wizard, "What to install")
    {
        var intro = Wrapping(new Label { Text = "Choose what this machine is for. Roles this machine cannot have say why.", X = 0, Y = 0 });
        Add(intro);
        View previous = intro;
        var chosen = Session.Answers.Roles;
        foreach (var option in Session.Options)
        {
            var box = new CheckBox
            {
                Text = InstallRoles.Label(option.Role),
                X = 0,
                Y = Pos.Bottom(previous) + (previous == intro ? 1 : 0),
                Enabled = option.Available,
                Value = option.Available && chosen.Contains(option.Role) ? CheckState.Checked : CheckState.UnChecked
            };
            box.ValueChanged += (_, _) =>
            {
                UpdateConfirmation();
                Edited();
            };
            _choices[option.Role] = box;
            Add(box);
            previous = box;
            if (option.Reason is { } reason)
            {
                var why = new Label { Text = reason, X = 4, Y = Pos.Bottom(box), Width = Dim.Fill(), Height = Dim.Auto(DimAutoStyle.Text, minimumContentDim: 1) };
                why.TextFormatter.WordWrap = true;
                why.SetScheme(Wizard.Theme.Warning);
                Add(why);
                previous = why;
            }
        }

        _confirmPrompt = Wrapping(new Label { Text = RoleGuards.RigConfirmationPrompt(Session.Survey), X = 0, Y = Pos.Bottom(previous) + 1 });
        _confirmPrompt.SetScheme(Wizard.Theme.Warning);
        _confirmation = new TextField { X = 0, Y = Pos.Bottom(_confirmPrompt), Width = 40, Text = Session.Answers.RigConfirmation ?? string.Empty };
        _confirmation.TextChanged += (_, _) => Edited();
        Add(_confirmPrompt, _confirmation);
        UpdateConfirmation();
    }

    public IReadOnlyDictionary<InstallRole, CheckBox> Choices => _choices;

    public TextField Confirmation => _confirmation;

    public IReadOnlyList<InstallRole> Chosen
        => InstallRoles.Ordered(_choices.Where(choice => choice.Value.Value == CheckState.Checked).Select(choice => choice.Key));

    public override string? Leave()
    {
        var roles = Chosen;
        Session.Answers = (Session.Answers with
        {
            Roles = roles,
            RigConfirmation = _confirmation.Visible ? _confirmation.Text : null
        }).Normalised(Session.DefaultController);
        var problems = Session.Problems();
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    public override IReadOnlyList<string> Describe()
    {
        var lines = Session.Options.Select(option =>
            $"[{(_choices[option.Role].Value == CheckState.Checked ? "x" : " ")}] {InstallRoles.Label(option.Role)}{(option.Reason is null ? string.Empty : $" (not here: {option.Reason})")}").ToList();
        if (_confirmation.Visible)
        {
            lines.Add(_confirmPrompt.Text);
        }

        return lines;
    }

    private void UpdateConfirmation()
    {
        var needed = RoleGuards.NeedsRigConfirmation(Session.Survey, Chosen);
        _confirmPrompt.Visible = needed;
        _confirmation.Visible = needed;
    }
}

/// <summary>Step 3: the questions of the roles chosen, with the recorded (or default) answers filled in.</summary>
internal sealed class SettingsPage : WizardPage
{
    private static readonly ConnectionMode[] Connections = Enum.GetValues<ConnectionMode>();

    private OptionSelector? _connection;
    private TextField? _httpsPort;
    private TextField? _httpPort;
    private TextField? _webPort;
    private TextField? _hostNames;
    private TextField? _domains;
    private Label? _namesHint;
    private Label? _httpPrompt;
    private TextField? _httpConfirmation;
    private OptionSelector? _cliFolder;
    private OptionSelector? _macAppFolder;
    private readonly List<string> _description = [];

    public SettingsPage(InstallerWizard wizard)
        : base(wizard, "Choices")
    {
    }

    public OptionSelector? Connection => _connection;

    public TextField? HttpsPort => _httpsPort;

    public TextField? HttpPort => _httpPort;

    public TextField? WebPort => _webPort;

    /// <summary>Other short names clients use for the controller, separated by spaces.</summary>
    public TextField? HostNames => _hostNames;

    /// <summary>The domains clients reach it under, separated by spaces.</summary>
    public TextField? Domains => _domains;

    /// <summary>Where the person types http to confirm plain HTTP; shown only when that needs confirming.</summary>
    public TextField? HttpConfirmation => _httpConfirmation;

    public OptionSelector? CliFolder => _cliFolder;

    public OptionSelector? MacAppFolder => _macAppFolder;

    public override void Showing()
    {
        foreach (var view in SubViews.ToArray())
        {
            Remove(view);
            view.Dispose();
        }

        _connection = _cliFolder = _macAppFolder = null;
        _httpsPort = _httpPort = _webPort = _hostNames = _domains = _httpConfirmation = null;
        _namesHint = _httpPrompt = null;
        _description.Clear();
        var answers = Session.Answers.Normalised();
        View? previous = null;

        if (answers.Controller is { } controller)
        {
            previous = Heading(previous, "How the controller is reached");
            _connection = Selector(previous, Connections.Select(ControllerSettings.Describe), Array.IndexOf(Connections, controller.Connection));
            previous = _connection;
            _connection.ValueChanged += (_, _) => UpdateConnection();
            _httpsPort = Field(ref previous, "HTTPS port (the API)", Number(controller.HttpsPort), 8);
            _httpPort = Field(ref previous, "HTTP port (the API)", Number(controller.HttpPort), 8);
            _webPort = Field(ref previous, "Web UI port", Number(controller.WebPort), 8);
            _description.Add($"Connection: {ControllerSettings.Describe(controller.Connection)}; ports {controller.HttpsPort} (HTTPS), {controller.HttpPort} (HTTP), {controller.WebPort} (web UI)");

            previous = Heading(previous, "Names clients use for it");
            _hostNames = Field(ref previous, "Other host names", string.Join(' ', controller.HostNames), 40);
            _domains = Field(ref previous, "Domains", string.Join(' ', controller.Domains), 40);
            _namesHint = Wrapping(new Label { Text = NamesHint(controller.Connection), X = 2, Y = Pos.Bottom(previous) });
            Add(_namesHint);
            previous = _namesHint;
            _description.Add($"Names: {Listed(controller.HostNames)}; domains: {Listed(controller.Domains)}");
        }

        if (answers.Cli is { } cli)
        {
            previous = Heading(previous, "Where hvo-roof goes");
            _cliFolder = Selector(previous, CliSettings.Folders.Select(folder => folder == CliSettings.HomeFolder ? $"{folder} (yours)" : $"{folder} (everyone's; needs sudo to write)"), Index(CliSettings.Folders, cli.Folder));
            previous = _cliFolder;
            _description.Add($"hvo-roof: {cli.Folder}");
        }

        if (answers.MacApp is { } macApp)
        {
            previous = Heading(previous, "Where the Mac app goes");
            _macAppFolder = Selector(previous, MacAppSettings.Folders.Select(folder => folder == MacAppSettings.HomeFolder ? $"{folder} (yours)" : $"{folder} (everyone's)"), Index(MacAppSettings.Folders, macApp.Folder));
            previous = _macAppFolder;
            _description.Add($"Mac app: {macApp.Folder}");
        }

        if (answers.Controller is not null && previous is not null)
        {
            // Last on the page, so the page does not move when it shows.
            _httpPrompt = Wrapping(new Label { Text = RoleGuards.HttpConfirmationPrompt, X = 0, Y = Pos.Bottom(previous) + 1 });
            _httpPrompt.SetScheme(Wizard.Theme.Warning);
            _httpConfirmation = new TextField { X = 0, Y = Pos.Bottom(_httpPrompt), Width = 12, Text = answers.HttpConfirmation ?? string.Empty };
            _httpConfirmation.TextChanged += (_, _) => Edited();
            Add(_httpPrompt, _httpConfirmation);
            UpdateHttpConfirmation();
        }

        if (previous is null)
        {
            Add(Wrapping(new Label { Text = "No questions for these roles.", X = 0, Y = 0 }));
            _description.Add("No questions for these roles.");
        }
    }

    public override string? Leave()
    {
        var answers = Session.Answers.Normalised();
        var problems = new List<string>();
        if (answers.Controller is { } controller && _connection is not null)
        {
            answers = answers with
            {
                Controller = controller with
                {
                    Connection = Connections[Math.Clamp(_connection.Value ?? 0, 0, Connections.Length - 1)],
                    HttpsPort = Port(_httpsPort!, "The HTTPS port", problems) ?? controller.HttpsPort,
                    HttpPort = Port(_httpPort!, "The HTTP port", problems) ?? controller.HttpPort,
                    WebPort = Port(_webPort!, "The web UI port", problems) ?? controller.WebPort,
                    HostNames = Names(_hostNames!.Text),
                    Domains = Names(_domains!.Text)
                },
                HttpConfirmation = _httpConfirmation is { Visible: true } confirmation ? confirmation.Text : null
            };
        }

        if (answers.Cli is not null && _cliFolder is not null)
        {
            answers = answers with { Cli = new CliSettings { Folder = CliSettings.Folders[Math.Clamp(_cliFolder.Value ?? 0, 0, CliSettings.Folders.Count - 1)] } };
        }

        if (answers.MacApp is not null && _macAppFolder is not null)
        {
            answers = answers with { MacApp = new MacAppSettings { Folder = MacAppSettings.Folders[Math.Clamp(_macAppFolder.Value ?? 0, 0, MacAppSettings.Folders.Count - 1)] } };
        }

        problems.AddRange(RoleGuards.Check(Session.Survey, answers.Normalised()));
        if (problems.Count > 0)
        {
            return string.Join(" ", problems);
        }

        Session.Answers = answers.Normalised();
        return null;
    }

    public override IReadOnlyList<string> Describe()
        => _httpConfirmation is { Visible: true } ? [.. _description, _httpPrompt!.Text] : _description;

    /// <summary>The connection chosen now, or null when the controller is not being installed.</summary>
    private ConnectionMode? ChosenConnection
        => _connection is null ? null : Connections[Math.Clamp(_connection.Value ?? 0, 0, Connections.Length - 1)];

    private void UpdateConnection()
    {
        if (ChosenConnection is { } connection && _namesHint is not null)
        {
            _namesHint.Text = NamesHint(connection);
        }

        UpdateHttpConfirmation();
    }

    // The names are those the controller answers to (AllowedHosts), and those on the certificate the installer makes. The
    // domains this machine seems to be in are named, not filled in: each domain listed lets the private CA sign for any
    // name in it.
    private string NamesHint(ConnectionMode connection)
    {
        var host = CertificateNames.ShortName(Session.Survey.HostName) ?? "localhost";
        var hint = $"Separate them with spaces. The controller answers to {host} and each of these, alone, under .local and under each domain";
        hint = connection is ConnectionMode.PrivateCa or ConnectionMode.SelfSigned ? $"{hint}, and its certificate is for them all." : $"{hint}.";
        var suggested = CertificateNames.SuggestedDomains(Session.Machine);
        return suggested.Count == 0 ? hint : $"{hint} This machine is in {string.Join(", ", suggested)}: add a domain only if clients use names in it.";
    }

    private void UpdateHttpConfirmation()
    {
        if (_httpPrompt is null || _httpConfirmation is null)
        {
            return;
        }

        var answers = Session.Answers.Normalised();
        var needed = ChosenConnection is { } connection
            && RoleGuards.NeedsHttpConfirmation(Session.Survey, answers with { Controller = answers.Controller! with { Connection = connection } });
        _httpPrompt.Visible = needed;
        _httpConfirmation.Visible = needed;
    }

    private View Heading(View? previous, string text)
    {
        var label = new Label { Text = text, X = 0, Y = previous is null ? 0 : Pos.Bottom(previous) + 1 };
        label.SetScheme(Wizard.Theme.Header);
        Add(label);
        return label;
    }

    private OptionSelector Selector(View previous, IEnumerable<string> labels, int value)
    {
        var selector = new OptionSelector { X = 2, Y = Pos.Bottom(previous), Labels = labels.ToArray(), Value = Math.Max(0, value) };
        selector.ValueChanged += (_, _) => Edited();
        Add(selector);
        return selector;
    }

    private TextField Field(ref View previous, string label, string value, int width)
    {
        var caption = new Label { Text = $"{label}:", X = 2, Y = Pos.Bottom(previous) + (previous is OptionSelector ? 1 : 0) };
        var field = new TextField { X = 26, Y = Pos.Top(caption), Width = width, Text = value };
        field.TextChanged += (_, _) => Edited();
        Add(caption, field);
        previous = caption;
        return field;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Listed(IReadOnlyList<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);

    // Names as a person types them: separated by spaces, commas or semicolons.
    private static IReadOnlyList<string> Names(string text)
        => text.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int Index(IReadOnlyList<string> folders, string folder) => Math.Max(0, folders.ToList().IndexOf(folder));

    private static int? Port(TextField field, string name, List<string> problems)
    {
        if (int.TryParse(field.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
        {
            return port;
        }

        problems.Add($"{name} must be a number from 1 to 65535, not '{field.Text}'.");
        return null;
    }
}

/// <summary>
/// Step 4: every change the install would make, checked against the machine, and the answers to save for installing the
/// same way elsewhere (hvo-roof-install --answers). Install goes ahead only when nothing blocks the plan.
/// </summary>
internal sealed class ReviewPage : WizardPage
{
    private readonly ReadingView _plan;
    private readonly TextField _savePath;
    private readonly Button _save;
    private IReadOnlyList<string> _lines = [];
    private CheckedPlan? _checked;
    private bool _checking;
    private int _visit;

    public ReviewPage(InstallerWizard wizard)
        : base(wizard, "Review the plan")
    {
        _plan = Lines(0, 3);
        var caption = new Label { Text = "Save these answers (no secrets) to:", X = 0, Y = Pos.AnchorEnd(2) };
        _savePath = new TextField { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(20) };
        _save = Wizard.Theme.Styled(new Button { Text = "Save answers", X = Pos.AnchorEnd(18), Y = Pos.AnchorEnd(1) });
        _save.Accepting += (_, e) =>
        {
            e.Handled = true;
            Save();
        };
        _savePath.Accepting += (_, e) =>
        {
            // Enter in the path saves; it does not install.
            e.Handled = true;
            Save();
        };
        Add(_plan, caption, _savePath, _save);
    }

    public override string NextLabel => "Install";

    public override bool CanGoNext => !_checking && _checked is { IsBlocked: false };

    // Back waits for the check, so the plan Install carries out is the one checked for the answers shown.
    public override bool CanGoBack => !_checking;

    public CheckedPlan? Plan => _checked;

    public TextField SavePath => _savePath;

    public Button SaveButton => _save;

    public override void Showing()
    {
        _savePath.Text = Session.DefaultAnswersPath;
        _checked = null;
        _checking = true;
        var visit = ++_visit;
        Show(["Checking this machine against the plan…"]);
        Wizard.Run(
            () => Session.CheckAsync(),
            (plan, error) =>
            {
                if (visit != _visit)
                {
                    // A check from an earlier visit, for answers that may since have changed.
                    return;
                }

                _checking = false;
                if (error is not null)
                {
                    Show([$"The plan could not be checked: {error.Message}"]);
                    Wizard.Say("Back changes the answers; F10 quits.", error: true);
                }
                else
                {
                    _checked = plan;
                    Show([$"Installing {InstallRoles.Describe(Session.Answers.Roles)} on {Session.Survey.HostName}, {Session.Version}:", string.Empty, .. PlanText.Lines(plan!)]);
                    if (plan!.IsBlocked)
                    {
                        // The first step that blocks it, in view: a long plan would otherwise hide it below.
                        _plan.Reveal(_lines.ToList().FindIndex(line => line.StartsWith($"  {PlanText.Word(StepChange.Blocked)} ", StringComparison.Ordinal)));
                        Wizard.Say("Something here blocks the plan: see above.", error: true);
                    }
                }

                Wizard.Refresh();
            });
    }

    public override string? Leave() => null;

    public override IReadOnlyList<string> Describe() => _lines;

    private void Show(IReadOnlyList<string> lines)
    {
        _lines = lines;
        _plan.Show(lines);
    }

    private void Save()
    {
        try
        {
            var path = Session.SaveAnswers(_savePath.Text);
            Wizard.Say($"Saved: install the same way with hvo-roof-install --answers {path}");
        }
        catch (Exception error) when (error is InstallerException or IOException or UnauthorizedAccessException)
        {
            Wizard.Say($"Not saved: {error.Message}", error: true);
        }
    }
}

/// <summary>Step 5: the install, step by step. It cannot be left until it has finished or stopped.</summary>
internal sealed class InstallingPage : WizardPage
{
    private readonly ReadingView _progress;
    private readonly List<string> _lines = [];

    public InstallingPage(InstallerWizard wizard)
        : base(wizard, "Installing")
    {
        _progress = Lines(0);
        Add(_progress);
    }

    public override bool CanGoNext => false;

    public override bool CanGoBack => false;

    public override void Showing()
    {
        var review = Wizard.Pages.OfType<ReviewPage>().Single();
        _lines.Clear();
        Wizard.Installing = true;
        Add($"Installing {InstallRoles.Describe(Session.Answers.Roles)}…");
        Wizard.Run<bool>(
            async () =>
            {
                await Session.ApplyAsync(review.Plan!, line => Wizard.Post(() => Add(line))).ConfigureAwait(false);
                return true;
            },
            (_, error) =>
            {
                Wizard.Installing = false;
                if (error is null)
                {
                    Wizard.Result = InstallerExitCode.Success;
                }
                else
                {
                    Wizard.Result = error is InstallerException installerError ? installerError.ExitCode : InstallerExitCode.Failed;
                    Wizard.Failure = error is InstallerRefusedException ? error.Message : $"The install stopped: {error.Message}";
                    Session.Log.Write(error is InstallerRefusedException ? $"Refused: {error.Message}" : Wizard.Failure);
                }

                Wizard.ShowPage(Wizard.PageIndex + 1);
            });
    }

    public override IReadOnlyList<string> Describe() => _lines;

    private void Add(string line)
    {
        _lines.Add(line);
        _progress.Show([.. _lines], followEnd: true);
    }
}

/// <summary>Step 6: what was installed, where it is reached and what comes next; or why the install stopped.</summary>
internal sealed class DonePage : WizardPage
{
    private readonly Label _outcome;
    private readonly ReadingView _details;
    private IReadOnlyList<string> _lines = [];

    public DonePage(InstallerWizard wizard)
        : base(wizard, "Done")
    {
        _outcome = Wrapping(new Label { X = 0, Y = 0 });
        _details = Lines(Pos.Bottom(_outcome) + 1);
        Add(_outcome, _details);
    }

    public override string NextLabel => "Quit";

    public override bool CanGoBack => false;

    public override void Showing()
    {
        if (Wizard.Failure is { } failure)
        {
            _outcome.Text = failure;
            _outcome.SetScheme(Wizard.Theme.Danger);
            _lines = Wizard.Result == InstallerExitCode.Refused
                ?
                [
                    "Nothing was changed.",
                    $"The log: {Session.Log.Path}"
                ]
                :
                [
                    "Nothing after that step was changed.",
                    $"The log: {Session.Log.Path}",
                    "Run the installer again to carry on: it changes only what is left."
                ];
        }
        else
        {
            _outcome.Text = "The install finished.";
            _outcome.SetScheme(Wizard.Theme.Open);
            _lines = Session.DoneLines();
        }

        _details.Show(_lines);
    }

    public override string? Leave()
    {
        Wizard.Quit();
        return null;
    }

    public override IReadOnlyList<string> Describe() => [_outcome.Text, .. _lines];
}
