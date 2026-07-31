using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

public sealed class PropTextureCatalogTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    [Fact]
    public void Shared_prop_diffuse_is_returned_once_with_all_skin_placements()
    {
        byte[] wolf = Skin("ASSETS/Characters/Shared/Fur.tex");
        byte[] dragon = Skin("assets/characters/shared/fur.tex");
        int reads = 0;

        var result = PropTextureCatalog.Discover(new[]
        {
            new PropSkinUsage("data/Characters/Wolf/Skins/Skin0.bin", 2),
            new PropSkinUsage("characters/wolf/skins/skin0", 3),
            new PropSkinUsage("Characters/Dragon/Skins/Skin0", 4),
        }, skin =>
        {
            reads++;
            return skin.Contains("wolf", StringComparison.OrdinalIgnoreCase) ? wolf : dragon;
        }, _ => null);

        var texture = Assert.Single(result);
        Assert.Equal("ASSETS/Characters/Shared/Fur.tex", texture.AssetPath);
        Assert.Equal(9, texture.Placements);
        Assert.Equal(2, texture.Skins);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Missing_malformed_and_non_texture_skins_are_ignored()
    {
        var result = PropTextureCatalog.Discover(new[]
        {
            new PropSkinUsage("Characters/Missing/Skins/Skin0", 1),
            new PropSkinUsage("Characters/Broken/Skins/Skin0", 1),
            new PropSkinUsage("Characters/NoTexture/Skins/Skin0", 1),
            new PropSkinUsage("", 50),
        }, skin => skin switch
        {
            "Characters/Broken/Skins/Skin0" => new byte[] { 1, 2, 3 },
            "Characters/NoTexture/Skins/Skin0" => Skin("ASSETS/not-a-texture.dds"),
            _ => null,
        }, _ => null);

        Assert.Empty(result);
    }

    [Fact]
    public void Material_override_diffuse_is_discovered_but_mask_data_is_not()
    {
        byte[] skin = Skin("ASSETS/Characters/Test/Base.tex", "ASSETS/Characters/Test/Horns.tex");

        var result = PropTextureCatalog.Discover(
            new[] { new PropSkinUsage("Characters/Test/Skins/Skin0", 6) },
            _ => skin,
            hash => hash == 0x1000u ? "StaticMaterialDef" : null);

        Assert.Equal(new[] { "ASSETS/Characters/Test/Base.tex", "ASSETS/Characters/Test/Horns.tex" },
            result.Select(item => item.AssetPath).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(result, item => Assert.Equal(6, item.Placements));
        Assert.DoesNotContain(result, item => item.AssetPath.Contains("Mask", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Combined_map_and_prop_row_explains_both_usage_counts()
    {
        var row = new RecolorTargetViewModel
        {
            Target = new RecolorTarget(1, "ASSETS/shared.tex"),
            Name = "shared.tex",
            Folder = "ASSETS",
            Kind = RecolorTargetKind.MapAndPropDiffuse,
            MapUses = 2,
            PropUses = 5,
        };

        Assert.Equal(7, row.UsedBy);
        Assert.Equal("MAP+PROP", row.KindBadge);
        Assert.Contains("2 map materials", row.Subtitle);
        Assert.Contains("5 mob/prop placements", row.Subtitle);
    }

    private static byte[] Skin(string texture, string? overrideTexture = null)
    {
        const uint materialHash = 0x2000u;
        var skinProperties = new List<BinTreeProperty>
        {
            new BinTreeString(H("simpleSkin"), "ASSETS/Characters/Test/Test.skn"),
            new BinTreeString(H("texture"), texture),
            new BinTreeString(H("glossTexture"), "ASSETS/Characters/Test/Test_Mask.tex"),
        };
        var objects = new List<BinTreeObject>();
        if (overrideTexture is not null)
        {
            skinProperties.Add(new BinTreeContainer(H("materialOverride"), BinPropertyType.Struct,
                new BinTreeProperty[]
                {
                    new BinTreeStruct(0, H("SkinCharacterDataProperties_CharacterMaterialOverride"),
                        new BinTreeProperty[]
                        {
                            new BinTreeString(H("submesh"), "Horns"),
                            new BinTreeObjectLink(H("material"), materialHash),
                        }),
                }));
            objects.Add(new BinTreeObject(materialHash, 0x1000u, new BinTreeProperty[]
            {
                new BinTreeString(H("name"), "HornsMaterial"),
                new BinTreeContainer(H("samplerValues"), BinPropertyType.Struct,
                    new BinTreeProperty[]
                    {
                        new BinTreeStruct(0, H("StaticMaterialShaderSamplerDef"), new BinTreeProperty[]
                        {
                            new BinTreeString(H("TextureName"), "Diffuse_Texture"),
                            new BinTreeString(H("texturePath"), overrideTexture),
                        }),
                        new BinTreeStruct(0, H("StaticMaterialShaderSamplerDef"), new BinTreeProperty[]
                        {
                            new BinTreeString(H("TextureName"), "Mask_Texture"),
                            new BinTreeString(H("texturePath"), "ASSETS/Characters/Test/Horns_Mask.tex"),
                        }),
                    }),
            }));
        }
        var skinMesh = new BinTreeEmbedded(H("skinMeshProperties"), H("SkinMeshDataProperties"), skinProperties);
        objects.Insert(0, new BinTreeObject(H("Characters/Test/Skins/Skin0"), H("CharacterSkinDataProperties"),
            new BinTreeProperty[] { skinMesh }));
        var tree = new BinTree(objects, Array.Empty<string>());
        using var output = new MemoryStream();
        tree.Write(output);
        return output.ToArray();
    }
}
