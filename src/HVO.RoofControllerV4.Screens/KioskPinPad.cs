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
    private string? _setupToken;
    private string? _chosenPin;
    /// <summary>Setup never unlocks the console or installs a session credential.</summary>
    public bool ChoosingPin => _setupToken is not null;
    public string ActionLabel => !ChoosingPin ? "Unlock" : _chosenPin is null ? "Choose PIN" : "Confirm PIN";
    public static string SelectionKey(RoofPinUserResponse user) => user.PrincipalId ?? user.Name;
    private RoofPinUserResponse? SelectedUser => Users.FirstOrDefault(user => SelectionKey(user) == Selected);


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
    public string Prompt => ChoosingPin
        ? _chosenPin is null ? "Choose your personal four-digit PIN." : "Enter your new PIN again to confirm."
        : Selected is null ? "Choose your name, then enter your PIN."
        : SelectedUser?.SetupPending == true ? $"First-time setup for {SelectedUser.Name}: enter the temporary PIN."
        : $"PIN for {(SelectedUser is { } user ? RoofIdentityText.DescribePerson(user.Name, user.DisplayName) : Selected)}: four digits.";

    /// <summary>True when the PIN can be sent: a person is chosen and the PIN has an allowed length.</summary>
    public bool CanUnlock => Selected is not null && !Unlocking && RoofIdentityContract.IsAcceptedPin(_pin);

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
            if (Selected is not null && !users.Any(user => SelectionKey(user) == Selected))
            {
                Select(null);
            }

            Error = users.Length == 0 ? NoPinUsers : null;
            if (users.Length == 1 && Selected is null)
            {
                Selected = SelectionKey(users[0]);
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
        _setupToken = null;
        _chosenPin = null;
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
        if (!char.IsAsciiDigit(digit) || Unlocking || _pin.Length >= RoofIdentityContract.MaximumAcceptedPinLength)
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
            if (_setupToken is not null)
            {
                if (!RoofIdentityContract.IsValidPin(pin) || pin == "0000")
                {
                    Error = "Choose a personal four-digit PIN different from the temporary PIN.";
                }
                else if (_chosenPin is null)
                {
                    _chosenPin = pin;
                }
                else if (_chosenPin != pin)
                {
                    _chosenPin = null;
                    Error = "The PINs did not match. Choose your PIN again.";
                }
                else
                {
                    await _console.Client.Auth.CompleteKioskSetupAsync(_setupToken, _chosenPin, pin);
                    _setupToken = null;
                    _chosenPin = null;
                    await LoadUsersAsync();
                    Error = "PIN saved. Sign in with your new PIN to unlock.";
                }
            }
            else if (SelectedUser?.PrincipalId is { } principalId)
            {
                var result = await _console.Client.Auth.SignInWithPrincipalPinAsync(principalId, pin);
                _setupToken = result.SetupToken;
                _chosenPin = null;
            }
            else
            {
                Error = await _console.UnlockAsync(Selected!, pin);
            }
        }
        catch (Exception error)
        {
            Error = RoofText.DescribeFailure(error);
            _setupToken = null;
            _chosenPin = null;
        }
        finally
        {
            Unlocking = false;
            Raise();
        }
    }

    private void Raise() => Changed?.Invoke();
}
