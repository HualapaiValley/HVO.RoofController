using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The Mac app's sign-in: a person's name and password, sent to the controller. The password is only held here until it
/// is sent, and is forgotten whatever the answer.
/// </summary>
/// <remarks>Used on the UI thread only; <see cref="Changed"/> is raised there.</remarks>
public sealed class KioskPasswordForm
{
    private readonly KioskConsole _console;
    private string _password = string.Empty;

    public KioskPasswordForm(KioskConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    /// <summary>The name typed, as typed.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>True when a password has been typed and not yet sent.</summary>
    public bool HasPassword => _password.Length > 0;

    /// <summary>True while the name and password are on their way to the controller.</summary>
    public bool SigningIn { get; private set; }

    /// <summary>Why the person is not signed in; null when there is nothing to say.</summary>
    public string? Error { get; private set; }

    /// <summary>True when the sign-in can be sent: a name that could be one, and a password, with nothing on its way.</summary>
    public bool CanSignIn => !SigningIn && RoofIdentityContract.IsValidName(Name.Trim()) && _password.Length is > 0 and <= RoofIdentityContract.MaximumPasswordLength;

    /// <summary>Raised after anything here changed.</summary>
    public event Action? Changed;

    /// <summary>The name, as it is typed. Typing clears the last answer.</summary>
    public void SetName(string? name)
    {
        name ??= string.Empty;
        if (SigningIn || name == Name)
        {
            return;
        }

        Name = name;
        Error = null;
        Raise();
    }

    /// <summary>The password, as it is typed. It is never shown. Typing clears the last answer.</summary>
    public void SetPassword(string? password)
    {
        password ??= string.Empty;
        if (SigningIn || password == _password)
        {
            return;
        }

        _password = password;
        Error = null;
        Raise();
    }

    /// <summary>Forgets the password and the answer; the name stays, for the next try.</summary>
    public void Clear()
    {
        if (SigningIn)
        {
            return;
        }

        _password = string.Empty;
        Error = null;
        Raise();
    }

    /// <summary>
    /// Sends the name and password. The password is forgotten whatever the answer; <see cref="Error"/> says why the person
    /// is not signed in.
    /// </summary>
    public async Task SignInAsync()
    {
        if (!CanSignIn)
        {
            return;
        }

        var password = _password;
        _password = string.Empty;
        SigningIn = true;
        Error = null;
        Raise();
        try
        {
            Error = await _console.SignInAsync(Name.Trim(), password);
        }
        finally
        {
            SigningIn = false;
            Raise();
        }
    }

    private void Raise() => Changed?.Invoke();
}
