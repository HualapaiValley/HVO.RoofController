using System.Globalization;
using System.Text;
using HVO.RoofControllerV4.Simulation.Emulator;

namespace HVO.RoofControllerV4.Simulation.Camera;

/// <summary>What the emulated camera does with a viewer.</summary>
public enum EmulatedCameraMode
{
    /// <summary>Streams a frame of the plant every frame interval.</summary>
    Live,

    /// <summary>Answers and then sends nothing, as a camera whose encoder has hung does.</summary>
    Frozen,

    /// <summary>Answers 503, as a camera server with the camera offline does. Open streams end.</summary>
    Unavailable,

    /// <summary>Answers 401, as a camera server that refuses the proxy's credentials does. Open streams end.</summary>
    Unauthorized
}

/// <summary>The emulated camera's state.</summary>
public sealed record EmulatedCameraStatus(
    EmulatedCameraMode Mode,
    double FramesPerSecond,
    int OpenStreams,
    long StreamsServed,
    long FramesSent);

/// <summary>
/// An MJPEG camera for the emulated plant, standing in for the Blue Iris server the controller's camera proxy reads:
/// <c>GET /mjpg/camNN/video.mjpg</c> answers <c>multipart/x-mixed-replace</c> with one JPEG of the plant per part, each
/// with its <c>Content-Length</c>. The mode injects the camera failures the web UI must survive.
/// </summary>
public sealed class EmulatedCamera
{
    public const string Boundary = "hvoframe";
    public const string ContentType = "multipart/x-mixed-replace; boundary=" + Boundary;

    /// <summary>The route the host maps: the Blue Iris MJPEG path, camera 1-99.</summary>
    public const string Route = "/mjpg/cam{camera:int:range(1,99)}/video.mjpg";

    private readonly Func<HatEmulatorStatus> _plant;
    private readonly object _gate = new();
    private CancellationTokenSource _disconnect = new();
    private EmulatedCameraMode _mode;
    private double _framesPerSecond;
    private int _openStreams;
    private long _streamsServed;
    private long _framesSent;

    public EmulatedCamera(Func<HatEmulatorStatus> plant, double framesPerSecond = 5)
    {
        _plant = plant ?? throw new ArgumentNullException(nameof(plant));
        FramesPerSecond = framesPerSecond;
    }

    public EmulatedCameraMode Mode
    {
        get { lock (_gate) { return _mode; } }
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The camera mode must be Live, Frozen, Unavailable or Unauthorized.");
            }

            lock (_gate)
            {
                _mode = value;
            }

            if (value is EmulatedCameraMode.Unavailable or EmulatedCameraMode.Unauthorized)
            {
                DisconnectAll();
            }
        }
    }

    /// <summary>Frames per second while live: 0.1 to 30.</summary>
    public double FramesPerSecond
    {
        get { lock (_gate) { return _framesPerSecond; } }
        set
        {
            if (double.IsNaN(value) || value < 0.1 || value > 30)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The camera's frame rate must be between 0.1 and 30 frames per second.");
            }

            lock (_gate)
            {
                _framesPerSecond = value;
            }
        }
    }

    /// <summary>The status code a new viewer gets: 200 while live or frozen.</summary>
    public int AnswerStatusCode => Mode switch
    {
        EmulatedCameraMode.Unavailable => 503,
        EmulatedCameraMode.Unauthorized => 401,
        _ => 200
    };

    public EmulatedCameraStatus GetStatus()
    {
        lock (_gate)
        {
            return new EmulatedCameraStatus(_mode, _framesPerSecond, _openStreams, _streamsServed, _framesSent);
        }
    }

    /// <summary>Ends every open stream (the viewers see the connection close), as a camera server restart does.</summary>
    public void DisconnectAll()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _disconnect;
            _disconnect = new CancellationTokenSource();
        }

        old.Cancel();
        old.Dispose();
    }

    /// <summary>Back to a live stream at <paramref name="framesPerSecond"/>, ending the open streams.</summary>
    public void Reset(double framesPerSecond = 5)
    {
        FramesPerSecond = framesPerSecond;
        Mode = EmulatedCameraMode.Live;
        DisconnectAll();
    }

    /// <summary>
    /// Writes the MJPEG body (after the host has sent the 200 and <see cref="ContentType"/>) until the viewer goes away,
    /// the stream is disconnected, or the mode turns to one that refuses viewers.
    /// </summary>
    public async Task StreamAsync(int cameraId, Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        CancellationToken disconnected;
        lock (_gate)
        {
            disconnected = _disconnect.Token;
            _openStreams++;
            _streamsServed++;
        }

        using var stream = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnected);
        var frame = 0L;
        try
        {
            while (!stream.IsCancellationRequested)
            {
                var mode = Mode;
                if (mode is EmulatedCameraMode.Unavailable or EmulatedCameraMode.Unauthorized)
                {
                    return;
                }

                if (mode == EmulatedCameraMode.Live)
                {
                    // Only the viewer leaving interrupts a part: a disconnect or a refusing mode ends the stream between
                    // parts, so a viewer can tell a closed stream from a broken one.
                    await WritePartAsync(body, CameraFrameRenderer.RenderJpeg(_plant(), cameraId, frame++), cancellationToken).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _framesSent++;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1 / FramesPerSecond), stream.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stream.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // The viewer's connection is gone.
        }
        finally
        {
            lock (_gate)
            {
                _openStreams--;
            }
        }
    }

    private static async Task WritePartAsync(Stream body, byte[] jpeg, CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n"));
        await body.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await body.WriteAsync(jpeg, cancellationToken).ConfigureAwait(false);
        await body.WriteAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
