using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Framelock.Core;

public enum SourceKind { Display, Window, Region }
public enum ScaleMode { Fit, Fill, Stretch }
public enum ScaleFilter { Auto, Bilinear, Bicubic, Lanczos, Area }
public enum RateControlMode { ConstantQuality, Cbr, Vbr }
public enum SpeedPreset { Performance, Balanced, Quality, MaxQuality }
public enum ContainerFormat { Mp4, Mkv, Mov }
public enum DesktopAudioMode { DefaultDevice, SpecificDevice, CapturedApp, SpecificApp, ExcludeApp }
public enum HdrMode { Auto, Off }
public enum OverlayKind { Image, Text }
public enum OverlayAnchor { Custom, TopLeft, TopCenter, TopRight, MiddleLeft, Center, MiddleRight, BottomLeft, BottomCenter, BottomRight }
public enum OverlayShowMode { Always, Interval }
public enum RecorderPriority { Normal, AboveNormal, High }

public sealed class AppSettings : ObservableObject
{
    // ---------------- Source ----------------
    private SourceKind _sourceKind = SourceKind.Display;
    public SourceKind SourceKind { get => _sourceKind; set => Set(ref _sourceKind, value); }

    private string? _displayId;
    /// <summary>GDI device name of the monitor (e.g. \\.\DISPLAY1). Null = primary.</summary>
    public string? DisplayId { get => _displayId; set => Set(ref _displayId, value); }

    private string? _windowTitle;
    public string? WindowTitle { get => _windowTitle; set => Set(ref _windowTitle, value); }
    private string? _windowExe;
    public string? WindowExe { get => _windowExe; set => Set(ref _windowExe, value); }
    private string? _windowClass;
    public string? WindowClass { get => _windowClass; set => Set(ref _windowClass, value); }
    private long _windowHandle;
    /// <summary>Last known HWND; re-resolved by exe/title/class when stale.</summary>
    public long WindowHandle { get => _windowHandle; set => Set(ref _windowHandle, value); }

    private string? _regionDisplayId;
    public string? RegionDisplayId { get => _regionDisplayId; set => Set(ref _regionDisplayId, value); }
    private int _regionX, _regionY, _regionWidth = 1920, _regionHeight = 1080;
    /// <summary>Region in physical pixels, relative to the monitor's top-left corner.</summary>
    public int RegionX { get => _regionX; set => Set(ref _regionX, value); }
    public int RegionY { get => _regionY; set => Set(ref _regionY, value); }
    public int RegionWidth { get => _regionWidth; set => Set(ref _regionWidth, value); }
    public int RegionHeight { get => _regionHeight; set => Set(ref _regionHeight, value); }

    private bool _captureCursor = true;
    public bool CaptureCursor { get => _captureCursor; set => Set(ref _captureCursor, value); }
    private bool _windowClientOnly = true;
    public bool WindowClientOnly { get => _windowClientOnly; set => Set(ref _windowClientOnly, value); }
    private HdrMode _hdrMode = HdrMode.Auto;
    public HdrMode HdrMode { get => _hdrMode; set => Set(ref _hdrMode, value); }
    private int _hdrPeakNits;
    /// <summary>0 = use the Windows "SDR content brightness" value.</summary>
    public int SdrWhiteNitsOverride { get => _hdrPeakNits; set => Set(ref _hdrPeakNits, value); }

    // ---------------- Output ----------------
    private bool _useSourceResolution;
    public bool UseSourceResolution { get => _useSourceResolution; set => Set(ref _useSourceResolution, value); }
    private int _outputWidth = 3840, _outputHeight = 2160;
    public int OutputWidth { get => _outputWidth; set => Set(ref _outputWidth, value); }
    public int OutputHeight { get => _outputHeight; set => Set(ref _outputHeight, value); }
    private ScaleMode _scaleMode = ScaleMode.Fit;
    public ScaleMode ScaleMode { get => _scaleMode; set => Set(ref _scaleMode, value); }
    private ScaleFilter _scaleFilter = ScaleFilter.Auto;
    public ScaleFilter ScaleFilter { get => _scaleFilter; set => Set(ref _scaleFilter, value); }
    private string _backgroundColor = "#000000";
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    private int _fps = 60;
    public int Fps { get => _fps; set => Set(ref _fps, value); }

    // ---------------- Encoder ----------------
    private string _videoEncoder = "auto";
    public string VideoEncoder { get => _videoEncoder; set => Set(ref _videoEncoder, value); }
    private RateControlMode _rateControl = RateControlMode.ConstantQuality;
    public RateControlMode RateControl { get => _rateControl; set => Set(ref _rateControl, value); }
    private int _quality = 20;
    public int Quality { get => _quality; set => Set(ref _quality, value); }
    private int _bitrateMbps = 80;
    public int BitrateMbps { get => _bitrateMbps; set => Set(ref _bitrateMbps, value); }
    private SpeedPreset _speedPreset = SpeedPreset.Balanced;
    public SpeedPreset SpeedPreset { get => _speedPreset; set => Set(ref _speedPreset, value); }
    private int _keyframeSeconds = 2;
    public int KeyframeSeconds { get => _keyframeSeconds; set => Set(ref _keyframeSeconds, value); }
    private bool _bFrames = true;
    public bool BFrames { get => _bFrames; set => Set(ref _bFrames, value); }
    private ContainerFormat _container = ContainerFormat.Mp4;
    public ContainerFormat Container { get => _container; set => Set(ref _container, value); }
    private bool _crashSafe = true;
    /// <summary>Record to MKV (survives crashes / power loss) and convert to the chosen container when finished.</summary>
    public bool CrashSafe { get => _crashSafe; set => Set(ref _crashSafe, value); }

