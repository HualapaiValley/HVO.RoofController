using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>Optional Plymouth configuration through the installer's embedded, testable boot-file helper.</summary>
public sealed class KioskSplashStep(KioskSplashSettings settings, int? rotation = null, string? card = null) : PlanStep
{
    public override StepKind Kind => StepKind.File;
    public override string Target => "/etc/hvo-roof-kiosk/splash/state.json";
    public override string Purpose => "optional image splash, completed boot images, and bounded console handoff; no reboot";

    private CommandLine Command(InstallContext context, bool apply)
    {
        var configured = settings.Enabled ? context.Machine.ReadText(KioskSteps.SettingsFile) : null;
        var kiosk = configured is null ? null : JsonNode.Parse(configured, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?["Kiosk"];
        var angle = rotation ?? kiosk?["Rotation"]?.GetValue<int>() ?? (!context.Survey.IsPi || context.Answers.Roles.Contains(Roles.InstallRole.Rig) ? 0 : 90);
        var display = card ?? kiosk?["Card"]?.GetValue<string>();
        string[] names = ["theme.script", "display-gate.py", "handoff.sh"];
        var assets = names.ToDictionary(name => name, name => KioskSteps.Resource("splash/" + name));
        var payload = JsonSerializer.Serialize(new { enabled = settings.Enabled, image = settings.Image, rotation = angle, card = display, root = context.Machine.Root, assets });
        return new CommandLine("python3", "-", apply ? "--apply" : "--check")
        {
            Input = "import io, sys\nsys.stdin = io.StringIO(" + JsonSerializer.Serialize(payload) + ")\n" + KioskSteps.Resource("splash/setup.py"),
            Timeout = TimeSpan.FromMinutes(30),
            OnOutputLine = apply ? context.Progress : null
        };
    }

    public override async Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        try
        {
            var result = await context.Machine.Commands.RunAsync(Command(context, false), cancellationToken).ConfigureAwait(false);
            return result.ExitCode switch
            {
                CommandResult.NotFound => new StepCheck(StepChange.Blocked, "Splash setup requires Python 3. On Raspberry Pi OS or Ubuntu run sudo apt-get update && sudo apt-get install python3, then rerun the installer."),
                0 => StepCheck.Unchanged(result.Output.Trim()),
                10 => new StepCheck(StepChange.Change, result.Output.Trim()),
                _ => new StepCheck(StepChange.Blocked, result.Reason)
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return new StepCheck(StepChange.Blocked, "Correct the kiosk's JSON settings before configuring its splash.");
        }
    }

    public override async Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        await KioskSteps.Run(context, "configure the kiosk splash", Command(context, true), cancellationToken).ConfigureAwait(false);
    }
}
