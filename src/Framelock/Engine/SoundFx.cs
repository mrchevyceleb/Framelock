using System.IO;
using System.Media;

namespace Framelock.Engine;

/// <summary>Tiny synthesized UI sounds (Framelock's own audio is excluded from desktop capture).</summary>
public static class SoundFx
{
    private static readonly Lazy<byte[]> StartWav = new(() => Tone(new[] { (880.0, 0.07), (1320.0, 0.09) }));
    private static readonly Lazy<byte[]> StopWav = new(() => Tone(new[] { (1320.0, 0.07), (660.0, 0.11) }));
    private static readonly Lazy<byte[]> TickWav = new(() => Tone(new[] { (1000.0, 0.05) }));
    private static readonly Lazy<byte[]> ShotWav = new(() => Tone(new[] { (1760.0, 0.04), (1320.0, 0.05) }));

    public static bool Enabled { get; set; } = true;

    public static void Start() => Play(StartWav.Value);
    public static void Stop() => Play(StopWav.Value);
    public static void Tick() => Play(TickWav.Value);
    public static void Shot() => Play(ShotWav.Value);

    private static void Play(byte[] wav)
    {
        if (!Enabled) return;
        try { new SoundPlayer(new MemoryStream(wav)).Play(); } catch { }
    }

    private static byte[] Tone((double freq, double seconds)[] notes)
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var (freq, seconds) in notes)
        {
            int n = (int)(rate * seconds);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, i / (rate * 0.005)) * Math.Min(1, (n - i) / (rate * 0.02));
                samples.Add((short)(Math.Sin(2 * Math.PI * freq * i / rate) * env * 0.25 * short.MaxValue));
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples.Count * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Count * 2);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
