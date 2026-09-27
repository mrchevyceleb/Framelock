using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Framelock.Core;

namespace Framelock.Encoding;

public sealed class FFmpegException(string message) : Exception(message);

public static unsafe class FFmpegSetup
{
    private static av_log_set_callback_callback? _logCallback;
    public static string Version { get; private set; } = "";
    public static bool Ready { get; private set; }

    public static void Init()
    {
        if (Ready) return;
        ffmpeg.RootPath = Paths.FFmpegFolder;
        DynamicallyLoadedBindings.Initialize();
        Version = ffmpeg.av_version_info();
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
        _logCallback = OnLog;
        ffmpeg.av_log_set_callback(new av_log_set_callback_callback_func { Pointer = Marshal.GetFunctionPointerForDelegate(_logCallback) });
        Ready = true;
        Log.Info($"FFmpeg {Version} loaded from {Paths.FFmpegFolder}");
    }

    private static void OnLog(void* avcl, int level, string format, byte* vl)
    {
        if (level > ffmpeg.AV_LOG_WARNING) return;
        const int size = 1024;
        var buf = stackalloc byte[size];
        int prefix = 1;
        ffmpeg.av_log_format_line(avcl, level, format, vl, buf, size, &prefix);
        var line = Marshal.PtrToStringUTF8((IntPtr)buf)?.TrimEnd();
        if (string.IsNullOrEmpty(line)) return;
        if (level <= ffmpeg.AV_LOG_ERROR) Log.Warn("[ffmpeg] " + line);
        else Log.Debug("[ffmpeg] " + line);
    }

    public static string ErrorText(int code)
    {
        const int size = 256;
        var buf = stackalloc byte[size];
        ffmpeg.av_strerror(code, buf, size);
        return Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"error {code}";
    }

    public static int Check(this int code, string what)
    {
        if (code < 0) throw new FFmpegException($"{what} failed: {ErrorText(code)} ({code})");
        return code;
    }

    public static int AVERROR_EAGAIN => -ffmpeg.EAGAIN;
}
