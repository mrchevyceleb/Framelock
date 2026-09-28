using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Framelock.Audio;
using Framelock.Core;
using Framelock.Encoding;
using Framelock.Engine;
using NAudio.Wave;

namespace Framelock.Ui;

/// <summary>
/// "Fix audio": rebalance game and mic after the fact, hear it, and save. The video is copied untouched;
/// only the mix track is rebuilt from the separate game/mic tracks.
/// </summary>
public partial class RemixWindow : Window
{
    private static readonly Dictionary<string, RemixWindow> OpenWindows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (on the UI thread) with the saved file's path.</summary>
    public static event Action<string>? Saved;

    private readonly string _path;
    private readonly MediaInfo _info;
    private RemixPreview? _preview;
    private WasapiOut? _out;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cts;
    private Task? _export;
    private bool _seekFromCode, _closeAfterExport;
    private readonly float[] _meters = new float[2];

    /// <summary>Opens the Fix audio window for a recording (or brings its existing window forward).</summary>
    public static async void ShowFor(string path)
    {
        try
        {
            if (OpenWindows.TryGetValue(path, out var existing)) { existing.Activate(); return; }
            var info = await Task.Run(() => MediaFile.Probe(path));
            if (OpenWindows.TryGetValue(path, out existing)) { existing.Activate(); return; }
            if (info.Audio.Count == 0)
            {
                Toast.Show(new Notification("No audio to fix", $"{Path.GetFileName(path)} was recorded without sound.", IsError: true));
                return;
            }
            var w = new RemixWindow(path, info);
            var main = Application.Current.MainWindow;
            if (main is { IsVisible: true } && main.WindowState != WindowState.Minimized) w.Owner = main;
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Show();
            w.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("Fix audio failed to open " + path, ex);
            Toast.Show(new Notification("Couldn't open the recording", ex.Message, IsError: true));
        }
    }

    /// <summary>App exit: stop running exports so no half-written temp file is left next to the recordings.</summary>
    public static void CancelAllExports()
    {
        var running = OpenWindows.Values.Where(w => w._export != null).ToList();
        foreach (var w in running) w._cts?.Cancel();
        try { Task.WaitAll(running.Select(w => w._export!).ToArray(), 5000); } catch { }
    }

    /// <summary>Dev aid: renders the window for <paramref name="path"/> to a PNG far off-screen (no taskbar button, no sound).</summary>
    internal static void RenderShot(string path, string png)
    {
        var w = new RemixWindow(path, MediaFile.Probe(path))
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
        };
        w.Show();
        w.UpdateLayout();
        w.Tick();
        w.UpdateLayout();
        var root = (FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        w.Close();
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(png);
        enc.Save(fs);
    }

