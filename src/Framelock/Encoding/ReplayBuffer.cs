using Framelock.Core;

namespace Framelock.Encoding;

/// <summary>
/// Keeps the last N seconds of encoded packets in memory (no re-encoding) so "Save replay" writes the moment you just played.
/// </summary>
public sealed class ReplayBuffer : IPacketSink, IDisposable
{
    private readonly LinkedList<EncodedPacket> _packets = new();
    private readonly object _lock = new();
    private readonly IReadOnlyList<StreamInfo> _streams;
    private long _bytes;
    private bool _disposed;

    public int Seconds { get; set; }
    public long MemoryBytes { get { lock (_lock) return _bytes; } }

    public double BufferedSeconds
    {
        get
        {
            lock (_lock)
            {
                var first = _packets.FirstOrDefault(p => p.IsVideo);
                var last = _packets.LastOrDefault(p => p.IsVideo);
                return first == null || last == null ? 0 : (last.PtsUs - first.PtsUs) / 1e6;
            }
        }
    }

    public ReplayBuffer(IReadOnlyList<StreamInfo> streams, int seconds)
    {
        _streams = streams;
        Seconds = seconds;
    }

    public void Write(EncodedPacket packet)
    {
        lock (_lock)
        {
            if (_disposed) { packet.Dispose(); return; }
            _packets.AddLast(packet);
            _bytes += packet.Size;
            if (packet.IsVideo && packet.IsKey) Trim(packet.PtsUs);
            // Hard bounds that don't depend on keyframes arriving (encoder stalled, very long GOPs, huge bitrates).
            long oldestAllowed = packet.PtsUs - (Seconds + 10) * 1_000_000L;
            while (_packets.First is { } first && first != _packets.Last && (first.Value.PtsUs < oldestAllowed || _bytes > MaxBytes))
            {
                _packets.RemoveFirst();
                _bytes -= first.Value.Size;
                first.Value.Dispose();
            }
        }
    }

    /// <summary>Memory ceiling: a quarter of the machine's memory, at most 4 GB. Saving starts at the first kept keyframe.</summary>
    private static readonly long MaxBytes = Math.Min(4L << 30, Math.Max(1L << 30, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4));

    /// <summary>Drops everything before the newest keyframe that still leaves at least <see cref="Seconds"/> buffered.</summary>
    private void Trim(long newestUs)
    {
        long keepFrom = newestUs - Seconds * 1_000_000L;
        LinkedListNode<EncodedPacket>? cut = null;
        for (var n = _packets.First; n != null; n = n.Next)
        {
            var p = n.Value;
            if (!p.IsVideo || !p.IsKey) continue;
            if (p.PtsUs <= keepFrom) cut = n;
            else break;
        }
        if (cut == null) return;
        while (_packets.First != null && _packets.First != cut)
        {
            var p = _packets.First.Value;
            _packets.RemoveFirst();
            _bytes -= p.Size;
            p.Dispose();
        }
    }

    public void EndOfStream() { }

    /// <summary>Writes the buffered moment to <paramref name="path"/> (MP4, fast-start). Snapshot is taken synchronously.</summary>
    public Task<FileSinkResult> SaveAsync(string path)
    {
        List<EncodedPacket> snapshot;
        lock (_lock) snapshot = _packets.Select(p => p.Clone()).ToList();
        MuxWriter mux;
        try { mux = new MuxWriter(path, ContainerFormat.Mp4, _streams, faststart: true); }
        catch (Exception ex)
        {
            foreach (var p in snapshot) p.Dispose();
            return Task.FromResult(new FileSinkResult(path, false, 0, 0, Array.Empty<Chapter>(), ex.Message));
        }

        return Task.Run(() =>
        {
            string? error = null;
            try
            {
                var start = snapshot.FirstOrDefault(p => p.IsVideo && p.IsKey);
                if (start == null) throw new InvalidOperationException("The replay buffer is still empty.");
                long offset = start.PtsUs;
                // Encoders hold a few frames of latency: end the audio where the video ends.
                long end = snapshot.Where(p => p.IsVideo).Max(p => p.PtsUs + p.DurationUs);
                bool begun = false;
                foreach (var p in snapshot)
                {
                    if (p == start) begun = true;
                    bool keep = p.IsVideo ? begun : p.PtsUs >= offset && p.PtsUs + p.DurationUs <= end;
                    if (keep) mux.Write(p, offset);
                    else p.Dispose();
                }
                snapshot.Clear();
                if (!mux.Finish()) throw new InvalidOperationException("Nothing to save.");
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("Saving replay failed", ex);
            }
            finally
            {
                foreach (var p in snapshot) p.Dispose();
                mux.Dispose();
            }
            Log.Info($"Replay saved: {path} ({mux.MaxVideoEndUs / 1e6:F1}s)");
            return new FileSinkResult(path, error == null, mux.MaxVideoEndUs, mux.BytesWritten, Array.Empty<Chapter>(), error);
        });
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var p in _packets) p.Dispose();
            _packets.Clear();
            _bytes = 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var p in _packets) p.Dispose();
            _packets.Clear();
            _bytes = 0;
        }
    }
}
