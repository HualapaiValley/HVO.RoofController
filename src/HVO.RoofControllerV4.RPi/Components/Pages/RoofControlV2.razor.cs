using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using System.Threading.Tasks;

namespace HVO.RoofControllerV4.RPi.Components.Pages;

/// <summary>
/// Code-behind for the RoofControlV2 operator console. Inherits behavior from <see cref="RoofControlBase"/> and adds
/// the console-log panel and error boundaries that keep camera or log failures away from the roof controls.
/// </summary>
public partial class RoofControlV2
{
    private int _consoleLogEntryCount;
    private ErrorBoundary? _cameraErrorBoundary;
    private ErrorBoundary? _consoleErrorBoundary;

    protected bool IsConsoleLogExpanded { get; private set; }

    protected bool AutoScrollLogs { get; private set; } = true;

    protected void ToggleConsoleLog()
    {
        IsConsoleLogExpanded = !IsConsoleLogExpanded;
    }

    protected void OnAutoScrollChanged(bool value)
    {
        if (AutoScrollLogs == value)
        {
            return;
        }

        AutoScrollLogs = value;
        StateHasChanged();
    }

    protected void RecoverCamera() => _cameraErrorBoundary?.Recover();

    protected void RecoverConsole() => _consoleErrorBoundary?.Recover();

    private Task OnLogEntryCountChanged(int count)
    {
        if (_consoleLogEntryCount == count)
        {
            return Task.CompletedTask;
        }

        _consoleLogEntryCount = count;
        return InvokeAsync(StateHasChanged);
    }
}
