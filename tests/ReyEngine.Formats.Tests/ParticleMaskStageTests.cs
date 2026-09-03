using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M634: the character window never resolved the multiplier (mask) texture stage. Every other stage was
/// passed - distortion, colour, erosion, palette - and the resolver for this one existed and was wired for
/// the map viewport and the particle editor since M117. Here the list was simply never built, so the
/// D3D11 pipeline logged "TEXTUREMULT: not authored", bound the renderer's white stand-in, and Riot's
/// shader multiplied the sprite by one. That is what "the mask is not applied" was.
/// </summary>
public sealed class ParticleMaskStageTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static VfxSystemDefinition System(string name, string? mult) => new(
        PathHash: HashAlgorithms.Fnv1a(name), Name: name, ParticlePath: "",
        Emitters: new[]
        {
            new VfxEmitterDefinition(
                Name: "quad",
                Rate: VfxCurveF.Const(10f),
                ParticleLifetime: VfxCurveF.Const(1f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: false,
                Disabled: false,
                BlendMode: 1,
                BirthScale: VfxCurve3.Const(new Vector3(20f, 20f, 20f)),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: "ASSETS/Test/p.dds",
                TexDiv: new Vector2(2f, 2f),
                NumFrames: 4,
                RandomStartFrame: false,
                IsMeshPrimitive: false,
                TextureMultPath: mult,
                TextureMultTexDiv: new Vector2(2f, 2f)),
        });

    // ===================================================== the window resolves the stage

    [Fact]
    public void PickingASystemResolvesItsMultiplierTextures()
    {
        // The list the D3D11 driver reads (ResolveSprite("TEXTUREMULT") -> item.EmitterMultTextures) has
        // to be the one the host's resolver produced, for the system that was picked.
        var preview = new MeshPreviewViewModel();
        var mask = new TextureImage?[] { new TextureImage(2, 2, new byte[16]) };
        VfxSystemDefinition? askedFor = null;
        preview.ResolveTextures = _ => new TextureImage?[1];
        preview.ResolveMultTextures = def => { askedFor = def; return mask; };

        var system = System("Test_Q_cas", "ASSETS/Test/mult.dds");
        preview.SetVfx(new Dictionary<uint, VfxSystemDefinition> { [system.PathHash] = system });
        preview.SelectedVfx = preview.VfxSystems.Single();

        Assert.NotNull(preview.Playback);
        var item = Assert.Single(preview.Playback!.Items);
        Assert.Same(system, askedFor);
        Assert.Same(mask, item.EmitterMultTextures);
    }

    [Fact]
    public void AHostThatSuppliesNoResolverStillGetsAnItem()
    {
        // The delegate is optional, like every other stage's: a test host or an older caller that never
        // wires it must not lose the sprite, only the mask.
        var preview = new MeshPreviewViewModel();
        preview.ResolveTextures = _ => new TextureImage?[1];

        var system = System("Test_Q_cas", "ASSETS/Test/mult.dds");
        preview.SetVfx(new Dictionary<uint, VfxSystemDefinition> { [system.PathHash] = system });
        preview.SelectedVfx = preview.VfxSystems.Single();

        var item = Assert.Single(preview.Playback!.Items);
        Assert.Null(item.EmitterMultTextures);
        Assert.Single(item.EmitterTextures);
    }

    // ===================================================== one builder, every path

    [Fact]
    public void EveryPlaybackItemInTheWindowComesFromTheOneBuilder()
    {
        // Four hand-written stage lists disagreed with each other; none carried the multiplier, two skipped
        // children and reflection cubemaps. A stage forgotten in one copy is a white stand-in, not an
        // error, so the copies are gone: BuildItem is the only constructor call left in the file.
        var text = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.cs");
        if (text is null) return;

        Assert.DoesNotContain("new VfxPlaybackItem(", text);
        Assert.Contains("emitterMultTextures: ResolveMultTextures?.Invoke(def)", text);
        Assert.Contains("emitterChildren: ResolveChildren(def, depth)", text);

        // The three callers set only what is theirs on top of the built item.
        Assert.Contains("BuildItem(def, anchor) with", text);          // clip events: bone + start frame
        Assert.Contains("BuildItem(def, at) with", text);              // spell composite: travel
        Assert.Contains("BuildItem(def, AnchorFor(def, PlaySelectedAtDummy))", text);   // manual pick
    }

    [Fact]
    public void TheHostWiresTheMultiplierResolverToTheCharacterWindow()
    {
        // The resolver was there all along; the assignment was not.
        var text = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (text is null) return;
        Assert.Contains("MeshPreview.ResolveMultTextures = ResolveSystemMultTextures;", text);
    }

    // ===================================================== and the data the fix is for

    [Fact]
    public void AatroxsMultiplierTexturesExistAndDecode()
    {
        // The stage is only worth resolving if the paths it names resolve. Every multiplier texture in
        // Aatrox's skin0 VFX closure is a String path (the census found 0 WadChunkLinks on any emitter
        // texture field across eight champions), is present in his WAD, and decodes.
        if (!Directory.Exists(Champions)) return;
        string wad = Path.Combine(Champions, "Aatrox.wad.client");
        if (!File.Exists(wad)) return;

        var database = new HashSyncService().LoadLocal(_ => { });
        using var archive = WadArchive.Open(wad, new WadPathResolver(database));

        var systems = new Dictionary<uint, VfxSystemDefinition>();
        var seen = new HashSet<ulong>();
        var queue = new Queue<ulong>();
        ulong start = HashAlgorithms.WadPath("data/characters/aatrox/skins/skin0.bin");
        if (!archive.TryGetEntry(start, out _)) return;
        seen.Add(start);
        queue.Enqueue(start);
        for (int guard = 0; queue.Count > 0 && guard < 64; guard++)
        {
            byte[] bytes;
            try { bytes = archive.Extract(queue.Dequeue()); }
            catch { continue; }
            foreach (var (key, value) in VfxSystemResolver.ExtractAll(bytes)) systems.TryAdd(key, value);
            foreach (string dependency in VfxSystemResolver.ExtractDependencies(bytes))
            {
                ulong hash = HashAlgorithms.WadPath(dependency);
                if (seen.Add(hash) && archive.TryGetEntry(hash, out _)) queue.Enqueue(hash);
            }
        }

        var paths = systems.Values.SelectMany(s => s.Emitters)
            .Where(e => !e.Disabled && e.TextureMultPath is { Length: > 0 })
            .Select(e => e.TextureMultPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.NotEmpty(paths);

        int decoded = 0;
        foreach (string path in paths)
        {
            ulong hash = BinTexturePath.HashOfReference(path);
            Assert.True(archive.TryGetEntry(hash, out _), path + " is not in Aatrox.wad.client");
            var image = TextureDecoder.Decode(archive.Extract(hash));
            Assert.True(image.Width > 0 && image.Height > 0, path + " decoded to nothing");
            decoded++;
        }
        Assert.Equal(paths.Count, decoded);
    }
}
