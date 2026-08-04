using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

public class MaterialShaderConverterTests
{
    private const string SourceShader = "Shaders/StaticMesh/SRX_Blend_Master";
    private const string TargetShader = "Shaders/StaticMesh/DefaultEnv_Flat";
    private const string OtherShader = "Shaders/StaticMesh/Indicator_Faelights";
    private const uint MaterialClass = 0xad4b8ac0;
    private const uint SamplerClass = 0x0904b150;
    private const uint ParamClass = 0xde480eef;
    private const uint SwitchClass = 0x7f35ca4d;
    private const uint TechniqueClass = 0x060a4413;
    private const uint PassClass = 0x8537d0c2;

    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    private static string? Resolve(uint hash) => hash switch
    {
        MaterialClass => "StaticMaterialDef",
        var h when h == H(SourceShader) => SourceShader,
        var h when h == H(TargetShader) => TargetShader,
        var h when h == H(OtherShader) => OtherShader,
        _ => null,
    };

    [Fact]
    public void Bulk_conversion_preserves_authored_values_and_maps_compatible_texture_names()
    {
        var doc = MaterialDocument.Parse(BuildMaterials(), Resolve);
        var target = new LeagueShaderDef(TargetShader, "StaticMesh",
            new List<ShaderTextureDef>
            {
                new("Diffuse_Texture", "ASSETS/Defaults/white.tex"),
                new("Normal_Texture", "ASSETS/Defaults/flat_normal.tex"),
                new("Mask_Texture", "ASSETS/Defaults/white.tex"),
                new("Detail_Texture", "ASSETS/Defaults/detail.tex"),
            },
            new List<ShaderParamDef> { new("TintColor", 1, 1, 1, 1) },
            new List<string> { "MULTIPLY_ALPHA" });

        var result = MaterialShaderConverter.Convert(doc.Materials,
            SourceShader, TargetShader, target);

        Assert.Equal(2, result.MatchedMaterials);
        Assert.Equal(2, result.ConvertedMaterials);
        Assert.Equal(2, result.ExistingCompatibleSamplers);
        Assert.Equal(6, result.AddedSamplers);
        Assert.Equal(4, result.PreservedTexturePaths);

        foreach (var material in doc.Materials.Where(m => m.Name.StartsWith("source", StringComparison.Ordinal)))
        {
            Assert.Equal(TargetShader, material.RenderShader);
            Assert.Equal("ASSETS/Map/authored_diffuse.tex", Slot(material, "DiffuseTexture").Path);
            Assert.Equal("ASSETS/Map/authored_diffuse.tex", Slot(material, "Diffuse_Texture").Path);
            Assert.Equal("ASSETS/Map/authored_normal.tex", Slot(material, "NormalMap").Path);
            Assert.Equal("ASSETS/Map/authored_normal.tex", Slot(material, "Normal_Texture").Path);
            Assert.Equal("ASSETS/Map/authored_mask.tex", Slot(material, "Mask_Texture").Path);
            Assert.Equal("ASSETS/Defaults/detail.tex", Slot(material, "Detail_Texture").Path);
            Assert.Equal("0.2, 0.4, 0.6, 0.8", Assert.Single(material.Parameters).CurrentText);
            Assert.True(material.Switches["MULTIPLY_ALPHA"]);
            Assert.Equal("1", material.Macros[MaterialBinding.MacroNoBakedLighting]);
            Assert.False(material.GetPassBool("cullEnable", true));
            Assert.True(material.GetPassBool("blendEnable", false));
        }

        var untouched = doc.Materials.Single(m => m.Name == "other");
        Assert.Equal(OtherShader, untouched.RenderShader);
        Assert.Equal(3, untouched.Slots.Count);

        // Prove the live tree writes the converted shader and preserved values back into a real bin.
        var reparsed = MaterialDocument.Parse(doc.Serialize(), Resolve);
        var saved = reparsed.Materials.Single(m => m.Name == "source_a");
        Assert.Equal(TargetShader, saved.RenderShader);
        Assert.Equal("ASSETS/Map/authored_diffuse.tex", Slot(saved, "Diffuse_Texture").Path);
        Assert.Equal("0.2, 0.4, 0.6, 0.8", Assert.Single(saved.Parameters).CurrentText);
        Assert.True(saved.Switches["MULTIPLY_ALPHA"]);
        Assert.Equal("1", saved.Macros[MaterialBinding.MacroNoBakedLighting]);
    }

    [Fact]
    public void Texture_role_matching_does_not_confuse_color_masks_with_diffuse_textures()
    {
        var doc = MaterialDocument.Parse(BuildMaterials(), Resolve);
        var material = doc.Materials.First();

        Assert.Equal("ASSETS/Map/authored_diffuse.tex",
            MaterialShaderConverter.FindCompatibleTexturePath(material.Slots, "BaseColor_Texture"));
        Assert.Equal("ASSETS/Map/authored_mask.tex",
            MaterialShaderConverter.FindCompatibleTexturePath(material.Slots, "Color_Mask"));
        Assert.Null(MaterialShaderConverter.FindCompatibleTexturePath(material.Slots, "Noise_Texture"));
        Assert.Null(MaterialShaderConverter.FindCompatibleTexturePath(material.Slots, "BAKED_NORMAL_TEXTURE"));
        Assert.Null(MaterialShaderConverter.FindCompatibleTexturePath(material.Slots, "EmissionMaskTex"));
    }

