using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M790: the two D3D11 builders bind a repeated parameter or sampler as its FIRST copy, the way every other
/// reader of the material does since M789 (<see cref="MaterialBinding.FirstOfEach"/>).
///
/// <para>Riot repeats names inside one StaticMaterialDef: 34 materials repeat a parameter and 26 a sampler.
/// Dx11SceneBuilder listed every copy, and Commit bound them in order, so the LAST won. Dx11CharacterScene
/// replaced texture entries as it went and listed every parameter, so the LAST won there too. Both now start
/// from <see cref="Dx11SceneBuilder.MaterialTextures"/> and <see cref="Dx11SceneBuilder.MaterialParameters"/>.</para>
///
/// <para>Everything that reads the install is a no-op without one, as in <see cref="MaterialDuplicateNameTests"/>.
/// The real-bin tests also return early once a later patch stops repeating the names, and their device halves
/// return early without a D3D11 device.</para>
/// </summary>
public sealed class Dx11FirstCopyBindingTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    private const string BakedTerrainShader = "Shaders/StaticMesh/DefaultEnv_Flat_BakedTerrain";
    private const string FlatShader = "Shaders/StaticMesh/DefaultEnv_Flat";
    private const string MaterialName = "Test/Materials/Repeats_MAT";

    private static readonly Dictionary<uint, string> Known = new()
    {
        [H("StaticMaterialDef")] = "StaticMaterialDef",
        [H(BakedTerrainShader)] = BakedTerrainShader,
        [H(FlatShader)] = FlatShader,
    };
    private static string? Resolve(uint h) => Known.TryGetValue(h, out var n) ? n : null;

    private static BinTreeEmbedded Sampler(string name, string path) =>
        new(0, 0x0904b150, new BinTreeProperty[]
        {
            new BinTreeString(H("TextureName"), name),
            new BinTreeString(H("texturePath"), path),
        });

    private static BinTreeEmbedded Param(string name, Vector4? value)
    {
        var props = new List<BinTreeProperty> { new BinTreeString(H("name"), name) };
        if (value is { } v) props.Add(new BinTreeVector4(H("value"), v));
        return new BinTreeEmbedded(0, H("StaticMaterialShaderParamDef"), props);
    }

    /// <summary>One StaticMaterialDef with these samplers and parameters, in this order, optionally drawn
    /// with <paramref name="shader"/>.</summary>
    private static MaterialBinding Material(BinTreeEmbedded[] samplers, BinTreeEmbedded[] parameters, string? shader = null)
    {
        var props = new List<BinTreeProperty> { new BinTreeString(H("name"), MaterialName) };
        if (samplers.Length > 0)
            props.Add(new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded, samplers));
        if (parameters.Length > 0)
            props.Add(new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded, parameters));
        if (shader is not null)
            props.Add(new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
                {
                    new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
                        {
                            new BinTreeObjectLink(H("shader"), H(shader)),
                        }),
                    }),
                }),
            }));

        var tree = new BinTree(new[] { new BinTreeObject(H(MaterialName), H("StaticMaterialDef"), props) },
            Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);
        return Assert.Single(MaterialDocument.Parse(ms.ToArray(), Resolve).Materials);
    }

    private static DxbcShader Shader(DxbcStage stage, params string[] textures) => new()
    {
        Bytecode = Array.Empty<byte>(), Stage = stage,
        Resources = textures.Select((n, i) => new DxbcResource(n, DxbcResourceKind.Texture, (uint)i, 1, 4, 5)).ToList(),
    };

    // ===================================================== the shared helpers (no I/O)

    [Fact]
    public void TheSharedHelpersKeepTheFirstCopyOfARepeatedSamplerAndParameter()
    {
        // Soraka skin53-61 Body_Flowmap_inst's shape: Specular_Mask and MaxSpec twice, and LQ_Lighting_Intensity
        // twice with no value on the second copy (M673: an authored zero).
        var b = Material(
            new[]
            {
                Sampler("Diffuse_Texture", "assets/test/diffuse.tex"),
                Sampler("Specular_Mask", "assets/test/first_mask.tex"),
                Sampler("Specular_Mask", "assets/test/second_mask.tex"),
            },
            new[]
            {
                Param("MaxSpec", new Vector4(64f, 0f, 0f, 0f)),
                Param("LQ_Lighting_Intensity", new Vector4(1f, 0f, 0f, 0f)),
                Param("LQ_Lighting_Intensity", null),
                Param("MaxSpec", new Vector4(43.2275f, 0f, 0f, 0f)),
            });
        var ps = Shader(DxbcStage.Pixel, "Diffuse_Texture__TX", "Specular_Mask__TX");
        var vs = Shader(DxbcStage.Vertex);

        // the one helper, under each builder's own target resolver
        foreach (var targetOf in new Func<string, string?>[]
                 {
                     s => Dx11SceneBuilder.ResolveTextureTarget(s, ps, vs),
                     s => Dx11CharacterScene.ResolveTextureTarget(s, ps, vs),
                 })
            Assert.Equal(
                new[] { ("Diffuse_Texture__TX", "assets/test/diffuse.tex"), ("Specular_Mask__TX", "assets/test/first_mask.tex") },
                Dx11SceneBuilder.MaterialTextures(b, targetOf).Select(t => (t.Target, t.Key)));

        var parameters = Dx11SceneBuilder.MaterialParameters(b);
        Assert.Equal(new[] { "MaxSpec", "LQ_Lighting_Intensity" }, parameters.Select(p => p.Name));
        Assert.Equal(new[] { 64f, 0f, 0f, 0f }, parameters[0].Value);
        Assert.Equal(new[] { 1f, 0f, 0f, 0f }, parameters[1].Value);

        // the material itself keeps every row
        Assert.Equal(3, b.Slots.Count);
        Assert.Equal(4, b.Parameters.Count);
    }

    [Fact]
    public void TwoSamplerNamesThatResolveToOneSlotBindTheFirst()
    {
        // The key is the slot a binding occupies, not the sampler name. A champion's generic "texture" sampler
        // resolves to the shader's diffuse slot, which a sampler named after that slot reaches too.
        var ps = Shader(DxbcStage.Pixel, "Diffuse_Texture__TX");
        var vs = Shader(DxbcStage.Vertex);
        string? TargetOf(string sampler) => Dx11CharacterScene.ResolveTextureTarget(sampler, ps, vs);

        var genericFirst = Material(
            new[] { Sampler("texture", "assets/test/a.tex"), Sampler("Diffuse_Texture", "assets/test/b.tex") },
            Array.Empty<BinTreeEmbedded>());
        var namedFirst = Material(
            new[] { Sampler("Diffuse_Texture", "assets/test/b.tex"), Sampler("texture", "assets/test/a.tex") },
            Array.Empty<BinTreeEmbedded>());

        Assert.Equal("assets/test/a.tex", Assert.Single(Dx11SceneBuilder.MaterialTextures(genericFirst, TargetOf)).Key);
        Assert.Equal("assets/test/b.tex", Assert.Single(Dx11SceneBuilder.MaterialTextures(namedFirst, TargetOf)).Key);
    }

    [Fact]
    public void ASamplerThatCannotBindTakesNoSlot()
    {
        // No path, or no slot of that name in this shader: skipped before the rule runs, as the builders always
        // skipped them, so the next copy that CAN bind still gets the slot.
        var ps = Shader(DxbcStage.Pixel, "Mask_Texture__TX");
        var vs = Shader(DxbcStage.Vertex);
        var b = Material(
            new[]
            {
                Sampler("Mask_Texture", ""),
                Sampler("Unused_Texture", "assets/test/unused.tex"),
                Sampler("Mask_Texture", "assets/test/mask.tex"),
            },
            Array.Empty<BinTreeEmbedded>());

        var bound = Assert.Single(Dx11SceneBuilder.MaterialTextures(b, s => Dx11SceneBuilder.ResolveTextureTarget(s, ps, vs)));
        Assert.Equal(("Mask_Texture__TX", "assets/test/mask.tex"), (bound.Target, bound.Key));
    }

    // ===================================================== the real map builder: its own replacements still win

    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    /// <summary>One triangle drawn with <paramref name="b"/>, through the real Prepare against the installed
    /// shader cache.</summary>
    private static Dx11SceneBuilder.PreparedSlice PrepareOne(MaterialBinding b, MapGeoGroup group, ShaderPermutationIndex perms)
    {
        using var cache = ShaderCacheReader.Open(Final, null, out var error);
        Assert.True(cache is not null, error);
        var map = new MapGeoAsset
        {
            Positions = new[] { 0f, 0f, 0f, 100f, 0f, 0f, 0f, 0f, 100f },
            Normals = new[] { 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f },
            Uvs = new[] { 0f, 0f, 1f, 0f, 0f, 1f },
            RawLightmapUvs = new[] { 0f, 0f, 1f, 0f, 0f, 1f },   // M320: the Riot shader takes raw Texcoord7
            Indices = new uint[] { 0, 1, 2 },
            Groups = new[] { group },
        };
        var scene = Dx11SceneBuilder.Prepare(cache!, perms, map, new[] { b }, _ => null, null, null, false);
        Assert.True(scene.Slices.Count == 1, string.Join(" | ", scene.FailureReasons.Select(kv => $"{kv.Key}: {kv.Value}")));
        return scene.Slices[0];
    }

    private static float[] One(Dx11SceneBuilder.PreparedSlice slice, string name) =>
        Assert.Single(slice.Parameters, p => p.Name.Equals(name, OIC)).Value;

    [Fact]
    public void TheBakedPaintAndBakedLightReplacementsStillBeatRepeatedAuthoredCopies()
    {
        // M319 replaces the baked paint texture and BAKED_PAINT_UV_SCALE_BIAS from the mapgeo, and M320 replaces
        // BAKED_LIGHT_SCALE_AND_BIAS. A material that authors each of them twice still loses to both.
        if (!Directory.Exists(Final)) return;
        var b = Material(
            new[]
            {
                Sampler("BAKED_DIFFUSE_TEXTURE", "assets/test/black.tex"),
                Sampler("BAKED_DIFFUSE_TEXTURE", "assets/test/white.tex"),
            },
            new[]
            {
                Param("BAKED_PAINT_UV_SCALE_BIAS", new Vector4(9f)),
                Param("BAKED_LIGHT_SCALE_AND_BIAS", new Vector4(9f)),
                Param("BAKED_PAINT_UV_SCALE_BIAS", new Vector4(10f)),
                Param("BAKED_LIGHT_SCALE_AND_BIAS", new Vector4(10f)),
            },
            BakedTerrainShader);
        var lightScale = new Vector2(0.5f, 0.25f);
        var lightBias = new Vector2(0.125f, 0.0625f);
        var paintScale = new Vector2(0.75f, 0.5f);
        var paintBias = new Vector2(0.25f, 0.125f);

        var slice = PrepareOne(b, new MapGeoGroup(MaterialName, 0, 3, LightmapTexture: "assets/test/lightmap.tex")
        {
            LightmapScale = lightScale, LightmapBias = lightBias,
            BakedPaintTexture = "assets/test/paint.tex", BakedPaintScale = paintScale, BakedPaintBias = paintBias,
        }, new ShaderPermutationIndex(Final));

        Assert.Equal(Dx11SceneBuilder.BakedPaintUvScaleBias(paintScale, paintBias), One(slice, "BAKED_PAINT_UV_SCALE_BIAS"));
        Assert.Equal(Dx11SceneBuilder.BakedPaintUvScaleBias(lightScale, lightBias), One(slice, "BAKED_LIGHT_SCALE_AND_BIAS"));
        string? paintTarget = Dx11SceneBuilder.BakedPaintTextureTarget(slice.Ps);
        Assert.NotNull(paintTarget);
        Assert.Equal("assets/test/paint.tex", Assert.Single(slice.Textures, t => t.Target.Equals(paintTarget, OIC)).Key);
    }

    [Fact]
    public void AShaderDefaultFillsOnlyANameTheMaterialLeavesOut()
    {
        // M257 adds the shader's own default for every parameter the material does not author. A name authored
        // twice IS authored: its first copy stands, and the default does not come back as a second entry.
        if (!Directory.Exists(Final)) return;
        var perms = new ShaderPermutationIndex(Final);
        Assert.True(perms.TryGetParameterDefaults(FlatShader, out var defaults), $"{FlatShader} is not in shaders.bin");
        var (name, fallback) = defaults.FirstOrDefault(kv => !kv.Key.Equals("TERRAIN_XFORM", OIC));
        Assert.True(name is not null, $"{FlatShader} declares no parameter default");

        var leftOut = Material(Array.Empty<BinTreeEmbedded>(), Array.Empty<BinTreeEmbedded>(), FlatShader);
        Assert.Equal(fallback, One(PrepareOne(leftOut, new MapGeoGroup(MaterialName, 0, 3), perms), name));

        var twice = Material(Array.Empty<BinTreeEmbedded>(),
            new[] { Param(name, new Vector4(7f)), Param(name, new Vector4(8f)) }, FlatShader);
        Assert.Equal(new[] { 7f, 7f, 7f, 7f }, One(PrepareOne(twice, new MapGeoGroup(MaterialName, 0, 3), perms), name));
    }

    // ===================================================== real data through the real builders and a real device

    private static readonly Lazy<HashDatabase?> Hashes = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); } catch { return null; }
    });

    /// <summary>A material's samplers that repeat a name, each with its bindable copies in file order.</summary>
    private static List<IGrouping<string, TextureSlot>> RepeatedSamplers(MaterialBinding m) =>
        m.Slots.Where(s => !string.IsNullOrWhiteSpace(s.Path))
            .GroupBy(s => s.SamplerName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();

    /// <summary>A material's numeric parameters that repeat a name, each with its copies in file order.</summary>
    private static List<IGrouping<string, MaterialParameter>> RepeatedParameters(MaterialBinding m) =>
        m.Parameters.Where(p => p.TryGetVector4(out _))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();

    private static float[] Values(MaterialParameter p)
    {
        Assert.True(p.TryGetVector4(out var v));
        return new[] { v.X, v.Y, v.Z, v.W };
    }

    private static List<PreviewMaterial> Committed(ShaderPreviewRenderer renderer, string material)
    {
        var made = renderer.Materials.Where(m => m.Name == material).ToList();
        Assert.True(made.Count > 0, $"{material} was prepared but never committed");
        return made;
    }

    [Fact]
    public void SummonersRiftIslandsBindTheirFirstDiffuseTextureOnD3D11()
    {
        // Earth_{South_BotJungle,South_BlueCamp,North_TopJungle}_Island_A_MAT list DiffuseTexture twice:
        // jungle_*_1bitalpha.tex, then earth_*.tex. The assertions follow whatever the installed patch ships.
        string wadPath = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(wadPath) || Hashes.Value is not { } db) return;
        var resolver = new WadPathResolver(db);
        using var wad = WadArchive.Open(wadPath, resolver);
        string? BinName(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? WadPathOf(ulong h) => db.TryGetPath(h, out var p) ? p : null;
        byte[]? Read(ulong h) => wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;

        const string geoPath = "data/maps/mapgeometry/map11/base.mapgeo";
        var matBin = Read(HashAlgorithms.WadPath("data/maps/mapgeometry/map11/base.materials.bin"));
        var geoBytes = Read(HashAlgorithms.WadPath(geoPath));
        if (matBin is null || geoBytes is null) return;   // the assets moved - nothing to assert against

        var doc = MaterialDocument.Parse(matBin, BinName, WadPathOf);
        var repeating = doc.Materials.Where(m => RepeatedSamplers(m).Count > 0).ToList();
        if (repeating.Count == 0) return;                 // a later patch deduplicated them

        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) return;
        var map = MapGeoDecoder.Decode(geoBytes, ExtendedChannelRule.From(matBin, BinName));
        // Only the repeating materials are handed over. Every other slice then fails fast as "no material
        // binding", so only these load shaders and decode textures: the real builder, a fraction of the work.
        var scene = Dx11SceneBuilder.Prepare(cache, new ShaderPermutationIndex(Final), map, repeating, Read,
            geoPath, null, false);

        var checks = new List<(string Material, string Target, string First, string Last)>();
        foreach (var s in scene.Slices)
        {
            var b = repeating.Single(m => m.Name == s.Name);
            // a per-mesh mapgeo override replaces the material's texture on purpose, so it is not this rule's
            var overridden = map.Groups[s.GroupIndex].TextureOverrides
                .Where(o => !string.IsNullOrWhiteSpace(o.Value))
                .Select(o => Dx11SceneBuilder.ResolveTextureTarget(o.Key, s.Ps, s.Vs))
                .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var copies in RepeatedSamplers(b))
            {
                if (Dx11SceneBuilder.ResolveTextureTarget(copies.Key, s.Ps, s.Vs) is not { } target
                    || overridden.Contains(target)) continue;
                string first = copies.First().Path.ToLowerInvariant(), last = copies.Last().Path.ToLowerInvariant();
                Assert.Equal(first, Assert.Single(s.Textures, t => t.Target.Equals(target, OIC)).Key);
                checks.Add((b.Name, target, first, last));
            }
        }
        Assert.True(checks.Count > 0, $"{repeating.Count} material(s) repeat a sampler, but no prepared slice binds one: "
            + string.Join(" | ", scene.FailureReasons.Select(kv => $"{kv.Key}: {kv.Value}")));

        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return;          // no D3D11 device on this machine
        Dx11SceneBuilder.Commit(renderer, scene, "");

        var everyKey = scene.Slices.SelectMany(s => s.Textures).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (material, target, first, last) in checks)
        {
            Assert.All(Committed(renderer, material), m => Assert.True(m.HasTexture(target), $"{material}: {target} unbound"));
            Assert.True(renderer.IsCached(first), $"{material}: the first copy {first} was never uploaded");
            // nothing else in this scene names the last copy, so it must never have reached the device
            if (!everyKey.Contains(last))
                Assert.False(renderer.IsCached(last), $"{material}: the last copy {last} was uploaded");
        }
    }

    public static IEnumerable<object[]> Skins() => new[]
    {
        new object[] { "Quinn", 14 },    // Wings_Mat and Wings_Mat_Mini_Valor list Idle_Color twice
        new object[] { "Soraka", 53 },   // Body_Flowmap_inst: MaxSpec, LQ_Lighting_Intensity and Specular_Mask twice
    };

    [Theory]
    [MemberData(nameof(Skins))]
    public void ChampionMaterialsBindTheirFirstCopyOnD3D11(string champion, int skin)
    {
        string wadPath = Path.Combine(Final, "Champions", champion + ".wad.client");
        if (!File.Exists(wadPath) || Hashes.Value is not { } db) return;
        var resolver = new WadPathResolver(db);
        using var wad = WadArchive.Open(wadPath, resolver);
        string? BinName(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? WadPathOf(ulong h) => db.TryGetPath(h, out var p) ? p : null;
        byte[]? Read(ulong h) => wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;

        var skinBin = Read(HashAlgorithms.WadPath($"data/characters/{champion.ToLowerInvariant()}/skins/skin{skin}.bin"));
        if (skinBin is null) return;
        var doc = MaterialDocument.Parse(skinBin, BinName, WadPathOf, p => Read(HashAlgorithms.WadPath(p)));
        var repeating = doc.Materials
            .Where(m => RepeatedParameters(m).Count > 0 || RepeatedSamplers(m).Count > 0).ToList();
        if (repeating.Count == 0) return;                 // a later patch deduplicated them
        if (doc.SkinMesh?.SimpleSkin is not { Length: > 0 } sknPath
            || Read(BinTexturePath.HashOfReference(sknPath)) is not { } skn) return;

        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) return;
        var scene = Dx11CharacterScene.Prepare(skn, skinBin, cache, new ShaderPermutationIndex(Final), Read, BinName,
            WadPathOf, fallbackShader: Dx11CharacterScene.DefaultCharacterShader);
        Assert.NotNull(scene);

        var parameterChecks = new List<(string Material, string Name, float[] First)>();
        var textureChecks = new List<(string Material, string Target, string First)>();
        foreach (var s in scene!.Slices)
        {
            if (repeating.FirstOrDefault(m => m.Name == s.Material) is not { } b) continue;
            // a material driver (M646) replaces what the material authors on purpose, so it is not this rule's
            var driven = b.DynamicParameters.Where(p => p.Enabled).Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var copies in RepeatedParameters(b))
            {
                if (driven.Contains(copies.Key)) continue;
                float[] first = Values(copies.First());
                Assert.Equal(first, Assert.Single(s.Parameters, p => p.Name.Equals(copies.Key, OIC)).Value);
                parameterChecks.Add((s.Material, copies.Key, first));
            }
            foreach (var copies in RepeatedSamplers(b))
            {
                if (Dx11CharacterScene.ResolveTextureTarget(copies.Key, s.Ps, s.Vs) is not { } target) continue;
                string first = copies.First().Path.ToLowerInvariant();
                Assert.Equal(first, Assert.Single(s.Textures, t => t.Target.Equals(target, OIC)).Key);
                textureChecks.Add((s.Material, target, first));
            }
        }
        Assert.True(parameterChecks.Count + textureChecks.Count > 0,
            $"{champion} skin{skin} repeats names in {string.Join(", ", repeating.Select(m => m.Name))}, "
            + $"but no drawn slice uses them: {string.Join(" | ", scene.Failures.Take(4))}");

        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return;          // no D3D11 device on this machine
        Dx11CharacterScene.Commit(renderer, scene, "");

        foreach (var (material, name, first) in parameterChecks)
            Assert.All(Committed(renderer, material), m => Assert.Equal(first, m.Params[name]));
        foreach (var (material, target, first) in textureChecks)
        {
            Assert.All(Committed(renderer, material), m => Assert.True(m.HasTexture(target), $"{material}: {target} unbound"));
            Assert.True(renderer.IsCached(first), $"{material}: the first copy {first} was never uploaded");
        }
    }
}
