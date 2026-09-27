namespace Framelock.Graphics;

internal static class Shaders
{
    public const string Source = """
cbuffer Params : register(b0)
{
    float4 DstRect;   // NDC x0, y0, x1, y1
    float4 SrcRect;   // UV  u0, v0, u1, v1
    float4 SrcSize;   // w, h, 1/w, 1/h of the bound texture
    float4 Misc;      // x: opacity, y: src px per dst px (x), z: src px per dst px (y), w: HDR scale (80 / SDR white nits)
    float4 Misc2;     // x: HDR decode flag, y: 1/composite width, z: 1/composite height, w: unused
};

Texture2D Src : register(t0);
SamplerState LinearClamp : register(s0);
SamplerState PointClamp : register(s1);
SamplerState TrilinearClamp : register(s2);

struct VSOut
{
    float4 Pos : SV_Position;
    float2 UV : TEXCOORD0;
};

VSOut VS(uint id : SV_VertexID)
{
    float2 t = float2(id & 1, id >> 1);
    VSOut o;
    o.Pos = float4(lerp(DstRect.x, DstRect.z, t.x), lerp(DstRect.y, DstRect.w, t.y), 0, 1);
    o.UV = float2(lerp(SrcRect.x, SrcRect.z, t.x), lerp(SrcRect.y, SrcRect.w, t.y));
    return o;
}

// ---------- HDR (scRGB, 1.0 = 80 nits) -> SDR (sRGB) ----------
float3 LinearToSrgb(float3 c)
{
    c = saturate(c);
    float3 lo = c * 12.92;
    float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
    return lerp(hi, lo, step(c, 0.0031308));
}

float3 ToneMap(float3 c)
{
    // Keep SDR content untouched, roll HDR highlights smoothly into the top of the range (hue preserving).
    float m = max(max(c.r, c.g), c.b);
    const float knee = 0.8;
    if (m > knee)
    {
        float mapped = knee + (1.0 - knee) * (1.0 - exp(-(m - knee) / (1.0 - knee)));
        c *= mapped / m;
    }
    return c;
}

float4 Finish(float4 c)
{
    if (Misc2.x > 0.5)
    {
        float3 lin = max(c.rgb, 0) * Misc.w;
        return float4(LinearToSrgb(ToneMap(lin)), 1);
    }
    return float4(saturate(c.rgb), 1);
}

// ---------- Filters ----------
float4 SampleBilinear(float2 uv) { return Src.SampleLevel(LinearClamp, uv, 0); }

// Catmull-Rom bicubic in 9 bilinear taps.
float4 SampleBicubic(float2 uv)
{
    float2 samplePos = uv * SrcSize.xy;
    float2 texPos1 = floor(samplePos - 0.5) + 0.5;
    float2 f = samplePos - texPos1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 offset12 = w2 / w12;
    float2 t0 = (texPos1 - 1) * SrcSize.zw;
    float2 t3 = (texPos1 + 2) * SrcSize.zw;
    float2 t12 = (texPos1 + offset12) * SrcSize.zw;
    float4 r = 0;
    r += Src.SampleLevel(LinearClamp, float2(t0.x,  t0.y), 0)  * w0.x  * w0.y;
    r += Src.SampleLevel(LinearClamp, float2(t12.x, t0.y), 0)  * w12.x * w0.y;
    r += Src.SampleLevel(LinearClamp, float2(t3.x,  t0.y), 0)  * w3.x  * w0.y;
    r += Src.SampleLevel(LinearClamp, float2(t0.x,  t12.y), 0) * w0.x  * w12.y;
    r += Src.SampleLevel(LinearClamp, float2(t12.x, t12.y), 0) * w12.x * w12.y;
    r += Src.SampleLevel(LinearClamp, float2(t3.x,  t12.y), 0) * w3.x  * w12.y;
    r += Src.SampleLevel(LinearClamp, float2(t0.x,  t3.y), 0)  * w0.x  * w3.y;
    r += Src.SampleLevel(LinearClamp, float2(t12.x, t3.y), 0)  * w12.x * w3.y;
    r += Src.SampleLevel(LinearClamp, float2(t3.x,  t3.y), 0)  * w3.x  * w3.y;
    return r;
}

float LanczosWeight(float x)
{
    x = abs(x);
    if (x < 1e-5) return 1.0;
    if (x >= 2.0) return 0.0;
    float px = 3.14159265 * x;
    return 2.0 * sin(px) * sin(px * 0.5) / (px * px);
}

float4 SampleLanczos(float2 uv)
{
    float2 pos = uv * SrcSize.xy - 0.5;
    float2 basePos = floor(pos);
    float2 f = pos - basePos;
    int2 maxP = int2(SrcSize.xy) - 1;
    float4 sum = 0;
    float wsum = 0;
    [unroll] for (int j = -1; j <= 2; j++)
    {
        float wy = LanczosWeight(j - f.y);
        [unroll] for (int i = -1; i <= 2; i++)
        {
            float w = LanczosWeight(i - f.x) * wy;
            int2 p = clamp(int2(basePos) + int2(i, j), int2(0, 0), maxP);
            sum += Src.Load(int3(p, 0)) * w;
            wsum += w;
        }
    }
    return sum / wsum;
}

// Box filter over the destination pixel's footprint - the right choice for big downscales (no shimmering/aliasing).
float4 SampleArea(float2 uv)
{
    float2 ratio = max(Misc.yz, 1.0);
    float2 footprint = ratio * SrcSize.zw;
    int2 n = clamp((int2)ceil(ratio), int2(1, 1), int2(8, 8));
    float2 stepUv = footprint / float2(n);
    float2 start = uv - footprint * 0.5 + stepUv * 0.5;
    float4 sum = 0;
    for (int j = 0; j < n.y; j++)
        for (int i = 0; i < n.x; i++)
            sum += Src.SampleLevel(LinearClamp, start + stepUv * float2(i, j), 0);
    return sum / (n.x * n.y);
}

float4 PSBilinear(VSOut i) : SV_Target { return Finish(SampleBilinear(i.UV)); }
float4 PSBicubic(VSOut i) : SV_Target  { return Finish(SampleBicubic(i.UV)); }
float4 PSLanczos(VSOut i) : SV_Target  { return Finish(SampleLanczos(i.UV)); }
float4 PSArea(VSOut i) : SV_Target     { return Finish(SampleArea(i.UV)); }

// Premultiplied-alpha overlay with global opacity.
float4 PSOverlay(VSOut i) : SV_Target  { return Src.Sample(TrilinearClamp, i.UV) * Misc.x; }

// Solid fill (Misc = rgba) - used for letterbox/background and the webcam border.
float4 PSSolid(VSOut i) : SV_Target { return float4(SrcSize.rgb, 1) * Misc.x; }

// ---------- RGB -> NV12, BT.709 limited range ----------
float PSLuma(VSOut i) : SV_Target
{
    float3 rgb = Src.SampleLevel(PointClamp, i.UV, 0).rgb;
    return 16.0 / 255.0 + dot(rgb, float3(0.2126, 0.7152, 0.0722)) * (219.0 / 255.0);
}

float2 PSChroma(VSOut i) : SV_Target
{
    // Left-sited chroma (H.264/HEVC default): [1 2 1] horizontally, 2-tap vertically.
    float3 a = Src.SampleLevel(LinearClamp, i.UV - float2(Misc2.y, 0), 0).rgb;
    float3 b = Src.SampleLevel(LinearClamp, i.UV, 0).rgb;
    float3 rgb = (a + b) * 0.5;
    float cb = 128.0 / 255.0 + dot(rgb, float3(-0.1146, -0.3854, 0.5)) * (224.0 / 255.0);
    float cr = 128.0 / 255.0 + dot(rgb, float3(0.5, -0.4542, -0.0458)) * (224.0 / 255.0);
    return float2(cb, cr);
}
""";
}
