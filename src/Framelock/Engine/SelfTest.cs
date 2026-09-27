using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Framelock.Core;
using Framelock.Encoding;

namespace Framelock.Engine;

/// <summary>
/// Headless end-to-end check: <c>Framelock.exe --selftest seconds=6 fps=120 w=1920 h=1080 encoder=auto pause=1 replay=1</c>.
/// Records the primary display, verifies the files with ffprobe and writes a report to logs/selftest.txt.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        var report = new StringBuilder();
        void Say(string s) { report.AppendLine(s); Log.Info("[selftest] " + s); }
        var opts = args.Where(a => a.Contains('=')).Select(a => a.Split('=', 2)).ToDictionary(a => a[0].ToLowerInvariant(), a => a[1]);
        string Opt(string k, string d) => opts.TryGetValue(k, out var v) ? v : d;
        int ok = 0;
        try
        {
            FFmpegSetup.Init();
            Say($"FFmpeg {FFmpegSetup.Version}");
            await EncoderCatalog.ProbeAsync();
            Say("Encoders: " + string.Join(", ", EncoderCatalog.Available.Select(e => e.Id)));

            var outDir = Path.Combine(Path.GetTempPath(), "framelock-selftest");
            Directory.CreateDirectory(outDir);
            foreach (var f in Directory.GetFiles(outDir)) try { File.Delete(f); } catch { }

            var s = new AppSettings
            {
                OutputFolder = outDir,
                CountdownSeconds = 0,
                PlaySounds = false,
                Fps = int.Parse(Opt("fps", "60")),
                OutputWidth = int.Parse(Opt("w", "1920")),
                OutputHeight = int.Parse(Opt("h", "1080")),
                UseSourceResolution = Opt("native", "0") == "1",
                VideoEncoder = Opt("encoder", "auto"),
                SpeedPreset = Enum.Parse<SpeedPreset>(Opt("speed", "Balanced"), true),
                Container = Enum.Parse<ContainerFormat>(Opt("container", "Mp4"), true),
                CrashSafe = Opt("crashsafe", "1") == "1",
                MicEnabled = Opt("mic", "1") == "1",
                DesktopAudioEnabled = Opt("desktop", "1") == "1",
                SplitEveryMinutes = 0,
                FileNameTemplate = "selftest {res} {fps}",
            };
            if (Opt("source", "display") == "region")
            {
                s.SourceKind = SourceKind.Region;
                s.RegionX = 100; s.RegionY = 100; s.RegionWidth = 1280; s.RegionHeight = 720;
            }
            s.Overlays.Add(new OverlayItem { Kind = OverlayKind.Text, Text = "@framelock", Anchor = OverlayAnchor.BottomRight, Width = 0.2 });
            foreach (var o in s.Overlays) Graphics.OverlayRenderer.Refresh(o);

            var rc = new RecorderController(new SettingsStore(s), Application.Current.Dispatcher);
            var saved = new List<string>();
            rc.Notify += n =>
            {
                Say($"notify: {n.Title} - {n.Message}{(n.IsError ? " [ERROR]" : "")}");
                if (n.FilePath != null && !n.IsError) saved.Add(n.FilePath);
            };
            rc.SetWindowVisible(false);
            if (Opt("replay", "0") == "1") s.ReplayEnabled = true;

            int seconds = int.Parse(Opt("seconds", "6"));
            var sw = Stopwatch.StartNew();
            await rc.StartRecordingAsync();
            if (!rc.IsRecording) throw new Exception("Recording did not start: " + rc.StatusText);
            Say($"Recording: {rc.OutputInfo} (start took {sw.ElapsedMilliseconds} ms)");
            // Put some motion on screen: a topmost window that changes colour every frame.
            var flicker = ShowMotionWindow();

            bool pause = Opt("pause", "0") == "1";
            double expected = seconds;
            if (pause)
            {
                await Task.Delay(seconds * 500);
                rc.TogglePause();
                await Task.Delay(1500);
                rc.TogglePause();
                await Task.Delay(seconds * 500);
            }
            else await Task.Delay(seconds * 1000);
            rc.AddMarker();
            await Task.Delay(300);
            expected += 0.3;
            var st = rc.Stats;
            Say($"Stats: out {st?.OutputFps:F1} fps, capture {st?.CaptureFps:F1} fps, lagged {st?.Lagged}, enc dropped {st?.EncoderDropped}, {st?.VideoMbps:F1} Mbps, work {st?.WorkMsAvg:F2}/{st?.WorkMsMax:F1} ms, source {st?.SourceWidth}x{st?.SourceHeight} hdr={st?.Hdr}, {st?.EncoderLabel}");
            Say($"Audio: desktop={rc.Pipeline?.Audio.DesktopStatus} mic={rc.Pipeline?.Audio.MicStatus}");
            if (s.ReplayEnabled) await rc.SaveReplayAsync();
            await rc.StopRecordingAsync();
            flicker.Close();
            rc.Shutdown();

            if (saved.Count == 0) throw new Exception("No file was saved");
            foreach (var file in saved.Distinct())
            {
                bool isReplay = Path.GetFileName(file).StartsWith("Replay", StringComparison.OrdinalIgnoreCase);
                if (!ProbeFile(file, s, isReplay ? 0 : expected, Say)) ok = 1;
            }
        }
        catch (Exception ex)
        {
            Say("FAILED: " + ex);
            ok = 1;
        }
        Say(ok == 0 ? "SELFTEST PASSED" : "SELFTEST FAILED");
        File.WriteAllText(Path.Combine(Paths.Logs, "selftest.txt"), report.ToString());
        return ok;
    }

    private static Window ShowMotionWindow()
    {
        var w = new Window
        {
            Width = 300, Height = 200, Left = 200, Top = 200, Topmost = true, WindowStyle = WindowStyle.None, ShowActivated = false,
            ShowInTaskbar = false, Title = "motion",
        };
        // Gentle motion (a bar sliding across a dark panel) - no flashing colours.
        var bar = new System.Windows.Shapes.Rectangle { Width = 24, Fill = System.Windows.Media.Brushes.SteelBlue, HorizontalAlignment = HorizontalAlignment.Left };
        w.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 22, 28));
        w.Content = bar;
        var clock = Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) => bar.Margin = new Thickness((clock.Elapsed.TotalSeconds * 150) % 276, 0, 0, 0);
        w.Closed += (_, _) => timer.Stop();
        w.Show();
        timer.Start();
        return w;
    }

    private static bool ProbeFile(string file, AppSettings s, double expectedSeconds, Action<string> say)
    {
        var ffprobe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        if (!File.Exists(ffprobe))
        {
            var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\third_party\ffmpeg\ffprobe.exe"));
            if (File.Exists(dev)) ffprobe = dev;
        }
        say($"--- {Path.GetFileName(file)} ({new FileInfo(file).Length / 1024} KB)");
        var psi = new ProcessStartInfo(ffprobe, $"-v error -count_packets -show_streams -show_format -show_chapters -of json \"{file}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var json = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (!string.IsNullOrWhiteSpace(err)) say("ffprobe stderr: " + err.Trim());
        using var doc = JsonDocument.Parse(json);
        bool pass = true;
        double Dbl(JsonElement e, string k) => e.TryGetProperty(k, out var v) && double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ToString() : "";
        int audio = 0;
        double vDur = 0;
        foreach (var st in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = Str(st, "codec_type");
            if (type == "video")
            {
                int w = st.GetProperty("width").GetInt32(), h = st.GetProperty("height").GetInt32();
                vDur = Dbl(st, "duration");
                long packets = long.Parse(Str(st, "nb_read_packets") is { Length: > 0 } np ? np : "0");
                double fps = vDur > 0 ? packets / vDur : 0;
                say($"video: {Str(st, "codec_name")} {Str(st, "codec_tag_string")} {w}x{h} {Str(st, "pix_fmt")} r={Str(st, "r_frame_rate")} avg={Str(st, "avg_frame_rate")} dur={vDur:F3}s packets={packets} (≈{fps:F2} fps) start={Str(st, "start_time")}");
                int ew = s.UseSourceResolution ? w : s.OutputWidth, eh = s.UseSourceResolution ? h : s.OutputHeight;
                if (w != ew || h != eh) { say($"FAIL: expected {ew}x{eh}"); pass = false; }
                if (Math.Abs(fps - s.Fps) > s.Fps * 0.02) { say($"FAIL: frame rate {fps:F2} != {s.Fps}"); pass = false; }
                if (expectedSeconds > 0 && Math.Abs(vDur - expectedSeconds) > 0.6) { say($"FAIL: duration {vDur:F2}s, expected ≈{expectedSeconds:F2}s"); pass = false; }
            }
            else if (type == "audio")
            {
                audio++;
                double aDur = Dbl(st, "duration");
                say($"audio: {Str(st, "codec_name")} {Str(st, "sample_rate")}Hz ch={Str(st, "channels")} dur={aDur:F3}s start={Str(st, "start_time")} name={(st.TryGetProperty("tags", out var tg) && (tg.TryGetProperty("title", out var t) || tg.TryGetProperty("handler_name", out t)) ? t.GetString() : "")}");
                if (vDur > 0 && Math.Abs(aDur - vDur) > 0.1) { say($"FAIL: audio/video duration differ by {(aDur - vDur) * 1000:F0} ms"); pass = false; }
            }
        }
        say($"format: {Str(doc.RootElement.GetProperty("format"), "format_name")} dur={Str(doc.RootElement.GetProperty("format"), "duration")} chapters={doc.RootElement.GetProperty("chapters").GetArrayLength()}");
        if (audio == 0 && (s.MicEnabled || s.DesktopAudioEnabled)) { say("FAIL: no audio streams"); pass = false; }
        return pass;
    }
}
