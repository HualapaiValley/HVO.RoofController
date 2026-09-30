using System.Globalization;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;
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

    /// <summary>Whether the page has anything to ask for what was chosen: the wizard passes over one that does not.</summary>
    public virtual bool Applies => true;

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

    /// <summary>Removes the page's views, for a page that builds them each time it is shown.</summary>
    protected void Clear()
    {
        foreach (var view in SubViews.ToArray())
        {
            Remove(view);
            view.Dispose();
        }
    }

    protected View Heading(View? previous, string text)
    {
        var label = new Label { Text = text, X = 0, Y = previous is null ? 0 : Pos.Bottom(previous) + 1 };
        label.SetScheme(Wizard.Theme.Header);
        Add(label);
        return label;
    }

    /// <summary>A line or two under <paramref name="previous"/>, in the page's column for answers' captions.</summary>
    protected View Hint(View previous, string text, bool warning = false)
    {
        var hint = Wrapping(new Label { Text = text, X = 2, Y = Pos.Bottom(previous) });
        if (warning)
        {
            hint.SetScheme(Wizard.Theme.Warning);
        }

        Add(hint);
        return hint;
    }

    protected OptionSelector Selector(View previous, IEnumerable<string> labels, int value)
    {
        var selector = new OptionSelector { X = 2, Y = Pos.Bottom(previous), Labels = labels.ToArray(), Value = Math.Max(0, value) };
        selector.ValueChanged += (_, _) => Edited();
        Add(selector);
        return selector;
    }

    protected TextField Field(ref View previous, string label, string value, int width, int column = 26)
    {
        var caption = new Label { Text = $"{label}:", X = 2, Y = Pos.Bottom(previous) + (previous is OptionSelector ? 1 : 0) };
        var field = new TextField { X = column, Y = Pos.Top(caption), Width = width, Text = value };
        field.TextChanged += (_, _) => Edited();
        Add(caption, field);
        previous = caption;
        return field;
    }

    protected CheckBox Check(ref View previous, string text, bool value)
    {
        var box = new CheckBox { Text = text, X = 2, Y = Pos.Bottom(previous), Value = value ? CheckState.Checked : CheckState.UnChecked };
        box.ValueChanged += (_, _) => Edited();
        Add(box);
        previous = box;
        return box;
    }

    protected static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
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
    private CheckBox? _hideCursor;
    private TextField? _pins;
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

    /// <summary>Whether the console's cursor is hidden behind the kiosk.</summary>
    public CheckBox? HideCursor => _hideCursor;

    /// <summary>The people on the controller to give a PIN for the kiosk, separated by spaces.</summary>
    public TextField? Pins => _pins;

    public OptionSelector? CliFolder => _cliFolder;

    public OptionSelector? MacAppFolder => _macAppFolder;

    public override void Showing()
    {
        Clear();
        _connection = _cliFolder = _macAppFolder = null;
        _httpsPort = _httpPort = _webPort = _hostNames = _domains = _httpConfirmation = _pins = null;
        _hideCursor = null;
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

        if (answers.Kiosk is { } kiosk)
        {
            previous = Heading(previous, "The kiosk on the touchscreen");
            _hideCursor = Check(ref previous, "Hide the console's cursor behind it (from the next reboot)", kiosk.HideCursor);
            _pins = Field(ref previous, "Give a PIN to", string.Join(' ', kiosk.Pins), 40);
            previous = Hint(previous, PinsHint());
            _description.Add($"Kiosk: the console's cursor {(kiosk.HideCursor ? "hidden" : "shown")}; PINs for {Listed(kiosk.Pins)}");
        }

        if (answers.Cli is { } cli)
        {
            previous = Heading(previous, "Where hvo-roof goes");
            _cliFolder = Selector(previous, CliSettings.Folders.Select(folder => folder == CliSettings.HomeFolder ? $"{folder} (yours)" : $"{folder} (everyone's: only if you may write to it without sudo)"), Index(CliSettings.Folders, cli.Folder));
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

        if (answers.Kiosk is not null && _hideCursor is not null && _pins is not null)
        {
            answers = answers with { Kiosk = new KioskSettings { HideCursor = _hideCursor.Value == CheckState.Checked, Pins = Names(_pins.Text) } };
        }

        if (answers.Cli is not null && _cliFolder is not null)
        {
            answers = answers with { Cli = new CliSettings { Folder = CliSettings.Folders[Math.Clamp(_cliFolder.Value ?? 0, 0, CliSettings.Folders.Count - 1)] } };
        }

        if (answers.MacApp is not null && _macAppFolder is not null)
        {
            answers = answers with { MacApp = answers.MacApp with { Folder = MacAppSettings.Folders[Math.Clamp(_macAppFolder.Value ?? 0, 0, MacAppSettings.Folders.Count - 1)] } };
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

    private static string Listed(IReadOnlyList<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);

    // Who may be given a PIN: the controller's operators and admins, by the name they sign in with; those without one
    // named, when the installer can read who they are.
    private string PinsHint()
    {
        const string Hint = "Operators and admins already on the controller, by the name they sign in with, separated by spaces. "
            + "Each is asked for a PIN after the review; someone who has one keeps it.";
        IReadOnlyList<IdentityEntry> people;
        try
        {
            people = ControllerIdentity.Read(Session.Machine, ControllerLayout.For(Session.Machine).IdentityFile).Users;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InstallerException)
        {
            return Hint;
        }

        var without = people.Where(person => !person.HasPin && !person.Role.Equals(RoofControllerApiContract.ViewerRole, StringComparison.OrdinalIgnoreCase))
            .Select(person => person.Name).ToList();
        return without.Count == 0 ? Hint : $"{Hint} Without one now: {string.Join(", ", without)}.";
    }

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
/// Step 4, when the controller (or a rig) is installed: its first admin, the camera it shows (or a rig's HAT emulator),
/// where it exports telemetry, and settings from a backup. Each may be left empty. The passwords come after the review,
/// once the plan says which it needs.
/// </summary>
internal sealed class ControllerPage : WizardPage
{
    private TextField? _adminName;
    private CheckBox? _adminPin;
    private TextField? _cameraServer;
    private TextField? _cameraUser;
    private TextField? _timeScale;
    private TextField? _framesPerSecond;
    private CheckBox? _openToLan;
    private TextField? _telemetry;
    private TextField? _importFrom;

    public ControllerPage(InstallerWizard wizard)
        : base(wizard, "The controller")
    {
    }

    public override bool Applies => InstallRoles.RunsController(Session.Answers.Roles);

    public TextField? AdminName => _adminName;

    public CheckBox? AdminPin => _adminPin;

    /// <summary>The Blue Iris server's address; null on a rig, which shows the HAT emulator's camera.</summary>
    public TextField? CameraServer => _cameraServer;

    public TextField? CameraUser => _cameraUser;

    /// <summary>A rig's emulator: how many times as fast as real time the emulated roof runs.</summary>
    public TextField? TimeScale => _timeScale;

    public TextField? FramesPerSecond => _framesPerSecond;

    public CheckBox? OpenToLan => _openToLan;

    public TextField? Telemetry => _telemetry;

    public TextField? ImportFrom => _importFrom;

    public override void Showing()
    {
        Clear();
        _adminName = _cameraServer = _cameraUser = _timeScale = _framesPerSecond = _telemetry = _importFrom = null;
        _adminPin = _openToLan = null;
        var controller = Session.Answers.Normalised().Controller ?? Session.DefaultController;

        var previous = Heading(null, "The first admin, who signs in to the web UI and adds everyone else");
        _adminName = Field(ref previous, "Name", controller.FirstAdmin?.Name ?? string.Empty, 30);
        _adminPin = Check(ref previous, "A PIN too, for signing in at the kiosk", controller.FirstAdmin?.Pin == true);
        previous = Hint(previous, "Added only while the controller has no admin. Empty adds nobody.");

        if (controller.Rig is { } rig)
        {
            previous = Heading(previous, "The HAT emulator");
            _timeScale = Field(ref previous, "Time scale", Decimal(rig.TimeScale), 8);
            Add(After(_timeScale, $"times as fast as real time ({Decimal(RigSettings.MinimumTimeScale)} to {Decimal(RigSettings.MaximumTimeScale)})"));
            _framesPerSecond = Field(ref previous, "Camera frames a second", Decimal(rig.CameraFramesPerSecond), 8);
            Add(After(_framesPerSecond, $"({Decimal(RigSettings.MinimumCameraFramesPerSecond)} to {Decimal(RigSettings.MaximumCameraFramesPerSecond)})"));
            _openToLan = Check(ref previous, "Open to the network: other machines may use the rig (HTTPS only)", rig.OpenToLan);
        }
        else
        {
            previous = Heading(previous, "The camera the controller shows (Blue Iris): empty leaves it as it is");
            _cameraServer = Field(ref previous, "Server", controller.Camera?.BaseUrl ?? string.Empty, 44);
            _cameraUser = Field(ref previous, "View-only user", controller.Camera?.UserName ?? string.Empty, 24);
            previous = Hint(previous, Installer.CameraCredentialReminder, warning: true);
        }

        previous = Heading(previous, "Telemetry, exported over OTLP/HTTP: empty turns the export off");
        _telemetry = Field(ref previous, "Endpoint", controller.TelemetryEndpoint ?? string.Empty, 44);

        previous = Heading(previous, "Settings from a backup, for a controller that has none yet");
        _importFrom = Field(ref previous, "Full path", controller.ImportSettingsFrom ?? string.Empty, 44);
    }

    public override string? Leave()
    {
        var answers = Session.Answers.Normalised();
        var controller = answers.Controller ?? Session.DefaultController;
        var problems = new List<string>();
        CameraSettings? camera = null;
        if (_cameraServer is not null && _cameraUser is not null)
        {
            if (!string.IsNullOrWhiteSpace(_cameraServer.Text))
            {
                camera = new CameraSettings { BaseUrl = _cameraServer.Text, UserName = _cameraUser.Text };
            }
            else if (!string.IsNullOrWhiteSpace(_cameraUser.Text))
            {
                problems.Add("The camera's user needs its server: give the Blue Iris server's address, or clear the user.");
            }
        }

        var rig = controller.Rig is { } current && _timeScale is not null && _framesPerSecond is not null
            ? current with
            {
                TimeScale = Decimal(_timeScale, "The rig's time scale", problems) ?? current.TimeScale,
                CameraFramesPerSecond = Decimal(_framesPerSecond, "The rig's camera frame rate", problems) ?? current.CameraFramesPerSecond,
                OpenToLan = _openToLan?.Value == CheckState.Checked
            }
            : controller.Rig;
        answers = answers with
        {
            Controller = controller with
            {
                FirstAdmin = string.IsNullOrWhiteSpace(_adminName?.Text) ? null : new FirstAdminSettings { Name = _adminName.Text, Pin = _adminPin?.Value == CheckState.Checked },
                Camera = camera,
                TelemetryEndpoint = _telemetry?.Text,
                ImportSettingsFrom = _importFrom?.Text,
                Rig = rig
            }
        };

        if (problems.Count == 0)
        {
            problems.AddRange(RoleGuards.Check(Session.Survey, answers.Normalised()));
        }

        if (problems.Count > 0)
        {
            return string.Join(" ", problems);
        }

        Session.Answers = answers.Normalised();
        return null;
    }

    public override IReadOnlyList<string> Describe()
    {
        var controller = Session.Answers.Normalised().Controller ?? Session.DefaultController;
        var lines = new List<string>
        {
            $"First admin: {(controller.FirstAdmin is { } admin ? $"{admin.Name}{(admin.Pin ? ", with a PIN" : string.Empty)}" : "none")}"
        };
        if (controller.Rig is { } rig)
        {
            lines.Add($"HAT emulator: {Decimal(rig.TimeScale)} times as fast as real time, {Decimal(rig.CameraFramesPerSecond)} camera frames a second; {(rig.OpenToLan ? "open to the network" : "this machine only")}");
        }
        else
        {
            lines.Add($"Camera: {(controller.Camera is { } camera ? $"{camera.BaseUrl}{(camera.UserName is { } user ? $" as {user}" : string.Empty)}" : "as it is")}");
        }

        lines.Add($"Telemetry: {controller.TelemetryEndpoint ?? "off"}");
        lines.Add($"Settings from a backup: {controller.ImportSettingsFrom ?? "none"}");
        return lines;
    }

    // What a field's number means, to its right.
    private static Label After(View field, string text) => new() { Text = text, X = Pos.Right(field) + 1, Y = Pos.Top(field) };

    private static string Decimal(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static double? Decimal(TextField field, string name, List<string> problems)
    {
        if (double.TryParse(field.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        problems.Add($"{name} must be a number, not '{field.Text}'.");
        return null;
    }
}

/// <summary>
/// Step 4, when hvo-roof or the Mac app is installed: the controller they connect to, and how this machine trusts it. Its
/// CA is fetched from it here, and trusted only once the person says its fingerprint is the one the controller shows (its
/// installer's Done page, or <c>hvo-roof-install cert show</c>). The Mac app also needs an admin, who signs in once after
/// the review so the installer can make the Mac's device key.
/// </summary>
internal sealed class ClientPage : WizardPage
{
    private static readonly string[] TrustLabels =
    [
        "Its private CA: fetched from it, and trusted once you check its fingerprint",
        "Its self-signed certificate, pinned to its fingerprint",
        "As this machine trusts any website: a certificate of your own, or plain HTTP"
    ];

    private const int ByCa = 0;
    private const int ByPin = 1;

    private TextField? _address;
    private Button? _fetch;
    private OptionSelector? _trust;
    private TextField? _pin;
    private View? _pinCaption;
    private Label? _authority;
    private CheckBox? _matches;
    private CheckBox? _keychain;
    private TextField? _admin;
    private FetchedCa? _fetched;
    private string? _failure;
    private bool _fetching;
    private int _fetches;

    public ClientPage(InstallerWizard wizard)
        : base(wizard, "Connecting to the controller")
    {
    }

    public override bool Applies => InstallRoles.UsesController(Session.Answers.Roles);

    public override bool CanGoNext => !_fetching;

    public TextField? Address => _address;

    public Button? FetchButton => _fetch;

    /// <summary>How this machine trusts the controller: by its CA, by a pin on its certificate, or as it trusts any website.</summary>
    public OptionSelector? Trust => _trust;

    /// <summary>The fingerprint of a self-signed certificate, to pin.</summary>
    public TextField? Pin => _pin;

    /// <summary>What the fetch found: the CA the controller serves and its fingerprint, or why it could not be fetched.</summary>
    public Label? Authority => _authority;

    /// <summary>Ticked once the person has compared the CA's fingerprint with the one the controller shows.</summary>
    public CheckBox? Matches => _matches;

    /// <summary>On a Mac: trust the CA in the login keychain too, for Safari and Chrome.</summary>
    public CheckBox? Keychain => _keychain;

    /// <summary>The admin who makes the Mac's device key; null when the Mac app is not installed.</summary>
    public TextField? Admin => _admin;

    public override void Showing()
    {
        Clear();
        _address = _pin = _admin = null;
        _pinCaption = null;
        _fetch = null;
        _trust = null;
        _authority = null;
        _matches = _keychain = null;
        _failure = null;
        _fetching = false;
        var answers = Session.Answers.Normalised();
        var client = answers.Client ?? new ClientSettings();
        _fetched = client.CaSha256 is { } recorded ? new FetchedCa(client.Controller, null, recorded) : null;
        var who = Who(answers.Roles);

        View previous = Heading(null, $"The controller {who.Name} {(who.Plural ? "connect" : "connects")} to");
        _address = Field(ref previous, "Address", client.Controller, 44);
        _fetch = Wizard.Theme.Styled(new Button { Text = "Fetch its CA", X = Pos.Right(_address) + 2, Y = Pos.Top(_address) });
        _fetch.Accepting += (_, e) =>
        {
            e.Handled = true;
            Fetch();
        };
        Add(_fetch);
        _address.TextChanged += (_, _) =>
        {
            _failure = null;
            UpdateAuthority();
        };
        previous = Hint(previous, "As this machine reaches it, with its port: https://roof.local:8443.");

        previous = Heading(previous, $"How {who.Name} {(who.Plural ? "trust" : "trusts")} it");
        _trust = Selector(previous, TrustLabels, client.CaSha256 is not null || client.Controller.Length == 0 ? ByCa : client.CertificateSha256 is not null ? ByPin : 2);
        _trust.ValueChanged += (_, _) => UpdateTrust();
        previous = _trust;

        // The pin and the CA each in the same place under the choice: only the one chosen shows.
        _pin = Field(ref previous, "Certificate's SHA-256", client.CertificateSha256 ?? string.Empty, 64);
        _pinCaption = previous;
        _authority = Wrapping(new Label { X = 2, Y = Pos.Top(_pinCaption) });
        Add(_authority);
        previous = _authority;
        _matches = Check(ref previous, "It is the fingerprint the controller shows: trust this CA", _fetched is not null);
        if (Session.Survey.Os == InstallerOs.MacOS)
        {
            _keychain = Check(ref previous, "Trust it in my login keychain too, for Safari and Chrome (macOS asks for your password)", client.TrustInKeychain);
        }

        if (answers.MacApp is { } macApp)
        {
            previous = Heading(previous, "The Mac app's device key");
            _admin = Field(ref previous, "Made by the admin", macApp.Admin, 30);
            previous = Hint(previous, "An admin on the controller, by the name they sign in with. They sign in once, after the review, so the installer "
                + "can make the Mac's own key (a viewer's): it goes in a file only you can read, and is never shown.");
        }

        UpdateAuthority();
        UpdateTrust();
    }

    public override string? Leave()
    {
        var answers = Session.Answers.Normalised();
        var address = _address!.Text.Trim();
        var trust = _trust!.Value ?? ByCa;
        var client = new ClientSettings { Controller = address };
        var problems = new List<string>();
        if (trust == ByCa)
        {
            if (Confirmed(address) is { } fingerprint)
            {
                client = client with { CaSha256 = fingerprint, TrustInKeychain = _keychain?.Value == CheckState.Checked };
            }
            else if (client.Address is not null)
            {
                problems.Add("Fetch the controller's CA, check its fingerprint is the one the controller shows, and tick that it is: "
                    + "or choose another way to trust it.");
            }
        }
        else if (trust == ByPin)
        {
            client = client with { CertificateSha256 = _pin!.Text };
        }

        answers = answers with { Client = client.Normalised() };
        if (answers.MacApp is { } macApp && _admin is not null)
        {
            answers = answers with { MacApp = macApp with { Admin = _admin.Text } };
            if (string.IsNullOrWhiteSpace(_admin.Text))
            {
                problems.Add("Give the admin who makes the Mac's device key, by the name they sign in with.");
            }
        }

        problems.AddRange(client.Problems());
        if (problems.Count == 0)
        {
            problems.AddRange(RoleGuards.Check(Session.Survey, answers.Normalised()));
        }

        if (problems.Count > 0)
        {
            return string.Join(" ", problems.Distinct());
        }

        Session.Answers = answers.Normalised();
        return null;
    }

    public override IReadOnlyList<string> Describe()
    {
        var trust = _trust?.Value ?? ByCa;
        var lines = new List<string> { $"Controller: {_address?.Text.Trim()}", $"Trusted by: {TrustLabels[trust]}" };
        if (trust == ByPin)
        {
            lines.Add($"Certificate's SHA-256: {_pin?.Text}");
        }
        else if (trust == ByCa)
        {
            lines.Add(_authority?.Text ?? string.Empty);
            lines.Add(_matches?.Value == CheckState.Checked ? "Its fingerprint checked: trusted" : "Its fingerprint not checked yet");
            if (_keychain is not null)
            {
                lines.Add($"Login keychain: {(_keychain.Value == CheckState.Checked ? "trusted there too" : "not changed")}");
            }
        }

        if (_admin is not null)
        {
            lines.Add($"The Mac's device key made by: {_admin.Text}");
        }

        return lines;
    }

    // The fingerprint fetched (or recorded) for this address, once the person has ticked that it matches.
    private string? Confirmed(string address)
        => _fetched is { } fetched && fetched.Address == address && _matches?.Value == CheckState.Checked ? fetched.Fingerprint : null;

    private void Fetch()
    {
        var text = _address!.Text.Trim();
        var problem = WebAddress.Problem(text, allowPath: false, "https://roof.local:8443") is { } wrong ? $"The controller's address {wrong}"
            : !text.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? "Only an https address has a CA to fetch."
            : null;
        if (problem is not null)
        {
            Wizard.Say(problem, error: true);
            return;
        }

        var address = new Uri(text);
        var fetch = ++_fetches;
        _fetching = true;
        _fetched = null;
        _failure = null;
        UpdateAuthority();
        _authority!.Text = $"Fetching the CA {address} serves…";
        Wizard.Refresh();
        Wizard.Run(
            async () =>
            {
                using var authority = await Session.Machine.FetchCaAsync(address, CancellationToken.None).ConfigureAwait(false);
                return new FetchedCa(text, RoofCertificateAuthority.Describe(authority), RoofCertificateAuthority.Fingerprint(authority));
            },
            (fetched, error) =>
            {
                if (fetch != _fetches)
                {
                    // A fetch the person has since started again.
                    return;
                }

                _fetching = false;
                if (error is not null)
                {
                    _failure = $"The installer could not fetch the controller's CA from {address}: {ClientTrust.Failure(error)}";
                    Session.Log.Write(_failure);
                }
                else
                {
                    Session.Log.Write($"Fetched the controller's CA from {address}: {fetched!.Name} ({fetched.Fingerprint}).");
                    _fetched = fetched;
                }

                UpdateAuthority();
                if (_fetched is not null)
                {
                    // What to do next: say whether it is the fingerprint the controller shows.
                    _matches!.SetFocus();
                }

                Wizard.Refresh();
            });
    }

    // What is known of the CA at the address typed now, and whether it may be ticked as matching.
    private void UpdateAuthority()
    {
        if (_authority is null || _matches is null || _address is null)
        {
            return;
        }

        var address = _address.Text.Trim();
        var current = _fetched is { } fetched && fetched.Address == address ? fetched : null;
        _authority.Text = current is { Name: { } name } ? $"It serves {name}, whose SHA-256 fingerprint is\n{Halves(current.Fingerprint)}\nDoes it match the controller's installer's Done page, or hvo-roof-install cert show on it?"
            : current is not null ? $"Trusted now: its CA, whose SHA-256 fingerprint is\n{Halves(current.Fingerprint)}\nFetch its CA again to check the one it serves now."
            : _failure ?? "Fetch its CA to see its fingerprint, and compare it with the one the controller shows.";
        _authority.SetScheme(current is null && _failure is not null ? Wizard.Theme.Danger : Wizard.Theme.Base);

        _matches.Enabled = current is not null && _trust?.Value == ByCa;
        if (current is null)
        {
            _matches.Value = CheckState.UnChecked;
        }
    }

    private void UpdateTrust()
    {
        var trust = _trust?.Value ?? ByCa;
        _fetch!.Enabled = trust == ByCa;
        _authority!.Visible = trust == ByCa;
        _matches!.Visible = trust == ByCa;
        _pin!.Visible = trust == ByPin;
        _pinCaption!.Visible = trust == ByPin;
        if (_keychain is not null)
        {
            _keychain.Enabled = trust == ByCa;
        }

        UpdateAuthority();
    }

    // A fingerprint on two lines, indented, so it fits an 80-column terminal: 16 pairs, then 16.
    private static string Halves(string fingerprint)
        => fingerprint.Length == 95 ? $"  {fingerprint[..48]}\n  {fingerprint[48..]}" : $"  {fingerprint}";

    private static (string Name, bool Plural) Who(IReadOnlyList<InstallRole> roles)
        => roles.Contains(InstallRole.Cli) && roles.Contains(InstallRole.MacApp) ? ("hvo-roof and the Mac app", true)
            : roles.Contains(InstallRole.MacApp) ? ("the Mac app", false)
            : ("hvo-roof", false);

    /// <summary>The CA the controller at <paramref name="Address"/> serves; <paramref name="Name"/> is null for one recorded, not fetched.</summary>
    private sealed record FetchedCa(string Address, string? Name, string Fingerprint);
}

/// <summary>
/// Step 5: every change the install would make, checked against the machine, and the answers to save for installing the
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

    public override string NextLabel => Wizard.Pages.OfType<PasswordsPage>().Single().Applies ? "Next" : "Install";

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

/// <summary>
/// Step 6, when the plan needs them: the passwords and PINs no file gave (<c>--admin-password-file</c> and the others),
/// each new one typed twice. They are never shown, saved or logged: Install uses them once.
/// </summary>
internal sealed class PasswordsPage : WizardPage
{
    private const int Column = 30;
    private readonly List<(InstallSecret Secret, TextField Typed, TextField? Again)> _fields = [];
    private readonly List<string> _description = [];
    private bool _given;

    public PasswordsPage(InstallerWizard wizard)
        : base(wizard, "Passwords")
    {
    }

    public override string NextLabel => "Install";

    // From the plan's check on, so the count of steps holds once they are given.
    public override bool Applies => _given || (Review.Plan is { } plan && Session.MissingSecrets(plan).Count > 0);

    /// <summary>Each secret asked for, its field, and the field it is typed again in (none for a password a person has already).</summary>
    public IReadOnlyList<(InstallSecret Secret, TextField Typed, TextField? Again)> Fields => _fields;

    private ReviewPage Review => Wizard.Pages.OfType<ReviewPage>().Single();

    public override void Showing()
    {
        Clear();
        _fields.Clear();
        _description.Clear();
        var missing = Session.MissingSecrets(Review.Plan!);
        var intro = missing.All(secret => secret.IsExisting) ? "It is never shown, saved or logged."
            : missing.Any(secret => secret.IsExisting) ? "Each new one is typed twice. None is shown, saved or logged."
            : "Each is typed twice, and never shown, saved or logged.";
        View previous = Wrapping(new Label { Text = intro, X = 0, Y = 0 });
        Add(previous);
        _description.Add(intro);
        var pinRuleShown = false;
        foreach (var secret in missing)
        {
            var what = InstallSecrets.Describe(secret);
            var typed = Field(ref previous, what, first: true);
            var again = secret.IsExisting ? null : Field(ref previous, "Again", first: false);
            _fields.Add((secret, typed, again));
            _description.Add($"{Capitalised(what)}: {(again is null ? "typed once" : "typed twice")}");

            // The PIN rule once, under the first PIN: the kiosk's people may each have one.
            var rule = secret.Kind == InstallSecretKind.AdminPassword ? RoofIdentityText.PasswordRule
                : secret.IsPin && !pinRuleShown ? RoofIdentityText.PinRule
                : null;
            pinRuleShown |= secret.IsPin;
            if (rule is not null)
            {
                previous = Hint(previous, rule);
            }
        }
    }

    public override string? Leave()
    {
        foreach (var (secret, typed, again) in _fields)
        {
            if (InstallSecrets.Problem(secret, typed.Text) is { } problem)
            {
                return $"{Capitalised(InstallSecrets.Describe(secret))}: {problem}";
            }

            if (again is not null && !string.Equals(typed.Text, again.Text, StringComparison.Ordinal))
            {
                return $"{Capitalised(InstallSecrets.Describe(secret))}: the two differ. Type it again in both.";
            }
        }

        foreach (var (secret, typed, again) in _fields)
        {
            Session.GiveSecret(secret, typed.Text);
            typed.Text = string.Empty;
            if (again is not null)
            {
                again.Text = string.Empty;
            }
        }

        _given = _fields.Count > 0;
        return null;
    }

    public override IReadOnlyList<string> Describe() => _description;

    private TextField Field(ref View previous, string caption, bool first)
    {
        var label = new Label { Text = first ? $"{Capitalised(caption)}:" : $"  {caption}:", X = 0, Y = Pos.Bottom(previous) + (first ? 1 : 0) };
        var field = new TextField { X = Column, Y = Pos.Top(label), Width = 32, Secret = true };
        field.TextChanged += (_, _) => Edited();
        Add(label, field);
        previous = label;
        return field;
    }

    private static string Capitalised(string text) => text.Length == 0 ? text : $"{char.ToUpperInvariant(text[0])}{text[1..]}";
}

/// <summary>Step 7: the install, step by step. It cannot be left until it has finished or stopped.</summary>
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

                Wizard.ShowPage(Wizard.Following()!.Value);
            });
    }

    public override IReadOnlyList<string> Describe() => _lines;

    private void Add(string line)
    {
        _lines.Add(line);
        _progress.Show([.. _lines], followEnd: true);
    }
}

/// <summary>Step 8: what was installed, where it is reached and what comes next; or why the install stopped.</summary>
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
