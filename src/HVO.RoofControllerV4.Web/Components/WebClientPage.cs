using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Components;

namespace HVO.RoofControllerV4.Web.Components;

/// <summary>How a page message looks: Bootstrap's alert of the same name.</summary>
public enum WebMessageLevel
{
    Success,
    Info,
    Warning,
    Danger
}

/// <summary>What a page says after a request: one or more lines, and how it looks.</summary>
public sealed record WebPageMessage(WebMessageLevel Level, IReadOnlyList<string> Lines)
{
    public WebPageMessage(WebMessageLevel level, string line)
        : this(level, [line])
    {
    }

    public string Text => string.Join(' ', Lines);
}

/// <summary>
/// A page that talks to the controller with the signed-in person's session: one request at a time, and a failure is
/// said in the shared words (<see cref="RoofText.DescribeFailure"/>) rather than thrown into the circuit. The page's
/// requests end when it closes.
/// </summary>
public abstract class WebClientPage : ComponentBase, IDisposable
{
    public const string SignedOut = "You are signed out. Sign in again, then try again.";

    private readonly CancellationTokenSource _closing = new();
    private bool _disposed;

    [Inject] protected WebSessionAccessor Sessions { get; set; } = default!;

    [Inject] protected ILoggerFactory LoggerFactory { get; set; } = default!;

    /// <summary>True while a request is in flight; the page's buttons are disabled.</summary>
    protected bool Busy { get; private set; }

    protected WebPageMessage? Message { get; set; }

    /// <summary>The failure of the last request, or null when it succeeded.</summary>
    protected Exception? LastFailure { get; private set; }

    protected CancellationToken Closing => _closing.Token;

    /// <summary>
    /// Sends <paramref name="send"/> with the person's session. On failure the page says <paramref name="failed"/>
    /// followed by why, and the result is false.
    /// </summary>
    protected async Task<bool> SendAsync(string failed, Func<WebSession, CancellationToken, Task> send)
    {
        if (Busy || _disposed)
        {
            return false;
        }

        Busy = true;
        LastFailure = null;
        StateHasChanged();
        try
        {
            var session = await Sessions.GetAsync();
            if (session is null)
            {
                Message = new WebPageMessage(WebMessageLevel.Danger, SignedOut);
                return false;
            }

            await send(session, _closing.Token);
            return true;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is RoofApiException or TimeoutException or HttpRequestException or RoofProtocolException)
        {
            LastFailure = ex;
            LoggerFactory.CreateLogger(GetType()).LogWarning("{Failed} {Reason}", failed, ex.Message);
            Message = new WebPageMessage(WebMessageLevel.Danger, $"{failed} {RoofText.DescribeFailure(ex)}");
            return false;
        }
        finally
        {
            Busy = false;
            if (!_disposed)
            {
                StateHasChanged();
            }
        }
    }

    /// <summary>True when the last request was refused with <paramref name="code"/>.</summary>
    protected bool FailedWith(Common.Models.RoofControllerErrorCode code) => LastFailure is RoofApiException refusal && refusal.Code == code;

    protected static WebPageMessage Success(params string[] lines) => new(WebMessageLevel.Success, lines);

    protected static WebPageMessage Info(params string[] lines) => new(WebMessageLevel.Info, lines);

    protected static WebPageMessage Warning(params string[] lines) => new(WebMessageLevel.Warning, lines);

    protected static WebPageMessage Danger(params string[] lines) => new(WebMessageLevel.Danger, lines);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            _closing.Cancel();
            _closing.Dispose();
        }
    }
}
