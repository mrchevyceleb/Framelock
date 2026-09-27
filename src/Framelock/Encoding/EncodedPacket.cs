using FFmpeg.AutoGen;

namespace Framelock.Encoding;

/// <summary>
/// An encoded packet (ref-counted FFmpeg buffer, no managed copy) tagged with its track and pipeline-timeline times.
/// Track 0 = video, 1..N = audio tracks.
/// </summary>
public sealed unsafe class EncodedPacket : IDisposable
{
    private AVPacket* _pkt;
    public AVPacket* Packet => _pkt;
    public int Track { get; }
    public bool IsVideo => Track == 0;
    public bool IsKey { get; }
    /// <summary>Microseconds since pipeline start.</summary>
    public long PtsUs { get; }
    public long DtsUs { get; }
    public long DurationUs { get; }
    public AVRational TimeBase { get; }
    public int Size => _pkt == null ? 0 : _pkt->size;

    public EncodedPacket(AVPacket* ownedPacket, int track, AVRational timeBase)
    {
        _pkt = ownedPacket;
        Track = track;
        TimeBase = timeBase;
        IsKey = (ownedPacket->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
        var us = new AVRational { num = 1, den = 1_000_000 };
        PtsUs = ffmpeg.av_rescale_q(ownedPacket->pts, timeBase, us);
        DtsUs = ownedPacket->dts == ffmpeg.AV_NOPTS_VALUE ? PtsUs : ffmpeg.av_rescale_q(ownedPacket->dts, timeBase, us);
        DurationUs = ffmpeg.av_rescale_q(ownedPacket->duration, timeBase, us);
    }

    private EncodedPacket(EncodedPacket src)
    {
        _pkt = ffmpeg.av_packet_clone(src._pkt);
        Track = src.Track;
        TimeBase = src.TimeBase;
        IsKey = src.IsKey;
        PtsUs = src.PtsUs;
        DtsUs = src.DtsUs;
        DurationUs = src.DurationUs;
    }

    /// <summary>Cheap: shares the underlying ref-counted buffer.</summary>
    public EncodedPacket Clone() => new(this);

    public void Dispose()
    {
        if (_pkt == null) return;
        var p = _pkt;
        ffmpeg.av_packet_free(&p);
        _pkt = null;
    }
}

/// <summary>Something that consumes encoded packets (file writer, replay buffer). Implementations take ownership.</summary>
public interface IPacketSink
{
    void Write(EncodedPacket packet);
    /// <summary>The encoders were shut down; no more packets will arrive.</summary>
    void EndOfStream();
}

/// <summary>Fans encoded packets out to every attached sink. Thread-safe.</summary>
public sealed class PacketHub
{
    private volatile IPacketSink[] _sinks = Array.Empty<IPacketSink>();
    private readonly object _lock = new();

    public int SinkCount => _sinks.Length;

    public void Add(IPacketSink sink)
    {
        lock (_lock) _sinks = _sinks.Append(sink).ToArray();
    }

    public void Remove(IPacketSink sink)
    {
        lock (_lock) _sinks = _sinks.Where(s => s != sink).ToArray();
    }

    public void Publish(EncodedPacket packet)
    {
        var sinks = _sinks;
        try
        {
            foreach (var s in sinks) s.Write(packet.Clone());
        }
        finally { packet.Dispose(); }
    }

    public void EndOfStream()
    {
        foreach (var s in _sinks) s.EndOfStream();
    }
}
