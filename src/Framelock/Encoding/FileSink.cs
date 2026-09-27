using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Framelock.Core;

namespace Framelock.Encoding;

public sealed record FileSinkResult(string Path, bool Success, long DurationUs, long Bytes, IReadOnlyList<Chapter> Markers, string? Error);

/// <summary>
/// Writes one recording file on its own thread. Every cut (start, pause, resume, stop, split) lands exactly on a forced
/// keyframe: video starts at the first keyframe at/after the requested time and ends right before the first keyframe at/after
/// the cut time, and audio is trimmed to the same instants, so segments join seamlessly and files never start mid-GOP.
/// </summary>
public sealed class FileSink : IPacketSink
{
    private enum State { WaitingStart, Active, Cutting, Paused, Done }
    private enum CtlKind { Pause, Resume, Stop, Marker }
    private readonly record struct Ctl(CtlKind Kind, long AtUs, string? Label = null);

    private readonly BlockingCollection<EncodedPacket> _queue = new(new ConcurrentQueue<EncodedPacket>());
    private readonly ConcurrentQueue<Ctl> _control = new();
    private readonly Thread _thread;
    private readonly MuxWriter _mux;
    private readonly int[] _audioTracks;
    private readonly TaskCompletionSource<FileSinkResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Chapter> _markers = new();
    private readonly bool _saveMarkers;

    private State _state = State.WaitingStart;
    private long _startAtUs;             // wait for a keyframe at/after this timeline time
    private long _offsetUs;              // timeline → file time
    private long _segmentStartUs;        // timeline time of the keyframe that opened the current segment
    private bool _everStarted;
    private long? _cutRequestUs;         // pending pause/stop cut time
    private bool _cutIsStop;
    private long _cutKeyUs;              // keyframe time where the current segment ended
    private long _cutDecidedAt;          // Stopwatch ticks
    private readonly HashSet<int> _audioDoneForCut = new();
    private long? _pendingResumeUs;
    private readonly List<EncodedPacket> _heldAudio = new();
    private volatile bool _eos;
    private string? _error;

    public string Path => _mux.Path;
    public Task<FileSinkResult> Completion => _done.Task;
    public long DurationUs => _mux.MaxVideoEndUs;
    public long BytesWritten => _mux.BytesWritten;
    public bool Started => _everStarted;
    public int QueueLength => _queue.Count;

