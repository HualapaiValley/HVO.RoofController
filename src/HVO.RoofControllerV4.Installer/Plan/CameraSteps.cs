using System.Globalization;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The camera the controller shows, as the files its camera proxy reads from the secrets folder when it starts
/// (<c>BlueIris__BaseUrl</c>, <c>__UserName</c> and <c>__Password</c>): a Blue Iris server with an optional view-only
/// user, or on a rig the HAT emulator's camera. Without a camera in the answers, the files are left as they are.
/// </summary>
public static class CameraSteps
{
    public const string BaseUrlSetting = "BlueIris__BaseUrl";
    public const string UserNameSetting = "BlueIris__UserName";
    public const string PasswordSetting = "BlueIris__Password";

    /// <summary>A rig's camera: the HAT emulator's, on its network, which asks for no user.</summary>
    public static string EmulatorCamera { get; } = string.Create(CultureInfo.InvariantCulture, $"http://{MachineSurveyor.HatEmulatorContainer}:{HatEmulatorStep.ControlPort}");

    /// <summary>
    /// The steps for <paramref name="camera"/> (a rig: the emulator's camera); none when there is none. The password
    /// comes before the user, so it still sees the old user when the user changes.
    /// </summary>
    public static IReadOnlyList<CameraFileStep> For(ControllerLayout layout, bool rig, CameraSettings? camera)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (rig)
        {
            return
            [
                new CameraSettingStep(layout, BaseUrlSetting, EmulatorCamera, "the camera the controller shows: the HAT emulator's"),
                new CameraSettingStep(layout, PasswordSetting, string.Empty, "the camera's password: none"),
                new CameraSettingStep(layout, UserNameSetting, string.Empty, "the camera's user: none, the emulator asks for none")
            ];
        }

        if (camera is null)
        {
            return [];
        }

        return camera.UserName is { } user
            ?
            [
                new CameraSettingStep(layout, BaseUrlSetting, camera.BaseUrl, "the Blue Iris server the controller shows the camera from"),
                new CameraPasswordStep(layout, user),
                new CameraSettingStep(layout, UserNameSetting, user, "the Blue Iris user the controller views the camera as")
            ]
            :
            [
                new CameraSettingStep(layout, BaseUrlSetting, camera.BaseUrl, "the Blue Iris server the controller shows the camera from"),
                new CameraSettingStep(layout, PasswordSetting, string.Empty, "the camera's password: none"),
                new CameraSettingStep(layout, UserNameSetting, string.Empty, "the camera's user: none, the server asks for none")
            ];
    }

    /// <summary>
    /// How much later than the controller started a file must have been written to count as written since: Docker
    /// Desktop's clock, which stamps the start on a Mac, can trail the Mac's by a moment.
    /// </summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether the running controller, started at <paramref name="started"/>, has not read the camera's settings as they
    /// are or will be: a step changes one or wrote one in this run, or one was written since it started (by a run that
    /// stopped before it redeployed the controller).
    /// </summary>
    public static async Task<bool> ChangedSinceAsync(InstallContext context, IReadOnlyList<CameraFileStep> steps, DateTimeOffset? started, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(steps);
        foreach (var step in steps)
        {
            if (context.HasApplied(step) && step.WroteValue)
            {
                return true;
            }

            if ((await step.CheckAsync(context, cancellationToken).ConfigureAwait(false)).MakesChange && step.ChangesValue)
            {
                return true;
            }

            if (WrittenSince(context, step.Target, started))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="path"/> was written after a container that started at <paramref name="started"/> read it.</summary>
    public static bool WrittenSince(InstallContext context, string path, DateTimeOffset? started)
    {
        ArgumentNullException.ThrowIfNull(context);
        return started is { } since && context.Machine.LastWritten(path) is { } written && written > since + ClockSlack;
    }
}

/// <summary>
/// One of the files the controller's camera proxy reads when it starts: written straight to a file only root reads,
/// without a newline, since the controller reads each file whole.
/// </summary>
public abstract class CameraFileStep(ControllerLayout layout, string setting) : PlanStep
{
    public sealed override StepKind Kind => StepKind.File;

    public sealed override string Target => Path.Join(layout.Secrets, setting);

    /// <summary>Whether the last check found the file's value must change, not only who may read it.</summary>
    public bool ChangesValue { get; private set; }

    /// <summary>Whether it wrote the file's value, not only its mode.</summary>
    public bool WroteValue { get; private set; }

    public sealed override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ChangesValue = false;
        try
        {
            return Task.FromResult(Check(context));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new StepCheck(StepChange.Info, "only root can read the secrets folder: run with sudo to check it"));
        }
    }

    /// <summary>What the file holds against the value it should, and its mode.</summary>
    protected abstract StepCheck Check(InstallContext context);

    /// <summary>The file against <paramref name="wanted"/>: made, changed, or given the mode only root reads.</summary>
    protected StepCheck Compare(InstallContext context, string wanted, string describe)
    {
        var current = context.Machine.ReadText(Target);
        if (!string.Equals(current, wanted, StringComparison.Ordinal))
        {
            return Changes(current is null ? StepChange.Create : StepChange.Change, describe);
        }

        return ModeCheck(context, describe);
    }

    /// <summary>A change of the file's value.</summary>
    protected StepCheck Changes(StepChange change, string describe)
    {
        ChangesValue = true;
        return new StepCheck(change, describe);
    }

    /// <summary>The file's value is right: whether its mode is.</summary>
    protected StepCheck ModeCheck(InstallContext context, string describe)
        => context.Machine.GetMode(Target) is { } mode && mode != Modes.PrivateFile
            ? new StepCheck(StepChange.Change, $"{Modes.Octal(mode)} → {Modes.Octal(Modes.PrivateFile)}")
            : StepCheck.Unchanged(describe);

    public sealed override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ChangesValue)
        {
            context.Machine.SetMode(Target, Modes.PrivateFile);
            context.Log.Write($"Set {Target} to {Modes.Octal(Modes.PrivateFile)}.");
            return Task.CompletedTask;
        }

        var (value, logged) = Value(context);
        context.Machine.WriteAtomically(Target, value, Modes.PrivateFile);
        WroteValue = true;
        context.Log.Write($"Wrote {Target}: {logged}.");
        return Task.CompletedTask;
    }

    /// <summary>The value to write, and how the log names it.</summary>
    protected abstract (string Value, string Logged) Value(InstallContext context);
}

