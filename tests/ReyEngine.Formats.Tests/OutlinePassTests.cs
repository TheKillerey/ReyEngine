using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M858: a skin's <c>skinMeshProperties.OutlineCategorySubmeshes</c> is read, and each listed submesh draws a
/// second time with the outline material. The entry is <c>{ category -> CharacterOutlineCategory, materialOverride,
/// Submeshes "a b c" }</c>; the material is the entry's own <c>materialOverride</c>, else the category's
/// <c>Material</c>. Jade_CharacterOutline was linked ONLY from these entries (236 links, none read), so it
/// could never draw.
/// </summary>
public sealed class OutlinePassTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly Dictionary<uint, string> Names = new[]
    {
        "StaticMaterialDef", "SkinCharacterDataProperties", "SkinMeshDataProperties", "CharacterOutlineCategory",
        "CharacterOutlineSubmeshes", "skinMeshProperties", "simpleSkin", "name",
    }.ToDictionary(H, n => n);
    private static string? Resolve(uint h) => Names.GetValueOrDefault(h);

    private const string OverrideMat = "Characters/X/Skins/Skin0/Materials/Outline_Prop_inst";
    private const string CategoryMat = "Maps/Shipping/Map453/Materials/Outline_Default_inst";
    private const string Category = "Maps/Shipping/Map453/CharacterOutlineCategory_Default";

    private static BinTreeObject Material(string path) => new(H(path), H("StaticMaterialDef"), new BinTreeProperty[]
    {
        new BinTreeString(H("name"), path),
    });

    private static BinTreeObject CategoryObject() => new(H(Category), H("CharacterOutlineCategory"), new BinTreeProperty[]
    {
        new BinTreeObjectLink(H("Material"), H(CategoryMat)),
    });

    private static BinTreeEmbedded Entry(string? category, string? materialOverride, string submeshes)
    {
        var props = new List<BinTreeProperty>();
        if (category is not null) props.Add(new BinTreeObjectLink(H("category"), H(category)));
        if (materialOverride is not null) props.Add(new BinTreeObjectLink(H("materialOverride"), H(materialOverride)));
        props.Add(new BinTreeString(H("Submeshes"), submeshes));
        return new BinTreeEmbedded(0, H("CharacterOutlineSubmeshes"), props.ToArray());
    }

    private static byte[] Skin(IEnumerable<BinTreeEmbedded> outlines, bool withOutlineField = true, params BinTreeObject[] local)
    {
        var smp = new List<BinTreeProperty>
        {
            new BinTreeString(H("simpleSkin"), "ASSETS/Characters/X/Skins/Base/X.skn"),
        };
        if (withOutlineField)
            smp.Add(new BinTreeContainer(H("OutlineCategorySubmeshes"), BinPropertyType.Embedded, outlines.Cast<BinTreeProperty>().ToArray()));
        var skin = new BinTreeObject(H("Characters/X/Skins/Skin0"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
        {
            new BinTreeEmbedded(H("skinMeshProperties"), H("SkinMeshDataProperties"), smp.ToArray()),
        });
        using var ms = new MemoryStream();
        new BinTree(local.Prepend(skin).ToArray(), new[] { "DATA/Characters/X/Linked.bin" }).Write(ms);
        return ms.ToArray();
    }

    private static LoadedBin Host(params BinTreeObject[] objects) =>
        new("data/maps/shipping/map453/map453.bin", new BinTree(objects, Array.Empty<string>()));

    [Fact]
    public void An_entry_with_a_materialOverride_draws_that_material_on_every_submesh_it_lists()
    {
        var bin = Skin(new[] { Entry(Category, OverrideMat, "Props Stairs") }, true, Material(OverrideMat));
        var doc = MaterialDocument.Parse(bin, Resolve);

        Assert.Equal(new[] { "Props", "Stairs" }, doc.OutlinePasses.Select(p => p.Submesh));
        Assert.All(doc.OutlinePasses, p => { Assert.Equal("materialOverride", p.Source); Assert.Equal(OverrideMat, p.Material.Name); });
        Assert.Empty(doc.OutlineNotes);
    }

    [Fact]
    public void An_outline_material_is_not_assigned_to_a_base_submesh()
    {
        // it must never become "the material of Props": the base draw keeps its own material
        var doc = MaterialDocument.Parse(Skin(new[] { Entry(Category, OverrideMat, "Props") }, true, Material(OverrideMat)), Resolve);
        Assert.Empty(Assert.Single(doc.Materials, m => m.Name == OverrideMat).Submeshes);
    }

    [Fact]
    public void An_entry_without_an_override_takes_its_categorys_material_from_the_host_bin()
    {
        var bin = Skin(new[] { Entry(Category, null, "Body Eyes") });
        var doc = MaterialDocument.Parse(bin, Resolve, null, _ => null, Host(CategoryObject(), Material(CategoryMat)));

        Assert.Equal(new[] { "Body", "Eyes" }, doc.OutlinePasses.Select(p => p.Submesh));
        var pass = doc.OutlinePasses[0];
        Assert.Equal("category", pass.Source);
        Assert.Equal(CategoryMat, pass.Material.Name);
        Assert.Equal("data/maps/shipping/map453/map453.bin", pass.Material.LinkedFromBin);   // linked, so read-only
        Assert.Same(pass.Material, doc.OutlinePasses[1].Material);
    }

    [Fact]
    public void Without_the_host_a_category_entry_is_reported_and_draws_nothing()
    {
        var doc = MaterialDocument.Parse(Skin(new[] { Entry(Category, null, "Body") }), Resolve, null, _ => null);
        Assert.Empty(doc.OutlinePasses);
        Assert.Contains("Body", Assert.Single(doc.OutlineNotes));
        Assert.Contains("category", doc.OutlineNotes[0]);
    }

    [Fact]
    public void A_category_in_a_dependency_bin_resolves_like_a_material_there()
    {
        byte[] dep;
        using (var ms = new MemoryStream())
        {
            new BinTree(new[] { CategoryObject(), Material(CategoryMat) }, Array.Empty<string>()).Write(ms);
            dep = ms.ToArray();
        }
        var doc = MaterialDocument.Parse(Skin(new[] { Entry(Category, null, "Body") }), Resolve, null,
            p => p == "DATA/Characters/X/Linked.bin" ? dep : null);

        Assert.Equal("Body", Assert.Single(doc.OutlinePasses).Submesh);
        Assert.Equal("DATA/Characters/X/Linked.bin", doc.OutlinePasses[0].Material.LinkedFromBin);
    }

    [Fact]
    public void A_skin_without_the_field_has_no_outline_passes_and_no_notes()
    {
        var doc = MaterialDocument.Parse(Skin(Array.Empty<BinTreeEmbedded>(), withOutlineField: false), Resolve);
        Assert.Empty(doc.OutlinePasses);
        Assert.Empty(doc.OutlineNotes);
    }

    [Fact]
    public void A_category_object_that_is_not_a_category_is_not_taken()
    {
        var notACategory = new BinTreeObject(H(Category), H("EsportsBannerData"), new BinTreeProperty[]
            { new BinTreeObjectLink(H("Material"), H(CategoryMat)) });
        var doc = MaterialDocument.Parse(Skin(new[] { Entry(Category, null, "Body") }), Resolve, null, _ => null,
            Host(notACategory, Material(CategoryMat)));
        Assert.Empty(doc.OutlinePasses);
    }

    /// <summary>The real asset: Jade Ahri skin 63. Four entries take the category's material from map453.bin,
    /// one (Props) names its own. The outline slice then draws through Riot's Jade_CharacterOutline shader.</summary>
    [Fact]
    public void Jade_Ahri_skin63_outlines_its_body_with_Jade_CharacterOutline_when_map453_hosts_the_category()
    {
        string ahri = Path.Combine(Final, "Champions", "Ahri.wad.client");
        string map = Path.Combine(Final, "Maps", "Shipping", "Map453.wad.client");
        if (!File.Exists(ahri) || !File.Exists(map)) { output.WriteLine("SKIPPED: Ahri / Map453 wads are not installed"); return; }
        var db = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(db);
        using var wad = WadArchive.Open(ahri, resolver);
        using var mapWad = WadArchive.Open(map, resolver);
        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        string? Name(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? WadP(ulong h) => db.TryGetPath(h, out var p) ? p : null;
        byte[]? Read(ulong h) => wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
        byte[]? ReadPath(string p) => Read(HashAlgorithms.WadPath(p));

        const string skinPath = "data/characters/jade_ahri/skins/skin63.bin";
        const string mapBin = "data/maps/shipping/map453/map453.bin";
        var skinBin = ReadPath(skinPath)!;
        var host = new LoadedBin(mapBin, SafeBinTree.Parse(mapWad.Extract(HashAlgorithms.WadPath(mapBin))));

        var with = MaterialDocument.Parse(skinBin, Name, WadP, ReadPath, host);
        var without = MaterialDocument.Parse(skinBin, Name, WadP, ReadPath);
        output.WriteLine("with host: " + string.Join("; ", with.OutlinePasses.Select(p => $"{p.Submesh}:{p.Material.Name.Split('/').Last()}({p.Source})")));
        output.WriteLine("without host notes: " + string.Join(" | ", without.OutlineNotes));

        Assert.Equal(new[] { "Body", "Eyes", "Props", "Tail_Large", "Tails" }, with.OutlinePasses.Select(p => p.Submesh).Order());
        Assert.Equal(4, with.OutlinePasses.Count(p => p.Source == "category"));
        Assert.All(with.OutlinePasses.Where(p => p.Source == "category"),
            p => Assert.Equal("Jade_CharacterOutline", p.Material.RenderShader?.Split('/').Last()));
        Assert.Equal(new[] { "Props" }, without.OutlinePasses.Select(p => p.Submesh));   // only the self-contained override

        // and the D3D11 scene: an outline slice per pass, on the Riot shader, after the base slices
        var meshRef = SkinMeshExtractor.Extract(skinBin, WadP)!;
        var skn = Read(BinTexturePath.HashOfReference(meshRef.SimpleSkin!))!;
        PreparedCharacterScene Prepare(LoadedBin? h) => Dx11CharacterScene.Prepare(skn, skinBin, cache, perms: null,
            readAsset: Read, resolveBinName: Name, resolveWadPath: WadP, fallbackShader: Dx11CharacterScene.DefaultCharacterShader, hostBin: h)!;
        var scene = Prepare(host);
        var outlines = scene.Slices.Where(s => s.IsOutline).ToList();
        output.WriteLine("outline slices: " + string.Join("; ", outlines.Select(s => $"{s.Submesh}->{s.VsDesc.ShaderName.Split('/').Last()}")));
        Assert.Contains(outlines, s => s.Submesh == "Body" && s.VsDesc.ShaderName.EndsWith("Jade_CharacterOutline"));
        Assert.All(outlines, s => Assert.True(s.SubmeshIndex >= 0));
        int firstOutline = scene.Slices.FindIndex(s => s.IsOutline);
        Assert.DoesNotContain(scene.Slices.Skip(firstOutline), s => !s.IsOutline);   // appended after every base slice
        Assert.DoesNotContain(Prepare(null).Slices, s => s.IsOutline && s.Submesh == "Body");   // no map, no category, no outline
    }
}
