using Microsoft.AspNetCore.Components;
using System.Threading.Tasks;

namespace HVO.RoofControllerV4.RPi.Components.Pages;

/// <summary>
/// Code-behind for the modernized RoofControlV2 UI. Inherits behavior from RoofControl
/// and adds optional parameters specific to the V2 layout.
/// </summary>
public partial class RoofControlV2
{
    private int _consoleLogEntryCount;

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