    [Fact]
    public void Editor_bulk_replace_is_one_undoable_operation()
    {
        var doc = MaterialDocument.Parse(BuildMaterials(), Resolve);
        var target = new LeagueShaderDef(TargetShader, "StaticMesh",
            new List<ShaderTextureDef> { new("Diffuse_Texture", "ASSETS/Defaults/white.tex") },
            new List<ShaderParamDef>(), new List<string>());
        var undo = new UndoRedoService();
        var editor = new MaterialEditorViewModel { UndoService = undo };
        editor.Load(doc, new WadAssetEntry { Path = "base_srx.materials.bin" });
        editor.SetCatalog(new ShaderCatalog { Shaders = new List<LeagueShaderDef> { target } });
        editor.BulkSourceShader = SourceShader;
        editor.BulkTargetShader = TargetShader;

        editor.BulkReplaceShaderCommand.Execute(null);

        Assert.Equal(2, doc.Materials.Count(m => m.RenderShader == TargetShader));
        Assert.All(doc.Materials.Where(m => m.Name.StartsWith("source", StringComparison.Ordinal)),
            m => Assert.Contains(m.Slots, s => s.SamplerName == "Diffuse_Texture"));
        Assert.Equal("Replace Shader on 2 Materials", undo.UndoName);

        Assert.True(undo.Undo());
        Assert.Equal(2, doc.Materials.Count(m => m.RenderShader == SourceShader));
        Assert.All(doc.Materials.Where(m => m.Name.StartsWith("source", StringComparison.Ordinal)),
            m => Assert.DoesNotContain(m.Slots, s => s.SamplerName == "Diffuse_Texture"));

        Assert.True(undo.Redo());
        Assert.Equal(2, doc.Materials.Count(m => m.RenderShader == TargetShader));
        Assert.All(doc.Materials.Where(m => m.Name.StartsWith("source", StringComparison.Ordinal)),
            m => Assert.Equal("ASSETS/Map/authored_diffuse.tex", Slot(m, "Diffuse_Texture").Path));
    }

    private static TextureSlot Slot(MaterialBinding material, string name) =>
        material.Slots.Single(s => s.SamplerName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static byte[] BuildMaterials()
    {
        var tree = new BinTree(new[]
        {
            Material("source_a", SourceShader),
            Material("source_b", SourceShader),
            Material("other", OtherShader),
        }, Array.Empty<string>());
        using var stream = new MemoryStream();
        tree.Write(stream);
        return stream.ToArray();
    }

    private static BinTreeObject Material(string name, string shader)
    {
        BinTreeProperty Sampler(string sampler, string path) => new BinTreeStruct(0, SamplerClass,
            new BinTreeProperty[]
            {
                new BinTreeString(H("TextureName"), sampler),
                new BinTreeString(H("texturePath"), path),
            });

        var pass = new BinTreeStruct(0, PassClass, new BinTreeProperty[]
        {
            new BinTreeObjectLink(H("shader"), H(shader)),
            new BinTreeBool(H("cullEnable"), false),
            new BinTreeBool(H("blendEnable"), true),
        });
        var technique = new BinTreeStruct(0, TechniqueClass, new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "normal"),
            new BinTreeContainer(H("passes"), BinPropertyType.Struct, new BinTreeProperty[] { pass }),
        });
        var featureSwitch = new BinTreeStruct(0, SwitchClass, new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "MULTIPLY_ALPHA"),
            new BinTreeBool(H("on"), true),
        });
        var macro = new KeyValuePair<BinTreeProperty, BinTreeProperty>(
            new BinTreeString(0, MaterialBinding.MacroNoBakedLighting), new BinTreeString(0, "1"));

        return new BinTreeObject(H(name), MaterialClass, new BinTreeProperty[]
        {
            new BinTreeString(H("name"), name),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Struct,
                new[]
                {
                    Sampler("DiffuseTexture", "ASSETS/Map/authored_diffuse.tex"),
                    Sampler("NormalMap", "ASSETS/Map/authored_normal.tex"),
                    Sampler("Mask_Texture", "ASSETS/Map/authored_mask.tex"),
                }),
            new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Struct,
                new BinTreeProperty[]
                {
                    new BinTreeStruct(0, ParamClass, new BinTreeProperty[]
                    {
                        new BinTreeString(H("name"), "TintColor"),
                        new BinTreeVector4(H("value"), new Vector4(0.2f, 0.4f, 0.6f, 0.8f)),
                    }),
                }),
            new BinTreeContainer(H("switches"), BinPropertyType.Struct,
                new BinTreeProperty[] { featureSwitch }),
            new BinTreeMap(H("shaderMacros"), BinPropertyType.String, BinPropertyType.String,
                new[] { macro }),
            new BinTreeContainer(H("techniques"), BinPropertyType.Struct,
                new BinTreeProperty[] { technique }),
        });
    }
}
