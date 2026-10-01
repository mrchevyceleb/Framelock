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
                s.RegionX = int.Parse(Opt("rx", "100")); s.RegionY = int.Parse(Opt("ry", "100"));
                s.RegionWidth = int.Parse(Opt("rw", "1280")); s.RegionHeight = int.Parse(Opt("rh", "720"));
            }
            if (Opt("source", "display") == "webcam" || Opt("webcam", "0") == "1")
            {
                if (Opt("source", "display") == "webcam") s.SourceKind = SourceKind.Webcam;
                else s.Overlays.Add(new OverlayItem { Kind = OverlayKind.Webcam, Anchor = OverlayAnchor.BottomLeft, Width = 0.3 });
                s.WebcamWidth = int.Parse(Opt("camw", "1280")); s.WebcamHeight = int.Parse(Opt("camh", "720"));
                s.WebcamFps = int.Parse(Opt("camfps", "30"));
                s.WebcamDeviceId = opts.GetValueOrDefault("camera");
                s.WebcamMirror = Opt("mirror", "0") == "1";
            }
            s.Overlays.Add(new OverlayItem { Kind = OverlayKind.Text, Text = "@framelock", Anchor = OverlayAnchor.BottomRight, Width = 0.2 });
            foreach (var o in s.Overlays) Graphics.OverlayRenderer.Refresh(o);

            Process? load = null;
            if (Opt("gpuload", "") is { Length: > 0 } iters)
            {
                // A separate process keeps the GPU busy like a game (GPU priority is per process).
                load = Process.Start(Environment.ProcessPath!, $"--gpuload={int.Parse(Opt("seconds", "6")) + 8},{iters},{Opt("strips", "64")}");
                await Task.Delay(2000);
                Say($"GPU load running ({iters} iters)");
            }
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
            if (s.SourceKind == SourceKind.Webcam || s.Overlays.Any(o => o.Kind == OverlayKind.Webcam)) Say(rc.WebcamStatus);
            // Put some motion on screen: a topmost window that changes colour every frame.
            var flicker = Opt("motion", "1") == "1" ? ShowMotionWindow() : null;

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
            flicker?.Close();
            try { load?.Kill(); } catch { }
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

    /// <summary>
    /// Headless check of Fix audio on a copy of <paramref name="file"/>: probe, remix (game 50%, mic 200%), verify the
    /// new mix against the separate tracks, exercise the preview player (no sound), grab a thumbnail and render the
    /// window to a PNG. Report: logs/remixcheck.txt.
    /// </summary>
    public static int RemixCheck(string file)
    {
        var report = new StringBuilder();
        void Say(string s) { report.AppendLine(s); Log.Info("[remixcheck] " + s); }
        int rc = 0;
        var dir = Path.Combine(Path.GetTempPath(), "framelock-remixcheck");
        try
        {
            FFmpegSetup.Init();
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir)) try { File.Delete(f); } catch { }
            var copy = Path.Combine(dir, "source" + Path.GetExtension(file));
            File.Copy(file, copy);
            var sw = Stopwatch.StartNew();
            var info = MediaFile.Probe(copy);
            Say($"probe: {info.DurationSeconds:F2}s {info.Width}x{info.Height} {info.Fps:F2}fps {info.VideoCodec}; audio: " +
                string.Join(", ", info.Audio.Select(a => $"#{a.StreamIndex} [{a.Name}] {a.Role} {a.BitrateKbps}k")) + $" ({sw.ElapsedMilliseconds} ms)");

            var levels = new RemixLevels(0.5f, 2f);
            sw.Restart();
            var result = AudioRemixer.ExportToFile(copy, info, levels, replaceOriginal: false);
            Say($"export: {Path.GetFileName(result)} in {sw.ElapsedMilliseconds} ms ({new FileInfo(result).Length / 1024} KB vs {new FileInfo(copy).Length / 1024} KB)");
            if (Directory.GetFiles(dir, "*.part").Length > 0) { Say("FAIL: temp file left behind"); rc = 1; }
            var s = new AppSettings { Fps = (int)Math.Round(info.Fps), OutputWidth = info.Width, OutputHeight = info.Height, MicEnabled = true, DesktopAudioEnabled = true };
            ProbeFile(result, s, 0, Say); // details only: the source's own quirks (e.g. skipped frames) carry over
            var outInfo = MediaFile.Probe(result);
            if (Math.Abs(outInfo.DurationSeconds - info.DurationSeconds) > 0.05 || Math.Abs(outInfo.Fps - info.Fps) > 0.01)
            { Say($"FAIL: remix {outInfo.DurationSeconds:F3}s {outInfo.Fps:F2}fps vs source {info.DurationSeconds:F3}s {info.Fps:F2}fps"); rc = 1; }
            Say("remix tracks: " + string.Join(", ", outInfo.Audio.Select(a => $"#{a.StreamIndex} [{a.Name}] {a.Role}")));
            if (outInfo.Audio.Count != info.Audio.Count) { Say("FAIL: audio track count changed"); rc = 1; }

            if (info.HasSeparateTracks)
            {
                // The new mix must match SoftClip(0.5 game + 2 mic) built from the (unchanged) separate tracks.
                var (d0, d) = DecodeAll(result, outInfo.Desktop!.StreamIndex);
                var (m0, m) = DecodeAll(result, outInfo.Mic!.StreamIndex);
                var (x0, mix) = DecodeAll(result, outInfo.Mix!.StreamIndex);
                var (o0, old) = DecodeAll(copy, info.Mix!.StreamIndex);
                Say($"starts (samples): game {d0} mic {m0} new mix {x0} old mix {o0}");
                // Compare sample-for-sample on the shared timeline.
                long from = Math.Max(Math.Max(d0, m0), x0), to = Math.Min(Math.Min(d0 + d.Length / 2, m0 + m.Length / 2), x0 + mix.Length / 2);
                double eErr = 0, eRef = 0, eOld = 0;
                for (long t = from; t < to; t++)
                    for (int c = 0; c < 2; c++)
                    {
                        double want = Audio.AudioEngine.SoftClip(d[(t - d0) * 2 + c] * levels.Desktop + m[(t - m0) * 2 + c] * levels.Mic);
                        double got = mix[(t - x0) * 2 + c];
                        eErr += (got - want) * (got - want);
                        eRef += want * want;
                        long oi = (t - o0) * 2 + c;
                        if (oi >= 0 && oi < old.Length) eOld += (old[oi] - want) * (old[oi] - want);
                    }
                int n = (int)Math.Max(0, (to - from) * 2);
                double rel = eRef > 0 ? Math.Sqrt(eErr / eRef) : 0, relOld = eRef > 0 ? Math.Sqrt(eOld / eRef) : 0;
                Say($"mix check: {n / 2 / 48000.0:F2}s, rms game {Rms(d):F4} mic {Rms(m):F4} new mix {Rms(mix):F4} old mix {Rms(old):F4}; " +
                    $"error vs expected {rel:P1} (old mix {relOld:P1}); lengths d={d.Length / 2} m={m.Length / 2} mix={mix.Length / 2}");
                if (eRef > 1e-6 && rel > 0.25) { Say("FAIL: new mix doesn't match the expected levels"); rc = 1; }
                if (Math.Abs(mix.Length - d.Length) > 2 * 2048) { Say("FAIL: mix length differs from the tracks"); rc = 1; }
            }

            // Preview player: read, seek, read to the end.
            using (var pv = new Audio.RemixPreview(copy, info))
            {
                pv.SetGain(0, 0.5f);
                pv.SetGain(1, 2f);
                var buf = new float[4800];
                int got = pv.Read(buf);
                pv.Seek(info.DurationSeconds / 2);
                got += pv.Read(buf);
                double mid = pv.Position;
                long total = 0;
                sw.Restart();
                int r;
                while ((r = pv.Read(buf)) > 0) total += r;
                Say($"preview: first reads {got} samples, after seek at {mid:F2}s, read {total / 2 / 48000.0:F2}s more to the end in {sw.ElapsedMilliseconds} ms, AtEnd={pv.AtEnd}");
                if (Math.Abs(mid - (info.DurationSeconds / 2 + 0.05)) > 0.2 || !pv.AtEnd) { Say("FAIL: preview seek/end"); rc = 1; }
            }

            sw.Restart();
            var thumb = Thumbnailer.Grab(copy, 320, 180, Math.Min(info.DurationSeconds * 0.1, 10));
            Say(thumb == null ? "FAIL: no thumbnail" : $"thumbnail {thumb.Width}x{thumb.Height} in {sw.ElapsedMilliseconds} ms");
            if (thumb == null) rc = 1;
            else
            {
                var bmp = System.Windows.Media.Imaging.BitmapSource.Create(thumb.Width, thumb.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, thumb.Bgra, thumb.Width * 4);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(dir, "thumb.png"));
                enc.Save(fs);
            }
            Ui.RemixWindow.RenderShot(copy, Path.Combine(dir, "window.png"));
            Say("window: " + Path.Combine(dir, "window.png"));
        }
        catch (Exception ex)
        {
            Say("FAILED: " + ex);
            rc = 1;
        }
        Say(rc == 0 ? "REMIXCHECK PASSED" : "REMIXCHECK FAILED");
        File.WriteAllText(Path.Combine(Paths.Logs, "remixcheck.txt"), report.ToString());
        return rc;
    }

    private static double Rms(float[] a) => a.Length == 0 ? 0 : Math.Sqrt(a.Select(x => (double)x * x).Sum() / a.Length);

    private static unsafe (long Start, float[] Samples) DecodeAll(string file, int stream)
    {
        FFmpeg.AutoGen.AVFormatContext* fmt = null;
        FFmpeg.AutoGen.ffmpeg.avformat_open_input(&fmt, file, null, null).Check("open");
        var pkt = FFmpeg.AutoGen.ffmpeg.av_packet_alloc();
        try
        {
            FFmpeg.AutoGen.ffmpeg.avformat_find_stream_info(fmt, null).Check("info");
            using var dec = new TrackDecoder(fmt, stream);
            var q = new SampleQueue();
            while (FFmpeg.AutoGen.ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                if (pkt->stream_index == stream) dec.Decode(pkt, q);
                FFmpeg.AutoGen.ffmpeg.av_packet_unref(pkt);
            }
            dec.Decode(null, q);
            long start = q.Start;
            int frames = (int)(q.End - start);
            var all = new float[frames * 2];
            q.Take(start, all, frames);
            return (start, all);
        }
        finally
        {
            FFmpeg.AutoGen.ffmpeg.av_packet_free(&pkt);
            FFmpeg.AutoGen.ffmpeg.avformat_close_input(&fmt);
        }
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