/// <summary>One of the camera's settings that is not secret: its server, or its user.</summary>
public sealed class CameraSettingStep(ControllerLayout layout, string setting, string value, string purpose) : CameraFileStep(layout, setting)
{
    public override string Purpose => purpose;

    private string Shown => value.Length == 0 ? "empty" : value;

    protected override StepCheck Check(InstallContext context) => Compare(context, value, Shown);

    protected override (string Value, string Logged) Value(InstallContext context) => (value, Shown);
}

/// <summary>
/// The camera user's password: the one given for this run, or the one already in place, which is kept while the user
/// stays the same. It is never in the answers, and never shown or logged; when it must be written and none was given,
/// the installer asks for it.
/// </summary>
public sealed class CameraPasswordStep(ControllerLayout layout, string userName) : CameraFileStep(layout, CameraSteps.PasswordSetting)
{
    private readonly string _userFile = Path.Join(layout.Secrets, CameraSteps.UserNameSetting);

    public override string Purpose => $"the password of the camera's user, {userName} (never shown)";

    private static string Typed => $"typed when the install runs, or given with {InstallSecrets.OptionName(InstallSecret.CameraPassword)}";

    protected override StepCheck Check(InstallContext context)
    {
        if (context.Secrets[InstallSecret.CameraPassword] is { } given)
        {
            return Compare(context, given, "the password given");
        }

        var current = context.Machine.ReadText(Target);
        if (string.IsNullOrEmpty(current))
        {
            return Changes(current is null ? StepChange.Create : StepChange.Change, Typed);
        }

        // A new user needs their own password: the one in place is the old user's.
        var user = context.Machine.ReadText(_userFile);
        return !string.Equals(user, userName, StringComparison.Ordinal)
            ? Changes(StepChange.Change, $"the user changes, so its password does: {Typed}")
            : ModeCheck(context, "kept");
    }

    public override IReadOnlyList<InstallSecret> SecretsNeeded(StepCheck check)
        => check is { MakesChange: true } && ChangesValue ? [InstallSecret.CameraPassword] : [];

    protected override (string Value, string Logged) Value(InstallContext context)
        => (context.Secrets[InstallSecret.CameraPassword]
                ?? throw new InstallerException($"The install needs {InstallSecrets.Describe(InstallSecret.CameraPassword)}, which was not given."),
            $"the password of the camera's user, {userName} (never shown)");
}
