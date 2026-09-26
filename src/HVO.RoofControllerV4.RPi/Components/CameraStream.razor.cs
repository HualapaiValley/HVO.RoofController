using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System;
using System.Threading.Tasks;

namespace HVO.RoofControllerV4.RPi.Components
{
    public partial class CameraStream : ComponentBase, IAsyncDisposable
    {
        private readonly string _playerId = $"cameraPlayer_{Guid.NewGuid():N}";
        private readonly string _canvasId = $"camCanvas_{Guid.NewGuid():N}";
        private readonly string _imageId = $"camImage_{Guid.NewGuid():N}";
        private readonly string _downloadLinkId = $"downloadLink_{Guid.NewGuid():N}";

        private string _src = string.Empty;
        private bool _inited;
        private bool _recording;
        private bool _isPaused;
        private IJSObjectReference? _module;

        [Inject] private NavigationManager Nav { get; set; } = default!;
        [Inject] private IJSRuntime JS { get; set; } = default!;

        private string PlayerSelector => $"#{_playerId}";
        private string CanvasSelector => $"#{_canvasId}";
        private string ImageSelector => $"#{_imageId}";
        private string ControlsSelector => $"{PlayerSelector} .controls-bar";

        protected override void OnInitialized()
        {
            base.OnInitialized();
            _src = BuildSrc();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && !_inited)
            {
                var module = await GetModuleAsync();
                await module.InvokeVoidAsync("init", CanvasSelector, ImageSelector, PlayerSelector, ControlsSelector);
                _inited = true;
                _isPaused = false;
                await InvokeAsync(StateHasChanged);
            }
        }

        private string BuildSrc() => $"{Nav.BaseUri}api/v1.0/camera/02/mjpeg?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        private async Task Play()
        {
            if (string.IsNullOrEmpty(_src))
            {
                _src = BuildSrc();
            }
            var module = await GetModuleAsync();
            await module.InvokeVoidAsync("play");
            _isPaused = false;
            await InvokeAsync(StateHasChanged);
        }

        private async Task Pause()
        {
            var module = await GetModuleAsync();
            await module.InvokeVoidAsync("pause");
            _isPaused = true;
            await InvokeAsync(StateHasChanged);
        }

        private async Task TogglePlayback()
        {
            if (!_inited) return;
            if (_isPaused) await Play();
            else await Pause();
        }

        private async Task Restart()
        {
            _src = BuildSrc();
            var module = await GetModuleAsync();
            await module.InvokeVoidAsync("restart", _src);
            _isPaused = false;
            await InvokeAsync(StateHasChanged);
        }

        private async Task Fullscreen()
        {
            var module = await GetModuleAsync();
            await module.InvokeVoidAsync("fullscreen", PlayerSelector);
        }

        private async Task Snapshot()
        {
            var module = await GetModuleAsync();
            await module.InvokeVoidAsync("snapshot", _downloadLinkId);
        }

        private async Task ToggleRecord()
        {
            var module = await GetModuleAsync();

            if (_recording)
            {
                await module.InvokeVoidAsync("stopRecord", _downloadLinkId);
                _recording = false;
            }
            else
            {
                await module.InvokeVoidAsync("startRecord");
                _recording = true;
            }
            await InvokeAsync(StateHasChanged);
        }

        private string PlaybackButtonIcon => _isPaused ? "bi-play-fill" : "bi-pause-fill";
        private string PlaybackButtonTitle => _isPaused ? "Play" : "Pause";
        private string RecordButtonIcon => _recording ? "bi-stop-fill" : "bi-record-circle";
        private string RecordButtonTitle => _recording ? "Stop recording" : "Start recording";
        private string StatusText => _inited ? (_recording ? "Recording" : _isPaused ? "Paused" : "Playing") : "Loading stream…";
        private string GetRecordingClass() => _recording ? "is-active" : string.Empty;

        private async ValueTask<IJSObjectReference> GetModuleAsync()
        {
            if (_module is not null)
            {
                return _module;
            }

            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/CameraStream.razor.js");
            return _module;
        }

        public async ValueTask DisposeAsync()
        {
            if (_module is not null)
            {
                try
                {
                    await _module.InvokeVoidAsync("dispose");
                    await _module.DisposeAsync();
                }
                catch
                {
                    // ignored - best effort cleanup
                }

                _module = null;
            }
        }
    }
}