    public FileSink(string path, ContainerFormat container, IReadOnlyList<StreamInfo> streams, long startAtUs, bool saveMarkers)
    {
        _mux = new MuxWriter(path, container, streams);
        _audioTracks = streams.Where(s => !s.IsVideo).Select(s => s.Track).ToArray();
        _startAtUs = startAtUs;
        _saveMarkers = saveMarkers;
        _thread = new Thread(Run) { Name = "Framelock file writer", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    // ---------------------------------------------------------------- public API (any thread)

    public void Write(EncodedPacket packet)
    {
        if (_queue.IsAddingCompleted) { packet.Dispose(); return; }
        try { _queue.Add(packet); }
        catch (InvalidOperationException) { packet.Dispose(); }
    }

    public void EndOfStream() => _eos = true;
    public void Pause(long atUs) => _control.Enqueue(new Ctl(CtlKind.Pause, atUs));
    public void Resume(long atUs) => _control.Enqueue(new Ctl(CtlKind.Resume, atUs));
    public void Stop(long atUs) => _control.Enqueue(new Ctl(CtlKind.Stop, atUs));
    public void AddMarker(long timelineUs, string label) => _control.Enqueue(new Ctl(CtlKind.Marker, timelineUs, label));

    // ---------------------------------------------------------------- writer thread

    private void Run()
    {
        try
        {
            while (_state != State.Done)
            {
                ProcessControl();
                if (_queue.TryTake(out var p, 50))
                {
                    try { Handle(p); }
                    catch { p.Dispose(); throw; }
                }
                CheckTimeouts();
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Log.Error($"Recording to {Path} failed", ex);
        }
        Finish();
    }

    private void ProcessControl()
    {
        while (_control.TryDequeue(out var c))
        {
            switch (c.Kind)
            {
                case CtlKind.Marker:
                    if (_everStarted)
                    {
                        long ms = Math.Max(0, (c.AtUs - _offsetUs) / 1000);
                        _markers.Add(new Chapter(ms, string.IsNullOrWhiteSpace(c.Label) ? $"Marker {_markers.Count + 1}" : c.Label!));
                    }
                    break;
                case CtlKind.Pause:
                    if (_state == State.Active) { _cutRequestUs = c.AtUs; _cutIsStop = false; }
                    break;
                case CtlKind.Resume:
                    if (_state == State.Paused) { _state = State.WaitingStart; _startAtUs = c.AtUs; }
                    else _pendingResumeUs = c.AtUs; // cut still in progress
                    break;
                case CtlKind.Stop:
                    if (_state is State.Active) { _cutRequestUs = c.AtUs; _cutIsStop = true; }
                    else if (_state == State.Cutting) _cutIsStop = true;
                    else _state = State.Done; // waiting/paused: nothing more to write
                    break;
            }
        }
    }

    private void Handle(EncodedPacket p)
    {
        if (p.IsVideo) HandleVideo(p);
        else HandleAudio(p);
    }

    private void HandleVideo(EncodedPacket p)
    {
        switch (_state)
        {
            case State.WaitingStart:
                if (p.IsKey && p.PtsUs >= _startAtUs - 500)
                {
                    _offsetUs = _everStarted ? _offsetUs + (p.PtsUs - _cutKeyUs) : p.PtsUs;
                    _everStarted = true;
                    _segmentStartUs = p.PtsUs;
                    _state = State.Active;
                    _mux.Write(p, _offsetUs);
                    // Audio that arrived before the video start: keep what belongs to this segment.
                    foreach (var a in _heldAudio)
                    {
                        if (a.PtsUs >= p.PtsUs) _mux.Write(a, _offsetUs);
                        else a.Dispose();
                    }
                    _heldAudio.Clear();
                }
                else p.Dispose();
                break;

            case State.Active:
                if (_cutRequestUs is long cut && p.IsKey && p.PtsUs >= cut - 500)
                {
                    _cutKeyUs = p.PtsUs;
                    _cutDecidedAt = Stopwatch.GetTimestamp();
                    _cutRequestUs = null;
                    _audioDoneForCut.Clear();
                    _state = State.Cutting;
                    // Audio held back while the cut was pending: write the part before the cut.
                    var held = _heldAudio.ToList();
                    _heldAudio.Clear();
                    foreach (var a in held) HandleAudio(a);
                    TryCompleteCut();
                    // This keyframe may already be the start of the next segment (instant resume).
                    if (_state == State.WaitingStart) HandleVideo(p);
                    else p.Dispose();
                }
                else _mux.Write(p, _offsetUs);
                break;

            default:
                p.Dispose();
                break;
        }
    }

    private void HandleAudio(EncodedPacket a)
    {
        switch (_state)
        {
            case State.Active:
                if (_cutRequestUs is long cut && a.PtsUs >= cut - 50_000)
                {
                    Hold(a); // don't know the exact cut keyframe yet
                }
                else if (a.PtsUs >= _segmentStartUs) _mux.Write(a, _offsetUs);
                else a.Dispose();
                break;

            case State.Cutting:
                if (a.PtsUs < _cutKeyUs) _mux.Write(a, _offsetUs);
                else
                {
                    _audioDoneForCut.Add(a.Track);
                    Hold(a);
                    TryCompleteCut();
                }
                break;

            case State.WaitingStart:
            case State.Paused:
                Hold(a);
                break;

            default:
                a.Dispose();
                break;
        }
    }

    private void Hold(EncodedPacket a)
    {
        _heldAudio.Add(a);
        // Never hold more than ~10 s of audio (e.g. a long pause).
        if (_heldAudio.Count > 2000)
        {
            _heldAudio[0].Dispose();
            _heldAudio.RemoveAt(0);
        }
    }

    private void TryCompleteCut()
    {
        if (_state != State.Cutting) return;
        if (_audioTracks.Any(t => !_audioDoneForCut.Contains(t))) return;
        CompleteCut();
    }

    private void CompleteCut()
    {
        if (_cutIsStop) { _state = State.Done; return; }
        if (_pendingResumeUs is long r) { _pendingResumeUs = null; _state = State.WaitingStart; _startAtUs = r; }
        else _state = State.Paused;
        // Held audio past the cut is kept for the next segment (filtered by its start keyframe).
    }

    private void CheckTimeouts()
    {
        if (_state == State.Cutting)
        {
            // An audio track may have gone quiet (device removed) - don't wait forever for it.
            if (Stopwatch.GetElapsedTime(_cutDecidedAt).TotalMilliseconds > 1500 || _eos) CompleteCut();
        }
        if (_eos && _queue.Count == 0 && _state != State.Done)
        {
            // Encoders are gone: nothing more will ever arrive.
            _state = State.Done;
        }
    }

    private void Finish()
    {
        _queue.CompleteAdding();
        while (_queue.TryTake(out var p)) p.Dispose();
        foreach (var a in _heldAudio) a.Dispose();
        _heldAudio.Clear();

        bool ok = false;
        long duration = _mux.MaxVideoEndUs;
        try
        {
            if (_saveMarkers && _markers.Count > 0)
                _mux.SetChapters(WithStartChapter(_markers), duration / 1000);
            ok = _mux.Finish() && _mux.HasVideo;
        }
        catch (Exception ex)
        {
            _error ??= ex.Message;
            Log.Error("Finalizing recording failed", ex);
        }
        finally { _mux.Dispose(); }

        if (!ok)
        {
            _error ??= "No video frames were recorded.";
            try { if (File.Exists(Path) && new FileInfo(Path).Length < 64 * 1024) File.Delete(Path); } catch { }
        }
        Log.Info($"Recording finished: {Path} ({duration / 1e6:F1}s, {_mux.BytesWritten / 1048576.0:F1} MB){(ok ? "" : " - " + _error)}");
        _done.TrySetResult(new FileSinkResult(Path, ok && _error == null, duration, _mux.BytesWritten, _markers.ToList(), _error));
    }

    public static List<Chapter> WithStartChapter(IReadOnlyList<Chapter> markers)
    {
        var list = markers.OrderBy(m => m.StartMs).ToList();
        if (list.Count == 0 || list[0].StartMs > 0) list.Insert(0, new Chapter(0, "Start"));
        return list;
    }
}
