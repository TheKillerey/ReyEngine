using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Lighting;
using ReyEngine.Rendering.D3D11;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M900: the D3D11 spotlight, through Riot's OWN pixel shader. A real board is drawn with its dynamic-light permutation pinned, once
/// without a light and once with a spotlight aimed straight down at the middle of the picture; the spotlight has to light a pool
/// there, square (no cookie) or round (a soft-disc cookie in the projected-texture atlas), and leave what is outside the cone alone.
///
/// <para>Needs the game (<c>C:\Riot Games\League of Legends\Game\DATA\FINAL</c>) and a D3D11 device; without either it says SKIPPED
/// in the test output and passes, like the other device tests. Set <c>REY_M900_OUT</c> to a folder to also keep the pictures.</para>
/// </summary>
public sealed class SpotlightDeviceTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private sealed record Shot(byte[] Png, byte[] Rgba, int Width, int Height, Vector3 Target, Vector3 Eye, string Summary);

    private Shot? Render(string wadName, string folder, string skin, Action<PreviewSettings, MapThumbnailFrame>? configure, out MapThumbnailOutcome outcome)
    {
        outcome = default!;
        string path = Path.Combine(Final, "Maps", "Shipping", wadName + ".wad.client");
        if (!File.Exists(path) || !File.Exists(Path.Combine(Final, "ShaderCache.dx11.wad.client"))) return null;
        var db = new HashSyncService().LoadLocal(_ => { });
        using var wad = WadArchive.Open(path, new WadPathResolver(db));
        string dir = $"data/maps/mapgeometry/{folder}/";
        string mapPath = dir + skin + ".mapgeo", matsPath = dir + skin + ".materials.bin";
        ulong matsHash = HashAlgorithms.WadPath(matsPath);
        byte[]? Read(ulong h) { try { return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null; } catch { return null; } }
        var job = new MapThumbnailJob
        {
            Key = skin, MapGeoPath = mapPath, MapGeoHash = HashAlgorithms.WadPath(mapPath),
            MaterialsBinPath = matsPath, MaterialsBinHash = matsHash,
            ControllerBins = wad.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                    && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && e.PathHash != matsHash)
                .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Select(e => e.PathHash).ToList(),
            FinalDirectory = Final,
            Read = Read, Has = h => wad.TryGetEntry(h, out _), Hashes = db,
            BinName = h => db.TryGetBinName(h, out var n) ? n : null,
            WadPath = h => db.TryGetPath(h, out var p) ? p : null,
        };

        Vector3 target = default, eye = default;
        ShaderPreviewRenderer? rendererSeen = null;
        var renderer = new MapThumbnailRenderer
        {
            DrawProps = false, KeepFrame = true, PinDynamicLighting = true,
            ConfigureFrame = (s, frame, r) => { target = frame.Target; eye = frame.Eye; configure?.Invoke(s, frame); rendererSeen = r; },
        };
        MapThumbnailOutcome result = default!;
        var worker = new Thread(() => { result = renderer.Render(job); renderer.ReleaseDevice(); }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromMinutes(3)), "the draw finished");
        outcome = result;
        if (rendererSeen is not null) output.WriteLine($"light path: {rendererSeen.ClusterLitSlices} slice(s) in Riot's loop, {rendererSeen.OverlayLitSlices} on the overlay; cluster lights {rendererSeen.ClusterLightCount}, non-empty cells {rendererSeen.ClusterNonEmptyCells}");
        if (result.DeviceFailed) { output.WriteLine("SKIPPED: no D3D11 device here - " + result.Failure); return null; }
        Assert.True(result.Png is not null && result.Frame is not null, "a picture: " + result.Failure);
        return new Shot(result.Png!, result.Frame!, MapThumbnailKey.RenderWidth, MapThumbnailKey.RenderHeight, target, eye, result.Summary);
    }

    /// <summary>The thumbnail renderer frames the map in DISPLAY space (X mirrored, as the viewport shows it); the lights live in the
    /// map's own space, which is the display point with X negated.</summary>
    private static Vector3 LightSpace(Vector3 display) => new(-display.X, display.Y, display.Z);

    private static double Luma(byte[] rgba, int w, int x, int y)
    {
        int o = (y * w + x) * 4;
        return 0.2126 * rgba[o] + 0.7152 * rgba[o + 1] + 0.0722 * rgba[o + 2];
    }

    private static DynamicSpotlight Spot(Vector3 at, float far, float fov, ulong cookie = 0) => new(
        default, "m900", new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, at.X, at.Y, at.Z, 1),   // local -Z = world down
        new Vector4(2.2f, 1.7f, 1.0f, 1f), fov, far, 10f, 1f, cookie, "", 0, 255, false);

    /// <summary>A soft white disc, black outside radius 0.92 - the cookie t46 ships.</summary>
    private static SpotCookieImage SoftDisc()
    {
        const int n = 128;
        var rgba = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n * 2 - 1, v = (y + 0.5f) / n * 2 - 1, r = MathF.Sqrt(u * u + v * v);
                byte c = (byte)(255 * Math.Clamp((0.92f - r) / 0.25f, 0f, 1f));
                int o = (y * n + x) * 4; rgba[o] = rgba[o + 1] = rgba[o + 2] = c; rgba[o + 3] = 255;
            }
        return new SpotCookieImage(rgba, n, n);
    }

    /// <summary>lit minus unlit, amplified, as a picture - where the light landed is easier to judge from this than from the lit frame.</summary>
    private static byte[] DiffPng(byte[] litPng, byte[] plainPng, int gain = 4)
    {
        using var a = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(litPng);
        using var b = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(plainPng);
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                var p = a[x, y]; var q = b[x, y];
                a[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24((byte)Math.Clamp((p.R - q.R) * gain, 0, 255),
                    (byte)Math.Clamp((p.G - q.G) * gain, 0, 255), (byte)Math.Clamp((p.B - q.B) * gain, 0, 255));
            }
        using var ms = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(a, ms);
        return ms.ToArray();
    }

    private static void Save(string name, byte[] png)
    {
        string? dir = System.Environment.GetEnvironmentVariable("REY_M900_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), png);
    }

    [Theory]
    [InlineData("Map30", "map30", "arenaa")]
    public void A_spotlight_aimed_down_lights_a_pool_through_riots_own_spot_loop(string wadName, string folder, string skin)
    {
        var plain = Render(wadName, folder, skin, null, out var plainOutcome);
        if (plain is null) { output.WriteLine($"SKIPPED: {wadName}.wad.client / the shader cache / a D3D11 device are not available"); return; }
        output.WriteLine("unlit: " + plain.Summary);

        // a square pool (no cookie) and a round one (the soft disc), the same light otherwise
        Shot? Lit(ulong cookie, SpotCookieImage? image, float far = 3000f)
        {
            Vector3 at = default;
            return Render(wadName, folder, skin, (s, frame) =>
            {
                at = LightSpace(frame.Target) + new Vector3(0, 700, 0);
                var packed = SpotlightPacking.Pack(new[] { Spot(at, far, 60f, cookie) },
                    new Dictionary<ulong, SpotCookieImage?> { [cookie] = image });
                s.DynamicLightsEnabled = true;
                s.SpotLights = packed.Lights;
                s.SpotCookieAtlas = packed.AtlasRgba is null ? null : new SpotCookieAtlas(packed.AtlasRgba, packed.AtlasWidth, packed.AtlasHeight);
            }, out _);
        }

        // calibration: a POINT light at the pool's centre must light this board through the same loop, or the harness is the problem
        var pointLit = Render(wadName, folder, skin, (s, frame) =>
        {
            s.DynamicLightsEnabled = true;
            s.DynamicLights = new[] { new PointLight(LightSpace(frame.Target), new Vector3(1.2f, 1f, 0.8f), 1500f) };
        }, out _);
        Assert.NotNull(pointLit);
        Save($"m900_{folder}_point_calibration.png", pointLit!.Png);
        { double d = 0; for (int y = 0; y < plain.Height; y++) for (int x = 0; x < plain.Width; x++) d += Math.Abs(Luma(pointLit.Rgba, plain.Width, x, y) - Luma(plain.Rgba, plain.Width, x, y));
          output.WriteLine($"point-light calibration: mean |delta luma| {d / (plain.Width * plain.Height):0.00}"); }

        var square = Lit(0, null);
        var round = Lit(0xC001, SoftDisc());
        Assert.NotNull(square); Assert.NotNull(round);
        Save($"m900_{folder}_unlit.png", plain.Png); Save($"m900_{folder}_spot_square.png", square!.Png); Save($"m900_{folder}_spot_round.png", round!.Png);

        Save($"m900_{folder}_diff_square.png", DiffPng(square.Png, plain.Png));
        Save($"m900_{folder}_diff_round.png", DiffPng(round.Png, plain.Png));
        Save($"m900_{folder}_diff_point.png", DiffPng(pointLit.Png, plain.Png));
        int w = plain.Width, h = plain.Height;
        double GainAt(Shot lit, int cx, int cy, int r)
        {
            double a = 0, b = 0; int n = 0;
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                { if (x < 0 || y < 0 || x >= w || y >= h) continue; a += Luma(lit.Rgba, w, x, y); b += Luma(plain.Rgba, w, x, y); n++; }
            return (a - b) / Math.Max(n, 1);
        }

        // find the pool: the pixel region that got brightest
        int bestX = 0, bestY = 0; double best = double.MinValue;
        for (int y = 8; y < h - 8; y += 8)
            for (int x = 8; x < w - 8; x += 8)
            {
                double g = GainAt(square!, x, y, 6);
                if (g > best) { best = g; bestX = x; bestY = y; }
            }
        output.WriteLine($"square pool peaks at ({bestX},{bestY}) of {w}x{h}, +{best:0.0} luma over the unlit picture");
        Assert.True(best > 12, $"the spotlight lights the ground (peak gain {best:0.0})");

        // far from the pool the picture is what it was: the frustum is a cone, not a global light
        int cornerX = bestX < w / 2 ? w - 24 : 24, cornerY = bestY < h / 2 ? h - 24 : 24;
        Assert.True(GainAt(square!, cornerX, cornerY, 10) < best * 0.25, "outside the cone nothing changes");

        // the cookie turns the square into a round pool: the disc loses its corners
        double sq = GainAt(square!, bestX, bestY, 6), rd = GainAt(round!, bestX, bestY, 6);
        output.WriteLine($"centre gain: square {sq:0.0}, round {rd:0.0}");
        Assert.True(rd > 6, "the cookie pool is lit at its centre too");
        int lit1 = 0, lit2 = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (Luma(square.Rgba, w, x, y) - Luma(plain.Rgba, w, x, y) > 8) lit1++;
                if (Luma(round.Rgba, w, x, y) - Luma(plain.Rgba, w, x, y) > 8) lit2++;
            }
        output.WriteLine($"lit pixels: square {lit1}, round {lit2}");
        Assert.True(lit1 > 200 && lit2 > 100 && lit2 < lit1, "a round cookie lights fewer pixels than the square frustum");
    }

    /// <summary>A point light and a spotlight in the SAME cluster cell: both loops run for the cell, and each still lights.</summary>
    [Fact]
    public void A_point_light_and_a_spotlight_in_one_cell_both_light_it()
    {
        const string wad = "Map30", folder = "map30", skin = "arenaa";
        var plain = Render(wad, folder, skin, null, out _);
        if (plain is null) { output.WriteLine("SKIPPED: Map30.wad.client / the shader cache / a D3D11 device are not available"); return; }

        Shot? Lit(bool spot, bool point)
        {
            return Render(wad, folder, skin, (s, frame) =>
            {
                var c = LightSpace(frame.Target);
                s.DynamicLightsEnabled = true;
                if (spot)
                {
                    var packed = SpotlightPacking.Pack(new[] { Spot(c + new Vector3(0, 700, 0), 3000f, 40f) }, new Dictionary<ulong, SpotCookieImage?>());
                    s.SpotLights = packed.Lights;
                }
                // beside the pool, a few hundred units away: well inside one 250-unit-ish cell neighbourhood of the spotlight's cone
                if (point) s.DynamicLights = new[] { new PointLight(c + new Vector3(450, 60, 0), new Vector3(0.2f, 0.7f, 1.6f), 700f) };
            }, out _);
        }

        var spotOnly = Lit(true, false); var pointOnly = Lit(false, true); var both = Lit(true, true);
        Assert.NotNull(spotOnly); Assert.NotNull(pointOnly); Assert.NotNull(both);
        Save("m900_map30_mixed_spot_only.png", spotOnly!.Png);
        Save("m900_map30_mixed_point_only.png", pointOnly!.Png);
        Save("m900_map30_mixed_both.png", both!.Png);
        Save("m900_map30_mixed_diff_both.png", DiffPng(both.Png, plain.Png));

        int w = plain.Width, h = plain.Height;
        int litSpot = 0, litPoint = 0, litBoth = 0, overlap = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double l0 = Luma(plain.Rgba, w, x, y);
                bool a = Luma(spotOnly.Rgba, w, x, y) - l0 > 8, b = Luma(pointOnly.Rgba, w, x, y) - l0 > 8, c = Luma(both.Rgba, w, x, y) - l0 > 8;
                if (a) litSpot++; if (b) litPoint++; if (c) litBoth++; if (a && b) overlap++;
            }
        output.WriteLine($"lit pixels: spot only {litSpot}, point only {litPoint}, both {litBoth}, in both singles {overlap}");
        Assert.True(litSpot > 200 && litPoint > 200, "each light lights on its own");
        Assert.True(litBoth >= litSpot + litPoint - overlap - 20, "with both in the scene, neither light is lost");
    }
}
