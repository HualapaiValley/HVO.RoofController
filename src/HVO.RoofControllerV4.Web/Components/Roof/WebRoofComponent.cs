using HVO.RoofControllerV4.Web.Roof;
using Microsoft.AspNetCore.Components;

namespace HVO.RoofControllerV4.Web.Components.Roof;

/// <summary>
/// A component that shows the roof: it renders the page's <see cref="WebRoofConsole"/> view, and again whenever the view
/// changes. The first one on a live page starts the console; while prerendering it shows the view before the start.
/// </summary>
public abstract class WebRoofComponent : ComponentBase, IDisposable
{
    [Inject]
    protected WebRoofConsole Roof { get; set; } = default!;

    [Inject]
    private ILogger<WebRoofComponent> Logger { get; set; } = default!;

    /// <summary>The view this component renders.</summary>
    protected WebRoofView View { get; private set; } = WebRoofView.NotStarted;

    protected override void OnInitialized()
    {
        View = Roof.View;
        Roof.Changed += OnChanged;
    }

    protected override Task OnAfterRenderAsync(bool firstRender) => firstRender ? Roof.StartAsync() : Task.CompletedTask;

    public void Dispose()
    {
        Roof.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }

    private void OnChanged() => _ = RenderViewAsync();

    private async Task RenderViewAsync()
    {
        try
        {
            await InvokeAsync(() =>
            {
                View = Roof.View;
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException)
        {
            // The page closed.
        }
        catch (Exception error)
        {
            Logger.LogWarning(error, "The roof view could not be rendered");
        }
    }
}
