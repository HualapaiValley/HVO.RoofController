using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The PIN pad that unlocks the kiosk: the people who may unlock it (read with the device key), the person chosen, and
/// the PIN typed so far, which is never shown. A PIN is only held here until it is sent or cleared. While the kiosk is
/// unlocked with a PIN, the same pad changes that person's own PIN (#205): the current PIN, then the new one twice.
/// </summary>
/// <remarks>Used on the UI thread only; <see cref="Changed"/> is raised there.</remarks>
public sealed class KioskPinPad
{
    /// <summary>Said when no one has a PIN for the kiosk.</summary>
    public const string NoPinUsers =
        "No one has a PIN for this kiosk yet. An admin gives an operator one with hvo-roof users set <name> --pin, or in the web UI.";

    /// <summary>Said when the current PIN typed to change it was wrong (#205).</summary>
    public const string WrongCurrentPin = "The current PIN is not correct. Your PIN was not changed.";

    /// <summary>The start of what is said when a PIN change was refused (#205).</summary>
    public const string PinNotChanged = "Your PIN was not changed.";

    /// <summary>Said when a PIN change got no answer: it may have reached the controller (#205).</summary>
    public const string PinMayHaveChanged =
        "Your PIN may have been changed: unlock with the new one next time to find out. If it is refused, the current one still works.";

    private readonly KioskConsole _console;
    private string _pin = string.Empty;
    private string? _setupToken;
    private string? _chosenPin;
    private ChangeStep? _change;
    private string? _currentPin;
    private string? _newPin;
    // Moves on whenever a change begins or ends: an answer for a change that has ended (the kiosk locked) is dropped.
    private int _changeRound;

    /// <summary>The steps of changing your own PIN (#205).</summary>
    private enum ChangeStep
    {
        Current,
        New,
        Confirm
    }

    /// <summary>Setup never unlocks the console or installs a session credential.</summary>
    public bool ChoosingPin => _setupToken is not null;

    /// <summary>True while the person who unlocked the kiosk changes their own PIN on the pad (#205).</summary>
    public bool ChangingPin => _change is not null;

    /// <summary>True once the PIN was changed, until the change ends: the page says so (#205).</summary>
    public bool PinChanged { get; private set; }

    public string ActionLabel => _change is { } step ? step == ChangeStep.Confirm ? "Change PIN" : "Next"
        : !ChoosingPin ? "Unlock" : _chosenPin is null ? "Choose PIN" : "Confirm PIN";
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
    public string Prompt => _change is { } step
        ? step switch
        {
            ChangeStep.Current => "Change your PIN: first enter your current PIN.",
            ChangeStep.New => "Choose your new four-digit PIN.",
            _ => "Enter your new PIN again to confirm."
        }
        : ChoosingPin
        ? _chosenPin is null ? "Choose your personal four-digit PIN." : "Enter your new PIN again to confirm."
        : Selected is null ? "Choose your name, then enter your PIN."
        : SelectedUser?.SetupPending == true ? $"First-time setup for {SelectedUser.Name}: enter the temporary PIN."
        : $"PIN for {(SelectedUser is { } user ? RoofIdentityText.DescribePerson(user.Name, user.DisplayName) : Selected)}: four digits.";

    /// <summary>
    /// True when the PIN can be sent: a person is chosen (or the unlocked person changes their own), and the PIN has an
    /// allowed length.
    /// </summary>
    public bool CanUnlock => !PinChanged && (Selected is not null || ChangingPin) && !Unlocking && RoofIdentityContract.IsAcceptedPin(_pin);

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

    /// <summary>
    /// Starts changing the unlocked person's own PIN (#205): the pad asks for the current PIN, then the new one twice.
    /// Nothing typed before is kept.
    /// </summary>
    public void BeginChange()
    {
        ForgetChange();
        _change = ChangeStep.Current;
        Raise();
    }

    /// <summary>
    /// Ends changing the PIN (Cancel, Done, another page, the kiosk locked): every PIN typed is forgotten, and an answer
    /// still on its way is dropped.
    /// </summary>
    public void EndChange()
    {
        if (_change is null && !PinChanged)
        {
            return;
        }

        ForgetChange();
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
            if (_change is { } step)
            {
                await ChangeAsync(step, pin);
            }
            else if (_setupToken is not null)
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

    // One step of changing the PIN: each PIN typed is checked here before the next is asked for, and the current and new
    // PINs are sent together once the new one is confirmed, then forgotten whatever the answer.
    private async Task ChangeAsync(ChangeStep step, string pin)
    {
        switch (step)
        {
            case ChangeStep.Current:
                _currentPin = pin;
                _change = ChangeStep.New;
                break;
            case ChangeStep.New when RoofIdentityText.CheckPinChange(_currentPin, pin, pin) is { } problem:
                Error = problem;
                break;
            case ChangeStep.New:
                _newPin = pin;
                _change = ChangeStep.Confirm;
                break;
            case ChangeStep.Confirm when RoofIdentityText.CheckPinChange(_currentPin, _newPin, pin) is { } mismatch:
                _newPin = null;
                _change = ChangeStep.New;
                Error = $"{mismatch} Choose your new PIN again.";
                break;
            default:
                var (current, replacement, round) = (_currentPin!, _newPin!, _changeRound);
                _currentPin = null;
                _newPin = null;
                string? refusal = null;
                try
                {
                    await _console.Client.Auth.ChangePinAsync(current, replacement);
                }
                catch (Exception error)
                {
                    refusal = DescribeChangeFailure(error);
                }

                if (round != _changeRound)
                {
                    // The change ended while it was on its way: the kiosk locked, or the person left the page.
                    return;
                }

                if (refusal is null)
                {
                    PinChanged = true;
                }
                else
                {
                    _change = ChangeStep.Current;
                    Error = refusal;
                }

                break;
        }
    }

    /// <summary>Why a PIN change failed, as the pad says it (#205).</summary>
    internal static string DescribeChangeFailure(Exception error) => error switch
    {
        RoofApiException { Code: RoofControllerErrorCode.SignInFailed } => WrongCurrentPin,
        _ when RoofCommandRules.MayHaveReachedController(error) => $"{RoofText.DescribeFailure(error)} {PinMayHaveChanged}",
        RoofApiException { Code: RoofControllerErrorCode.InvalidRequest or RoofControllerErrorCode.IdentityReadOnly, Detail: { Length: > 0 } detail }
            => $"{PinNotChanged} {detail}",
        _ => $"{PinNotChanged} {RoofText.DescribeFailure(error)}"
    };

    private void ForgetChange()
    {
        _changeRound++;
        _change = null;
        _currentPin = null;
        _newPin = null;
        _pin = string.Empty;
        PinChanged = false;
        Error = null;
    }

    private void Raise() => Changed?.Invoke();
}
