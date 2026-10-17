using System.Numerics;
using System.Text;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M845: the D3D11 additive dynamic-light overlay used to push itself toward the camera by a CONSTANT
/// clip-space amount (<c>pos.z -= 0.0006 * pos.w</c>). With standard depth (clear 1, LESS) and the editor's
/// near plane (min(1, distance * 0.0025)), 0.0006 of NDC depth is hundreds to over a thousand world units at
/// map viewing range, so a surface BURIED under the visible one still passed LESS_EQUAL and received light.
///
/// <para>Real device, synthetic scene, no game files: a lit ground quad (a hand-compiled base pass standing in
/// for the game's shader), a smaller quad buried <c>gap</c> units under it and one point light above. The buried
/// quad is fully hidden from the camera, so rendering it must not change a single pixel. The test also
/// checks the other half of the bias's job: the visible surface itself still gets the overlay everywhere (no
/// z-fight holes), including at a grazing angle where depth slope is large.</para>
/// </summary>
public sealed class Dx11OverlayDepthBiasDeviceTests(ITestOutputHelper output)
{
    private const int W = 480, H = 320;

    private const string BaseHlsl = @"
cbuffer C : register(b0) { float4x4 MVIEWPROJ; };
struct VIn  { float3 pos : POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD0; };
struct VOut { float4 pos : SV_Position; };
// Deliberately a different arithmetic order from the overlay's mul(): the game's own vertex shader does not
// land bit-identically on the overlay's clip position either, and that gap is what the bias has to absorb.
VOut vsmain(VIn i)
{
    VOut o;
    float4 p = float4(i.pos, 1.0);
    o.pos = MVIEWPROJ[0] * p.x + MVIEWPROJ[1] * p.y + MVIEWPROJ[2] * p.z + MVIEWPROJ[3] * p.w;
    // The two vertex shaders land on the same plane a few depth ULPs apart; make that gap deliberate so the
    // z-fight half of the test cannot pass by luck of identical arithmetic. 3e-7 of NDC depth is about
    // 5 ULPs of a D32_FLOAT near 1.0 (1.2 world units at 2000, 30 units at 10000 with near = 1).
    o.pos.z -= 3e-7 * o.pos.w;
    return o;
}
float4 psmain(VOut i) : SV_Target { return float4(0.10, 0.10, 0.10, 1.0); }";

