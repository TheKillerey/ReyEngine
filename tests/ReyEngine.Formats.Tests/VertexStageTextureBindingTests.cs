using System;
using System.IO;
using System.Linq;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M774: "On arena F there is a mesh that moves like a banner or grass... sharing the same material as the mesh
/// that should not move." Cloth_Base_StaticMesh weights its sway by floor(Mask_Texture.G * DeformMaskStrength)
/// in the VERTEX shader; the map builder only looked for material textures in the PIXEL shader, so the mask was
/// dropped and the vertex stage read the white stand-in - weight 1 on every vertex, the brazier included.
/// </summary>
public sealed class VertexStageTextureBindingTests
{
    private static DxbcShader Shader(DxbcStage stage, params string[] textures) => new()
    {
        Bytecode = Array.Empty<byte>(), Stage = stage,
        Resources = textures.Select((n, i) => new DxbcResource(n, DxbcResourceKind.Texture, (uint)i, 1, 3, 5)).ToList(),
    };

    [Fact]
    public void A_texture_only_the_vertex_shader_reads_is_bound()
    {
        var ps = Shader(DxbcStage.Pixel, "Diffuse_Texture__TX", "FOW_MAP_SharedTexture");
        var vs = Shader(DxbcStage.Vertex, "Mask_Texture__TX");
        Assert.Equal("Mask_Texture__TX", Dx11SceneBuilder.ResolveTextureTarget("Mask_Texture", ps, vs));
        Assert.Equal("Diffuse_Texture__TX", Dx11SceneBuilder.ResolveTextureTarget("Diffuse_Texture", ps, vs));
        Assert.Null(Dx11SceneBuilder.ResolveTextureTarget("Nothing_Texture", ps, vs));
    }

    [Fact]
    public void The_pixel_shader_still_wins_when_both_declare_the_name()
    {
        var ps = Shader(DxbcStage.Pixel, "Shared_Texture__TX");
        var vs = Shader(DxbcStage.Vertex, "shared_texture__tx");
        Assert.Equal("Shared_Texture__TX", Dx11SceneBuilder.ResolveTextureTarget("Shared_Texture", ps, vs));
    }

    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    [Fact]
    public void Arena_F_red_cloth_binds_its_displacement_mask()
    {
        string wadPath = Path.Combine(Final, @"Maps\Shipping\Map30.wad.client");
        if (!File.Exists(wadPath)) return;
        var db = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(db);
        using var wad = WadArchive.Open(wadPath, resolver);
        string? BinName(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? WadPathOf(ulong h) => db.TryGetPath(h, out var p) ? p : null;
        byte[]? Read(ulong h) => wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;

        const string geoPath = "data/maps/mapgeometry/map30/arenaf.mapgeo";
        var matBin = wad.Extract(HashAlgorithms.WadPath("data/maps/mapgeometry/map30/arenaf.materials.bin"));
        var map = MapGeoDecoder.Decode(wad.Extract(HashAlgorithms.WadPath(geoPath)), ExtendedChannelRule.From(matBin, BinName));
        var doc = MaterialDocument.Parse(matBin, BinName, WadPathOf);
        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) return;

        var scene = Dx11SceneBuilder.Prepare(cache, new ShaderPermutationIndex(Final), map, doc.Materials, Read,
            geoPath, null, false);
        var cloth = scene.Slices.Where(s => s.Name.EndsWith("Cherry_ArenaSurrounding_RedCloth_Displacement_MAT",
            StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(cloth);
        Assert.All(cloth, s => Assert.Contains(s.Textures, t =>
            t.Target.Equals("Mask_Texture__TX", StringComparison.OrdinalIgnoreCase)
            && t.Key.EndsWith("cherry_battlearenasurroundings_redcloth_mask.tex", StringComparison.OrdinalIgnoreCase)));
    }
}
