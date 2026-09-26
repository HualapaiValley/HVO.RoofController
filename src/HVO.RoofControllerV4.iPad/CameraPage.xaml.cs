using System.ComponentModel;
using System.Net;
using HVO.RoofControllerV4.iPad.Services;
using HVO.RoofControllerV4.iPad.ViewModels;

namespace HVO.RoofControllerV4.iPad;

/// <summary>
/// Shows the camera stream only while the tab is visible and the app is in the foreground. Leaving the tab loads
/// about:blank, which closes the stream connection.
/// </summary>
public partial class CameraPage : ContentPage
{
    private const string SignalScheme = "hvo-camera";
    private const string LoadedSignal = "hvo-camera://loaded";
    private const string FailedSignal = "hvo-camera://failed";
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(15);

    private readonly RoofControllerViewModel _viewModel;
    private CancellationTokenSource? _loadCts;
    private bool _isPageVisible;
    private int _loadVersion;
    private bool _firstFrameShown;

    public CameraPage(RoofControllerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
        _viewModel.CameraSourceChanged += OnCameraSourceChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _isPageVisible = true;
        await _viewModel.InitializeAsync();
        await LoadStreamAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _isPageVisible = false;
        UnloadStream("Camera paused", "The stream is closed while this tab is not shown.");
    }

    private void OnReloadClicked(object? sender, EventArgs e) => _ = LoadStreamAsync();

    private void OnCameraSourceChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_isPageVisible)
            {
                _ = LoadStreamAsync();
            }
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RoofControllerViewModel.IsAppInForeground))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_isPageVisible)
            {
                return;
            }

            if (_viewModel.IsAppInForeground)
            {
                _ = LoadStreamAsync();
            }
            else
            {
                UnloadStream("Camera paused", "The stream is closed while the app is in the background.");
            }
        });
    }

    private async Task LoadStreamAsync()
    {
        CancelPendingLoad();
        var cts = new CancellationTokenSource(ResolveTimeout);
        _loadCts = cts;
        var version = ++_loadVersion;
        _firstFrameShown = false;

        SetStatus("Connecting…", "#ADB5BD");
        ShowPlaceholder("Connecting to the camera…", null);
        CameraView.Source = new UrlWebViewSource { Url = "about:blank" };

        CameraStreamResolution resolution;
        try
        {
            resolution = await _viewModel.ResolveCameraStreamAsync(cts.Token);
        }
        catch (Exception ex)
        {
            resolution = new CameraStreamResolution(null, $"The camera stream could not be prepared: {ex.Message}");
        }

        if (version != _loadVersion || !_isPageVisible)
        {
            return;
        }

        if (resolution.StreamUri is not { } streamUri)
        {
            SetStatus("Connection failed", "#DC3545");
            ShowPlaceholder("Camera unavailable", resolution.Error);
            return;
        }

        CameraView.Source = new HtmlWebViewSource { Html = BuildStreamHtml(streamUri) };
        CameraView.IsVisible = true;
        CameraPlaceholder.IsVisible = false;

        await Task.Delay(FirstFrameTimeout);
        if (version == _loadVersion && _isPageVisible && !_firstFrameShown && CameraView.IsVisible)
        {
            SetStatus("No image yet", "#F0AD4E");
        }
    }

    private void UnloadStream(string title, string detail)
    {
        CancelPendingLoad();
        _loadVersion++;
        CameraView.Source = new UrlWebViewSource { Url = "about:blank" };
        SetStatus("Not loaded", "#ADB5BD");
        ShowPlaceholder(title, detail);
    }

    private void CancelPendingLoad()
    {
        var previous = _loadCts;
        _loadCts = null;
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
    }

    private void OnCameraNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (e.Url is null || !e.Url.StartsWith(SignalScheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;

        if (e.Url.StartsWith(LoadedSignal, StringComparison.OrdinalIgnoreCase))
        {
            _firstFrameShown = true;
            SetStatus("Stream loaded (first frame)", "#198754");
        }
        else if (e.Url.StartsWith(FailedSignal, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Connection failed", "#DC3545");
            ShowPlaceholder("Camera connection failed", "The stream could not be loaded or was interrupted. Use Reload to try again.");
        }
    }

    private void SetStatus(string text, string color)
    {
        CameraStatusLabel.Text = text;
        CameraStatusLabel.TextColor = Color.FromArgb(color);
    }

    private void ShowPlaceholder(string title, string? detail)
    {
        CameraView.IsVisible = false;
        CameraPlaceholder.IsVisible = true;
        CameraPlaceholderTitle.Text = title;
        CameraPlaceholderDetail.Text = detail ?? string.Empty;
        CameraPlaceholderDetail.IsVisible = !string.IsNullOrWhiteSpace(detail);
    }

    private static string BuildStreamHtml(Uri streamUri)
    {
        var source = WebUtility.HtmlEncode(streamUri.AbsoluteUri);

        // onload/naturalWidth only prove that a first frame arrived; a stream that later freezes is not detected.
        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>html,body{margin:0;height:100%;background:#13161F;}img{display:block;width:100%;height:100%;object-fit:contain;}</style>
            </head>
            <body>
            <img id="stream" alt="" src="{{source}}">
            <script>
            (function () {
              var img = document.getElementById('stream');
              var state = 0;
              function signal(url) { window.location.href = url; }
              function loaded() { if (state === 0) { state = 1; signal('{{LoadedSignal}}'); } }
              img.addEventListener('load', loaded);
              img.addEventListener('error', function () { if (state < 2) { state = 2; signal('{{FailedSignal}}'); } });
              var timer = setInterval(function () {
                if (state !== 0) { clearInterval(timer); return; }
                if (img.naturalWidth > 0) { clearInterval(timer); loaded(); }
              }, 250);
            })();
            </script>
            </body>
            </html>
            """;
    }
}
