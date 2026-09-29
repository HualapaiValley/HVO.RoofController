using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>
/// The controller: its version, host and resource use (admin), its health report, the readiness probe, and a restart
/// (admin). A restart would load a pending hand edit of the settings file, so the prompt shows it first.
/// </summary>
internal sealed class RoofUiSystemPage : RoofUiPage
{
    internal const string RestartQuestion = RoofSystemText.RestartQuestion;

    private readonly ListView _lines;
    private string[] _information = [];
    private string[] _health = [];
    private bool _loaded;

    public RoofUiSystemPage(RoofTerminalUi ui)
        : base(ui, "System")
    {
        _lines = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true };
        Add(_lines);
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

    public override void ConnectionChanged()
    {
        _loaded = false;
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
        var admin = Ui.Caller is null || IsAdmin;
        _ = Ui.Run("Reading the controller's health…", async (client, cancellationToken) =>
        {
            var report = await client.Health.GetReportAsync(cancellationToken).ConfigureAwait(false);
            string[] information;
            if (admin)
            {
                var info = await client.System.GetInformationAsync(cancellationToken).ConfigureAwait(false);
                var metrics = await client.System.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
                information = Rows(RoofSystemText.DescribeInformation(info, metrics)).Split('\n');
            }
            else
            {
                information = ["The version, host and resource use need the Admin role."];
            }

            var health = Table(
                    ["CHECK", "STATUS", "DESCRIPTION"],
                    report.Checks.Select(check => (IReadOnlyList<string>)[check.Name, check.Status, check.Description ?? string.Empty]))
                .Prepend($"Health: {report.Status}")
                .ToArray();
            Ui.Post(() =>
            {
                _information = information;
                _health = health;
                _loaded = true;
                Show();
                Ui.Say($"Health: {report.Status}.");
            });
        });
    }

    private void Show()
    {
        if (Ui.Connection is null)
        {
            SetLines(_lines, ["No controller is configured: use Setup (F5)."]);
            return;
        }

        SetLines(_lines, [.. _information, string.Empty, .. _health]);
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
