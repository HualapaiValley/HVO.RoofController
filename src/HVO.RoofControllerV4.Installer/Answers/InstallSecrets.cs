using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Answers;

/// <summary>What kind of secret an install may need.</summary>
public enum InstallSecretKind
{
    /// <summary>The first admin's password.</summary>
    AdminPassword,

    /// <summary>The first admin's PIN, for the kiosk.</summary>
    AdminPin,

    /// <summary>The password of the Blue Iris user the controller shows the camera as.</summary>
    CameraPassword,

    /// <summary>A person's PIN for the kiosk (<see cref="InstallSecret.Person"/>), given when the kiosk is installed.</summary>
    Pin,

    /// <summary>
    /// The password of the admin who signs in once to make the Mac app's device key: one they have already, so it is
    /// typed once.
    /// </summary>
    SignInPassword
}

/// <summary>
/// A secret an install may need, which is never in the answers: a person types it, or gives it in a file. A PIN for the
/// kiosk names its person; names compare as the controller compares them, without regard to case.
/// </summary>
public readonly record struct InstallSecret(InstallSecretKind Kind, string? Person = null) : IComparable<InstallSecret>
{
    public static InstallSecret AdminPassword => new(InstallSecretKind.AdminPassword);

    public static InstallSecret AdminPin => new(InstallSecretKind.AdminPin);

    public static InstallSecret CameraPassword => new(InstallSecretKind.CameraPassword);

    public static InstallSecret SignInPassword => new(InstallSecretKind.SignInPassword);

    /// <summary>The secrets with an option of their own (<c>--admin-password-file</c> and the others).</summary>
    public static IReadOnlyList<InstallSecret> WithOwnOption { get; } = [AdminPassword, AdminPin, CameraPassword, SignInPassword];

    /// <summary><paramref name="person"/>'s PIN, for the kiosk.</summary>
    public static InstallSecret PinFor(string person)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(person);
        return new(InstallSecretKind.Pin, person.Trim());
    }

    /// <summary>True for a PIN: digits, typed on the kiosk's keypad.</summary>
    public bool IsPin => Kind is InstallSecretKind.AdminPin or InstallSecretKind.Pin;

    /// <summary>True for a secret a person has already (a password they sign in with): typed once, not twice.</summary>
    public bool IsExisting => Kind == InstallSecretKind.SignInPassword;

    public bool Equals(InstallSecret other)
        => Kind == other.Kind && string.Equals(Person, other.Person, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
        => HashCode.Combine(Kind, Person is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Person));

    /// <summary>The order they are asked in: the admin's, the camera's, then each person's PIN by name.</summary>
    public int CompareTo(InstallSecret other)
        => Kind != other.Kind ? Kind.CompareTo(other.Kind) : StringComparer.OrdinalIgnoreCase.Compare(Person, other.Person);

    public override string ToString() => Person is null ? Kind.ToString() : $"{Kind}({Person})";
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
    public static string Describe(InstallSecret secret) => secret.Kind switch
    {
        InstallSecretKind.AdminPassword => "the first admin's password",
        InstallSecretKind.AdminPin => "the first admin's PIN",
        InstallSecretKind.CameraPassword => "the camera's password",
        InstallSecretKind.Pin => $"{secret.Person}'s PIN",
        InstallSecretKind.SignInPassword => "the Mac app's admin's password",
        _ => throw new ArgumentOutOfRangeException(nameof(secret), secret, null)
    };

    /// <summary>The option that gives <c>--pin-file NAME=FILE</c>: a person's PIN, in a file.</summary>
    public const string PinFileOption = "--pin-file";

    /// <summary>The option that gives the secret in a file: <c>--admin-password-file</c>, or <see cref="PinFileOption"/> for a person's PIN.</summary>
    public static string OptionName(InstallSecret secret) => secret.Kind switch
    {
        InstallSecretKind.AdminPassword => "--admin-password-file",
        InstallSecretKind.AdminPin => "--admin-pin-file",
        InstallSecretKind.CameraPassword => "--camera-password-file",
        InstallSecretKind.Pin => PinFileOption,
        InstallSecretKind.SignInPassword => "--sign-in-password-file",
        _ => throw new ArgumentOutOfRangeException(nameof(secret), secret, null)
    };

    /// <summary>How the option is given: <c>--admin-password-file FILE</c>, or <c>--pin-file olga=FILE</c>.</summary>
    public static string OptionUsage(InstallSecret secret)
        => secret.Kind == InstallSecretKind.Pin ? $"{PinFileOption} {secret.Person}=FILE" : $"{OptionName(secret)} FILE";

    /// <summary>What is wrong with <paramref name="value"/> for <paramref name="secret"/>, or null when nothing is.</summary>
    public static string? Problem(InstallSecret secret, string? value) => secret.Kind switch
    {
        InstallSecretKind.AdminPassword => RoofIdentityContract.IsValidPassword(value) ? null : RoofIdentityText.PasswordRule,
        InstallSecretKind.AdminPin or InstallSecretKind.Pin => RoofIdentityContract.IsValidPin(value) ? null : RoofIdentityText.PinRule,
        InstallSecretKind.CameraPassword => string.IsNullOrEmpty(value) ? "The camera's password may not be empty."
            : value.Any(char.IsControl) ? "The camera's password may not hold a control character."
            : null,
        InstallSecretKind.SignInPassword => string.IsNullOrEmpty(value) ? "The password may not be empty."
            : value.Any(char.IsControl) ? "The password may not hold a control character."
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
        var option = secret.Kind == InstallSecretKind.Pin ? $"{PinFileOption} {secret.Person}" : OptionName(secret);
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
