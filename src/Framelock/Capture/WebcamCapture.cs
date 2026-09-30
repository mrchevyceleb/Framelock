using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Framelock.Core;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Framelock.Capture;

public sealed record WebcamDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record WebcamConfig(string? DeviceId, int Width, int Height, int Fps);
public sealed record WebcamFrame(int Width, int Height, byte[] Pixels);

/// <summary>CPU camera frames, triple-buffered so capture and GPU upload never share a writable array.</summary>
public sealed class WebcamCapture : IDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private readonly object _frameLock = new();
    private byte[] _work = [], _shared = [], _video = [];
    private bool _fresh, _disposed;
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastFrameTicks;
    private string? _error;

    public string Name { get; private set; } = "Webcam";
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double FrameRate { get; private set; }
    public string? Error => Volatile.Read(ref _error);
    public bool IsReady => Error == null && _firstFrame.Task.IsCompletedSuccessfully;
    public bool IsStalled => IsReady && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastFrameTicks)).TotalSeconds > 5;
    public event Action<string>? Failed;

    public static async Task<WebcamDevice[]> GetDevicesAsync() =>
        (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)).Select(d => new WebcamDevice(d.Id, d.Name)).ToArray();

    /// <summary>Call from the UI (STA) thread, as required by MediaCapture.InitializeAsync.</summary>
    public async Task StartAsync(WebcamConfig config)
    {
        try
        {
            var devices = await GetDevicesAsync();
            var device = string.IsNullOrEmpty(config.DeviceId) ? devices.FirstOrDefault() : devices.FirstOrDefault(d => d.Id == config.DeviceId);
            if (device == null) throw new InvalidOperationException("Webcam not found. Connect a camera and choose it in Webcam settings.");
            Name = device.Name;
            _capture = new MediaCapture();
            _capture.Failed += (_, e) => Fail("Webcam disconnected or unavailable: " + e.Message);
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            });
            var source = _capture.FrameSources.Values
                .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                .OrderBy(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord ? 0 : 1).FirstOrDefault()
                ?? throw new InvalidOperationException("This camera has no colour video stream.");
            static double Fps(MediaFrameFormat f) => f.FrameRate.Denominator == 0 ? 0 : (double)f.FrameRate.Numerator / f.FrameRate.Denominator;
            // Treat fractional broadcast rates (29.97/59.94) as the requested 30/60 fps.
            // Within each rate bucket prefer resolution; some cameras only offer 60 fps at 720p.
            var format = source.SupportedFormats.Where(f => f.VideoFormat is { Width: > 0, Height: > 0 } && Fps(f) > 0)
                .OrderBy(f => Math.Round(Math.Abs(Fps(f) - config.Fps)))
                .ThenBy(f => Math.Abs((double)f.VideoFormat.Width - config.Width) + Math.Abs((double)f.VideoFormat.Height - config.Height))
                .ThenBy(f => Math.Abs(Fps(f) - config.Fps))
                .FirstOrDefault() ?? throw new InvalidOperationException("This camera has no supported video formats.");
            await source.SetFormatAsync(format);
            Width = (int)format.VideoFormat.Width;
            Height = (int)format.VideoFormat.Height;
            FrameRate = Fps(format);
            _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += OnFrameArrived;
            var result = await _reader.StartAsync();
            if (result != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException("Webcam failed to start: " + result);
            await _firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Log.Info($"Webcam started: {Name}, {Width}x{Height}@{FrameRate:0.##}");
        }
        catch (UnauthorizedAccessException)
        {
            Dispose();
            throw new InvalidOperationException("Camera access is blocked. Enable camera access for desktop apps in Windows Settings → Privacy & security → Camera.");
        }
        catch { Dispose(); throw; }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            using var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap == null) return;
            using var bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            lock (_frameLock)
            {
                if (_disposed) return;
                int bytes = checked(bgra.PixelWidth * bgra.PixelHeight * 4);
                if (_work.Length != bytes) _work = new byte[bytes];
                bgra.CopyToBuffer(_work.AsBuffer());
                // Camera frames are opaque, even when a driver leaves the alpha channel empty.
                for (int i = 3; i < bytes; i += 4) _work[i] = 255;
                Width = bgra.PixelWidth;
                Height = bgra.PixelHeight;
                (_work, _shared) = (_shared, _work);
                _fresh = true;
                Interlocked.Exchange(ref _lastFrameTicks, Stopwatch.GetTimestamp());
                _firstFrame.TrySetResult();
            }
        }
        catch (Exception ex) { Fail("Webcam frame failed: " + ex.Message); }
    }

    public WebcamFrame? TakeFrame()
    {
        lock (_frameLock)
        {
            if (!_fresh || _disposed) return null;
            (_shared, _video) = (_video, _shared);
            _fresh = false;
            return new WebcamFrame(Width, Height, _video);
        }
    }

    private void Fail(string message)
    {
        if (Interlocked.CompareExchange(ref _error, message, null) != null) return;
        _firstFrame.TrySetException(new InvalidOperationException(message));
        Failed?.Invoke(message);
    }

    public void Dispose()
    {
        lock (_frameLock) { if (_disposed) return; _disposed = true; }
        if (_reader != null) { _reader.FrameArrived -= OnFrameArrived; _reader.Dispose(); _reader = null; }
        _capture?.Dispose();
        _capture = null;
    }
}
