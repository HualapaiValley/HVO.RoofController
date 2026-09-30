using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The PIN pad that unlocks the kiosk: the people who may unlock it (read with the device key), the person chosen, and
/// the PIN typed so far, which is never shown. A PIN is only held here until it is sent or cleared.
/// </summary>
/// <remarks>Used on the UI thread only; <see cref="Changed"/> is raised there.</remarks>
public sealed class KioskPinPad
{
    /// <summary>Said when no one has a PIN for the kiosk.</summary>
    public const string NoPinUsers =
        "No one has a PIN for this kiosk yet. An admin gives an operator one with hvo-roof users set <name> --pin, or in the web UI.";

    private readonly KioskConsole _console;
    private string _pin = string.Empty;

    public KioskPinPad(KioskConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    /// <summary>The people who may unlock the kiosk, in the controller's order.</summary>
    public IReadOnlyList<RoofPinUserResponse> Users { get; private set; } = [];

    /// <summary>True while the people are read.</summary>
    public bool Loading { get; private set; }

    /// <summary>The person the PIN is for, or null before one is chosen.</summary>
    public string? Selected { get; private set; }

    /// <summary>How many digits have been typed.</summary>
    public int Length => _pin.Length;

    /// <summary>One dot for each digit typed.</summary>
    public string Masked => new('●', _pin.Length);

    /// <summary>True while the PIN is on its way to the controller.</summary>
    public bool Unlocking { get; private set; }

    /// <summary>Why the kiosk did not unlock, or why the people could not be read; null when there is nothing to say.</summary>
    public string? Error { get; private set; }

    /// <summary>What to do next, for the line above the pad.</summary>
    public string Prompt => Selected is null ? "Choose your name, then enter your PIN."
        : $"PIN for {Selected}: {RoofIdentityContract.MinimumPinLength} to {RoofIdentityContract.MaximumPinLength} digits.";

    /// <summary>True when the PIN can be sent: a person is chosen and the PIN has an allowed length.</summary>
    public bool CanUnlock => Selected is not null && !Unlocking && RoofIdentityContract.IsValidPin(_pin);

    /// <summary>Raised after anything here changed.</summary>
    public event Action? Changed;

    /// <summary>Reads the people who may unlock the kiosk. A person chosen before stays chosen while they still may.</summary>
    public async Task LoadUsersAsync(CancellationToken cancellationToken = default)
    {
        if (Loading)
        {
            return;
        }

        Loading = true;
        Raise();
        try
        {
            var users = await _console.Client.Auth.GetPinUsersAsync(cancellationToken);
            Users = users;
            if (Selected is not null && !users.Any(user => user.Name == Selected))
            {
                Select(null);
            }

            Error = users.Length == 0 ? NoPinUsers : null;
            if (users.Length == 1 && Selected is null)
            {
                Selected = users[0].Name;
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            Error = $"The people who may unlock the kiosk could not be read. {RoofText.DescribeFailure(error)}";
        }
        finally
        {
            Loading = false;
            Raise();
        }
    }

    /// <summary>Chooses the person the PIN is for; the PIN typed so far is cleared.</summary>
    public void Select(string? name)
    {
        Selected = name;
        _pin = string.Empty;
        if (Users.Count > 0)
        {
            Error = null;
        }

        Raise();
    }

    /// <summary>Types a digit. Nothing happens past the longest PIN, or while the PIN is on its way.</summary>
    public void Press(char digit)
    {
        if (!char.IsAsciiDigit(digit) || Unlocking || _pin.Length >= RoofIdentityContract.MaximumPinLength)
        {
            return;
        }

        _pin += digit;
        Error = null;
        Raise();
    }

    /// <summary>Removes the last digit typed.</summary>
    public void Backspace()
    {
        if (_pin.Length == 0 || Unlocking)
        {
            return;
        }

        _pin = _pin[..^1];
        Raise();
    }

    /// <summary>Forgets the PIN typed so far.</summary>
    public void Clear()
    {
        if (Unlocking)
        {
            return;
        }

        _pin = string.Empty;
        Raise();
    }

    /// <summary>Sends the PIN. The PIN is forgotten whatever the answer; <see cref="Error"/> says why it did not unlock.</summary>
    public async Task UnlockAsync()
    {
        if (!CanUnlock)
        {
            return;
        }

        var pin = _pin;
        _pin = string.Empty;
        Unlocking = true;
        Error = null;
        Raise();
        try
        {
            Error = await _console.UnlockAsync(Selected!, pin);
        }
        finally
        {
            Unlocking = false;
            Raise();
        }
    }

    private void Raise() => Changed?.Invoke();
}
