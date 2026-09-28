using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Framelock.Core;
using Framelock.Encoding;

namespace Framelock.Ui;

/// <summary>One finished video in the output folder.</summary>
public sealed class RecordingItem : ObservableObject
{
    public string Path { get; }
    public long Size { get; }
    public DateTime Modified { get; }
    public string Key { get; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public RecordingItem(FileInfo f)
    {
        Path = f.FullName;
        Size = f.Length;
        Modified = f.LastWriteTime;
        Key = $"{Path}|{Size}|{Modified.Ticks}";
    }

    public string When
    {
        get
        {
            var d = Modified.Date;
            string day = d == DateTime.Today ? "Today" : d == DateTime.Today.AddDays(-1) ? "Yesterday"
                : d.Year == DateTime.Today.Year ? Modified.ToString("MMM d") : Modified.ToString("MMM d, yyyy");
            return $"{day} {Modified:t} · {Engine.RecorderController.FormatBytes(Size)}";
        }
    }

    private MediaInfo? _info;
    public MediaInfo? Info
    {
        get => _info;
        set
        {
            if (!Set(ref _info, value)) return;
            OnPropertyChanged(nameof(Meta));
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(HasDuration));
            OnPropertyChanged(nameof(CanFixAudio));
            OnPropertyChanged(nameof(FixAudioTip));
        }
    }

    private string? _error;
    public string? Error { get => _error; set { if (Set(ref _error, value)) OnPropertyChanged(nameof(Meta)); } }

    public string Meta => Info is { } i
        ? string.Join(" · ", new[]
          {
              i.HasVideo ? $"{i.Width}×{i.Height}" : "Audio only",
              i.Fps > 0 ? $"{Math.Round(i.Fps, i.Fps % 1 == 0 ? 0 : 2)} fps" : null,
              i.Audio.Count switch { 0 => "no audio", 1 => "1 audio track", var n => $"{n} audio tracks" },
          }.Where(s => s != null))
        : Error ?? "Reading…";

    public string Duration => Info is { DurationSeconds: > 0 } i ? Engine.RecorderController.FormatDuration(TimeSpan.FromSeconds(i.DurationSeconds)) : "";

    public bool HasDuration => Duration.Length > 0;

    public bool CanFixAudio => Info is { Audio.Count: > 0 };

    public string FixAudioTip => Info switch
    {
        null => "Reading the file…",
        { HasSeparateTracks: true } => "Change the game and mic volume, then save",
        { Audio.Count: > 0 } => "Change the volume (game and mic were recorded as one track)",
        _ => "This recording has no audio",
    };

    private ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; set => Set(ref _thumb, value); }
}

/// <summary>The Recordings tab: newest videos in the output folder, with thumbnails read in the background.</summary>
public sealed class RecordingsModel : ObservableObject
{
    private const int MaxItems = 100;
    private static readonly string ThumbFolder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Framelock", "thumbs");
    private static readonly ConcurrentDictionary<string, MediaInfo> InfoCache = new();

    public ObservableCollection<RecordingItem> Items { get; } = new();
    private CancellationTokenSource? _loadCts;
    public bool Loaded { get; private set; }

    private bool _isEmpty;
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }
    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public void Refresh(string folder)
    {
        Loaded = true;
        List<FileInfo> files;
        try
        {
            files = Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles()
                    .Where(f => MediaFile.VideoExtensions.Contains(f.Extension.ToLowerInvariant())
                                && !f.Name.EndsWith(".recording.mkv", StringComparison.OrdinalIgnoreCase)
                                && (f.Attributes & FileAttributes.Hidden) == 0)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(MaxItems)
                    .ToList()
                : new List<FileInfo>();
        }
        catch (Exception ex)
        {
            Log.Warn("Listing recordings failed: " + ex.Message);
            files = new List<FileInfo>();
        }

        // Keep items (and their thumbnails) that didn't change, so a refresh doesn't flicker.
        var old = Items.ToDictionary(i => i.Key);
        var next = files.Select(f => old.TryGetValue(new RecordingItem(f).Key, out var keep) ? keep : new RecordingItem(f)).ToList();
        if (!next.SequenceEqual(Items))
        {
            Items.Clear();
            foreach (var i in next) Items.Add(i);
        }
        IsEmpty = Items.Count == 0;
        long total = files.Sum(f => f.Length);
        Summary = Items.Count == 0 ? "" : $"{Items.Count}{(files.Count == MaxItems ? "+" : "")} video{(Items.Count == 1 ? "" : "s")} · {Engine.RecorderController.FormatBytes(total)}";

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var todo = Items.Where(i => i.Info == null || i.Thumb == null).ToList();
        if (todo.Count > 0) _ = Task.Run(() => LoadDetails(todo, cts.Token));
    }

    public void RefreshIfLoaded(string folder)
    {
        if (Loaded) Refresh(folder);
    }

    private static void LoadDetails(List<RecordingItem> items, CancellationToken ct)
    {
        var ui = Application.Current?.Dispatcher;
        if (ui == null) return;
        try { Directory.CreateDirectory(ThumbFolder); } catch { }
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            MediaInfo? info = null;
            string? error = null;
            try
            {
                if (!InfoCache.TryGetValue(item.Key, out info))
                {
                    info = MediaFile.Probe(item.Path);
                    InfoCache[item.Key] = info;
                }
            }
            catch (Exception ex)
            {
                error = IsBeingWritten(item.Path) ? "Still saving…" : "Can't read this file";
                Log.Debug($"Probe {item.Path}: {ex.Message}");
            }
            ImageSource? thumb = null;
            if (info is { HasVideo: true }) thumb = LoadThumb(item, info);
            ui.BeginInvoke(() =>
            {
                if (info != null) item.Info = info;
                item.Error = error;
                if (thumb != null) item.Thumb = thumb;
            });
        }
    }

    private static bool IsBeingWritten(string path)
    {
        try { using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); return false; }
        catch (IOException) { return true; }
        catch { return false; }
    }

    private static ImageSource? LoadThumb(RecordingItem item, MediaInfo info)
    {
        var file = System.IO.Path.Combine(ThumbFolder, Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(item.Key))) + ".jpg");
        try
        {
            if (!File.Exists(file))
            {
                var t = Thumbnailer.Grab(item.Path, 320, 180, Math.Min(info.DurationSeconds * 0.1, 10));
                if (t == null) return null;
                var src = BitmapSource.Create(t.Width, t.Height, 96, 96, PixelFormats.Bgra32, null, t.Bgra, t.Width * 4);
                var enc = new JpegBitmapEncoder { QualityLevel = 85 };
                enc.Frames.Add(BitmapFrame.Create(src));
                var tmp = file + ".tmp";
                using (var fs = File.Create(tmp)) enc.Save(fs);
                File.Move(tmp, file, true);
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Debug($"Thumbnail {item.Path}: {ex.Message}");
            return null;
        }
    }
}
