using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The kiosk's system page: the controller's health and readiness, and for an admin its version, host and resource use,
/// and a restart. It says and asks what the CLI and the web UI do.
/// </summary>
/// <remarks>Used on the UI thread only; <see cref="Changed"/> is raised there.</remarks>
public sealed class KioskSystemPanel
{
    public const string InformationNeedsAdmin = "The version, host and resource use need the Admin role.";

    public const string RestartNeedsAdmin = "Restarting the controller needs the Admin role.";

    private readonly KioskConsole _console;

    public KioskSystemPanel(KioskConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    /// <summary>The overall health ("Healthy"…), or null before it is read.</summary>
    public string? Health { get; private set; }

    /// <summary>Each health check: name, status and description.</summary>
    public IReadOnlyList<(string Name, string Status, string Description)> Checks { get; private set; } = [];

    /// <summary>The version, host and resource use (admin), as label and value.</summary>
    public IReadOnlyList<(string Label, string Value)> Information { get; private set; } = [];

    /// <summary>What is on its way to the controller, or null.</summary>
    public string? Busy { get; private set; }

    /// <summary>What the last request came to, or null.</summary>
    public KioskNotice? Message { get; private set; }

    /// <summary>A question to answer before the restart is sent, or null.</summary>
    public KioskQuestion? Question { get; private set; }

    /// <summary>Raised after anything here changed.</summary>
    public event Action? Changed;

    /// <summary>Forgets everything read: the kiosk was locked.</summary>
    public void Reset()
    {
        Health = null;
        Checks = [];
        Information = [];
        Busy = null;
        Message = null;
        Question = null;
        Raise();
    }

    /// <summary>Reads the health report and, for an admin, the version, host and resource use.</summary>
    public Task LoadAsync() => RunAsync("Reading the controller's health…", async () =>
    {
        var report = await _console.Client.Health.GetReportAsync();
        IReadOnlyList<(string Label, string Value)> information = [];
        if (_console.View.IsAdmin)
        {
            var info = await _console.Client.System.GetInformationAsync();
            var metrics = await _console.Client.System.GetMetricsAsync();
            information = [.. RoofSystemText.DescribeInformation(info, metrics)];
        }

        Health = report.Status;
        Checks = [.. report.Checks.Select(check => (check.Name, check.Status, check.Description ?? string.Empty))];
        Information = information;
        Message = new KioskNotice($"Health: {report.Status}.", report.Status == "Healthy" ? KioskNoticeLevel.Info : KioskNoticeLevel.Warning, Now);
    });

    /// <summary>Asks whether the controller is ready (anonymous).</summary>
    public Task CheckReadinessAsync() => RunAsync("Asking whether the controller is ready…", async () =>
    {
        var result = await _console.Client.Health.GetReadinessAsync();
        Message = result.IsHealthy
            ? new KioskNotice($"Ready: {result.Status}.", KioskNoticeLevel.Info, Now)
            : new KioskNotice($"Not ready: {result.Status}.", KioskNoticeLevel.Danger, Now);
    });

    /// <summary>
    /// Reads the settings for a pending hand edit (a restart would load it), then asks before restarting. Only an admin
    /// is asked.
    /// </summary>
    public Task AskRestartAsync()
    {
        if (!_console.View.IsAdmin)
        {
            Message = new KioskNotice(RestartNeedsAdmin, KioskNoticeLevel.Warning, Now);
            Raise();
            return Task.CompletedTask;
        }

        return RunAsync("Checking for a pending hand edit…", async () =>
        {
            var catalogue = await _console.Client.Settings.GetCatalogueAsync();
            var form = RoofSettingsForm.Create(catalogue, await _console.Client.Settings.GetAsync());
            List<string> lines = [RoofSystemText.RestartQuestion];
            if (form.PendingHandEdit is { } pending)
            {
                lines.Add(RoofSystemText.RestartLoadsHandEdit);
                lines.AddRange(RoofSettingsText.DescribeHandEdit(form, pending));
            }

            var confirm = form.PendingHandEdit?.RequiresConfirmation == true;
            Question = new KioskQuestion(
                "Restart the controller",
                lines,
                [new KioskAnswer(confirm ? "Confirm and restart" : "Restart", () => RestartAsync(confirm))]);
        });
    }

    /// <summary>Drops the question without sending anything.</summary>
    public void Dismiss()
    {
        Question = null;
        Raise();
    }

    private DateTimeOffset Now => _console.Client.Options.TimeProvider.GetUtcNow();

    private Task RestartAsync(bool confirm)
    {
        Question = null;
        return RunAsync("Restarting: stopping the roof and verifying the stop…", async () =>
        {
            var restart = await _console.Client.System.RestartAsync(confirm);
            Message = new KioskNotice($"{restart.Message} {RoofSystemText.RestartComingBack}", KioskNoticeLevel.Info, Now);
        });
    }

    private async Task RunAsync(string busy, Func<Task> send)
    {
        if (Busy is not null)
        {
            return;
        }

        Busy = busy;
        Raise();
        try
        {
            await send();
        }
        catch (Exception error)
        {
            Message = new KioskNotice(RoofText.DescribeFailure(error), KioskNoticeLevel.Danger, Now);
        }
        finally
        {
            Busy = null;
            Raise();
        }
    }

    private void Raise() => Changed?.Invoke();
}
