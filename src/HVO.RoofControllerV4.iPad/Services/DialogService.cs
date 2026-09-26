using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace HVO.RoofControllerV4.iPad.Services;

/// <inheritdoc />
public sealed class DialogService : IDialogService
{
    private const string RetryButton = "Retry";
    private const string KeepOfflineButton = "Keep offline";

    private readonly ILogger<DialogService> _logger;

    public DialogService(ILogger<DialogService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ConnectivityPromptResult> ShowConnectivityPromptAsync(string title, string message, string? detail = null, CancellationToken cancellationToken = default)
    {
        var page = GetActivePage();
        if (page is null)
        {
            _logger.LogWarning("No active page available to display connectivity prompt");
            return ConnectivityPromptResult.KeepOffline;
        }

        var displayMessage = string.IsNullOrWhiteSpace(detail)
            ? message
            : $"{message}\n\nLast error: {detail}";

        try
        {
            var response = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return (string?)null;
                }

                var sheetTitle = string.IsNullOrWhiteSpace(displayMessage)
                    ? title
                    : $"{title}\n\n{displayMessage}";

                return await page.DisplayActionSheetAsync(
                    sheetTitle,
                    KeepOfflineButton,
                    null,
                    RetryButton);
            }).ConfigureAwait(false);

            return response == RetryButton ? ConnectivityPromptResult.Retry : ConnectivityPromptResult.KeepOffline;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to display connectivity prompt");
            return ConnectivityPromptResult.KeepOffline;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        var page = GetActivePage();
        if (page is null)
        {
            _logger.LogWarning("No active page available to display confirmation {Title}", title);
            return false;
        }

        try
        {
            return await MainThread.InvokeOnMainThreadAsync(() => page.DisplayAlertAsync(title, message, accept, cancel)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to display confirmation {Title}", title);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task AlertAsync(string title, string message, string dismiss = "OK")
    {
        var page = GetActivePage();
        if (page is null)
        {
            _logger.LogWarning("No active page available to display alert {Title}", title);
            return;
        }

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => page.DisplayAlertAsync(title, message, dismiss)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to display alert {Title}", title);
        }
    }

    private static Page? GetActivePage() => Application.Current?.Windows.FirstOrDefault()?.Page;
}
