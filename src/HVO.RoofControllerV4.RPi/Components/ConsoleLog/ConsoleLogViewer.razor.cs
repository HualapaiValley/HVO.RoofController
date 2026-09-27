using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HVO.RoofControllerV4.RPi.Logging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace HVO.RoofControllerV4.RPi.Components.ConsoleLog;

/// <summary>
/// Shows the in-memory console log. Log entries arrive on arbitrary threads, often in bursts, so refreshes are
/// coalesced onto the renderer at most every <see cref="RefreshInterval"/>. Nothing here may log: a log call would
/// raise another entry and feed back into this handler.
/// </summary>
public partial class ConsoleLogViewer : ComponentBase, IAsyncDisposable
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);

    private readonly List<ConsoleLogEntry> _entries = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private ElementReference _viewport;
    private IJSObjectReference? _module;
    private bool _pendingScroll;
    private bool _isSubscribed;
    private bool _currentAutoScroll = true;
    private bool _isDisposed;
    private int _refreshScheduled;

    [Parameter]
    public bool AutoScroll { get; set; } = true;

    [Parameter]
    public EventCallback<bool> AutoScrollChanged { get; set; }

    [Parameter]
    public bool ShowAutoScrollToggle { get; set; } = true;

    [Parameter]
    public string? EmptyMessage { get; set; }

    [Parameter]
    public string? AriaLabel { get; set; }

    [Parameter]
    public EventCallback<int> EntryCountChanged { get; set; }

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    [Inject]
    private ConsoleLogBuffer ConsoleLogBuffer { get; set; } = default!;

    private IReadOnlyList<ConsoleLogEntry> Entries => _entries;

    private string EffectiveEmptyMessage => string.IsNullOrWhiteSpace(EmptyMessage) ? "No console output." : EmptyMessage!;

    private string EffectiveAriaLabel => string.IsNullOrWhiteSpace(AriaLabel) ? "Console output" : AriaLabel!;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        _currentAutoScroll = AutoScroll;
        SubscribeToConsoleLogBuffer();
    }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        if (AutoScroll != _currentAutoScroll)
        {
            _currentAutoScroll = AutoScroll;

            if (_currentAutoScroll && _entries.Count > 0)
            {
                _pendingScroll = true;
            }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (_isDisposed || !(firstRender || _pendingScroll) || !_currentAutoScroll || Entries.Count == 0)
        {
            return;
        }

        _pendingScroll = false;
        try
        {
            var module = await GetModuleAsync();
            await module.InvokeVoidAsync("scrollLogToBottom", _viewport);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException or ObjectDisposedException)
        {
            // Scrolling is cosmetic; the circuit may be going away.
        }
    }

    private async Task HandleAutoScrollChanged(ChangeEventArgs args)
    {
        var isChecked = args.Value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => _currentAutoScroll
        };

        _currentAutoScroll = isChecked;
        AutoScroll = isChecked;

        if (_currentAutoScroll && Entries.Count > 0)
        {
            _pendingScroll = true;
        }

        if (AutoScrollChanged.HasDelegate)
        {
            await AutoScrollChanged.InvokeAsync(isChecked);
        }
        else
        {
            StateHasChanged();
        }
    }

    private void SubscribeToConsoleLogBuffer()
    {
        if (_isSubscribed)
        {
            return;
        }

        _entries.Clear();
        _entries.AddRange(ConsoleLogBuffer.GetSnapshot());
        ConsoleLogBuffer.EntryAdded += OnConsoleLogEntryAdded;
        _isSubscribed = true;

        NotifyEntryCountChanged();

        if (_currentAutoScroll && _entries.Count > 0)
        {
            _pendingScroll = true;
        }
    }

    private void OnConsoleLogEntryAdded(object? sender, ConsoleLogEntry entry)
    {
        if (_isDisposed || Interlocked.Exchange(ref _refreshScheduled, 1) == 1)
        {
            return;
        }

        _ = RefreshAfterDelayAsync();
    }

    private async Task RefreshAfterDelayAsync()
    {
        try
        {
            await Task.Delay(RefreshInterval, _disposeCts.Token).ConfigureAwait(false);
            Volatile.Write(ref _refreshScheduled, 0);

            if (_isDisposed)
            {
                return;
            }

            var snapshot = ConsoleLogBuffer.GetSnapshot();
            await InvokeAsync(() => ApplySnapshot(snapshot)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled on dispose, or the renderer is gone. Logging here would re-enter this handler.
            Volatile.Write(ref _refreshScheduled, 0);
        }
    }

    private void ApplySnapshot(IReadOnlyList<ConsoleLogEntry> snapshot)
    {
        if (_isDisposed)
        {
            return;
        }

        _entries.Clear();
        _entries.AddRange(snapshot);

        if (_currentAutoScroll)
        {
            _pendingScroll = true;
        }

        NotifyEntryCountChanged();
        StateHasChanged();
    }

    private void NotifyEntryCountChanged()
    {
        if (EntryCountChanged.HasDelegate)
        {
            _ = EntryCountChanged.InvokeAsync(_entries.Count);
        }
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync()
    {
        if (_module is not null)
        {
            return _module;
        }

        _module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/ConsoleLog/ConsoleLogViewer.razor.js");
        return _module;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (_isSubscribed)
        {
            ConsoleLogBuffer.EntryAdded -= OnConsoleLogEntryAdded;
            _isSubscribed = false;
        }

        _disposeCts.Cancel();
        _disposeCts.Dispose();

        var module = _module;
        _module = null;
        if (module is not null)
        {
            try
            {
                await module.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException or ObjectDisposedException)
            {
                // The JS runtime is already gone.
            }
        }

        GC.SuppressFinalize(this);
    }

    private static string GetLogLevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => level.ToString().ToUpperInvariant()
    };

    private static string GetLogLevelCssClass(LogLevel level) => level switch
    {
        LogLevel.Trace => "hvo-console-level-trace",
        LogLevel.Debug => "hvo-console-level-debug",
        LogLevel.Information => "hvo-console-level-info",
        LogLevel.Warning => "hvo-console-level-warning",
        LogLevel.Error => "hvo-console-level-error",
        LogLevel.Critical => "hvo-console-level-critical",
        _ => "hvo-console-level-default"
    };
}
