using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// Confirms permission to leave the local UI, never permission to enter a Linux account. The host supplies only
/// application shutdown; systemd exposes a fresh, unauthenticated login after the kiosk process has ended.
/// </summary>
public sealed class KioskTerminalExit(KioskConsole console, Func<Task<bool>> shutdown)
{
    /// <summary>The local exit action's label.</summary>
    public const string Title = "Exit UI to terminal";
    /// <summary>The explanation when no current operator PIN session authorizes UI exit.</summary>
    public const string NeedsOperator = "Unlock with an operator PIN to exit this UI. Linux login requires a separate Linux account.";
    private CancellationTokenSource? _checking;
    private RoofSessionResponse? _confirmedSession;
    private bool _exiting;

    /// <summary>True while the same person's exit confirmation is open.</summary>
    public bool IsConfirming { get; private set; }
    /// <summary>True during verification or once the host has accepted shutdown.</summary>
    public bool IsBusy => _checking is not null || _exiting;
    /// <summary>A public status or refusal, without exception or credential details.</summary>
    public string? Message { get; private set; }
    /// <summary>Raised when the page needs to show the current confirmation state.</summary>
    public event Action? Changed;

    /// <summary>Asks about the currently signed-in person. Device-only and password sessions cannot authorize this flow.</summary>
    public void Ask()
    {
        if (IsBusy) return;
        Cancel();
        _confirmedSession = Session();
        IsConfirming = _confirmedSession is not null;
        Message = IsConfirming ? null : NeedsOperator;
        Changed?.Invoke();
    }

    /// <summary>Dismisses confirmation and cancels a pending authorization check without closing the kiosk.</summary>
    public void Cancel()
    {
        _checking?.Cancel();
        _confirmedSession = null;
        IsConfirming = false;
        Message = null;
        Changed?.Invoke();
    }

    /// <summary>Revalidates the same PIN session at the controller before asking the host to shut down.</summary>
    public async Task ConfirmAsync()
    {
        if (IsBusy || !IsConfirming) return;
        var session = _confirmedSession;
        if (session is null || !ReferenceEquals(session, Session()))
        {
            Cancel();
            Message = NeedsOperator;
            Changed?.Invoke();
            return;
        }

        using var checking = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _checking = checking;
        Message = "Checking your session…";
        Changed?.Invoke();
        try
        {
            var caller = await console.Client.Auth.GetCallerAsync(checking.Token);
            if (checking.IsCancellationRequested || !IsConfirming) return;
            if (!ReferenceEquals(session, Session()) || caller.SessionId != session.SessionId ||
                caller.Kind != RoofCredentialKind.Pin ||
                caller.Role is not (RoofControllerApiContract.OperatorRole or RoofControllerApiContract.AdminRole))
            {
                Message = NeedsOperator;
                IsConfirming = false;
                return;
            }

            if (await shutdown())
            {
                _exiting = true;
                Message = "Exiting to Linux login…";
            }
            else Message = "The UI could not exit. Try again or use the Linux service recovery instructions.";
        }
        catch (OperationCanceledException) when (!IsConfirming) { }
        catch (Exception)
        {
            // No exception details or credentials belong on this public screen.
            Message = "Exit refused: your session could not be verified. Unlock again or check the controller connection.";
        }
        finally
        {
            _checking = null;
            Changed?.Invoke();
        }
    }

    private RoofSessionResponse? Session()
    {
        var session = (console.Client.Credential as RoofKioskCredential)?.PinSession;
        return console.Wording.SignIn == KioskSignIn.Pin && console.View.IsUnlocked && console.View.IsOperator &&
            session is { Kind: RoofCredentialKind.Pin } && session.ExpiresUtc > console.Client.Options.TimeProvider.GetUtcNow()
            ? session : null;
    }
}
