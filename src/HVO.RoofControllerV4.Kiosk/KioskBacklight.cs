using HVO.RoofControllerV4.Screens;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.Kiosk;

/// <summary>
/// Turns the backlight off when the kiosk blanks its screen and on when a touch wakes it, by writing
/// <see cref="KioskOptions.BacklightFile"/>. The touchscreen still reports touches with the backlight off. A write that
/// fails is logged; the screen is then only drawn black.
/// </summary>
public sealed class KioskBacklight : IDisposable
{
    private readonly KioskConsole _console;
    private readonly string _path;
    private readonly string _on;
    private readonly string _off;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private bool? _blank;
    private bool _failed;
    private bool _disposed;

    public KioskBacklight(KioskConsole console, string path, string on, string off, ILogger<KioskBacklight>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(on);
        ArgumentException.ThrowIfNullOrWhiteSpace(off);
        _console = console;
        _path = path;
        _on = on;
        _off = off;
        _logger = logger ?? NullLogger<KioskBacklight>.Instance;
        console.Changed += OnChanged;
        OnChanged();
    }

    /// <summary>Turns the backlight on and stops following the kiosk: a kiosk that is not running leaves it on.</summary>
    public void Dispose()
    {
        _console.Changed -= OnChanged;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_blank != false)
            {
                Write(_on);
            }
        }
    }

    private void OnChanged()
    {
        var blank = _console.View.IsBlank;
        lock (_gate)
        {
            if (_disposed || _blank == blank)
            {
                return;
            }

            _blank = blank;
            Write(blank ? _off : _on);
        }
    }

    private void Write(string value)
    {
        try
        {
            File.WriteAllText(_path, value);
            if (_failed)
            {
                _failed = false;
                _logger.LogInformation("Backlight control {Path} written again", _path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_failed)
            {
                _failed = true;
                _logger.LogWarning(ex, "Backlight control {Path} could not be written; a blank screen is drawn black with the backlight on", _path);
            }
        }
    }
}
