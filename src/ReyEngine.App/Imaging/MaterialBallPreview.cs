using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.Imaging;

/// <summary>
/// M351k: the material ball at the top of the inspector's detail pane — a CPU-shaded lit sphere sampling
/// the material's diffuse texture through its UV transform.
///
/// <para>Deliberately NOT the DX11 shader pipeline. This preview must update on every edit of any of 120
/// materials in a side panel; a 112px Lambert+Blinn ball is ~12k pixels and renders in well under a
/// millisecond with no GPU round-trip, no swap-chain, and no chance of taking the editor down with a
/// device error. What it shows honestly: the bound diffuse, the UV tiling, and shape. What it does not
/// claim to show: the actual Riot shader — the viewport's Riot Approx mode remains the truth for that.</para>
/// </summary>
public static class MaterialBallPreview
{
    public static WriteableBitmap Render(TextureImage? diffuse, Vector2 uvScale, Vector2 uvOffset, int size = 112)
    {
        var bmp = new WriteableBitmap(new PixelSize(size, size), new Avalonia.Vector(96, 96),
                                      PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        var px = new byte[size * size * 4];
        var light = Vector3.Normalize(new Vector3(-0.45f, 0.55f, 0.72f));
        var half = Vector3.Normalize(light + new Vector3(0f, 0f, 1f));
        float r = size * 0.48f, cx = size / 2f, cy = size / 2f;
        bool hasTex = diffuse is { Width: > 0, Height: > 0 };

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float nx = (x - cx) / r, ny = (y - cy) / r;
            float d2 = nx * nx + ny * ny;
            if (d2 > 1f) continue;                              // transparent outside the ball
            float nz = MathF.Sqrt(1f - d2);
            var n = new Vector3(nx, -ny, nz);

            // Base colour: diffuse sampled front-planar through the material's UV transform (wrapping),
            // so tiling edits are visible on the ball; neutral grey when nothing resolves.
            float cr = 0.62f, cg = 0.62f, cb = 0.64f;
            if (hasTex)
            {
                float u = (nx + 1f) * 0.5f * uvScale.X + uvOffset.X;
                float v = (ny + 1f) * 0.5f * uvScale.Y + uvOffset.Y;
                u -= MathF.Floor(u); v -= MathF.Floor(v);
                int tx = Math.Clamp((int)(u * diffuse!.Width), 0, diffuse.Width - 1);
                int ty = Math.Clamp((int)(v * diffuse.Height), 0, diffuse.Height - 1);
                int t = (ty * diffuse.Width + tx) * 4;
                cr = diffuse.Rgba[t] / 255f;
                cg = diffuse.Rgba[t + 1] / 255f;
                cb = diffuse.Rgba[t + 2] / 255f;
            }

            float lambert = MathF.Max(Vector3.Dot(n, light), 0f);
            float shade = 0.22f + 0.78f * lambert;
            float spec = MathF.Pow(MathF.Max(Vector3.Dot(n, half), 0f), 40f) * 0.35f;
            float edge = Math.Clamp((1f - MathF.Sqrt(d2)) * r * 2f, 0f, 1f);   // soft rim AA

            int o = (y * size + x) * 4;
            px[o + 0] = ToByte(cb * shade + spec);   // B
            px[o + 1] = ToByte(cg * shade + spec);   // G
            px[o + 2] = ToByte(cr * shade + spec);   // R
            px[o + 3] = (byte)(edge * 255f);
        }

        using var fb = bmp.Lock();
        int stride = size * 4;
        for (int y = 0; y < size; y++)
            Marshal.Copy(px, y * stride, IntPtr.Add(fb.Address, y * fb.RowBytes), stride);
        return bmp;
    }

    private static byte ToByte(float f) => (byte)Math.Clamp((int)(f * 255f + 0.5f), 0, 255);
}
