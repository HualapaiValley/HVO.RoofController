using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace HVO.RoofControllerV4.iPad.Services;

/// <inheritdoc />
public sealed class DialogService : IDialogService
{
    private readonly ILogger<DialogService> _logger;

    public DialogService(ILogger<DialogService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ConnectivityPromptResult> ShowConnectivityPromptAsync(string title, string message, string? detail = null, CancellationToken cancellationToken = default)
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null)
        {
            _logger.LogWarning("No active page available to display connectivity prompt");
            return ConnectivityPromptResult.Retry;
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

                return await page.DisplayActionSheet(
                    sheetTitle,
                    "Cancel",
                    "Exit",
                    "Retry");
            }).ConfigureAwait(false);

            return response switch
            {
                "Retry" => ConnectivityPromptResult.Retry,
                "Exit" => ConnectivityPromptResult.Exit,
                _ => ConnectivityPromptResult.Cancel
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to display connectivity prompt");
            return ConnectivityPromptResult.Retry;
        }
    }

    /// <inheritdoc />
    public async Task ExitApplicationAsync()
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                try
                {
                    Application.Current?.Quit();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to quit application programmatically");
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while attempting to exit application");
        }
    }
}
