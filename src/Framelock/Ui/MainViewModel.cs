using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Framelock.Audio;
using Framelock.Capture;
using Framelock.Core;
using Framelock.Encoding;
using Framelock.Engine;
using Framelock.Graphics;
using NAudio.CoreAudioApi;

namespace Framelock.Ui;

public sealed record EncoderChoice(string Id, string Label);

/// <summary>Everything the main window binds to. Settings edits go straight to <see cref="AppSettings"/> (autosaved).</summary>
public sealed class MainViewModel : ObservableObject
{
    public AppSettings S { get; }
    public RecorderController Rec { get; }
    public UpdateService Updates => App.Updates;

    public ObservableCollection<DisplayInfo> Displays { get; } = new();
    public ObservableCollection<WindowInfo> Windows { get; } = new();
    public ObservableCollection<EncoderChoice> Encoders { get; } = new();
    public ObservableCollection<AudioDevice> RenderDevices { get; } = new();
    public ObservableCollection<AudioDevice> CaptureDevices { get; } = new();
    public ObservableCollection<string> AudioApps { get; } = new();
    public ObservableCollection<WebcamDevice> Webcams { get; } = new();
    public ResolutionPreset[] WebcamResolutions { get; } =
    {
        new("720p", 1280, 720, "16:9"), new("1080p", 1920, 1080, "16:9"),
        new("4K", 3840, 2160, "16:9"), new("480p", 640, 480, "4:3"),
    };
    private string _webcamDeviceStatus = "Looking for cameras…";
    public string WebcamDeviceStatus { get => _webcamDeviceStatus; private set => Set(ref _webcamDeviceStatus, value); }

