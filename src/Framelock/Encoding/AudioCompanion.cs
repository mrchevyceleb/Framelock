using System.IO;
using FFmpeg.AutoGen;
using Framelock.Core;

namespace Framelock.Encoding;

/// <summary>Editing audio lives beside the video; the video itself has one unambiguous playback mix.</summary>
public static class AudioCompanion
{
    private const string PairPrefix = "Framelock audio pair: ";
    private static bool IsPairLine(string line) => line.StartsWith(PairPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(line[PairPrefix.Length..], "N", out _);
    internal static string PairComment(string id, string? comment = null)
    {
        if (string.IsNullOrEmpty(comment)) return PairPrefix + id;
        // Retain existing user comments while replacing only our own identity line.
        var lines = comment.Split('\n');
        string retained = string.Join("\n", lines.Where(line => !IsPairLine(line.TrimEnd('\r'))));
        return retained.Length == 0 ? PairPrefix + id : retained + "\n" + PairPrefix + id;
    }
    internal static string? PairId(string? comment)
    {
        if (comment == null) return null;
        var pairs = comment.Split('\n').Select(line => line.TrimEnd('\r')).Where(IsPairLine).ToArray();
        return pairs.Length == 1 && Guid.TryParseExact(pairs[0][PairPrefix.Length..], "N", out var id) ? id.ToString("N") : null;
    }
    public static string PathFor(string video) => video + ".audio.mka";

    public static void CopyFrom(string input, string output, MediaInfo info, string? pairId = null)
    {
        if (!info.HasSeparateTracks) return;
        if (info.AudioSourcePath != null && pairId == info.RecordingId) File.Copy(info.AudioSourcePath, output);
        else Remuxer.Remux(info.AudioSourcePath ?? input, output, ContainerFormat.Mkv,
            streamIndexes: new HashSet<int> { info.Desktop!.StreamIndex, info.Mic!.StreamIndex }, pairId: pairId);
    }

    internal static MediaInfo RefreshInfo(string input, MediaInfo previous)
    {
        var current = MediaFile.Probe(input);
        if (previous.HasSeparateTracks && (!current.HasSeparateTracks || previous.AudioSourcePath != current.AudioSourcePath || previous.RecordingId != current.RecordingId))
            throw new InvalidOperationException("The editing audio changed. Close Fix audio and open it again.");
        return current;
    }

    internal static unsafe void ValidateSource(AVFormatContext* context, MediaInfo info)
    {
        if (info.RecordingId != null && PairId(MediaFile.Tag(context->metadata, "comment")) != info.RecordingId)
            throw new InvalidOperationException("The editing audio belongs to another recording.");
        var tracks = info.HasSeparateTracks ? new[] { info.Desktop!, info.Mic! }
            : new[] { info.Mix ?? info.Audio.First() };
        foreach (var track in tracks)
        {
            int index = track.StreamIndex;
            if (index < 0 || index >= context->nb_streams || context->streams[index]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                throw new InvalidOperationException("The recording audio changed. Close Fix audio and open it again.");
            var metadata = context->streams[index]->metadata;
            var role = MediaFile.RoleOf(MediaFile.Tag(metadata, "title") ?? MediaFile.Tag(metadata, "handler_name") ?? "");
            if (role != track.Role) throw new InvalidOperationException("The recording audio tracks changed. Close Fix audio and open it again.");
        }
    }

    public static void MoveAlongside(string? audioPath, string video)
    {
        if (audioPath == null || !File.Exists(audioPath)) return;
        string destination = PathFor(video);
        if (!string.Equals(audioPath, destination, StringComparison.OrdinalIgnoreCase)) File.Move(audioPath, destination);
    }

    public static bool Exists(string video) => File.Exists(PathFor(video));

    /// <summary>Publishes a complete pair without overwriting an existing recording.</summary>
    internal static void Publish(string tempVideo, string tempAudio, string output)
    {
        bool audioMoved = false;
        try
        {
            if (File.Exists(output)) throw new IOException("The output recording already exists.");
            if (File.Exists(tempAudio)) { File.Move(tempAudio, PathFor(output)); audioMoved = true; }
            File.Move(tempVideo, output);
        }
        catch
        {
            if (audioMoved) File.Delete(PathFor(output));
            throw;
        }
    }

    /// <summary>Lossless sharing copy of an older recording, with only its combined audio in the video.</summary>
    public static string ExportForSharing(string input, MediaInfo info, IProgress<double>? progress = null)
    {
        info = RefreshInfo(input, info);
        string pairId = info.RecordingId ?? Guid.NewGuid().ToString("N");
        var mix = info.Mix ?? (info.Audio.Count == 1 ? info.Audio[0] : null);
        if (mix == null && info.Audio.Count > 0)
            throw new InvalidOperationException("This file has no identified combined audio track. Use Fix audio to create a mix first.");
        string extension = Path.GetExtension(input), stem = Path.GetFileNameWithoutExtension(input);
        string output = Path.Combine(Path.GetDirectoryName(input)!, stem + " (share)" + extension);
        for (int i = 2; File.Exists(output) || Exists(output); i++)
            output = Path.Combine(Path.GetDirectoryName(input)!, $"{stem} (share {i}){extension}");
        string temp = Path.Combine(Path.GetDirectoryName(input)!, $".{stem}.{Guid.NewGuid():N}.part");
        string audioTemp = PathFor(temp);
        try
        {
            var keep = new HashSet<int>();
            if (mix != null) keep.Add(mix.StreamIndex);
            Remuxer.Remux(input, temp, AudioRemixer.ContainerOf(input), progress, keep, keepVideo: true, pairId: pairId);
            CopyFrom(input, audioTemp, info, pairId);
            Publish(temp, audioTemp, output);
            return output;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(audioTemp)) File.Delete(audioTemp);
        }
    }
}

/// <summary>One video/mix writer and an optional audio-only editing writer on the exact same cut timeline.</summary>
internal sealed class RecordingMuxWriter : IDisposable
{
    private readonly MuxWriter _video;
    private readonly MuxWriter? _audio;
    public string Path => _video.Path;
    public string? AudioTracksPath => _audio?.Path;
    public long BytesWritten => _video.BytesWritten + (_audio?.BytesWritten ?? 0);
    public long MaxVideoEndUs => _video.MaxVideoEndUs;
    public bool HasVideo => _video.HasVideo;