    private RemixWindow(string path, MediaInfo info)
    {
        _path = path;
        _info = info;
        InitializeComponent();
        OpenWindows[path] = this;

        FileName.Text = Path.GetFileName(path);
        FileName.ToolTip = path;
        var meta = new List<string>();
        if (info.DurationSeconds > 0) meta.Add(RecorderController.FormatDuration(TimeSpan.FromSeconds(info.DurationSeconds)));
        if (info.HasVideo) meta.Add($"{info.Width}×{info.Height}");
        meta.Add(info.HasSeparateTracks ? "game and mic on separate tracks" : "one audio track");
        FileMeta.Text = string.Join(" · ", meta);

        if (!info.HasSeparateTracks)
        {
            // Game and mic were baked into one track: only the overall volume can change.
            DesktopLabel.Text = "Volume";
            MicRow.Visibility = Visibility.Collapsed;
            LevelsHint.Text = "Game and mic were recorded as one track, so only the overall volume can change. " +
                              "Turn on Audio › Separate tracks to rebalance them in future recordings.";
        }

        SeekSlider.Maximum = Math.Max(0.1, info.DurationSeconds);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        StartPreview();
        _timer.Start();

        SourceInitialized += (_, _) => Native.ExcludeFromCapture(new WindowInteropHelper(this).Handle, true);
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _timer.Stop();
            StopPreview();
            OpenWindows.Remove(_path);
        };
    }

    // ------------------------------------------------------------------ preview

    private void StartPreview()
    {
        try
        {
            _preview = new RemixPreview(_path, _info);
            ApplyLevels();
            _out = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 80);
            _out.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(_preview));
            _out.PlaybackStopped += (_, e) => Dispatcher.BeginInvoke(() =>
            {
                if (e.Exception != null) Log.Warn("Preview playback stopped: " + e.Exception.Message);
                if (_preview?.AtEnd == true) _preview.Seek(0);
                UpdatePlayGlyph();
            });
            PlayButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Audio preview unavailable: " + ex.Message);
            StopPreview();
            PlayButton.IsEnabled = false;
            PlayButton.ToolTip = "Preview unavailable (no audio output device?)";
        }
    }

    private void StopPreview()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        _out = null;
        _preview?.Dispose();
        _preview = null;
    }

    private bool Playing => _out?.PlaybackState == PlaybackState.Playing;

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_out == null || _preview == null) return;
        if (Playing) _out.Pause();
        else
        {
            if (_preview.AtEnd) _preview.Seek(0);
            _out.Play();
        }
        UpdatePlayGlyph();
    }

    private void UpdatePlayGlyph()
    {
        PlayGlyph.Text = Playing ? "" : "";
        PlayButton.ToolTip = Playing ? "Pause (Space)" : "Play (Space)";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox) { TogglePlay(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seekFromCode) return;
        _preview?.Seek(e.NewValue);
        UpdateTime(e.NewValue);
    }

    private void Tick()
    {
        var p = _preview;
        if (p == null) return;
        if (!SeekSlider.IsMouseCaptureWithin)
        {
            _seekFromCode = true;
            SeekSlider.Value = Math.Min(p.Position, SeekSlider.Maximum);
            _seekFromCode = false;
            UpdateTime(p.Position);
        }
        bool playing = Playing;
        for (int k = 0; k < p.Peaks.Length && k < _meters.Length; k++)
        {
            float peak = playing ? p.Peaks[k] : 0;
            p.Peaks[k] = 0;
            _meters[k] = Math.Max(peak, _meters[k] * 0.85f);
        }
        DesktopMeter.Value = MeterValue(_meters[0]);
        MicMeter.Value = MeterValue(_meters[1]);
        if (!playing && PlayGlyph.Text != "") UpdatePlayGlyph();
    }

    private static double MeterValue(float v) => v <= 0.001 ? 0 : Math.Clamp((20 * Math.Log10(v) + 60) / 60, 0, 1);

    private void UpdateTime(double pos) =>
        TimeText.Text = $"{Clock(pos)} / {Clock(_info.DurationSeconds)}";

    private static string Clock(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ------------------------------------------------------------------ levels

    private RemixLevels Levels => new(
        DesktopMute.IsChecked == true ? 0 : (float)DesktopSlider.Value,
        MicMute.IsChecked == true ? 0 : (float)MicSlider.Value);

    private void Levels_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ApplyLevels();
    }

    private void ApplyLevels()
    {
        if (DesktopMuteGlyph == null) return;
        DesktopMuteGlyph.Text = DesktopMute.IsChecked == true ? "" : "";
        DesktopMuteGlyph.Foreground = (System.Windows.Media.Brush)FindResource(DesktopMute.IsChecked == true ? "RecBrush" : "TextBrush");
        MicMuteGlyph.Text = MicMute.IsChecked == true ? "" : "";
        MicMuteGlyph.Foreground = (System.Windows.Media.Brush)FindResource(MicMute.IsChecked == true ? "RecBrush" : "TextBrush");
        var l = Levels;
        _preview?.SetGain(0, l.Desktop);
        _preview?.SetGain(1, l.Mic);
    }

    private void Slider_Reset(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider s) s.Value = 1;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        DesktopSlider.Value = 1;
        MicSlider.Value = 1;
        DesktopMute.IsChecked = false;
        MicMute.IsChecked = false;
    }

    // ------------------------------------------------------------------ save

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_export != null) return;
        var levels = Levels;
        bool replace = SaveReplace.IsChecked == true;
        if (_out != null && Playing) _out.Pause();
        UpdatePlayGlyph();
        StopPreview(); // releases the file (needed to replace it)
        SetBusy(true);
        ErrorText.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<double>(p =>
        {
            ExportProgress.Value = p;
            ProgressText.Text = $"Saving… {p:P0}";
        });
        try
        {
            _export = Task.Run(() => AudioRemixer.ExportToFile(_path, _info, levels, replace, progress, ct), ct);
            var result = await (Task<string>)_export;
            Log.Info($"Fixed audio ({levels.Desktop:P0} game, {levels.Mic:P0} mic) → {result}");
            Saved?.Invoke(result);
            var size = new FileInfo(result).Length;
            Toast.Show(new Notification(replace ? "Audio fixed" : "Saved with new audio", $"{Path.GetFileName(result)} · {RecorderController.FormatBytes(size)}", result));
            _export = null;
            _closeAfterExport = false;
            Close();
            return;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Cancelled";
        }
        catch (OriginalKeptException ex)
        {
            Log.Warn(ex.Message);
            Saved?.Invoke(ex.RemixPath);
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error("Fix audio export failed", ex);
            ErrorText.Text = "Couldn't save: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        _export = null;
        if (_closeAfterExport) { Close(); return; }
        SetBusy(false);
        ProgressPanel.Visibility = Visibility.Collapsed;
        if (File.Exists(_path)) StartPreview();
    }

    private void SetBusy(bool busy)
    {
        ProgressPanel.Visibility = busy ? Visibility.Visible : ProgressPanel.Visibility;
        ExportProgress.Value = 0;
        ProgressText.Text = "Saving…";
        SaveButton.IsEnabled = !busy;
        DesktopRow.IsEnabled = MicRow.IsEnabled = !busy;
        SaveNew.IsEnabled = SaveReplace.IsEnabled = !busy;
        PlayButton.IsEnabled = !busy;
        SeekSlider.IsEnabled = !busy;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_export != null) _cts?.Cancel();
        else Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_export == null) return;
        // Never leave a half-written file behind: cancel, then close once the export has cleaned up.
        e.Cancel = true;
        _closeAfterExport = true;
        _cts?.Cancel();
        ProgressText.Text = "Cancelling…";
    }
}
