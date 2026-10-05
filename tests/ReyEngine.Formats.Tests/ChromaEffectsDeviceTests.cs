using System.Numerics;
using System.Reflection;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M826: the EFFECTS RECOLOUR on a real D3D11 device. The card's real sliders republish the playing effects with the recoloured definitions and
/// textures; the real particle driver (<see cref="D3D11MapParticles"/>) plays them through Riot's own particle shaders and the frames are compared
/// - and, with <c>REYENGINE_M826_SHOTS</c> set to a folder, written as PNGs to be looked at.
///
/// <para>No-ops (not failures) when the game install, the hash dictionary, the shader cache or a D3D11 device is absent.</para>
/// </summary>
public sealed class ChromaEffectsDeviceTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private const string Final = Game + @"\DATA\FINAL";
    private const int Size = 512;

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reyengine-m826d-" + Guid.NewGuid().ToString("N"));

    public ChromaEffectsDeviceTests(ITestOutputHelper output) { _output = output; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly Lazy<HashDatabase?> Database = new(() => { try { return new HashSyncService().LoadLocal(_ => { }); } catch { return null; } });
    private static string? Name(uint h) => Database.Value is { } db && db.TryGetBinName(h, out var n) ? n : null;

    private static object? Call(MainWindowViewModel vm, string name, params object?[] args)
    {
        try { return typeof(MainWindowViewModel).GetMethod(name, Private)!.Invoke(vm, args); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }

    /// <summary>The champion's WAD and the shared ones, read by path or chunk reference.</summary>
    private sealed class Wads : IDisposable
    {
        private readonly List<WadArchive> _archives = new();
        public Wads(string champion, HashDatabase db)
        {
            var resolver = new WadPathResolver(db);
            foreach (string path in new[] { Path.Combine(Final, "Champions", champion + ".wad.client"), Path.Combine(Final, "Global.wad.client"), Path.Combine(Final, "DATA.wad.client") })
                if (File.Exists(path)) _archives.Add(WadArchive.Open(path, resolver));
        }
        public byte[]? Read(string path) => Read(BinTexturePath.HashOfReference(path));
        public byte[]? Read(ulong hash)
        {
            foreach (var archive in _archives)
                try { if (archive.TryGetEntry(hash, out _)) return archive.Extract(hash); } catch { /* subchunked: next */ }
            return null;
        }
        public void Dispose() { foreach (var a in _archives) a.Dispose(); }
    }

    /// <summary>The skin's VFX library the way the host walks it: the skin bin and every bin it links.</summary>
    private static (IReadOnlyDictionary<uint, VfxSystemDefinition> Systems, IReadOnlyDictionary<uint, uint> Map) LoadVfx(Wads wads, string skinBin)
    {
        var systems = new Dictionary<uint, VfxSystemDefinition>();
        var map = new Dictionary<uint, uint>();
        var visited = new HashSet<ulong> { HashAlgorithms.WadPath(skinBin) };
        var queue = new Queue<string>();
        queue.Enqueue(skinBin);
        while (queue.Count > 0 && visited.Count < 80)
        {
            if (wads.Read(queue.Dequeue()) is not { } bytes) continue;
            foreach (var (k, v) in VfxSystemResolver.ExtractAll(bytes)) systems.TryAdd(k, v);
            foreach (var (k, v) in VfxSystemResolver.ExtractResourceMap(bytes)) map.TryAdd(k, v);
            foreach (var dependency in VfxSystemResolver.ExtractDependencies(bytes))
                if (visited.Add(HashAlgorithms.WadPath(dependency))) queue.Enqueue(dependency);
        }
        return (systems, map);
    }

    private static TextureImage? Texture(Wads wads, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return wads.Read(path) is { } bytes ? TextureDecoder.Decode(bytes) : null; }
        catch { return null; }
    }

    private static IReadOnlyList<TextureImage?> Stage(Wads wads, VfxSystemDefinition system, Func<VfxEmitterDefinition, string?> path) =>
        system.Emitters.Select(e => Texture(wads, path(e))).ToList();

    private static VfxPlaybackItem Item(Wads wads, VfxSystemDefinition system, Vector3 at) => new(system, at,
        Stage(wads, system, e => e.TexturePath), null, Stage(wads, system, e => e.TextureMultPath), null,
        Stage(wads, system, e => e.ParticleColorTexturePath), null, Stage(wads, system, e => e.Palette?.TexturePath)) { Seed = 826 };

    private sealed record Frame(byte[] Bgra, int Lit);

    /// <summary>Play a playback through the real driver for <paramref name="seconds"/> and draw it.</summary>
    private static Frame? Render(ShaderPreviewRenderer renderer, ShaderCacheReader cache, VfxPlayback playback, float seconds)
    {
        var eye = new Vector3(0f, 520f, 950f);
        var target = new Vector3(0f, 60f, -120f);
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, 5f, 30000f);
        var mirrored = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;
        var driver = new D3D11MapParticles(renderer, cache);
        // the driver warms a few systems a frame within a wall-clock budget, so which tick a system starts on varies from run to run; with no budget they all warm on the first
        // pump and the same playback draws the same frame every time - which is what a before and after comparison needs
        ((ParticleWarmupQueue)typeof(D3D11MapParticles).GetField("_warmup", Private)!.GetValue(driver)!).BudgetMs = 1e9;
        driver.SetPlayback(playback);
        for (int i = 0; i < (int)(seconds * 30f); i++) driver.Tick(1f / 30f, mirrored, mirrored * proj, eye, 950f);
        var settings = new PreviewSettings
        {
            SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
            AlphaBlend = true, DepthTest = true, CullBackFaces = true, SortByPipeline = true,
            MirrorX = true, TransposeMatrices = true, Bloom = false, Shadows = false, ClearColor = new Vector4(0.039f, 0.051f, 0.075f, 1f),
        };
        renderer.GroundGrid = false;
        var frame = renderer.RenderFrame(Size, Size, settings, out var error, new List<string>());
        if (frame is null) throw new InvalidOperationException("no frame: " + error);
        var bytes = frame.ToArray();
        int lit = 0;
        for (int i = 0; i + 3 < bytes.Length; i += 4)
            if (Math.Abs(bytes[i] - 0.075f * 255) > 14 || Math.Abs(bytes[i + 1] - 0.051f * 255) > 14 || Math.Abs(bytes[i + 2] - 0.039f * 255) > 14) lit++;   // BGRA
        driver.SetPlayback(null);
        return new Frame(bytes, lit);
    }

    [Fact]
    public async Task LilliaRoseQuartz_TheEffectsRecolourReachesTheRealD3D11ParticleFrame_AndSavesWhatItShows()
    {
        const string champion = "Lillia";
        const string skinBin = "data/characters/lillia/skins/skin49.bin";
        if (!File.Exists(Path.Combine(Final, "Champions", champion + ".wad.client")) || Database.Value is not { } db) return;
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(db), out _);
        if (cache is null) { _output.WriteLine("SKIPPED: no shader cache"); return; }
        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) { _output.WriteLine("SKIPPED: no D3D11 device"); return; }
        using var wads = new Wads(champion, db);

        // ---- the card on the real skin, the way the Character window holds it
        string root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(root);
        var project = ReyProjectService.OpenFolder(root);
        project.GameDirectory = Game;
        project.ProjectVersion = 2;
        ReyProjectService.Save(project, project.ProjectFilePath!);
        var vm = new MainWindowViewModel { Project = project };
        vm.Settings.AutoSaveEdits = false;
        Call(vm, "BuildMounts");
        Assert.True((bool)Call(vm, "MakeCharacterWadReadable", Path.Combine(Final, "Champions", champion + ".wad.client"))!);
        var card = vm.MeshPreview;
        card.SetChromaSkin(skinBin);
        card.ChromaUiPost = a => { a(); return Task.CompletedTask; };
        card.UseDx11Preview = true;
        int pushed = 0;
        card.PushChromaDx11 = items =>
        {
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, rgba, width, height) in items)
                if (renderer.UpdatePooledTexture(key, rgba, width, height)) { touched.Add(key); pushed++; }
            foreach (string key in touched) renderer.RegeneratePooledMips(key);
        };
        var (systems, map) = LoadVfx(wads, skinBin);
        card.SetVfx(systems, map);
        await card.ScanColoursCommand.ExecuteAsync(null);
        await card.ChromaIdleAsync();
        Assert.True(card.HasChromaEffects, card.ChromaEffectProblem);
        foreach (var row in card.ChromaTextures) row.IsIncluded = false;      // the effects only
        foreach (var row in card.ChromaParameters) row.IsIncluded = false;
        card.SelectAllEffectColorsCommand.Execute(null);
        card.SelectAllEffectTexturesCommand.Execute(null);
        await card.ChromaIdleAsync();

        // ---- which of the skin's systems draw something at the origin: try the colourful ones one by one
        var candidates = card.ChromaInventory!.Effects.Where(e => systems.ContainsKey(e.PathHash) && e.Emitters.Any(m => !m.Disabled && m.Colors.Any(c => (c.Constant ?? c.First) is { } v && SkinEffectColors.Chroma(v) > 0.3f)))
            .Select(e => systems[e.PathHash]).Where(s => s.Emitters.Any(m => m.IsVisual && !m.Disabled)).Take(60).ToList();
        _output.WriteLine($"{candidates.Count} colourful candidate system(s)");
        var chosen = new List<VfxSystemDefinition>();
        foreach (var system in candidates)
        {
            if (chosen.Count >= 6) break;
            var frame = Render(renderer, cache, new VfxPlayback(new[] { Item(wads, system, Vector3.Zero) }), 1.2f);
            _output.WriteLine($"  {system.Name}: {frame!.Lit} lit px");
            if (frame.Lit > 1500) chosen.Add(system);
        }
        Assert.True(chosen.Count >= 2, "no colourful system of the skin draws at the origin");
        var items = chosen.Select((s, i) => Item(wads, s, new Vector3(((i % 3) - 1) * 380f, 0f, -(i / 3) * 380f))).ToList();
        var playback = new VfxPlayback(items);
        card.Playback = playback;       // the card's own playing set: the sliders republish it
        VfxPlayback? shown = card.Playback;
        card.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MeshPreviewViewModel.Playback)) shown = card.Playback; };

        var before = Render(renderer, cache, shown!, 1.5f)!;
        var again = Render(renderer, cache, shown!, 1.5f)!;
        long noise = 0;
        for (int i = 0; i < before.Bgra.Length; i++) noise += Math.Abs(before.Bgra[i] - again.Bgra[i]);
        _output.WriteLine($"before: {before.Lit} lit px; the same playback drawn twice differs by {noise / (double)before.Bgra.Length:0.000}/255");
        Shot("lillia49-effects-before.png", before);

        // ---- the sliders: hue +120 (a channel rotation), the effects restart with the new colours
        card.ChromaHue = 120;
        await card.ChromaIdleAsync();
        Assert.NotNull(shown);
        Assert.NotSame(playback, shown);                                                            // republished
        int recoloured = shown!.Items.Count(i => !ReferenceEquals(i.System, items.First(o => o.System.PathHash == i.System.PathHash).System));
        Assert.True(recoloured >= 1, "no playing system got a recoloured definition");
        _output.WriteLine($"{recoloured} of {shown.Items.Count} playing system(s) recoloured; {pushed} pooled texture(s) updated in place");
        Assert.True(pushed > 0, "no pooled texture of the Direct3D 11 renderer was updated: the keys the card pushes under are not the ones the particle pipeline bound");
        var after = Render(renderer, cache, shown, 1.5f)!;
        _output.WriteLine($"after hue +120: {after.Lit} lit px");
        Shot("lillia49-effects-hue120.png", after);

        // the picture changed, and by hue: lit pixels carry a different dominant channel
        long changed = 0; double dr = 0, dg = 0, db2 = 0, br = 0, bg = 0, bb = 0; long n = 0;
        for (int i = 0; i + 3 < before.Bgra.Length; i += 4)
        {
            int d = Math.Abs(before.Bgra[i] - after.Bgra[i]) + Math.Abs(before.Bgra[i + 1] - after.Bgra[i + 1]) + Math.Abs(before.Bgra[i + 2] - after.Bgra[i + 2]);
            if (d > 24) changed++;
            if (before.Bgra[i] + before.Bgra[i + 1] + before.Bgra[i + 2] > 120) { n++; bb += before.Bgra[i]; bg += before.Bgra[i + 1]; br += before.Bgra[i + 2]; db2 += after.Bgra[i]; dg += after.Bgra[i + 1]; dr += after.Bgra[i + 2]; }
        }
        _output.WriteLine($"{changed} pixel(s) changed; mean R,G,B over the lit area {br / Math.Max(1, n):0},{bg / Math.Max(1, n):0},{bb / Math.Max(1, n):0} -> {dr / Math.Max(1, n):0},{dg / Math.Max(1, n):0},{db2 / Math.Max(1, n):0}");
        Assert.True(changed > 1500, $"only {changed} pixels changed under a +120 degree hue shift of the effects");

        // how long one slider position takes from the slider to the republished playback (Debug build; every colour field and texture of the skin, all of it switched on)
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int hue = 130; hue < 140; hue++) { card.ChromaHue = hue; await card.ChromaIdleAsync(); }
        _output.WriteLine($"one slider position: {clock.ElapsedMilliseconds / 10.0:0} ms ({card.ChromaEffectSystems.Count(sr => sr.IsIncluded)} systems, {card.ChromaEffectTextures.Count(tr => tr.IsIncluded)} textures on; Debug, the pool push and the republish included)");

        // ---- back to no change: the playing effects are the skin's own again
        card.ChromaHue = 0;
        await card.ChromaIdleAsync();
        var reset = Render(renderer, cache, shown!, 1.5f)!;
        Shot("lillia49-effects-reset.png", reset);
        // the same playback drawn twice differs a little (the driver's particles do not start on the same tick every time, so a flame is brighter in one frame than in the next),
        // so the comparison is the HUE of what is lit: the saturation-weighted circular mean hue of the pixels that carry colour
        double hOriginal = MeanHue(before), hReset = MeanHue(reset), hAfter = MeanHue(after);
        _output.WriteLine($"mean hue of the coloured pixels: original {hOriginal:0} deg, after reset {hReset:0} deg, hue +120 {hAfter:0} deg");
        Assert.True(HueDistance(hOriginal, hReset) < 15, "resetting the slider did not give the original colours back");
        Assert.True(HueDistance(hAfter, hOriginal + 120) < 45, $"the hue shift of +120 moved the effects from {hOriginal:0} to {hAfter:0} degrees");

        // ---- saved: what the bins hold is what the preview showed (the same definitions, read through the resolver)
        card.ChromaHue = 120;
        await card.ChromaIdleAsync();
        var preview = shown!;
        await card.SaveChromaRecolourNowAsync();
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);
        var record = Assert.Single(project.ChromaEffectRecolors!);
        int compared = 0;
        foreach (var item in preview.Items)
        {
            var info = card.ChromaInventory!.Effects.First(e => e.PathHash == item.System.PathHash);
            var saved = VfxSystemResolver.ExtractAll((byte[])Call(vm, "ReadAsset", HashAlgorithms.WadPath(info.Bin))!)[item.System.PathHash];
            Assert.Equal(saved.Emitters.Count, item.System.Emitters.Count);
            for (int e = 0; e < saved.Emitters.Count; e++)
            {
                AssertSameCurve(saved.Emitters[e].BirthColor, item.System.Emitters[e].BirthColor, $"{item.System.Name}/{saved.Emitters[e].Name} birthColor");
                if (saved.Emitters[e].ColorOverLife is { } over && item.System.Emitters[e].ColorOverLife is { } shownOver)
                    AssertSameCurve(over, shownOver, $"{item.System.Name}/{saved.Emitters[e].Name} color");
                compared++;
            }
        }
        _output.WriteLine($"{compared} emitter(s) of the playing systems: the saved definition is the previewed one; {record.Colors.Count} colour field(s) owned");

    }

    /// <summary>The circular mean hue (degrees) of the pixels that carry colour, weighted by saturation x value; the clear colour and greys weigh nothing.</summary>
    private static double MeanHue(Frame frame)
    {
        double x = 0, y = 0;
        for (int i = 0; i + 3 < frame.Bgra.Length; i += 4)
        {
            float b = frame.Bgra[i] / 255f, g = frame.Bgra[i + 1] / 255f, r = frame.Bgra[i + 2] / 255f;
            if (!Hsv.FromRgb(r, g, b, out float h, out float sat, out float v) || v < 0.2f || sat < 0.25f) continue;
            double w = sat * v;
            x += w * Math.Cos(h * 2 * Math.PI); y += w * Math.Sin(h * 2 * Math.PI);
        }
        double deg = Math.Atan2(y, x) * 180 / Math.PI;
        return deg < 0 ? deg + 360 : deg;
    }

    private static double HueDistance(double a, double b)
    {
        double d = Math.Abs(((a - b) % 360 + 360) % 360);
        return Math.Min(d, 360 - d);
    }

    private void Shot(string name, Frame frame)
    {
        if (System.Environment.GetEnvironmentVariable("REYENGINE_M826_SHOTS") is not { Length: > 0 } shots) return;
        Directory.CreateDirectory(shots);
        ChromaRecolourRealDataTests.WritePng(Path.Combine(shots, name), frame.Bgra, Size, Size);
    }

    private static void AssertSameCurve(VfxCurve4 saved, VfxCurve4 shown, string what)
    {
        Assert.True(SkinEffectColors.SameRgb(saved.Constant, shown.Constant), $"{what}: constant {saved.Constant} vs {shown.Constant}");
        Assert.Equal(saved.Values?.Length ?? 0, shown.Values?.Length ?? 0);
        for (int i = 0; i < (saved.Values?.Length ?? 0); i++)
            Assert.True(SkinEffectColors.SameRgb(saved.Values![i], shown.Values![i]), $"{what}: key {i} {saved.Values[i]} vs {shown.Values[i]}");
    }
}
