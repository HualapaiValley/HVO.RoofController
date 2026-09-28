using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace HVO.RoofControllerV4.RPi.Components
{
    /// <summary>
    /// Same-origin MJPEG viewer. A JS player reads the stream with fetch(), draws each frame on a canvas and reports
    /// connection state back; "Live" is only shown while frames are arriving. Every interop call tolerates the circuit
    /// going away, because a camera failure must never affect the roof controls around it.
    /// </summary>
    public partial class CameraStream : ComponentBase, IAsyncDisposable
    {
        internal const string ModulePath = "./Components/CameraStream.razor.js";

        private ElementReference _container;
        private ElementReference _canvas;
        private ElementReference _controls;
        private ElementReference _downloadLink;

        private IJSObjectReference? _module;
        private IJSObjectReference? _player;
        private DotNetObjectReference<CameraStream>? _selfReference;
        private bool _disposed;
        private bool _recording;
        private bool _paused;
        private string _state = "connecting";
        private string? _detail;
        private DateTimeOffset? _lastFrameUtc;

        [Parameter] public string CameraId { get; set; } = "02";

        [Inject] private IJSRuntime JS { get; set; } = default!;
        [Inject] private ILogger<CameraStream> Logger { get; set; } = default!;

        private string StreamUrl => $"api/v1.0/camera/{Uri.EscapeDataString(CameraId)}/mjpeg";
        private bool IsReady => _player is not null && !_disposed;
        private bool IsLive => _state == "live";
        private bool HasFrame => _lastFrameUtc is not null;

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!firstRender || _disposed)
            {
                return;
            }

            try
            {
                _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
                if (_disposed)
                {
                    await ReleaseAsync();
                    return;
                }

                _selfReference = DotNetObjectReference.Create(this);
                _player = await _module.InvokeAsync<IJSObjectReference>(
                    "createPlayer", _container, _canvas, _controls, _downloadLink, _selfReference, StreamUrl);
                if (_disposed)
                {
                    await ReleaseAsync();
                    return;
                }

                StateHasChanged();
            }
            catch (Exception ex) when (IsDisconnection(ex))
            {
            }
            catch (JSException ex)
            {
                Logger.LogWarning(ex, "The camera player could not start");
                SetState("error", "The camera player could not start");
            }
        }

        /// <summary>Called by the JS player whenever the stream state changes.</summary>
        [JSInvokable]
        public Task OnStreamStateChanged(string state, string? detail, double? lastFrameUnixMs)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            return InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (lastFrameUnixMs is { } unixMs && double.IsFinite(unixMs))
                {
                    _lastFrameUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)unixMs);
                }

                SetState(state, detail);
            });
        }

        private void SetState(string state, string? detail)
        {
            _state = string.IsNullOrWhiteSpace(state) ? "offline" : state;
            _detail = detail;
            StateHasChanged();
        }

        private async Task TogglePlayback()
        {
            var command = _paused ? "play" : "pause";
            if (await InvokePlayerAsync(command))
            {
                _paused = !_paused;
            }
        }

        private async Task Restart()
        {
            if (await InvokePlayerAsync("restart"))
            {
                _paused = false;
            }
        }

        private Task Fullscreen() => InvokePlayerAsync("fullscreen");

        private Task Snapshot() => InvokePlayerAsync("snapshot");

        private async Task ToggleRecord()
        {
            if (_recording)
            {
                await InvokePlayerAsync("stopRecord");
                _recording = false;
                return;
            }

            var player = _player;
            if (player is null || _disposed)
            {
                return;
            }

            try
            {
                _recording = await player.InvokeAsync<bool>("startRecord");
            }
            catch (Exception ex) when (IsDisconnection(ex))
            {
            }
            catch (JSException ex)
            {
                Logger.LogWarning(ex, "Camera recording could not start");
            }
        }

        private async Task<bool> InvokePlayerAsync(string command)
        {
            var player = _player;
            if (player is null || _disposed)
            {
                return false;
            }

            try
            {
                await player.InvokeVoidAsync(command);
                return true;
            }
            catch (Exception ex) when (IsDisconnection(ex))
            {
                return false;
            }
            catch (JSException ex)
            {
                Logger.LogWarning(ex, "Camera command {Command} failed", command);
                return false;
            }
        }

        private static bool IsDisconnection(Exception ex)
            => ex is JSDisconnectedException or OperationCanceledException or ObjectDisposedException;

        private string PlaybackButtonIcon => _paused ? "bi-play-fill" : "bi-pause-fill";
        private string PlaybackButtonTitle => _paused ? "Resume" : "Pause";
        private string RecordButtonIcon => _recording ? "bi-stop-fill" : "bi-record-circle";
        private string RecordButtonTitle => _recording ? "Stop recording" : "Start recording";

        private string StatusText
        {
            get
            {
                var text = _state switch
                {
                    "live" => "Live",
                    "connecting" => "Connecting",
                    "reconnecting" => "Reconnecting",
                    "waiting" => "Waiting for video",
                    "stalled" => "Stalled",
                    "unauthorized" => "Not authorized",
                    "paused" => "Paused",
                    "error" => "Unavailable",
                    _ => "Offline"
                };

                return _recording ? $"{text} • REC" : text;
            }
        }

        private string StatusChipClass => _state switch
        {
            "live" => "status-chip--live",
            "stalled" or "offline" or "unauthorized" or "error" => "status-chip--bad",
            _ => string.Empty
        };

        private string OverlayIcon => _state switch
        {
            "paused" => "bi-pause-circle",
            "connecting" or "reconnecting" or "waiting" => "bi-hourglass-split",
            "unauthorized" => "bi-shield-lock",
            _ => "bi-camera-video-off"
        };

        private string OverlayTitle
        {
            get
            {
                if (_lastFrameUtc is { } lastFrame)
                {
                    var time = lastFrame.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    return _state == "paused" ? $"Paused — last frame {time}" : $"No live video — last frame {time}";
                }

                return _state switch
                {
                    "connecting" or "reconnecting" => "Connecting to the camera…",
                    "waiting" => "Waiting for the first frame…",
                    "stalled" => "No frames from the camera",
                    "unauthorized" => "Not authorized to view the camera",
                    "paused" => "Paused",
                    "error" => "Camera view unavailable",
                    _ => "Camera offline"
                };
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await ReleaseAsync();
            GC.SuppressFinalize(this);
        }

        private async Task ReleaseAsync()
        {
            var player = _player;
            var module = _module;
            var selfReference = _selfReference;
            _player = null;
            _module = null;
            _selfReference = null;

            try
            {
                // Each step runs even when the one before it fails, so a player whose dispose throws still has its
                // reference and the module released. Once the circuit is gone no step can reach the browser, so the
                // rest are skipped.
                var connected = true;
                if (player is not null)
                {
                    connected = await ReleaseStepAsync(() => player.InvokeVoidAsync("dispose"))
                        && await ReleaseStepAsync(player.DisposeAsync);
                }

                if (connected && module is not null)
                {
                    await ReleaseStepAsync(module.DisposeAsync);
                }
            }
            finally
            {
                selfReference?.Dispose();
            }
        }

        /// <summary>Runs one cleanup step; false when the circuit is gone.</summary>
        private async Task<bool> ReleaseStepAsync(Func<ValueTask> step)
        {
            try
            {
                await step();
            }
            catch (Exception ex) when (IsDisconnection(ex))
            {
                return false;
            }
            catch (JSException ex)
            {
                Logger.LogDebug(ex, "Camera player cleanup failed");
            }

            return true;
        }
    }
}