    public ResolutionPreset[] Resolutions => Presets.Resolutions;
    public int[] FrameRates => Presets.FrameRates;
    public int[] AudioBitrates => Presets.AudioBitrates;
    public int[] ReplayLengths => Presets.ReplayLengths;
    public int[] Countdowns => Presets.Countdowns;
    public QuickPreset[] QuickPresets => Presets.Quick;
    public Array DesktopModes => Enum.GetValues<DesktopAudioMode>();
    public Array ScaleFilters => Enum.GetValues<ScaleFilter>();
    public Array Priorities => Enum.GetValues<RecorderPriority>();
    public Array Anchors => Enum.GetValues<OverlayAnchor>();
    public int[] SplitChoices { get; } = { 0, 5, 10, 15, 30, 60 };
    public int[] KeyframeChoices { get; } = { 1, 2, 3, 4, 5 };
    public string[] FontFamilies { get; } = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s).ToArray();

    private readonly DispatcherTimer _meterTimer;

    public MainViewModel(AppSettings settings, RecorderController rec)
    {
        S = settings;
        Rec = rec;
        RefreshDisplays();
        RefreshWindows();
        RefreshAudioDevices();
        RefreshEncoders();
        _ = RefreshWebcamsAsync();
        EncoderCatalog.ProbeCompleted += () => Application.Current.Dispatcher.BeginInvoke(RefreshEncoders);
        S.PropertyChanged += OnSettingChanged;
        S.Overlays.CollectionChanged += (_, e) =>
        {
            // Unsubscribe-then-subscribe keeps exactly one handler per item across add/remove/move/replace.
            if (e.OldItems != null) foreach (OverlayItem o in e.OldItems) o.PropertyChanged -= OnOverlayChanged;
            if (e.NewItems != null) foreach (OverlayItem o in e.NewItems) { o.PropertyChanged -= OnOverlayChanged; o.PropertyChanged += OnOverlayChanged; }
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                foreach (var o in S.Overlays) { o.PropertyChanged -= OnOverlayChanged; o.PropertyChanged += OnOverlayChanged; }
            Rec.PushOverlays();
        };
        foreach (var o in S.Overlays) o.PropertyChanged += OnOverlayChanged;
        SelectedOverlay = S.Overlays.FirstOrDefault();

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _meterTimer.Start();
        Rec.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecorderController.Stats)) OnPropertyChanged(nameof(StatsText));
        };

        RecordCommand = new RelayCommand(() => _ = Rec.ToggleRecordingAsync());
        PauseCommand = new RelayCommand(Rec.TogglePause, () => Rec.IsRecording);
        ScreenshotCommand = new RelayCommand(() => _ = Rec.TakeScreenshotAsync());
        SaveReplayCommand = new RelayCommand(() => _ = Rec.SaveReplayAsync(), () => Rec.ReplayActive);
        MarkerCommand = new RelayCommand(Rec.AddMarker, () => Rec.IsRecording);
        OpenFolderCommand = new RelayCommand(() => OpenFolder(S.OutputFolder));
        AddImageOverlayCommand = new RelayCommand(AddImageOverlay);
        AddTextOverlayCommand = new RelayCommand(AddTextOverlay);
        AddWebcamOverlayCommand = new RelayCommand(AddWebcamOverlay);
        ReconnectWebcamCommand = new RelayCommand(() => _ = Rec.SyncWebcamAsync(retry: true), () => !Rec.IsBusy);
        RemoveOverlayCommand = new RelayCommand(RemoveOverlay, () => SelectedOverlay != null);
        DuplicateOverlayCommand = new RelayCommand(DuplicateOverlay, () => SelectedOverlay != null);
        MoveOverlayUpCommand = new RelayCommand(() => MoveOverlay(-1), () => SelectedOverlay != null);
        MoveOverlayDownCommand = new RelayCommand(() => MoveOverlay(1), () => SelectedOverlay != null);
        BrowseOverlayImageCommand = new RelayCommand(BrowseOverlayImage, () => SelectedOverlay?.Kind == OverlayKind.Image);
        ApplyQuickPresetCommand = new RelayCommand(p => { if (p is QuickPreset q) ApplyQuickPreset(q); });
        SetAnchorCommand = new RelayCommand(p => { if (SelectedOverlay != null && p is string a) SelectedOverlay.Anchor = Enum.Parse<OverlayAnchor>(a); });
        UseSuggestedBitrateCommand = new RelayCommand(() => S.BitrateMbps = SuggestedBitrate);

        OpenRecordingCommand = new RelayCommand(p => { if (p is RecordingItem r) OpenFile(r.Path); });
        RevealRecordingCommand = new RelayCommand(p => { if (p is RecordingItem r) RevealFile(r.Path); });
        FixAudioCommand = new RelayCommand(p => { if (p is RecordingItem r) RemixWindow.ShowFor(r.Path); });
        RecycleRecordingCommand = new RelayCommand(p => { if (p is RecordingItem r) RecycleRecording(r); });
        RefreshRecordingsCommand = new RelayCommand(() => Recordings.Refresh(S.OutputFolder));
        // New files (recordings, replays, fixed audio) show up in the Recordings tab right away.
        Rec.Notify += n => { if (n.FilePath != null) Application.Current.Dispatcher.BeginInvoke(() => Recordings.RefreshIfLoaded(S.OutputFolder)); };
        RemixWindow.Saved += _ => Recordings.RefreshIfLoaded(S.OutputFolder);
        var folder = S.OutputFolder;
        Task.Run(() => AudioRemixer.CleanStaleTemps(folder));
    }

    public RecordingsModel Recordings { get; } = new();
    public ICommand OpenRecordingCommand { get; }
    public ICommand RevealRecordingCommand { get; }
    public ICommand FixAudioCommand { get; }
    public ICommand RecycleRecordingCommand { get; }
    public ICommand RefreshRecordingsCommand { get; }

    private void RecycleRecording(RecordingItem r)
    {
        try
        {
            Native.MoveToRecycleBin(r.Path);
            Recordings.Items.Remove(r);
            Recordings.Refresh(S.OutputFolder);
            Toast.Show(new Notification("Moved to the Recycle Bin", System.IO.Path.GetFileName(r.Path)));
        }
        catch (Exception ex) { Toast.Show(new Notification("Couldn't delete", ex.Message, IsError: true)); }
    }

    public ICommand RecordCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ScreenshotCommand { get; }
    public ICommand SaveReplayCommand { get; }
    public ICommand MarkerCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand AddImageOverlayCommand { get; }
    public ICommand AddTextOverlayCommand { get; }
    public ICommand AddWebcamOverlayCommand { get; }
    public ICommand ReconnectWebcamCommand { get; }
    public ICommand RemoveOverlayCommand { get; }
    public ICommand DuplicateOverlayCommand { get; }
    public ICommand MoveOverlayUpCommand { get; }
    public ICommand MoveOverlayDownCommand { get; }
    public ICommand BrowseOverlayImageCommand { get; }
    public ICommand ApplyQuickPresetCommand { get; }
    public ICommand SetAnchorCommand { get; }
    public ICommand UseSuggestedBitrateCommand { get; }

    // ================================================================== source

    private bool _refreshingWebcams;
    public async Task RefreshWebcamsAsync()
    {
        if (_refreshingWebcams) return;
        _refreshingWebcams = true;
        try
        {
            var devices = await WebcamCapture.GetDevicesAsync();
            // Keep a saved, unplugged camera selected; never silently record a different camera.
            var available = devices.ToList();
            if (!string.IsNullOrEmpty(S.WebcamDeviceId) && devices.All(d => d.Id != S.WebcamDeviceId))
                available.Add(new WebcamDevice(S.WebcamDeviceId, "Saved camera (disconnected)"));
            // Retain existing items so open camera pickers keep their selection throughout refresh.
            for (int i = Webcams.Count - 1; i >= 0; i--)
                if (available.All(d => d.Id != Webcams[i].Id)) Webcams.RemoveAt(i);
            foreach (var device in available)
                if (Webcams.All(d => d.Id != device.Id)) Webcams.Add(device);
            if (S.WebcamDeviceId == null) S.WebcamDeviceId = devices.FirstOrDefault()?.Id;
            WebcamDeviceStatus = devices.Length == 0 ? "No webcam found. Connect one, then reopen the camera list." : "Choose your camera below.";
            OnPropertyChanged(nameof(WebcamDeviceId));
            OnPropertyChanged(nameof(SourceSummary));
        }
        catch (Exception ex) { WebcamDeviceStatus = "Couldn't list cameras: " + ex.Message; }
        finally { _refreshingWebcams = false; }
    }

    public string? WebcamDeviceId
    {
        get => S.WebcamDeviceId;
        set
        {
            // ComboBox temporarily clears its selection as the device list is refreshed.
            if (_refreshingWebcams || string.IsNullOrEmpty(value)) return;
            S.WebcamDeviceId = value;
        }
    }

    public ResolutionPreset SelectedWebcamResolution
    {
        get => WebcamResolutions.FirstOrDefault(r => r.Width == S.WebcamWidth && r.Height == S.WebcamHeight) ?? WebcamResolutions[0];
        set
        {
            if (value == null) return;
            S.WebcamWidth = value.Width;
            S.WebcamHeight = value.Height;
            OnPropertyChanged();
        }
    }

    public void RefreshDisplays()
    {
        var list = DisplayInfo.GetAll();
        Displays.Clear();
        foreach (var d in list) Displays.Add(d);
        if (S.DisplayId == null || list.All(d => d.DeviceName != S.DisplayId)) S.DisplayId = list.FirstOrDefault()?.DeviceName;
        if (S.RegionDisplayId == null || list.All(d => d.DeviceName != S.RegionDisplayId)) S.RegionDisplayId = S.DisplayId;
    }

    public void RefreshWindows()
    {
        var current = S.WindowHandle;
        var list = WindowInfo.GetCapturable(true);
        Windows.Clear();
        foreach (var w in list) Windows.Add(w);
        var match = Windows.FirstOrDefault(w => w.Handle.ToInt64() == current)
                    ?? Windows.FirstOrDefault(w => string.Equals(w.ExeName, S.WindowExe, StringComparison.OrdinalIgnoreCase) && w.Title == S.WindowTitle)
                    ?? Windows.FirstOrDefault(w => string.Equals(w.ExeName, S.WindowExe, StringComparison.OrdinalIgnoreCase));
        _selectedWindow = null;
        if (match != null) SelectedWindow = match; // re-matched after a restart: persist the new HWND too
        else OnPropertyChanged(nameof(SelectedWindow));
    }

    private WindowInfo? _selectedWindow;
    public WindowInfo? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (!Set(ref _selectedWindow, value) || value == null) return;
            S.WindowTitle = value.Title;
            S.WindowExe = value.ExeName;
            S.WindowClass = value.ClassName;
            S.WindowHandle = value.Handle.ToInt64();
        }
    }

    public string SourceSummary => S.SourceKind switch
    {
        SourceKind.Window => _selectedWindow?.Title ?? S.WindowTitle ?? "No window selected",
        SourceKind.Region => $"{S.RegionWidth}×{S.RegionHeight} at {S.RegionX},{S.RegionY}",
        SourceKind.Webcam => Webcams.FirstOrDefault(c => c.Id == S.WebcamDeviceId)?.Name ?? "Webcam",
        _ => Displays.FirstOrDefault(d => d.DeviceName == S.DisplayId)?.Label ?? "Display",
    };

    // ================================================================== output

    public ResolutionPreset SelectedResolution
    {
        get
        {
            if (S.UseSourceResolution) return Resolutions[0];
            return Resolutions.FirstOrDefault(r => r.Width == S.OutputWidth && r.Height == S.OutputHeight) ?? Resolutions[^1];
        }
        set
        {
            if (value == null) return;
            if (value.IsSource) S.UseSourceResolution = true;
            else if (value.IsCustom) { S.UseSourceResolution = false; _customRequested = true; }
            else
            {
                S.UseSourceResolution = false;
                S.OutputWidth = value.Width;
                S.OutputHeight = value.Height;
                _customRequested = false;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomResolution));
        }
    }
    private bool _customRequested;
    public bool IsCustomResolution => !S.UseSourceResolution && (_customRequested || SelectedResolution.IsCustom);

    public int CustomWidth
    {
        get => S.OutputWidth;
        set => S.OutputWidth = Math.Clamp(value & ~1, 128, 8192);
    }
    public int CustomHeight
    {
        get => S.OutputHeight;
        set => S.OutputHeight = Math.Clamp(value & ~1, 128, 8192);
    }

    public string OutputSummary
    {
        get
        {
            var st = Rec.Pipeline?.Config;
            string res = st != null ? $"{st.Width}×{st.Height}" : S.UseSourceResolution ? "Source size" : $"{S.OutputWidth}×{S.OutputHeight}";
            return $"{res} · {S.Fps} fps";
        }
    }

    private void ApplyQuickPreset(QuickPreset q)
    {
        S.UseSourceResolution = false;
        S.OutputWidth = q.Width;
        S.OutputHeight = q.Height;
        S.Fps = q.Fps;
        S.Quality = q.Quality;
        S.RateControl = RateControlMode.ConstantQuality;
        _customRequested = false;
        OnPropertyChanged(nameof(SelectedResolution));
        OnPropertyChanged(nameof(IsCustomResolution));
        Toast.Show(new Notification("Preset applied", q.Name + " · " + q.Description));
    }

    // ================================================================== encoder

    public void RefreshEncoders()
    {
        var keep = S.VideoEncoder;
        Encoders.Clear();
        var (w, h) = OutputSizeGuess();
        var auto = EncoderCatalog.Probed ? EncoderCatalog.Resolve("auto", w, h).Label : "detecting…";
        Encoders.Add(new EncoderChoice("auto", $"Automatic  ·  {auto}"));
        foreach (var e in EncoderCatalog.Available) Encoders.Add(new EncoderChoice(e.Id, e.Label));
        if (EncoderCatalog.Probed && Encoders.All(e => e.Id != keep)) keep = "auto";
        S.VideoEncoder = keep;
        OnPropertyChanged(nameof(SelectedEncoder));
        OnPropertyChanged(nameof(EncoderWarning));
    }

    /// <summary>Wrapper so the ComboBox can be repopulated without losing the stored choice.</summary>
    public EncoderChoice? SelectedEncoder
    {
        get => Encoders.FirstOrDefault(e => e.Id == S.VideoEncoder) ?? Encoders.FirstOrDefault();
        set { if (value != null) S.VideoEncoder = value.Id; }
    }

    private (int w, int h) OutputSizeGuess()
    {
        if (!S.UseSourceResolution) return (S.OutputWidth, S.OutputHeight);
        var c = Rec.Pipeline?.Config;
        return c != null ? (c.Width, c.Height) : (3840, 2160);
    }

    public string? EncoderWarning
    {
        get
        {
            if (!EncoderCatalog.Probed) return null;
            var (w, h) = OutputSizeGuess();
            var e = EncoderCatalog.Find(S.VideoEncoder);
            if (e != null && Math.Max(w, h) > e.MaxDimension) return $"{e.CodecLabel} can't encode {w}×{h} - HEVC or AV1 will be used instead.";
            if (!EncoderCatalog.Available.Any(x => x.IsHardware)) return "No hardware encoder found - using the CPU. High resolutions at high frame rates may drop frames.";
            return null;
        }
    }

    public string QualityLabel => Presets.QualityLabel(S.Quality);
    public int SuggestedBitrate { get { var (w, h) = OutputSizeGuess(); return Presets.SuggestedBitrateMbps(w, h, S.Fps); } }
    public string SuggestedBitrateText => $"Suggested for {OutputSummary}: {SuggestedBitrate} Mbps";

    public string EstimatedSize
    {
        get
        {
            if (S.RateControl == RateControlMode.ConstantQuality) return "File size depends on how busy the picture is.";
            double gbPerHour = (S.BitrateMbps + S.AudioBitrateKbps / 1000.0 * (S.SeparateAudioTracks ? 3 : 1)) * 3600 / 8 / 1000;
            return $"≈ {gbPerHour:F1} GB per hour";
        }
    }

    // ================================================================== audio

    public void RefreshAudioDevices()
    {
        RenderDevices.Clear();
        RenderDevices.Add(new AudioDevice("", "System default", false));
        foreach (var d in AudioDevices.Get(DataFlow.Render)) RenderDevices.Add(d);
        CaptureDevices.Clear();
        CaptureDevices.Add(new AudioDevice("", "System default", false));
        foreach (var d in AudioDevices.Get(DataFlow.Capture)) CaptureDevices.Add(d);
        AudioApps.Clear();
        foreach (var exe in WindowInfo.GetAudioCandidateProcesses().Select(p => p.exe).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            AudioApps.Add(exe);
        if (!string.IsNullOrEmpty(S.DesktopAppExe) && !AudioApps.Contains(S.DesktopAppExe)) AudioApps.Insert(0, S.DesktopAppExe);
        OnPropertyChanged(nameof(DesktopDeviceId));
        OnPropertyChanged(nameof(MicDeviceId));
    }

    public string DesktopDeviceId
    {
        get => S.DesktopDeviceId ?? "";
        set => S.DesktopDeviceId = string.IsNullOrEmpty(value) ? null : value;
    }
    public string MicDeviceId
    {
        get => S.MicDeviceId ?? "";
        set => S.MicDeviceId = string.IsNullOrEmpty(value) ? null : value;
    }

    private float _desktopLevel, _micLevel;
    public float DesktopLevel { get => _desktopLevel; private set => Set(ref _desktopLevel, value); }
    public float MicLevel { get => _micLevel; private set => Set(ref _micLevel, value); }
    private bool _micGateOpen = true, _pttActive;
    public bool MicGateOpen { get => _micGateOpen; private set => Set(ref _micGateOpen, value); }
    public bool PushToTalkActive { get => _pttActive; private set => Set(ref _pttActive, value); }
    private string _desktopStatus = "", _micStatus = "";
    public string DesktopStatus { get => _desktopStatus; private set => Set(ref _desktopStatus, value); }
    public string MicStatus { get => _micStatus; private set => Set(ref _micStatus, value); }

    private void UpdateMeters()
    {
        var a = Rec.Pipeline?.Audio;
        if (a == null) { DesktopLevel = MicLevel = 0; return; }
        // Fast attack, smooth release.
        DesktopLevel = a.DesktopLevel >= DesktopLevel ? a.DesktopLevel : DesktopLevel * 0.85f + a.DesktopLevel * 0.15f;
        MicLevel = a.MicLevel >= MicLevel ? a.MicLevel : MicLevel * 0.85f + a.MicLevel * 0.15f;
        MicGateOpen = a.MicGateOpen;
        PushToTalkActive = a.PushToTalkActive;
        DesktopStatus = a.DesktopStatus;
        MicStatus = a.MicStatus;
    }

    public string StatsText
    {
        get
        {
            var st = Rec.Stats;
            if (st == null) return "";
            var parts = new List<string> { $"{(st.Encoding ? "Output" : "Preview")} {st.OutputFps:F0} fps" };
            if (st.Encoding) parts.Add($"{st.VideoMbps:F0} Mbps");
            if (st.Lagged > 0 || st.EncoderDropped > 0) parts.Add($"{st.Lagged + st.EncoderDropped} skipped");
            if (st.Hdr) parts.Add("HDR→SDR");
            return string.Join("  ·  ", parts);
        }
    }

    // ================================================================== overlays

    private OverlayItem? _selectedOverlay;
    public OverlayItem? SelectedOverlay
    {
        get => _selectedOverlay;
        set { if (Set(ref _selectedOverlay, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    private static readonly HashSet<string> TextRenderProps = new()
    {
        nameof(OverlayItem.Text), nameof(OverlayItem.FontFamily), nameof(OverlayItem.Bold), nameof(OverlayItem.Italic),
        nameof(OverlayItem.TextColor), nameof(OverlayItem.OutlineColor), nameof(OverlayItem.OutlineWidth), nameof(OverlayItem.Shadow),
        nameof(OverlayItem.TextBackground), nameof(OverlayItem.ImagePath), nameof(OverlayItem.Kind),
    };

    private void OnOverlayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not OverlayItem o) return;
        if (e.PropertyName is nameof(OverlayItem.LoadError) or nameof(OverlayItem.AspectRatio) or nameof(OverlayItem.WebcamAspectRatio)) return;
        if (e.PropertyName != null && TextRenderProps.Contains(e.PropertyName))
        {
            // Re-render on the next idle tick so fast typing doesn't render every keystroke twice.
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { OverlayRenderer.Refresh(o); Rec.PushOverlays(); });
            return;
        }
        Rec.PushOverlays();
    }

    private void AddImageOverlay()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an image (PNG with transparency works best)",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        var o = new OverlayItem
        {
            Kind = OverlayKind.Image,
            Name = Path.GetFileNameWithoutExtension(dlg.FileName),
            ImagePath = CopyToLibrary(dlg.FileName),
            Anchor = OverlayAnchor.TopRight,
            Width = 0.12,
        };
        OverlayRenderer.Refresh(o);
        if (o.LoadError != null) { Toast.Show(new Notification("Couldn't load image", o.LoadError, IsError: true)); return; }
        S.Overlays.Add(o);
        SelectedOverlay = o;
    }

    private void AddTextOverlay()
    {
        var o = new OverlayItem { Kind = OverlayKind.Text, Name = "Handle", Text = "@yourhandle", Anchor = OverlayAnchor.BottomLeft, Width = 0.16 };
        OverlayRenderer.Refresh(o);
        S.Overlays.Add(o);
        SelectedOverlay = o;
    }

    private void AddWebcamOverlay()
    {
        var o = new OverlayItem
        {
            Kind = OverlayKind.Webcam, Name = "Webcam", Anchor = OverlayAnchor.BottomRight, Width = 0.22,
            WebcamAspectRatio = (double)S.WebcamHeight / Math.Max(1, S.WebcamWidth),
        };
        S.Overlays.Add(o);
        SelectedOverlay = o;
    }

    private void BrowseOverlayImage()
    {
        if (SelectedOverlay == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        SelectedOverlay.ImagePath = CopyToLibrary(dlg.FileName);
    }

    /// <summary>Copies the image next to the settings so overlays survive the original being moved or deleted.</summary>
    private static string CopyToLibrary(string path)
    {
        try
        {
            Directory.CreateDirectory(Paths.OverlayLibrary);
            if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(Paths.OverlayLibrary), StringComparison.OrdinalIgnoreCase)) return path;
            var dest = Path.Combine(Paths.OverlayLibrary, Path.GetFileName(path));
            if (File.Exists(dest))
            {
                // Same name already in the library: reuse it only if it's the same image, never overwrite.
                if (File.ReadAllBytes(dest).AsSpan().SequenceEqual(File.ReadAllBytes(path))) return dest;
                dest = Path.Combine(Paths.OverlayLibrary, $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid().ToString("N")[..6]}{Path.GetExtension(path)}");
            }
            File.Copy(path, dest, false);
            return dest;
        }
        catch (Exception ex)
        {
            Log.Warn("Copying overlay image failed: " + ex.Message);
            return path;
        }
    }

    private void RemoveOverlay()
    {
        if (SelectedOverlay == null) return;
        int i = S.Overlays.IndexOf(SelectedOverlay);
        S.Overlays.Remove(SelectedOverlay);
        SelectedOverlay = S.Overlays.Count == 0 ? null : S.Overlays[Math.Clamp(i, 0, S.Overlays.Count - 1)];
    }

    private void DuplicateOverlay()
    {
        var src = SelectedOverlay;
        if (src == null) return;
        var json = System.Text.Json.JsonSerializer.Serialize(src);
        var copy = System.Text.Json.JsonSerializer.Deserialize<OverlayItem>(json)!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = src.Name + " copy";
        if (copy.Anchor == OverlayAnchor.Custom) { copy.X = Math.Min(0.9, copy.X + 0.03); copy.Y = Math.Min(0.9, copy.Y + 0.03); }
        OverlayRenderer.Refresh(copy);
        S.Overlays.Insert(S.Overlays.IndexOf(src) + 1, copy);
        SelectedOverlay = copy;
    }

    private void MoveOverlay(int delta)
    {
        if (SelectedOverlay == null) return;
        int i = S.Overlays.IndexOf(SelectedOverlay), j = Math.Clamp(i + delta, 0, S.Overlays.Count - 1);
        if (i != j) S.Overlays.Move(i, j);
    }

    // ================================================================== misc

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Quality): OnPropertyChanged(nameof(QualityLabel)); break;
            case nameof(AppSettings.OutputFolder): Recordings.RefreshIfLoaded(S.OutputFolder); break;
            case nameof(AppSettings.SourceKind) or nameof(AppSettings.DisplayId) or nameof(AppSettings.WindowTitle)
                or nameof(AppSettings.RegionX) or nameof(AppSettings.RegionY) or nameof(AppSettings.RegionWidth) or nameof(AppSettings.RegionHeight)
                or nameof(AppSettings.WebcamDeviceId):
                OnPropertyChanged(nameof(SourceSummary));
                OnPropertyChanged(nameof(WebcamDeviceId)); break;
            case nameof(AppSettings.WebcamWidth) or nameof(AppSettings.WebcamHeight):
                OnPropertyChanged(nameof(SelectedWebcamResolution)); break;
            case nameof(AppSettings.OutputWidth) or nameof(AppSettings.OutputHeight) or nameof(AppSettings.UseSourceResolution) or nameof(AppSettings.Fps):
                OnPropertyChanged(nameof(SelectedResolution));
                OnPropertyChanged(nameof(IsCustomResolution));
                OnPropertyChanged(nameof(CustomWidth));
                OnPropertyChanged(nameof(CustomHeight));
                OnPropertyChanged(nameof(OutputSummary));
                OnPropertyChanged(nameof(SuggestedBitrateText));
                OnPropertyChanged(nameof(EncoderWarning));
                RefreshEncoders();
                break;
            case nameof(AppSettings.VideoEncoder): OnPropertyChanged(nameof(EncoderWarning)); OnPropertyChanged(nameof(SelectedEncoder)); break;
        }
        if (e.PropertyName is nameof(AppSettings.BitrateMbps) or nameof(AppSettings.RateControl) or nameof(AppSettings.AudioBitrateKbps) or nameof(AppSettings.SeparateAudioTracks))
            OnPropertyChanged(nameof(EstimatedSize));
    }

    public void OnPipelineChanged()
    {
        OnPropertyChanged(nameof(OutputSummary));
        OnPropertyChanged(nameof(SuggestedBitrateText));
        if (S.UseSourceResolution) RefreshEncoders();
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Open folder failed: " + ex.Message); }
    }

    public static void OpenFile(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Open failed: " + ex.Message); }
    }

    public static void RevealFile(string file)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Reveal failed: " + ex.Message); }
    }
}
