using System.Collections.Concurrent;
using System.IO;

namespace Framelock.Core;

public static class Paths
{
    public static string AppData { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Framelock");
    public static string Logs => Path.Combine(AppData, "logs");
    public static string OverlayLibrary => Path.Combine(AppData, "overlays");
    public static string SettingsFile => Path.Combine(AppData, "settings.json");
    public static string DefaultOutputFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Framelock");
    public static string FFmpegFolder => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public static void Ensure()
    {
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(OverlayLibrary);
    }
}

/// <summary>Tiny thread-safe file logger with a background writer.</summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 10_000);
    private static StreamWriter? _writer;
    private static Thread? _thread;

    public static string? FilePath { get; private set; }

    public static void Init()
    {
        Paths.Ensure();
        FilePath = Path.Combine(Paths.Logs, $"framelock-{DateTime.Now:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        _thread = new Thread(() =>
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try { _writer.WriteLine(line); } catch { /* disk issues must never crash the recorder */ }
            }
        }) { IsBackground = true, Name = "Log writer" };
        _thread.Start();
        PruneOld();
    }

    private static void PruneOld()
    {
        try
        {
            foreach (var f in new DirectoryInfo(Paths.Logs).GetFiles("framelock-*.log").OrderByDescending(f => f.Name).Skip(10))
                f.Delete();
        }
        catch { }
    }

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        Queue.TryAdd(line);
    }

    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR", ex == null ? msg : $"{msg}: {ex}");
    public static void Debug(string msg) => Write("DBG", msg);

    public static void Shutdown()
    {
        Queue.CompleteAdding();
        _thread?.Join(1000);
        _writer?.Dispose();
    }
}
