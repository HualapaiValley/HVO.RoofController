using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Answers;

/// <summary>A secret an install may need, which is never in the answers: a person types it, or gives it in a file.</summary>
public enum InstallSecret
{
    /// <summary>The first admin's password.</summary>
    AdminPassword,

    /// <summary>The first admin's PIN, for the kiosk.</summary>
    AdminPin,

    /// <summary>The password of the Blue Iris user the controller shows the camera as.</summary>
    CameraPassword
}

/// <summary>
/// The secrets of one run: held only in memory, and never saved, logged or shown. A step says which it needs
/// (<see cref="Plan.PlanStep.SecretsNeeded"/>); the installer asks for those, or reads them from the files named on its command
/// line, before it changes anything.
/// </summary>
public sealed class InstallSecrets
{
    private readonly Dictionary<InstallSecret, string> _values = [];

    /// <summary>The secret, or null when it was not given.</summary>
    public string? this[InstallSecret secret] => _values.GetValueOrDefault(secret);

    public bool Has(InstallSecret secret) => _values.ContainsKey(secret);

    /// <summary>Keeps <paramref name="value"/>, which <see cref="Problem"/> must have passed.</summary>
    public void Set(InstallSecret secret, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Problem(secret, value) is { } problem)
        {
            throw new ArgumentException(problem, nameof(value));
        }

        _values[secret] = value;
    }

    /// <summary>What the secret is, as the installer names it when it asks: "the first admin's password".</summary>
    public static string Describe(InstallSecret secret) => secret switch
    {
        InstallSecret.AdminPassword => "the first admin's password",
        InstallSecret.AdminPin => "the first admin's PIN",
        InstallSecret.CameraPassword => "the camera's password",
        _ => throw new ArgumentOutOfRangeException(nameof(secret), secret, null)
    };

    /// <summary>The option that gives the secret in a file: <c>--admin-password-file</c>.</summary>
    public static string OptionName(InstallSecret secret) => secret switch
    {
        InstallSecret.AdminPassword => "--admin-password-file",
        InstallSecret.AdminPin => "--admin-pin-file",
        InstallSecret.CameraPassword => "--camera-password-file",
        _ => throw new ArgumentOutOfRangeException(nameof(secret), secret, null)
    };

    /// <summary>What is wrong with <paramref name="value"/> for <paramref name="secret"/>, or null when nothing is.</summary>
    public static string? Problem(InstallSecret secret, string? value) => secret switch
    {
        InstallSecret.AdminPassword => RoofIdentityContract.IsValidPassword(value) ? null : RoofIdentityText.PasswordRule,
        InstallSecret.AdminPin => RoofIdentityContract.IsValidPin(value) ? null : RoofIdentityText.PinRule,
        InstallSecret.CameraPassword => string.IsNullOrEmpty(value) ? "The camera's password may not be empty."
            : value.Any(char.IsControl) ? "The camera's password may not hold a control character."
            : null,
        _ => throw new ArgumentOutOfRangeException(nameof(secret), secret, null)
    };

    /// <summary>
    /// The secret in the first line of <paramref name="path"/>, a file only its owner may read (as the deploy script asks
    /// of a key file). Throws <see cref="InstallerUsageException"/> when it cannot be read, others can read it, or its
    /// secret is not one <see cref="Problem"/> passes.
    /// </summary>
    public static string ReadFile(InstallerMachine machine, InstallSecret secret, string path)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var option = OptionName(secret);
        string? text;
        try
        {
            text = machine.ReadText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InstallerUsageException($"The file {path} ({option}) could not be read: {error.Message}");
        }

        if (text is null)
        {
            throw new InstallerUsageException($"There is no file {path} ({option}).");
        }

        if (machine.GetMode(path) is { } mode && (mode & Others) != 0)
        {
            throw new InstallerUsageException($"Others can read {path} ({option}): make it yours alone (chmod 600 {path}), then run the installer again.");
        }

        var line = text.Split('\n')[0].TrimEnd('\r');
        return Problem(secret, line) is { } problem
            ? throw new InstallerUsageException($"The file {path} ({option}) does not hold {Describe(secret)}: {problem}")
            : line;
    }

    private const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
}
