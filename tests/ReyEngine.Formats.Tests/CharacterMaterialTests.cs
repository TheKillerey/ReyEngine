using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M703: giving a character's submesh a material of its own - the step that makes its shader
/// changeable at all, because a character material is optional and Riot's scenery characters (and
/// everything the Character Creator writes) ship none. The entry written is the form 1,997 shipped
/// materialOverride entries use: a Material object link and the submesh it is for.</summary>
public sealed class CharacterMaterialTests
{
    private const string Map11 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly CharacterPackageSpec Spec = new(
        "MyGolem", "MyGolem.skn", "MyGolem.skl", "MyGolem_TX_CM.tex",
        new[] { new CharacterClipSpec("Idle1", "mygolem_idle1.anm", Loop: true) });

    private static byte[] CreatedSkinBin() =>
        CharacterPackageBuilder.Build(Spec).Bins.Single(b => b.Path.EndsWith("skins/skin0.bin", StringComparison.Ordinal)).Bytes;

    private static LeagueShaderDef Shader(string name, params string[] textures) =>
        new(name, name.Split('/')[1],
            textures.Select(t => new ShaderTextureDef(t, "assets/shared/materials/default.tex")).ToList(),
            new List<ShaderParamDef>(), new List<string>());

    [Fact]
    public void TheMaterialIsNamedTheWayRiotNamesThem()
    {
        Assert.Equal("Characters/MyGolem/Skins/Skin0/Materials/Body_inst",
            CharacterMaterialBinder.MaterialPathFor("Characters/MyGolem/Skins/Skin0", "Body"));
        Assert.Equal("Characters/X/Skins/Skin0/Materials/Blinn1_inst",
            CharacterMaterialBinder.MaterialPathFor("Characters/X/Skins/Skin0/", "blinn1"));
        // a name the path cannot take is made into one rather than refused: anything outside
        // letters/digits/underscore goes, and a leading digit gets an S so the name is a valid identifier
        Assert.Equal("Characters/X/Skins/Skin0/Materials/S2sided_inst",
            CharacterMaterialBinder.MaterialPathFor("Characters/X/Skins/Skin0", "2 sided"));
        Assert.Equal("Characters/X/Skins/Skin0/Materials/Two_sided_inst",
            CharacterMaterialBinder.MaterialPathFor("Characters/X/Skins/Skin0", "two_sided"));
        Assert.EndsWith("/Materials/Submesh_inst", CharacterMaterialBinder.MaterialPathFor("Characters/X/Skins/Skin0", "!!"));
    }

    [Fact]
    public void TheShaderIsTheMeasuredDefaultAndAlwaysASkinnedOne()
    {
        var catalogue = new ShaderCatalog
        {
            Shaders =
            {
                Shader("Shaders/StaticMesh/DefaultEnv", "Diffuse_Texture"),
                Shader("Shaders/SkinnedMesh/Diffuse_Bloom", "Diffuse_Texture", "Mask_Texture_red"),
                Shader("Shaders/SkinnedMesh/FresnelAlpha_Basic", "Diffuse_Texture"),
            },
        };
        // the preferred one: the only widely used skinned shader asking for nothing but a diffuse
        Assert.Equal("Shaders/SkinnedMesh/FresnelAlpha_Basic", CharacterMaterialBinder.PickShader(catalogue)!.Name);

        var withoutPreferred = new ShaderCatalog
        {
            Shaders =
            {
                Shader("Shaders/StaticMesh/DefaultEnv", "Diffuse_Texture"),
                Shader("Shaders/SkinnedMesh/Something_Big", "A", "B", "C"),
                Shader("Shaders/SkinnedMesh/Something_Small", "Diffuse_Texture"),
            },
        };
        Assert.Equal("Shaders/SkinnedMesh/Something_Small", CharacterMaterialBinder.PickShader(withoutPreferred)!.Name);
        // never a static one, whatever the catalogue holds
        var mapOnly = new ShaderCatalog { Shaders = { Shader("Shaders/StaticMesh/DefaultEnv", "Diffuse_Texture") } };
        Assert.Null(CharacterMaterialBinder.PickShader(mapOnly));
        Assert.Null(CharacterMaterialBinder.PickShader(null));
    }

