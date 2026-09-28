using System.IO;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen;
using Framelock.Core;

namespace Framelock.Encoding;

/// <summary>New levels for a finished recording. With separate tracks: game and mic; otherwise Desktop is the overall volume.</summary>
public readonly record struct RemixLevels(float Desktop, float Mic);

/// <summary>Replace mode: the remix was saved under its own name, but the original couldn't go to the Recycle Bin.</summary>
public sealed class OriginalKeptException(string remixPath, Exception inner)
    : IOException($"The new audio was saved as \"{Path.GetFileName(remixPath)}\". The original couldn't go to the Recycle Bin, so it was kept too.", inner)
{
    public string RemixPath { get; } = remixPath;
}

/// <summary>
/// Rebuilds a recording's mix with new game/mic levels. Video and the separate tracks are copied untouched (no quality
/// loss); only the mix track is re-encoded. The separate tracks are exactly what went into the original mix, so
/// SoftClip(game·g + mic·m) is the same signal the recorder would have produced at those levels.
/// </summary>
public static unsafe class AudioRemixer
{
    /// <summary>(stream, gain) pairs that make up the new mix, and the stream it replaces (-1 = insert).</summary>
    public static (List<(int Stream, float Gain)> Sources, int Replace, string MixName, int Kbps) Plan(MediaInfo info, RemixLevels levels)
    {
        if (info.HasSeparateTracks)
        {
            var mix = info.Mix;
            return (new() { (info.Desktop!.StreamIndex, levels.Desktop), (info.Mic!.StreamIndex, levels.Mic) },
                mix?.StreamIndex ?? -1, mix?.Name ?? "Mix (game + mic)", Bitrate(mix ?? info.Desktop));
        }
        var only = info.Mix ?? info.Audio.FirstOrDefault() ?? throw new InvalidOperationException("This recording has no audio.");
        return (new() { (only.StreamIndex, levels.Desktop) }, only.StreamIndex, only.Name, Bitrate(only));
    }

    /// <summary>
    /// Output limiter. A mix of game + mic is soft-clipped exactly like the recorder does. A single track at 100% or less
    /// passes through untouched: it was already limited when it was recorded.
    /// </summary>
    internal static float Limit(float x, bool single, float singleGain) => single && singleGain <= 1 ? x : Audio.AudioEngine.SoftClip(x);

    private static int Bitrate(AudioTrackInfo t) => t.BitrateKbps >= 64 ? t.BitrateKbps : 192;

    /// <summary>"Name (remix).mp4", then "Name (remix 2).mp4", … next to the original.</summary>
    public static string SuggestPath(string input)
    {
        var dir = Path.GetDirectoryName(input)!;
        var ext = Path.GetExtension(input);
        var name = Regex.Replace(Path.GetFileNameWithoutExtension(input), @" \(remix(?: \d+)?\)$", "");
        var p = Path.Combine(dir, $"{name} (remix){ext}");
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{name} (remix {i}){ext}");
        return p;
    }

