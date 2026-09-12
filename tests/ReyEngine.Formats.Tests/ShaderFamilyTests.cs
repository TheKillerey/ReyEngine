using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M701: the shader picker offers the family the material is DRAWN with. Measured over the
/// installed game: 2,235 of 2,237 character materials are on Shaders/SkinnedMesh (the other two on a UI
/// shader, which is why this warns rather than refuses), and 5,870 of 5,870 map materials are on
/// Shaders/StaticMesh. Crossing the two is the client's "Missing shader constant WORLD_MATRIX" with
/// nothing drawn, and the editor used to offer all 350 shaders to both.</summary>
public sealed class ShaderFamilyTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";

    [Fact]
    public void TheFamilyFollowsWhatTheMaterialDraws()
    {
        Assert.True(ShaderFamilies.Fits("Shaders/SkinnedMesh/Diffuse_Bloom", MaterialSourceKind.ChampionSkin));
        Assert.False(ShaderFamilies.Fits("Shaders/StaticMesh/DefaultEnv", MaterialSourceKind.ChampionSkin));
        Assert.True(ShaderFamilies.Fits("shaders/staticmesh/defaultenv", MaterialSourceKind.MapMaterials));
        Assert.False(ShaderFamilies.Fits("Shaders/SkinnedMesh/Diffuse_Bloom", MaterialSourceKind.MapMaterials));
        Assert.False(ShaderFamilies.Fits("", MaterialSourceKind.MapMaterials));
        Assert.False(ShaderFamilies.Fits(null, MaterialSourceKind.MapMaterials));

        Assert.Null(ShaderFamilies.Warning("Shaders/SkinnedMesh/Onsen", MaterialSourceKind.ChampionSkin));
        Assert.Null(ShaderFamilies.Warning(null, MaterialSourceKind.ChampionSkin));
        var crossed = ShaderFamilies.Warning("Shaders/StaticMesh/DefaultEnv", MaterialSourceKind.ChampionSkin);
        Assert.NotNull(crossed);
        Assert.Contains("missing shader constant", crossed!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bone matrices", crossed);
        var otherWay = ShaderFamilies.Warning("Shaders/SkinnedMesh/Onsen", MaterialSourceKind.MapMaterials);
        Assert.Contains("world matrix", otherWay!, StringComparison.OrdinalIgnoreCase);
        // a family that is neither still gets a line, without claiming to know what will happen
        var odd = ShaderFamilies.Warning("Shaders/UI/Something", MaterialSourceKind.ChampionSkin);
        Assert.Contains("Check it in game", odd!);
    }

    [Fact]
    public void TheListIsTheRightFamilyPlusWhateverTheFileAlreadyUses()
    {
        var catalogue = new[]
        {
            "Shaders/SkinnedMesh/Diffuse_Bloom", "Shaders/SkinnedMesh/Onsen",
            "Shaders/StaticMesh/DefaultEnv", "Shaders/StaticMesh/Flowmap_River",
            "Shaders/UI/Simple", "Shaders/Particles/Quad",
        };
        var skinned = ShaderFamilies.Offer(catalogue, Array.Empty<string>(), MaterialSourceKind.ChampionSkin).ToList();
        Assert.Equal(new[] { "Shaders/SkinnedMesh/Diffuse_Bloom", "Shaders/SkinnedMesh/Onsen" }, skinned);

        var map = ShaderFamilies.Offer(catalogue, Array.Empty<string>(), MaterialSourceKind.MapMaterials).ToList();
        Assert.Equal(new[] { "Shaders/StaticMesh/DefaultEnv", "Shaders/StaticMesh/Flowmap_River" }, map);

        // a shader the document already uses is never hidden, whatever family it is in - two shipped
        // character materials really are on a UI shader
        var withUsed = ShaderFamilies.Offer(catalogue, new[] { "Shaders/UI/Simple" }, MaterialSourceKind.ChampionSkin).ToList();
        Assert.Contains("Shaders/UI/Simple", withUsed);
        Assert.Equal(3, withUsed.Count);
        Assert.Single(withUsed, s => s == "Shaders/UI/Simple");   // not twice when it is also in the catalogue
        var dup = ShaderFamilies.Offer(catalogue, new[] { "Shaders/SkinnedMesh/Onsen" }, MaterialSourceKind.ChampionSkin).ToList();
        Assert.Equal(2, dup.Count);
    }

    [Fact]
    public void ARealChampionSkinIsOfferedSkinnedShadersOnly()
    {
        string wad = Path.Combine(Champions, "Aatrox.wad.client");
        if (!File.Exists(wad)) return;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { return; }
        using var archive = WadArchive.Open(wad, new WadPathResolver(db));
        ulong hash = HashAlgorithms.WadPath("data/characters/aatrox/skins/skin0.bin");
        if (!archive.TryGetEntry(hash, out var entry)) return;
        byte[] bin = archive.Extract(hash);

        var doc = MaterialDocument.Parse(bin, h => db.TryGetBinName(h, out var n) ? n : null,
            h => db.TryGetPath(h, out var p) ? p : null);
        Assert.Equal(MaterialSourceKind.ChampionSkin, doc.Kind);
        Assert.NotEmpty(doc.Materials);

        var editor = new MaterialEditorViewModel();
        editor.SetCatalog(Catalogue());
        editor.Load(doc, entry, bin);

        Assert.NotEmpty(editor.KnownShaders);
        Assert.DoesNotContain(editor.KnownShaders, s => s.StartsWith("Shaders/StaticMesh/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(editor.KnownShaders, s => s.StartsWith("Shaders/SkinnedMesh/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("skinned-mesh", editor.ShaderListNote);
        // the shaders this skin itself uses are all offered, so nothing it authored is missing from the list
        foreach (var used in editor.UsedShaders) Assert.Contains(used, editor.KnownShaders);
    }

    [Fact]
    public void LoadingAMaterialThatIsAlreadyOnTheWrongFamilySaysSo()
    {
        // M702: a file can arrive carrying a shader from any earlier tool, and the client's report for it
        // is a missing shader constant with nothing drawn - which is very hard to trace back to a material.
        string wad = Path.Combine(Champions, "Aatrox.wad.client");
        if (!File.Exists(wad)) return;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { return; }
        using var archive = WadArchive.Open(wad, new WadPathResolver(db));
        ulong hash = HashAlgorithms.WadPath("data/characters/aatrox/skins/skin0.bin");
        if (!archive.TryGetEntry(hash, out var entry)) return;
        byte[] bin = archive.Extract(hash);
        var doc = MaterialDocument.Parse(bin, h => db.TryGetBinName(h, out var n) ? n : null,
            h => db.TryGetPath(h, out var p) ? p : null);

        var warnings = new List<string>();
        var editor = new MaterialEditorViewModel { Warn = warnings.Add };
        editor.SetCatalog(Catalogue());
        editor.Load(doc, entry, bin);
        Assert.Empty(warnings);   // a shipped skin is on its own family throughout

        // put one of its materials on a map shader, the way a picker offering all 350 used to allow
        var victim = editor.Materials.First(m => m.CanChangeShader);
        editor.ChangeShader(victim, "Shaders/StaticMesh/DefaultEnv");
        Assert.Contains("Missing shader constant", victim.ShaderChangeStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(warnings, w => w.Contains("Missing shader constant", StringComparison.OrdinalIgnoreCase));

        // and it is reported again the next time that file is opened
        warnings.Clear();
        var reopened = new MaterialEditorViewModel { Warn = warnings.Add };
        reopened.SetCatalog(Catalogue());
        reopened.Load(MaterialDocument.Parse(editor.Serialize()!, h => db.TryGetBinName(h, out var n) ? n : null,
            h => db.TryGetPath(h, out var p) ? p : null), entry, bin);
        Assert.Contains(warnings, w => w.Contains("Missing shader constant", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A catalogue holding both families, so the filter has something to remove.</summary>
    private static ShaderCatalog Catalogue() => new()
    {
        Environment = "Test",
        Shaders =
        {
            Def("Shaders/SkinnedMesh/Diffuse_Bloom"), Def("Shaders/SkinnedMesh/Onsen"),
            Def("Shaders/SkinnedMesh/Scrolling_ColorDodge_Masked"),
            Def("Shaders/StaticMesh/DefaultEnv"), Def("Shaders/StaticMesh/Flowmap_River"),
            Def("Shaders/Particles/Quad"),
        },
    };

    private static LeagueShaderDef Def(string name) =>
        new(name, name.Split('/')[1], new List<ShaderTextureDef>(), new List<ShaderParamDef>(), new List<string>());
}