    // ---------------- Audio ----------------
    private bool _desktopAudio = true;
    public bool DesktopAudioEnabled { get => _desktopAudio; set => Set(ref _desktopAudio, value); }
    private DesktopAudioMode _desktopMode = DesktopAudioMode.DefaultDevice;
    public DesktopAudioMode DesktopAudioMode { get => _desktopMode; set => Set(ref _desktopMode, value); }
    private string? _desktopDeviceId;
    public string? DesktopDeviceId { get => _desktopDeviceId; set => Set(ref _desktopDeviceId, value); }
    private string? _desktopAppExe;
    public string? DesktopAppExe { get => _desktopAppExe; set => Set(ref _desktopAppExe, value); }
    private double _desktopVolume = 1.0;
    public double DesktopVolume { get => _desktopVolume; set => Set(ref _desktopVolume, value); }
    private bool _desktopMuted;
    public bool DesktopMuted { get => _desktopMuted; set => Set(ref _desktopMuted, value); }

    private bool _micEnabled = true;
    public bool MicEnabled { get => _micEnabled; set => Set(ref _micEnabled, value); }
    private string? _micDeviceId;
    public string? MicDeviceId { get => _micDeviceId; set => Set(ref _micDeviceId, value); }
    private double _micVolume = 1.0;
    public double MicVolume { get => _micVolume; set => Set(ref _micVolume, value); }
    private bool _micMuted;
    public bool MicMuted { get => _micMuted; set => Set(ref _micMuted, value); }
    private bool _micNoiseGate;
    public bool MicNoiseGate { get => _micNoiseGate; set => Set(ref _micNoiseGate, value); }
    private double _micGateDb = -45;
    public double MicGateDb { get => _micGateDb; set => Set(ref _micGateDb, value); }
    private bool _micVoiceProcessing;
    public bool MicVoiceProcessing { get => _micVoiceProcessing; set => Set(ref _micVoiceProcessing, value); }
    private bool _micPushToTalk;
    public bool MicPushToTalk { get => _micPushToTalk; set => Set(ref _micPushToTalk, value); }
    private string _pushToTalkKey = "CapsLock";
    public string PushToTalkKey { get => _pushToTalkKey; set => Set(ref _pushToTalkKey, value); }
    private int _micSyncOffsetMs;
    /// <summary>Positive = delay the mic, negative = advance it.</summary>
    public int MicSyncOffsetMs { get => _micSyncOffsetMs; set => Set(ref _micSyncOffsetMs, value); }

    private bool _separateTracks = true;
    public bool SeparateAudioTracks { get => _separateTracks; set => Set(ref _separateTracks, value); }
    private int _audioBitrate = 320;
    public int AudioBitrateKbps { get => _audioBitrate; set => Set(ref _audioBitrate, value); }

    // ---------------- Overlays ----------------
    public ObservableCollection<OverlayItem> Overlays { get; set; } = new();

    // ---------------- Replay buffer ----------------
    private bool _replayEnabled;
    public bool ReplayEnabled { get => _replayEnabled; set => Set(ref _replayEnabled, value); }
    private int _replaySeconds = 60;
    public int ReplaySeconds { get => _replaySeconds; set => Set(ref _replaySeconds, value); }

    // ---------------- Hotkeys ----------------
    private string _hkRecord = "Ctrl+Alt+F9";
    public string HotkeyRecord { get => _hkRecord; set => Set(ref _hkRecord, value); }
    private string _hkPause = "Ctrl+Alt+F8";
    public string HotkeyPause { get => _hkPause; set => Set(ref _hkPause, value); }
    private string _hkReplay = "Ctrl+Alt+F10";
    public string HotkeyReplay { get => _hkReplay; set => Set(ref _hkReplay, value); }
    private string _hkShot = "Ctrl+Alt+F11";
    public string HotkeyScreenshot { get => _hkShot; set => Set(ref _hkShot, value); }
    private string _hkMic = "Ctrl+Alt+F7";
    public string HotkeyMicMute { get => _hkMic; set => Set(ref _hkMic, value); }
    private string _hkMarker = "Ctrl+Alt+F12";
    public string HotkeyMarker { get => _hkMarker; set => Set(ref _hkMarker, value); }