    /// <summary>Writes the remixed file to <paramref name="output"/> (container picked from its extension, or <paramref name="container"/>).</summary>
    public static void Export(string input, string output, MediaInfo info, RemixLevels levels, ContainerFormat container,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (sources, replace, mixName, kbps) = Plan(info, levels);
        AVFormatContext* inCtx = null;
        AVFormatContext* outCtx = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        var decoders = new List<TrackDecoder>();
        AudioEncoder? enc = null;
        var pending = new List<EncodedPacket>();
        try
        {
            ffmpeg.avformat_open_input(&inCtx, input, null, null).Check("Opening the recording");
            ffmpeg.avformat_find_stream_info(inCtx, null).Check("Reading the recording");
            if (replace >= inCtx->nb_streams || (replace >= 0 && inCtx->streams[replace]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO))
                throw new InvalidOperationException("The recording changed since it was opened. Close this window and open it again.");
            string fmtName = container switch { ContainerFormat.Mkv => "matroska", ContainerFormat.Mov => "mov", _ => "mp4" };
            ffmpeg.avformat_alloc_output_context2(&outCtx, null, fmtName, output).Check("Creating the output");
            var oc = outCtx; // the local functions below can't capture outCtx (its address is taken)

            enc = new AudioEncoder(1, mixName, kbps, pending.Add);
            var map = new int[inCtx->nb_streams];
            int mixOut = -1;
            AVStream* NewMixStream()
            {
                var st = ffmpeg.avformat_new_stream(oc, null);
                ffmpeg.avcodec_parameters_from_context(st->codecpar, enc!.Context).Check("Mix stream setup");
                st->time_base = enc.TimeBase;
                st->disposition = ffmpeg.AV_DISPOSITION_DEFAULT;
                ffmpeg.av_dict_set(&st->metadata, "title", mixName, 0);
                ffmpeg.av_dict_set(&st->metadata, "handler_name", mixName, 0);
                mixOut = st->index;
                return st;
            }
            for (int i = 0; i < inCtx->nb_streams; i++)
            {
                map[i] = -1;
                var ist = inCtx->streams[i];
                var type = ist->codecpar->codec_type;
                if (type != AVMediaType.AVMEDIA_TYPE_VIDEO && type != AVMediaType.AVMEDIA_TYPE_AUDIO) continue;
                if (type == AVMediaType.AVMEDIA_TYPE_AUDIO && mixOut < 0 && (replace < 0 || i == replace)) NewMixStream();
                if (i == replace) continue; // the old mix is rebuilt, not copied
                var ost = ffmpeg.avformat_new_stream(outCtx, null);
                ffmpeg.avcodec_parameters_copy(ost->codecpar, ist->codecpar).Check("Copying codec parameters");
                ost->codecpar->codec_tag = 0;
                if (container != ContainerFormat.Mkv && ost->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC)
                    ost->codecpar->codec_tag = (uint)('h' | ('v' << 8) | ('c' << 16) | ('1' << 24));
                ost->time_base = ist->time_base;
                ost->avg_frame_rate = ist->avg_frame_rate;
                ost->r_frame_rate = ist->r_frame_rate;
                ost->disposition = ist->disposition;
                ffmpeg.av_dict_copy(&ost->metadata, ist->metadata, 0);
                if (type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    ost->disposition &= ~ffmpeg.AV_DISPOSITION_DEFAULT;
                    if (MediaFile.Tag(ist->metadata, "title") is { } title) ffmpeg.av_dict_set(&ost->metadata, "handler_name", title, 0);
                }
                map[i] = ost->index;
            }
            if (mixOut < 0) NewMixStream();

            ffmpeg.av_dict_copy(&outCtx->metadata, inCtx->metadata, 0);
            CopyChapters(inCtx, outCtx);

            var queues = new SampleQueue[sources.Count];
            var gains = new float[sources.Count];
            var srcIndex = new int[inCtx->nb_streams];
            Array.Fill(srcIndex, -1);
            for (int k = 0; k < sources.Count; k++)
            {
                decoders.Add(new TrackDecoder(inCtx, sources[k].Stream));
                queues[k] = new SampleQueue();
                gains[k] = Math.Max(0, sources[k].Gain);
                srcIndex[sources[k].Stream] = k;
            }

            AVIOContext* pb = null;
            ffmpeg.avio_open(&pb, output, ffmpeg.AVIO_FLAG_WRITE).Check($"Opening {output}");
            outCtx->pb = pb;
            AVDictionary* opts = null;
            if (container != ContainerFormat.Mkv) ffmpeg.av_dict_set(&opts, "movflags", "+faststart", 0);
            int hr = ffmpeg.avformat_write_header(outCtx, &opts);
            ffmpeg.av_dict_free(&opts);
            hr.Check("Writing the header");

            var lastDts = Enumerable.Repeat(long.MinValue, (int)outCtx->nb_streams).ToArray();
            void WriteOut(AVPacket* p, int oi, AVRational from)
            {
                var ost = oc->streams[oi];
                p->stream_index = oi;
                ffmpeg.av_packet_rescale_ts(p, from, ost->time_base);
                p->pos = -1;
                if (p->dts == ffmpeg.AV_NOPTS_VALUE) p->dts = p->pts;
                if (lastDts[oi] != long.MinValue && p->dts <= lastDts[oi]) p->dts = lastDts[oi] + 1;
                if (p->pts != ffmpeg.AV_NOPTS_VALUE && p->pts < p->dts) p->pts = p->dts;
                lastDts[oi] = p->dts;
                ffmpeg.av_interleaved_write_frame(oc, p).Check("Writing");
            }

            int block = enc.FrameSize;
            var mix = new float[block * 2];
            var tmp = new float[block * 2];
            long mixPos = long.MinValue;
            long demuxPos = long.MinValue; // newest source packet read so far (samples)
            bool single = queues.Length == 1;
            void Produce()
            {
                if (mixPos == long.MinValue)
                {
                    var started = queues.Where(q => q.Anchored).ToList();
                    if (started.Count == 0) return;
                    long first = started.Min(q => q.Start);
                    // Wait until every track has started (or has none), unless one is missing for longer than the slack.
                    if (queues.Any(q => !q.Anchored && !q.Ended) && demuxPos < first + TrackDecoder.GapSlack) return;
                    mixPos = first;
                }
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    long need = mixPos + block;
                    // A track with no audio here (a gap, or it ended early) is silence once the others are well past it.
                    bool ready = queues.All(q => q.Ended || (q.Anchored && q.End >= need)) || demuxPos >= need + TrackDecoder.GapSlack;
                    if (!ready) break;
                    if (queues.All(q => q.Ended) && mixPos >= queues.Max(q => q.Anchored ? q.End : long.MinValue)) break;
                    Array.Clear(mix);
                    for (int k = 0; k < queues.Length; k++)
                    {
                        queues[k].Take(mixPos, tmp, block);
                        float g = gains[k];
                        if (g == 0) continue;
                        for (int i = 0; i < mix.Length; i++) mix[i] += tmp[i] * g;
                    }
                    for (int i = 0; i < mix.Length; i++) mix[i] = Limit(mix[i], single, gains[0]);
                    enc!.Encode(mix, mixPos);
                    mixPos += block;
                }
                Flush();
            }
            void Flush()
            {
                foreach (var ep in pending)
                    using (ep) WriteOut(ep.Packet, mixOut, ep.TimeBase);
                pending.Clear();
            }

            long duration = inCtx->duration > 0 ? inCtx->duration : 1;
            long lastReport = long.MinValue;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int rr = ffmpeg.av_read_frame(inCtx, pkt);
                if (rr < 0)
                {
                    if (rr != ffmpeg.AVERROR_EOF) Log.Warn($"Remix: stopped reading at {FFmpegSetup.ErrorText(rr)} (damaged file?)");
                    break;
                }
                try
                {
                    int si = pkt->stream_index;
                    if (si >= map.Length) continue;
                    var ist = inCtx->streams[si];
                    if (progress != null && pkt->pts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        long us = ffmpeg.av_rescale_q(pkt->pts, ist->time_base, new AVRational { num = 1, den = 1_000_000 });
                        if (us - lastReport > 250_000) { lastReport = us; progress.Report(Math.Clamp((double)us / duration, 0, 0.99)); }
                    }
                    if (srcIndex[si] >= 0)
                    {
                        var dec = decoders[srcIndex[si]];
                        demuxPos = Math.Max(demuxPos, dec.PositionOf(pkt));
                        dec.Decode(pkt, queues[srcIndex[si]]);
                        Produce();
                    }
                    if (map[si] >= 0) WriteOut(pkt, map[si], ist->time_base);
                }
                finally { ffmpeg.av_packet_unref(pkt); }
            }
            for (int k = 0; k < decoders.Count; k++)
            {
                decoders[k].Decode(null, queues[k]);
                queues[k].Ended = true;
            }
            Produce();
            enc.Flush();
            Flush();
            ffmpeg.av_write_trailer(outCtx).Check("Finishing the file");
            progress?.Report(1);
        }
        finally
        {
            foreach (var p in pending) p.Dispose();
            enc?.Dispose();
            foreach (var d in decoders) d.Dispose();
            ffmpeg.av_packet_free(&pkt);
            if (inCtx != null) ffmpeg.avformat_close_input(&inCtx);
            if (outCtx != null)
            {
                if (outCtx->pb != null) ffmpeg.avio_closep(&outCtx->pb);
                ffmpeg.avformat_free_context(outCtx);
            }
        }
    }

    private static void CopyChapters(AVFormatContext* inCtx, AVFormatContext* outCtx)
    {
        if (inCtx->nb_chapters == 0) return;
        var arr = (AVChapter**)ffmpeg.av_mallocz((ulong)(sizeof(AVChapter*) * inCtx->nb_chapters));
        for (int i = 0; i < inCtx->nb_chapters; i++)
        {
            var src = inCtx->chapters[i];
            var ch = (AVChapter*)ffmpeg.av_mallocz((ulong)sizeof(AVChapter));
            ch->id = src->id;
            ch->time_base = src->time_base;
            ch->start = src->start;
            ch->end = src->end;
            ffmpeg.av_dict_copy(&ch->metadata, src->metadata, 0);
            arr[i] = ch;
        }
        outCtx->chapters = arr;
        outCtx->nb_chapters = inCtx->nb_chapters;
    }

    /// <summary>Removes temp files that an export killed mid-way (app closed during a long save) left in <paramref name="folder"/>.</summary>
    public static void CleanStaleTemps(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.EnumerateFiles(folder, ".*.part"))
            {
                if (!Regex.IsMatch(Path.GetFileName(f), @"^\..+\.[0-9a-f]{32}\.part$")) continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromHours(1)) continue; // might still be in use
                try { File.Delete(f); Log.Info("Removed a leftover Fix audio temp file: " + f); } catch { }
            }
        }
        catch (Exception ex) { Log.Debug("Temp cleanup: " + ex.Message); }
    }

    public static ContainerFormat ContainerOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mkv" => ContainerFormat.Mkv,
        ".mov" => ContainerFormat.Mov,
        _ => ContainerFormat.Mp4,
    };

    /// <summary>
    /// Exports next to the original, then either keeps both (returns the new file) or swaps it in place of the
    /// original, which goes to the Recycle Bin. A failed or cancelled export leaves the original untouched.
    /// </summary>
    public static string ExportToFile(string input, MediaInfo info, RemixLevels levels, bool replaceOriginal,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var container = ContainerOf(input);
        var temp = Path.Combine(Path.GetDirectoryName(input)!, $".{Path.GetFileNameWithoutExtension(input)}.{Guid.NewGuid():N}.part");
        try
        {
            Export(input, temp, info, levels, container, progress, ct);
            ct.ThrowIfCancellationRequested();
            // Give the new file a real name before the original is touched, so no later failure can lose it.
            var remix = SuggestPath(input);
            File.Move(temp, remix);
            if (!replaceOriginal) return remix;

            DateTime created = File.GetCreationTime(input), written = File.GetLastWriteTime(input);
            try { Native.MoveToRecycleBin(input); }
            catch (Exception ex)
            {
                Log.Warn("Couldn't move the original to the Recycle Bin: " + ex.Message);
                throw new OriginalKeptException(remix, ex);
            }
            try
            {
                File.Move(remix, input);
                // Keep the recording's date so it stays in place in the Recordings list.
                try { File.SetCreationTime(input, created); File.SetLastWriteTime(input, written); } catch { }
                return input;
            }
            catch (Exception ex)
            {
                // The original is in the Recycle Bin; the remix keeps its own name.
                Log.Warn("Couldn't put the remix in place of the original: " + ex.Message);
                return remix;
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) { Log.Warn("Couldn't remove " + temp + ": " + ex.Message); }
        }
    }
}
