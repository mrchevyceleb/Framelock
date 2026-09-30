using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Framelock.Core;

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON with a debounced autosave.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly System.Threading.Timer _saveTimer;
    public AppSettings Settings { get; }

    private readonly bool _persist = true;

    public SettingsStore()
    {
        Settings = Load();
        _saveTimer = new System.Threading.Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        Hook(Settings);
    }

    /// <summary>In-memory store (self-test); never touches the settings file.</summary>
    public SettingsStore(AppSettings settings)
    {
        Settings = settings;
        _persist = false;
        _saveTimer = new System.Threading.Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void Hook(AppSettings s)
    {
        s.PropertyChanged += (_, _) => ScheduleSave();
        s.Overlays.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (OverlayItem o in e.NewItems) o.PropertyChanged += OverlayChanged;
            ScheduleSave();
        };
        foreach (var o in s.Overlays) o.PropertyChanged += OverlayChanged;
    }

    private void OverlayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverlayItem.Bitmap) or nameof(OverlayItem.LoadError) or nameof(OverlayItem.AspectRatio) or nameof(OverlayItem.WebcamAspectRatio)) return;
        ScheduleSave();
    }

    public void ScheduleSave() => _saveTimer.Change(600, Timeout.Infinite);

    public void SaveNow()
    {
        if (!_persist) return;
        try
        {
            Paths.Ensure();
            var json = JsonSerializer.Serialize(Settings, Options);
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception ex) { Log.Error("Saving settings failed", ex); }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Options);
                if (s != null) return Sanitize(s);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings file unreadable, using defaults", ex);
            try { File.Copy(Paths.SettingsFile, Paths.SettingsFile + ".bad", true); } catch { }
        }
        return Sanitize(new AppSettings());
    }

    private static AppSettings Sanitize(AppSettings s)
    {
        s.Fps = Math.Clamp(s.Fps, 1, 240);
        s.WebcamFps = Math.Clamp(s.WebcamFps, 1, 240);
        s.WebcamWidth = Math.Clamp(s.WebcamWidth, 160, 3840);
        s.WebcamHeight = Math.Clamp(s.WebcamHeight, 120, 2160);
        s.OutputWidth = Math.Clamp(s.OutputWidth & ~1, 128, 8192);
        s.OutputHeight = Math.Clamp(s.OutputHeight & ~1, 128, 8192);
        s.Quality = Math.Clamp(s.Quality, 1, 51);
        s.BitrateMbps = Math.Clamp(s.BitrateMbps, 1, 1000);
        s.ReplaySeconds = Math.Clamp(s.ReplaySeconds, 5, 1200);
        s.KeyframeSeconds = Math.Clamp(s.KeyframeSeconds, 1, 10);
        s.CountdownSeconds = Math.Clamp(s.CountdownSeconds, 0, 10);
        if (string.IsNullOrWhiteSpace(s.OutputFolder)) s.OutputFolder = Paths.DefaultOutputFolder;
        s.Overlays ??= new();
        return s;
    }
}