    // ---------------- General ----------------
    private string _outputFolder = Paths.DefaultOutputFolder;
    public string OutputFolder { get => _outputFolder; set => Set(ref _outputFolder, value); }
    private string _fileNameTemplate = "{source} {date} {time}";
    public string FileNameTemplate { get => _fileNameTemplate; set => Set(ref _fileNameTemplate, value); }
    private int _countdown = 3;
    public int CountdownSeconds { get => _countdown; set => Set(ref _countdown, value); }
    private bool _showHud = true;
    public bool ShowHud { get => _showHud; set => Set(ref _showHud, value); }
    private bool _showBorder = true;
    public bool ShowRegionBorder { get => _showBorder; set => Set(ref _showBorder, value); }
    private bool _hideApp = true;
    public bool HideAppFromCapture { get => _hideApp; set => Set(ref _hideApp, value); }
    private bool _minimizeOnRecord;
    public bool MinimizeWhileRecording { get => _minimizeOnRecord; set => Set(ref _minimizeOnRecord, value); }
    private bool _closeToTray = true;
    public bool CloseToTray { get => _closeToTray; set => Set(ref _closeToTray, value); }
    private bool _playSounds = true;
    public bool PlaySounds { get => _playSounds; set => Set(ref _playSounds, value); }
    private RecorderPriority _priority = RecorderPriority.AboveNormal;
    public RecorderPriority Priority { get => _priority; set => Set(ref _priority, value); }
    private bool _previewWhileRecording = true;
    public bool PreviewWhileRecording { get => _previewWhileRecording; set => Set(ref _previewWhileRecording, value); }
    private int _splitMinutes;
    /// <summary>0 = never split. Otherwise start a new file every N minutes (at a keyframe, no gap).</summary>
    public int SplitEveryMinutes { get => _splitMinutes; set => Set(ref _splitMinutes, value); }
    private bool _saveMarkers = true;
    public bool SaveMarkers { get => _saveMarkers; set => Set(ref _saveMarkers, value); }
}

public sealed class OverlayItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _name = "Overlay";
    public string Name { get => _name; set => Set(ref _name, value); }
    private OverlayKind _kind;
    public OverlayKind Kind { get => _kind; set => Set(ref _kind, value); }
    private bool _visible = true;
    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    private string? _imagePath;
    public string? ImagePath { get => _imagePath; set => Set(ref _imagePath, value); }

    private string _text = "@yourhandle";
    public string Text { get => _text; set => Set(ref _text, value); }
    private string _fontFamily = "Segoe UI";
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }
    private bool _bold = true;
    public bool Bold { get => _bold; set => Set(ref _bold, value); }
    private bool _italic;
    public bool Italic { get => _italic; set => Set(ref _italic, value); }
    private string _textColor = "#FFFFFF";
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }
    private string _outlineColor = "#000000";
    public string OutlineColor { get => _outlineColor; set => Set(ref _outlineColor, value); }
    private double _outlineWidth = 0.08;
    /// <summary>Outline thickness as a fraction of the font size (0 = none).</summary>
    public double OutlineWidth { get => _outlineWidth; set => Set(ref _outlineWidth, value); }
    private bool _shadow = true;
    public bool Shadow { get => _shadow; set => Set(ref _shadow, value); }
    private string _backgroundColor = "#00000000";
    public string TextBackground { get => _backgroundColor; set => Set(ref _backgroundColor, value); }

    // Layout, in normalized output coordinates (0..1)
    private double _x = 0.02, _y = 0.03, _width = 0.18;
    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    /// <summary>Width as a fraction of the output width. Height follows the content aspect ratio.</summary>
    public double Width { get => _width; set => Set(ref _width, value); }
    private OverlayAnchor _anchor = OverlayAnchor.TopLeft;
    public OverlayAnchor Anchor { get => _anchor; set => Set(ref _anchor, value); }
    private double _margin = 0.025;
    public double Margin { get => _margin; set => Set(ref _margin, value); }
    private double _opacity = 1.0;
    public double Opacity { get => _opacity; set => Set(ref _opacity, value); }

    private OverlayShowMode _showMode = OverlayShowMode.Always;
    public OverlayShowMode ShowMode { get => _showMode; set => Set(ref _showMode, value); }
    private int _intervalSeconds = 300;
    public int IntervalSeconds { get => _intervalSeconds; set => Set(ref _intervalSeconds, value); }
    private int _durationSeconds = 10;
    public int DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, value); }
    private bool _fade = true;
    public bool Fade { get => _fade; set => Set(ref _fade, value); }

    // ---------- runtime (not persisted) ----------
    private OverlayBitmap? _bitmap;
    [JsonIgnore] public OverlayBitmap? Bitmap { get => _bitmap; set { if (Set(ref _bitmap, value)) OnPropertyChanged(nameof(AspectRatio)); } }
    /// <summary>Content height / width.</summary>
    [JsonIgnore] public double AspectRatio => _bitmap is { Width: > 0 } b ? (double)b.Height / b.Width : 0.25;
    private string? _loadError;
    [JsonIgnore] public string? LoadError { get => _loadError; set => Set(ref _loadError, value); }
}

/// <summary>Premultiplied BGRA pixels ready for GPU upload. Immutable once created.</summary>
public sealed class OverlayBitmap
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Pixels { get; init; }
}
