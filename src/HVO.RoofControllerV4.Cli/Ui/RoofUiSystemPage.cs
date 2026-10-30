using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>
/// The controller: its version and resource use (every role, #209), its host's details (admin), its health report, the
/// readiness probe, and a restart (admin). A restart would load a pending hand edit of the settings file, so the prompt
/// shows it first.
/// </summary>
internal sealed class RoofUiSystemPage : RoofUiPage
{
    internal const string RestartQuestion = RoofSystemText.RestartQuestion;

    private readonly TerminalReadingView _lines;
    private string[] _summary = [];
    private string? _unavailable;
    private string[] _information = [];
    private string[] _health = [];
    private bool _loaded;
    private bool? _shownStale;

    public RoofUiSystemPage(RoofTerminalUi ui)
        : base(ui, "System")
    {
        _lines = new TerminalReadingView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true };
        var client = new FrameView { Title = "CLIENT", X = 0, Y = 0, Width = Dim.Percent(40), Height = Dim.Fill(1) };
        var controller = new FrameView { Title = "CONTROLLER / HEALTH", X = Pos.Right(client), Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
        Ui.Theme.SetFrame(client);
        Ui.Theme.SetFrame(controller);
        var local = new TerminalReadingView { Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        local.Show([SelfVersion, "Transport: REST + status hub", "Host details and restart require Admin.", "Readings are last read; stale data is hidden."]);
        client.Add(local);
        _lines.Height = Dim.Fill();
        controller.Add(_lines);
        Add(client, controller);
        var refresh = AddButton("Refresh", null, Reload);
        var ready = AddButton("Readiness", refresh, CheckReadiness);
        RestartButton = AddButton("Restart", ready, Restart);
    }

    public Button RestartButton { get; }

    public override void Shown()
    {
        if (!_loaded)
        {
            Reload();
        }
    }

    public override void StatusChanged()
    {
        if (_shownStale != Ui.IsStale) Show();
    }

    public override void ConnectionChanged()
    {
        _loaded = false;
        _summary = [];
        _unavailable = null;
        _information = [];
        _health = [];
        Show();
        if (ReferenceEquals(Ui.CurrentPage, this))
        {
            Reload();
        }
    }

    public override string Describe() => string.Join('\n', LinesOf(_lines));

    public void Reload()
    {
        _summary = [];
        _unavailable = null;
        _information = [];
        _health = [];
        Show();
        var admin = Ui.Caller is null || IsAdmin;
        _ = Ui.Run("Reading the controller's health…", async (client, cancellationToken) =>
        {
            try
            {
                await ReadAsync(client, admin, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The page says why there is nothing to show; the message line says what failed.
                Ui.Post(() =>
                {
                    _unavailable = RoofSystemText.DescribeUnavailable(error);
                    Show();
                });
                throw;
            }
        });
    }

    /// <summary>
    /// Reads the summary (every role; none from a controller older than it), the host's details for an admin, and the
    /// health report.
    /// </summary>
    private async Task ReadAsync(RoofControllerClient client, bool admin, CancellationToken cancellationToken)
    {
        string[] summary = [];
        try
        {
            summary = Rows(RoofSystemText.DescribeSummary(await client.System.GetSummaryAsync(cancellationToken).ConfigureAwait(false))).Split('\n');
        }
        catch (RoofApiException refused) when (refused.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // An older controller: it has no summary, and an admin still reads the host's details.
        }

        string[] information = [];
        if (admin)
        {
            try
            {
                var info = await client.System.GetInformationAsync(cancellationToken).ConfigureAwait(false);
                var metrics = await client.System.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
                information = Rows(RoofSystemText.DescribeInformation(info, metrics)).Split('\n');
            }
            catch (RoofApiException refused) when (refused.StatusCode == System.Net.HttpStatusCode.Forbidden && summary.Length > 0)
            {
                // A credential whose role was not known: the details are an admin's, the summary is everyone's.
            }
        }

        var report = await client.Health.GetReportAsync(cancellationToken).ConfigureAwait(false);
        var health = Table(
                ["CHECK", "STATUS", "DESCRIPTION"],
                report.Checks.Select(check => (IReadOnlyList<string>)[check.Name, check.Status, check.Description ?? string.Empty]))
            .Prepend($"Health: {report.Status}")
            .ToArray();
        Ui.Post(() =>
        {
            _summary = summary;
            _information = information;
            _health = health;
            _loaded = true;
            Show();
            Ui.Say($"Health: {report.Status}.");
        });
    }

    private static string SelfVersion => $"{RoofCli.CommandName} version: {RoofSystemText.DescribeClient(RoofCli.CommandName, typeof(RoofCli).Assembly).Value}";

    private void Show()
    {
        _shownStale = Ui.IsStale;
        if (Ui.Connection is null)
        {
            SetLines(_lines, [SelfVersion, $"Controller version: unavailable. {RoofSystemText.ControllerNotConfigured} Use Setup (F5)."]);
            return;
        }

        if (Ui.IsStale)
        {
            SetLines(_lines, [SelfVersion, "Controller version: unavailable (controller status is not live)."]);
            return;
        }

        // This program's own version for everyone: it may be older or newer than the controller's.
        string[] summary = _summary.Length > 0 ? ["Controller (last read):", .. _summary]
            : _unavailable is { } why ? ["Controller version: unavailable.", why]
            : ["Controller version: unavailable (not read)."];
        string[] details = !_loaded || _unavailable is not null ? []
            : _information.Length > 0 ? [string.Empty, "Host details (admin, last read):", .. _information]
            : [string.Empty, RoofCli.HostDetailsNeedAdmin];
        SetLines(_lines, [SelfVersion, .. summary, .. details, string.Empty, .. _health]);
    }

    private void CheckReadiness() => _ = Ui.Run("Asking whether the controller is ready…", async (client, cancellationToken) =>
    {
        var result = await client.Health.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
        Ui.Post(() => Ui.Say(result.IsHealthy ? $"Ready: {result.Status}." : $"Not ready: {result.Status}.", error: !result.IsHealthy));
    });

    /// <summary>Reads the settings for a pending hand edit (a restart would load it), then asks.</summary>
    private void Restart()
    {
        if (Ui.Caller is not null && !IsAdmin)
        {
            Ui.Say("Restarting the controller needs the Admin role.", error: true);
            return;
        }

        _ = Ui.Run("Checking for a pending hand edit…", async (client, cancellationToken) =>
        {
            var catalogue = await client.Settings.GetCatalogueAsync(cancellationToken).ConfigureAwait(false);
            var settings = await client.Settings.GetAsync(cancellationToken).ConfigureAwait(false);
            var form = RoofSettingsForm.Create(catalogue, settings);
            Ui.Post(() => AskRestart(form));
        });
    }

    private void AskRestart(RoofSettingsForm form)
    {
        Ui.Say(string.Empty);
        var message = RestartQuestion;
        if (form.PendingHandEdit is { } pending)
        {
            message += $"\n\n{RoofSystemText.RestartLoadsHandEdit}\n"
                + string.Join('\n', RoofSettingsText.DescribeHandEdit(form, pending));
        }

        var confirm = form.PendingHandEdit?.RequiresConfirmation == true;
        RoofUiAction[] actions = [new(confirm ? "Confirm and restart" : "Restart", _ => Send(confirm))];
        Ui.Ask(new RoofUiPrompt("Restart the controller", message, [], actions));
    }

    private string? Send(bool confirm)
    {
        _ = Ui.Run("Restarting: stopping the roof and verifying the stop…", async (client, cancellationToken) =>
        {
            var restart = await client.System.RestartAsync(confirm, cancellationToken).ConfigureAwait(false);
            Ui.Post(() => Ui.Say($"{restart.Message} {RoofSystemText.RestartComingBack}"));
        });
        return null;
    }
}
