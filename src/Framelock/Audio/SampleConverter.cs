using System.Buffers.Binary;
using NAudio.Wave;

namespace Framelock.Audio;

/// <summary>Converts any WASAPI capture format to 48 kHz interleaved stereo float (downmix + linear resample when needed).</summary>
public sealed class SampleConverter
{
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly int _channels, _bits, _blockAlign, _rate;
    private readonly bool _float;
    private readonly double _step;
    private double _frac;
    private float _prevL, _prevR;
    private float[] _tmp = new float[4096];

    public const int OutRate = 48000;
    public bool IsPassthrough { get; }

    public SampleConverter(WaveFormat f)
    {
        _channels = f.Channels;
        _bits = f.BitsPerSample;
        _blockAlign = f.BlockAlign;
        _rate = f.SampleRate;
        _float = f.Encoding == WaveFormatEncoding.IeeeFloat
                 || (f is WaveFormatExtensible x && x.SubFormat == SubtypeFloat);
        if (!_float && f.Encoding != WaveFormatEncoding.Pcm && !(f is WaveFormatExtensible y && y.SubFormat == SubtypePcm))
            throw new NotSupportedException($"Unsupported capture format {f}");
        _step = (double)_rate / OutRate;
        IsPassthrough = _float && _bits == 32 && _channels == 2 && _rate == OutRate;
    }

    /// <summary>Converts a capture packet; returns the number of output frames written to <paramref name="output"/> (grown as needed).</summary>
    public int Convert(ReadOnlySpan<byte> input, bool silent, ref float[] output)
    {
        int frames = input.Length / _blockAlign;
        if (frames == 0) return 0;
        if (_tmp.Length < frames * 2) _tmp = new float[frames * 2];
        var st = _tmp;

        if (silent) Array.Clear(st, 0, frames * 2);
        else if (IsPassthrough)
        {
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(input[..(frames * 8)]).CopyTo(st);
        }
        else
        {
            for (int i = 0; i < frames; i++)
            {
                var frame = input.Slice(i * _blockAlign, _blockAlign);
                float l, r;
                if (_channels == 1) { l = r = Sample(frame, 0); }
                else if (_channels == 2) { l = Sample(frame, 0); r = Sample(frame, 1); }
                else
                {
                    // FL FR FC LFE BL BR (SL SR) → stereo, LFE dropped
                    float c = _channels > 2 ? Sample(frame, 2) : 0;
                    float bl = _channels > 4 ? Sample(frame, 4) : 0, br = _channels > 5 ? Sample(frame, 5) : 0;
                    float sl = _channels > 6 ? Sample(frame, 6) : 0, sr = _channels > 7 ? Sample(frame, 7) : 0;
                    l = (Sample(frame, 0) + 0.707f * (c + bl + sl)) * 0.6f;
                    r = (Sample(frame, 1) + 0.707f * (c + br + sr)) * 0.6f;
                }
                st[2 * i] = l;
                st[2 * i + 1] = r;
            }
        }

        if (_rate == OutRate)
        {
            if (output.Length < frames * 2) output = new float[frames * 2];
            Array.Copy(st, output, frames * 2);
            return frames;
        }

        // Linear resampler, continuous across packets.
        int maxOut = (int)Math.Ceiling((frames + 1) / _step) + 2;
        if (output.Length < maxOut * 2) output = new float[maxOut * 2];
        int n = 0;
        double pos = _frac;
        while (pos < frames)
        {
            int i0 = (int)Math.Floor(pos);
            double t = pos - i0;
            float l0 = i0 - 1 < 0 ? _prevL : st[2 * (i0 - 1)], r0 = i0 - 1 < 0 ? _prevR : st[2 * (i0 - 1) + 1];
            float l1 = st[2 * i0], r1 = st[2 * i0 + 1];
            // pos is measured from the previous packet's last frame (index -1)
            output[2 * n] = (float)(l0 + (l1 - l0) * t);
            output[2 * n + 1] = (float)(r0 + (r1 - r0) * t);
            n++;
            pos += _step;
        }
        _frac = pos - frames;
        _prevL = st[2 * (frames - 1)];
        _prevR = st[2 * (frames - 1) + 1];
        return n;
    }

    private float Sample(ReadOnlySpan<byte> frame, int ch)
    {
        int bps = _bits / 8;
        var s = frame.Slice(ch * bps, bps);
        if (_float) return _bits == 64 ? (float)BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s);
        return _bits switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
            24 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
            8 => (s[0] - 128) / 128f,
            _ => 0,
        };
    }
}
