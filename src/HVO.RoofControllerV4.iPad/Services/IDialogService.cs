using System.Threading;
using System.Threading.Tasks;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Provides UI dialog helpers for prompting the operator.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Displays a connectivity failure prompt with Retry, Cancel, and Exit actions.
    /// </summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">The main message to display.</param>
    /// <param name="detail">Optional detail or error text.</param>
    /// <param name="cancellationToken">Token used to cancel the prompt.</param>
    /// <returns>The user's selected action.</returns>
    Task<ConnectivityPromptResult> ShowConnectivityPromptAsync(string title, string message, string? detail = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to gracefully exit the application.
    /// </summary>
    Task ExitApplicationAsync();
}

/// <summary>
/// Represents the action the user chose when prompted for connectivity recovery.
/// </summary>
public enum ConnectivityPromptResult
{
    Retry,
    Cancel,
    Exit
}