    private static unsafe byte[] Compile(string entry, string target)
    {
        var src = Encoding.ASCII.GetBytes(BaseHlsl);
        var compiler = D3DCompiler.GetApi();
        ID3D10Blob* code = null, errs = null;
        var e = Encoding.ASCII.GetBytes(entry + "\0");
        var t = Encoding.ASCII.GetBytes(target + "\0");
        int hr;
        fixed (byte* sp = src) fixed (byte* ep = e) fixed (byte* tp = t)
            hr = compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null, ep, tp, 0u, 0u, &code, &errs);
        if (hr < 0 || code is null)
        {
            string msg = errs is null ? "" : Encoding.ASCII.GetString(
                new ReadOnlySpan<byte>(errs->GetBufferPointer(), (int)errs->GetBufferSize()));
            throw new InvalidOperationException("HLSL: " + msg);
        }
        var bytes = new ReadOnlySpan<byte>(code->GetBufferPointer(), (int)code->GetBufferSize()).ToArray();
        code->Release();
        return bytes;
    }

    private static PreviewMesh Scene(float groundHalf, float buriedHalf, float gap, bool buried)
    {
        var verts = new List<PreviewVertex>();
        var idx = new List<uint>();
        void Quad(float half, float y)
        {
            uint b = (uint)verts.Count;
            foreach (var (x, z) in new[] { (-half, -half), (half, -half), (half, half), (-half, half) })
                verts.Add(new PreviewVertex
                {
                    Position = new Vector3(x, y, z), Normal = Vector3.UnitY,
                    Uv0 = new Vector4(x > 0 ? 1 : 0, z > 0 ? 1 : 0, 0, 0),
                });
            idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        Quad(groundHalf, 0f);
        if (buried) Quad(buriedHalf, -gap);
        return new PreviewMesh { Name = "m845", Vertices = verts.ToArray(), Indices = idx.ToArray(), Radius = groundHalf };
    }

    private byte[]? Render(float distance, float pitch, float gap, bool buried, bool lit, string? png)
    {
        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out var initError)) { output.WriteLine("SKIPPED: no D3D11 device - " + initError); return null; }

        var vs = DxbcReflection.Parse(Compile("vsmain", "vs_5_0"));
        var ps = DxbcReflection.Parse(Compile("psmain", "ps_5_0"));
        var mat = renderer.BuildMaterial("m845", vs, ps, 0, -1, out var report);
        Assert.True(mat is not null, report.Error);
        mat!.MapGroupIndex = 0;          // a map slice: the only kind the overlay draws
        mat.CullBackFaces = false;
        renderer.SetMesh(Scene(3000f, 2000f, gap, buried));
        renderer.AddMaterial(mat);

        var eye = new Vector3(0f, MathF.Sin(pitch), MathF.Cos(pitch)) * distance;
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        float near = MathF.Min(1f, distance * 0.0025f);      // OrbitCamera.EffectiveNear
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, (float)W / H, near, 200000f);
        var settings = new PreviewSettings
        {
            SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
            MirrorX = false, CullBackFaces = false, DepthTest = true, SortByPipeline = false,
            Bloom = false, Shadows = false, ClearColor = new Vector4(0f, 0f, 0f, 1f),
            DynamicLightsEnabled = lit,
            DynamicLights = new[] { new PointLight(new Vector3(0f, 1500f, 0f), Vector3.One, 20000f, 0.4f) },
            DynamicLightIntensity = 1f,
        };
        var frame = renderer.RenderFrame(W, H, settings, out var error, new List<string>());
        Assert.True(frame is not null, error);
        if (png is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(png)!);
            ChromaRecolourRealDataTests.WritePng(png, frame!, W, H);
        }
        return frame;
    }

    private static string OutDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? Path.GetTempPath(), "output", "m845");
    }

    private static int Lum(byte[] f, int px) => f[px * 4] + f[px * 4 + 1] + f[px * 4 + 2];

    [Theory]
    [InlineData(2000f, 0.7f, 20f)]
    [InlineData(2000f, 0.7f, 200f)]
    [InlineData(10000f, 0.7f, 100f)]
    [InlineData(10000f, 0.7f, 400f)]
    [InlineData(4000f, 0.7f, 100f)]
    public void ABuriedSurfaceReceivesNoLightAndTheVisibleOneIsFullyLit(float distance, float pitch, float gap)
    {
        string tag = $"d{distance:0}_gap{gap:0}";
        string dir = OutDir();
        var dark = Render(distance, pitch, gap, false, false, null);
        if (dark is null) return;
        var single = Render(distance, pitch, gap, false, true, Path.Combine(dir, $"{tag}_ground_only.png"))!;
        var withBuried = Render(distance, pitch, gap, true, true, Path.Combine(dir, $"{tag}_with_buried.png"))!;

        int footprint = 0, holes = 0, leaked = 0, maxLeak = 0;
        long singleSum = 0;
        for (int p = 0; p < W * H; p++)
        {
            bool onGround = Lum(dark, p) > 0;
            if (onGround)
            {
                footprint++;
                int add = Lum(single, p) - Lum(dark, p);
                singleSum += add;
                if (add <= 3) holes++;
            }
            int d = Math.Abs(Lum(withBuried, p) - Lum(single, p));
            if (d > 0) { leaked++; maxLeak = Math.Max(maxLeak, d); }
        }
        output.WriteLine($"{tag}: ground footprint {footprint}px, mean overlay add/pixel (sum rgb) "
            + $"{(footprint == 0 ? 0 : singleSum / (double)footprint):0.0}, "
            + $"holes(add<=3) {holes}, buried-surface leak: {leaked}px differ, max delta {maxLeak}");

        Assert.True(footprint > 2000, "the ground did not render");
        Assert.True(singleSum / (double)footprint > 5, "the overlay added no light to the visible surface");
        Assert.True(holes <= footprint / 200, $"z-fight holes on the visible surface: {holes}/{footprint}");
        Assert.True(leaked == 0, $"the buried quad (gap {gap}) still adds light: {leaked}px differ, max {maxLeak}");
    }

    [Theory]
    [InlineData(2000f, 0.12f)]
    [InlineData(10000f, 0.12f)]
    public void AGrazingVisibleSurfaceStaysFullyLit(float distance, float pitch)
    {
        string dir = OutDir();
        var dark = Render(distance, pitch, 0f, false, false, null);
        if (dark is null) return;
        var lit = Render(distance, pitch, 0f, false, true, Path.Combine(dir, $"grazing_d{distance:0}.png"))!;
        int footprint = 0, holes = 0;
        for (int p = 0; p < W * H; p++)
        {
            if (Lum(dark, p) == 0) continue;
            footprint++;
            if (Lum(lit, p) - Lum(dark, p) <= 3) holes++;
        }
        output.WriteLine($"grazing d{distance:0}: footprint {footprint}px, holes {holes}");
        Assert.True(footprint > 500);
        Assert.True(holes <= footprint / 100, $"holes {holes}/{footprint}");
    }
}
