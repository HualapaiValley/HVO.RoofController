using System.Threading;
using System.Threading.Tasks;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Provides UI dialog helpers for prompting the operator.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Displays a connectivity failure prompt with Retry and Keep offline actions. The app keeps running (and Stop
    /// stays available) whichever is chosen.
    /// </summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">The main message to display.</param>
    /// <param name="detail">Optional detail or error text.</param>
    /// <param name="cancellationToken">Token used to cancel the prompt.</param>
    /// <returns>The user's selected action.</returns>
    Task<ConnectivityPromptResult> ShowConnectivityPromptAsync(string title, string message, string? detail = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the operator to confirm an action. Returns true only when <paramref name="accept"/> is chosen.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);

    /// <summary>
    /// Shows an informational message.
    /// </summary>
    Task AlertAsync(string title, string message, string dismiss = "OK");
}

/// <summary>
/// Represents the action the user chose when prompted for connectivity recovery.
/// </summary>
public enum ConnectivityPromptResult
{
    Retry,
    KeepOffline
}
