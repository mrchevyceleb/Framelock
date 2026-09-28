using System.Diagnostics;
using Framelock.Core;
using Framelock.Graphics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Framelock.Engine;

/// <summary>
/// Dev aid: keeps the GPU at 100% like a demanding game (heavy 4K pixel shader, two frames in flight), off-screen, so
/// frame pacing under load can be tested. Runs as its own process: GPU scheduling priority is per process.
/// </summary>
public static class GpuLoad
{
    private const string Hlsl = @"
float4 VS(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 PS(float4 pos : SV_Position) : SV_Target
{
    float3 c = float3(pos.xy * 0.001, 0.5);
    [loop] for (int i = 0; i < ITERS; i++) c = frac(sin(c.yzx * 12.9898 + c * 78.233) * 43758.5453);
    return float4(c, 1);
}";

    public static int Run(double seconds, int iters, int strips)
    {
        // A plain device at normal priority, like a game (D3DContext.Create would raise this process's GPU priority).
        using var adapter = D3DContext.FindBestAdapter();
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None, new[] { FeatureLevel.Level_11_0 }, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        using var dev = device!;
        using var ctx = context!;
        var src = Hlsl.Replace("ITERS", iters.ToString());
        using var vs = dev.CreateVertexShader(Compiler.Compile(src, "VS", "load.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
        using var ps = dev.CreatePixelShader(Compiler.Compile(src, "PS", "load.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
        const int W = 3840, H = 2160;
        using var rt = dev.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, W, H, 1, 1, BindFlags.RenderTarget));
        using var rtv = dev.CreateRenderTargetView(rt);
        var fence = new ID3D11Texture2D[3];
        for (int i = 0; i < fence.Length; i++)
            fence[i] = dev.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, 1, 1, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        var sw = Stopwatch.StartNew();
        long frames = 0;
        double lastLog = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            ctx.OMSetRenderTargets(rtv);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(vs);
            ctx.PSSetShader(ps);
            // A game draws a frame in many pieces; one huge draw can't be interrupted by anyone.
            for (int k = 0; k < strips; k++)
            {
                ctx.RSSetViewport(0, H * k / (float)strips, W, H / (float)strips);
                ctx.Draw(3, 0);
            }
            int slot = (int)(frames % fence.Length);
            ctx.CopySubresourceRegion(fence[slot], 0, 0, 0, 0, rt, 0, new Vortice.Mathematics.Box(0, 0, 0, 1, 1, 1));
            ctx.Flush();
            frames++;
            // Like a game with two frames queued: wait for the frame before last.
            if (frames >= fence.Length)
            {
                var old = fence[(int)(frames % fence.Length)];
                ctx.Map(old, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                ctx.Unmap(old, 0);
            }
            if (sw.Elapsed.TotalSeconds - lastLog >= 2)
            {
                Log.Info($"[gpuload] {frames / sw.Elapsed.TotalSeconds:F1} fps ({iters} iters)");
                lastLog = sw.Elapsed.TotalSeconds;
            }
        }
        foreach (var f in fence) f.Dispose();
        return 0;
    }
}