    public RecordingMuxWriter(string path, ContainerFormat container, IReadOnlyList<StreamInfo> streams,
        bool faststart = false, string? audioTracksPath = null)
    {
        var separate = streams.Where(s => s.Track > 1).ToArray();
        audioTracksPath ??= AudioCompanion.PathFor(path);
        if (File.Exists(path) || (separate.Length > 0 && File.Exists(audioTracksPath)))
            throw new IOException("The recording or its editing audio already exists.");
        string pairId = Guid.NewGuid().ToString("N");
        _video = new MuxWriter(path, container, streams.Where(s => s.Track <= 1).ToArray(), faststart, pairId);
        try
        {
            if (separate.Length == 0) return;
            _audio = new MuxWriter(audioTracksPath, ContainerFormat.Mkv, separate, pairId: pairId);
        }
        catch { _video.Dispose(); throw; }
    }

    public void Write(EncodedPacket packet, long offsetUs)
    {
        if (packet.Track > 1 && _audio != null) _audio.Write(packet, offsetUs);
        else _video.Write(packet, offsetUs);
    }

    public void SetChapters(IReadOnlyList<Chapter> chapters, long totalMs) => _video.SetChapters(chapters, totalMs);

    public bool Finish()
    {
        bool written = _video.Finish();
        if (_audio != null && !_audio.Finish()) throw new IOException("The video was saved, but no editing audio was written.");
        return written;
    }

    public void Dispose() { _video.Dispose(); _audio?.Dispose(); }
}