    [Fact]
    public void ASkinWithNoOverridesGetsTheListInTheFormRiotWrites()
    {
        byte[] skin = CreatedSkinBin();
        Assert.Empty(CharacterMaterialBinder.Overrides(skin));

        byte[]? bound = CharacterMaterialBinder.Bind(skin, "blinn1", "Characters/MyGolem/Skins/Skin0/Materials/Blinn1_inst", out var error);
        Assert.Null(error);
        Assert.NotNull(bound);

        var entry = Assert.Single(CharacterMaterialBinder.Overrides(bound!, h => h == H("Characters/MyGolem/Skins/Skin0/Materials/Blinn1_inst")
            ? "Characters/MyGolem/Skins/Skin0/Materials/Blinn1_inst" : null));
        Assert.Equal("blinn1", entry.Submesh);
        Assert.Equal("Characters/MyGolem/Skins/Skin0/Materials/Blinn1_inst", entry.MaterialPath);
        Assert.Null(entry.Texture);

        // the wire form: a Container (list) of Embedded entries - the client drops a property whose form
        // disagrees with its class, and a dropped material is a submesh that silently keeps the old look
        var tree = new BinTree(new MemoryStream(bound!, false));
        var smp = tree.Objects.Values.Select(o => o.Properties.GetValueOrDefault(H("skinMeshProperties")))
            .OfType<BinTreeStruct>().Single();
        var list = Assert.IsType<BinTreeContainer>(smp.Properties[H("materialOverride")]);
        Assert.Equal(BinPropertyType.Embedded, list.ElementType);
        var element = Assert.IsType<BinTreeEmbedded>(Assert.Single(list.Elements));
        Assert.Equal(H("SkinMeshDataProperties_MaterialOverride"), element.ClassHash);
        Assert.Equal(new[] { H("Material"), H("submesh") }, element.Properties.Keys);
        Assert.Equal(H("Characters/MyGolem/Skins/Skin0/Materials/Blinn1_inst"),
            Assert.IsType<BinTreeObjectLink>(element.Properties[H("Material")]).Value);

        // binding the same submesh again updates it rather than adding a second entry
        byte[]? again = CharacterMaterialBinder.Bind(bound!, "blinn1", "Characters/MyGolem/Skins/Skin0/Materials/Other_inst", out error);
        Assert.Null(error);
        var only = Assert.Single(CharacterMaterialBinder.Overrides(again!));
        Assert.Equal("blinn1", only.Submesh);

        Assert.Null(CharacterMaterialBinder.Bind(skin, "", "x", out error));
        Assert.NotNull(error);
        Assert.Null(CharacterMaterialBinder.Bind(new byte[] { 1, 2, 3 }, "a", "b", out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ATextureOverrideOnTheSameSubmeshIsKept()
    {
        // SRU_Train's skin is the shipped case: materialOverride entries that carry only a texture
        if (!File.Exists(Map11)) return;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { return; }
        using var wad = WadArchive.Open(Map11, new WadPathResolver(db));
        ulong hash = HashAlgorithms.WadPath("data/characters/sru_train/skins/skin0.bin");
        if (!wad.TryGetEntry(hash, out _)) return;
        byte[] skin = wad.Extract(hash);

        var before = CharacterMaterialBinder.Overrides(skin);
        var textured = before.FirstOrDefault(o => o.Texture is not null && o.MaterialPath is null);
        Assert.NotNull(textured);

        byte[]? bound = CharacterMaterialBinder.Bind(skin, textured!.Submesh, "Characters/SRU_Train/Skins/Skin0/Materials/Poro_inst", out var error);
        Assert.Null(error);
        // the name is one this tool just invented, so the hash database cannot spell it back - the reader
        // returns the hash, which is what every other bin reader in the editor does with an unknown name
        string invented = "Characters/SRU_Train/Skins/Skin0/Materials/Poro_inst";
        var after = CharacterMaterialBinder.Overrides(bound!, h => h == H(invented) ? invented : null);
        Assert.Equal(before.Count, after.Count);                 // updated, not appended
        var updated = after.Single(o => o.Submesh == textured.Submesh);
        Assert.Equal(textured.Texture, updated.Texture);          // the user's texture is not ours to drop
        Assert.Equal(invented, updated.MaterialPath);
        Assert.Equal($"0x{H(invented):x8}", CharacterMaterialBinder.Overrides(bound!).Single(o => o.Submesh == textured.Submesh).MaterialPath);
    }

    [Fact]
    public void TheAddedMaterialIsSomethingTheEditorCanThenEdit()
    {
        byte[] skin = CreatedSkinBin();
        var shader = Shader("Shaders/SkinnedMesh/FresnelAlpha_Basic", "Diffuse_Texture");
        byte[]? updated = CharacterMaterialBinder.AddMaterial(skin, "Characters/MyGolem/Skins/Skin0", "blinn1",
            shader, "assets/characters/mygolem/skins/base/mygolem_tx_cm.tex", out var error, out var materialPath);
        Assert.Null(error);
        Assert.NotNull(updated);
        Assert.Equal("Characters/MyGolem/Skins/Skin0/Materials/Blinn1_inst", materialPath);

        // the editor's own reader finds it, attached to that submesh, on a shader a character can draw with
        string? Name(uint h) => h == H(materialPath) ? materialPath
            : h == H("Shaders/SkinnedMesh/FresnelAlpha_Basic") ? "Shaders/SkinnedMesh/FresnelAlpha_Basic" : null;
        // the sampler is written as a wad chunk link (M590), so reading its path back needs the wad resolver
        const string diffusePath = "assets/characters/mygolem/skins/base/mygolem_tx_cm.tex";
        var doc = MaterialDocument.Parse(updated!, Name, h => h == HashAlgorithms.WadPath(diffusePath) ? diffusePath : null);
        Assert.Equal(MaterialSourceKind.ChampionSkin, doc.Kind);
        // two: the skin's own default block, which has no technique and so no shader to change - the
        // reason this feature exists - and the one just authored
        Assert.Equal(2, doc.Materials.Count);
        var fallback = doc.Materials.Single(m => m.IsDefault);
        Assert.False(fallback.CanChangeShader);
        var material = doc.Materials.Single(m => !m.IsDefault);
        Assert.Equal(materialPath, material.Name);
        Assert.Contains("blinn1", material.Submeshes);
        Assert.True(material.CanChangeShader);                                     // it has a technique pass
        Assert.True(ShaderFamilies.Fits(material.RenderShader, MaterialSourceKind.ChampionSkin));
        // and it starts by drawing what the submesh already drew
        Assert.Contains(material.Slots, s => s.Path.Equals(diffusePath, StringComparison.OrdinalIgnoreCase));

        // adding it twice is refused rather than writing a second object under the same name
        Assert.Null(CharacterMaterialBinder.AddMaterial(updated!, "Characters/MyGolem/Skins/Skin0", "blinn1",
            shader, null, out var second, out _));
        Assert.Contains("already exists", second!);
    }

    [Fact]
    public void TheSubmeshRowOffersItOnlyWhenTheSubmeshHasNoMaterialOfItsOwn()
    {
        var host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.CharacterMaterials.cs");
        var rows = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.Outliner.cs");
        var xaml = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        if (host is null || rows is null || xaml is null) return;
        Assert.Contains("row.HasOwnMaterial = HasMaterialEditor && MaterialEditor.Materials.Any(m =>", rows);
        Assert.Contains("Command=\"{Binding AddMaterialForSubmeshCommand}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding CanAddMaterial}\"", xaml);
        Assert.Contains("CharacterMaterialBinder.AddMaterial(", host);
        Assert.Contains("await SaveCharacterMaterialOverride();", host);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
