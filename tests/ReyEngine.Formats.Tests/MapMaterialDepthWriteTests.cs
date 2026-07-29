using System.IO;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// <para>M279. <see cref="MaterialProfile.DepthWrite"/> is now the single value BOTH map viewports
/// switch on, so what a pass has to author to end up in the transparent bucket is worth pinning.</para>
///
/// <para>The GL viewport has always used it - <c>MainWindowViewModel.ToSubmeshMaterial</c> turns
/// RenderMode into an AlphaMode and <c>ViewportMeshRenderer</c> draws AlphaMode &gt;= 2 in a second pass
/// with <c>DepthMask(false)</c>, and "AlphaMode &gt;= 2" is exactly "!DepthWrite". The D3D11 viewport did
/// not: <c>Dx11SceneBuilder.Commit</c> gave every slice the depth mask and let the pipeline sort reorder
/// it, so a decal stamped depth at its own plane and DEPTH-REJECTED the ground it was supposed to
/// composite over. Measured on Map453/jade_container with an isolated decal over its own paving, as the
/// mean per-channel distance from the ideal composite over the decal's partially transparent margin:
/// 33.2 of 255 before, 6.2 after; on new_stone_road, 70.7 before and 0.5 after.</para>
///
/// <para>These tests are on the INPUT side of that fix, because the output side cannot be reached
/// without a D3D11 device and a game install. They are not a restatement of the one-line DepthWrite
/// property: each one serialises a real .materials.bin, parses it with the real reader and runs the real
/// <c>MaterialProfiles.ClassifyRenderMode</c> rules over it. Break any of those rules and the D3D11 map
/// viewport silently goes back to stamping depth on 17% of its slices - 1,526 of the 9,036 materials
/// censused across Map453/Map12/Map11/Map22.</para>
/// </summary>
public class MapMaterialDepthWriteTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private const uint StaticMaterialDefHash = 0x1000u;
    private const uint TechniqueHash = 0x1001u;
    private const uint PassHash = 0x1002u;
    private const uint ParamHash = 0x1003u;
    private const uint ShaderLinkHash = 0x2000u;

    private static string? Resolve(uint h) => h switch
    {
        StaticMaterialDefHash => "StaticMaterialDef",
        ShaderLinkHash => "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest",
        _ => null,
    };

    /// <summary>One StaticMaterialDef whose first technique's first pass carries the render state, which is
    /// where Riot actually puts it - the class hash is only ever "StaticMaterialDef".</summary>
    private static byte[] Bin(string name, bool blendEnable, float? alphaTestValue, bool linkShader)
    {
        var passProps = new List<BinTreeProperty> { new BinTreeBool(H("blendEnable"), blendEnable) };
        if (linkShader) passProps.Add(new BinTreeObjectLink(H("shader"), ShaderLinkHash));

        var objProps = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), name),
            new BinTreeContainer(H("techniques"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, TechniqueHash, new BinTreeProperty[]
                {
                    new BinTreeContainer(H("passes"), BinPropertyType.Struct, new BinTreeProperty[]
                    {
                        new BinTreeStruct(0, PassHash, passProps.ToArray()),
                    }),
                }),
            }),
        };

        // paramValues is where AlphaTestValue lives. Its PRESENCE is what marks a material alpha-tested;
        // its MAGNITUDE is what decides whether the surface still wants its soft gradient composited.
        if (alphaTestValue is { } cut)
            objProps.Add(new BinTreeContainer(H("paramValues"), BinPropertyType.Struct, new BinTreeProperty[]
            {
                new BinTreeStruct(0, ParamHash, new BinTreeProperty[]
                {
                    new BinTreeString(H("name"), "AlphaTestValue"),
                    new BinTreeF32(H("value"), cut),
                }),
            }));

        var tree = new BinTree(new[] { new BinTreeObject(0xC0DEu, StaticMaterialDefHash, objProps.ToArray()) },
            Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static MaterialProfile Profile(string name, bool blendEnable, float? alphaTestValue, bool linkShader = true)
    {
        var doc = MaterialDocument.Parse(Bin(name, blendEnable, alphaTestValue, linkShader), Resolve);
        var b = Assert.Single(doc.Materials);
        Assert.Equal(MaterialSourceKind.MapMaterials, doc.Kind);
        return b.Profile;
    }

    /// <summary>Map453's base_chasm1 decal, reproduced field for field: blendEnable with a 0.005 alpha
    /// floor. The floor rejects fully transparent texels; the 62.5% of the texture that is partially
    /// transparent still has to composite, which is what makes this the no-depth-write case.</summary>
    [Fact]
    public void A_blended_pass_with_a_tiny_alpha_floor_is_a_soft_decal_and_must_not_write_depth()
    {
        var p = Profile("base_chasm1_decalVersion3_no_shadow", blendEnable: true, alphaTestValue: 0.005f);
        Assert.Equal(MaterialRenderMode.TransparentCutout, p.RenderMode);
        Assert.False(p.DepthWrite,
            "a soft decal that writes depth rejects the ground behind its own transparent margin");
        Assert.True(p.AlphaCutout, "the shader's discard still runs - the cutout is not a blend state");
    }

    /// <summary>The counter-case, and the reason this cannot just be "blendEnable means transparent".
    /// Foliage authors blendEnable too, with a real cutoff around 0.3. Sweeping it into the no-depth-write
    /// pass would make every leaf card ghost through every other one.</summary>
    [Fact]
    public void A_blended_pass_with_a_real_alpha_cutoff_is_foliage_and_must_keep_writing_depth()
    {
        var p = Profile("Jade_Foliage_Leaves_Mat", blendEnable: true, alphaTestValue: 0.3f);
        Assert.Equal(MaterialRenderMode.Cutout, p.RenderMode);
        Assert.True(p.DepthWrite, "a hard cutout is opaque in the depth buffer");
    }

    /// <summary>Glass/water: blend with no alpha test at all.</summary>
    [Fact]
    public void A_blended_pass_with_no_alpha_test_is_transparent_and_must_not_write_depth()
    {
        var p = Profile("Jade_LakeMask_AA_Mat", blendEnable: true, alphaTestValue: null, linkShader: false);
        Assert.Equal(MaterialRenderMode.Transparent, p.RenderMode);
        Assert.False(p.DepthWrite);
    }

    /// <summary>The 43% majority. If this ever flipped, the transparent tail would swallow the whole map
    /// and the depth buffer would stop resolving anything - so it is worth asserting explicitly rather
    /// than assuming the default is safe.</summary>
    [Fact]
    public void An_unblended_pass_with_no_alpha_test_is_opaque_and_writes_depth()
    {
        var p = Profile("Jade_GroundBase_AA_MAT", blendEnable: false, alphaTestValue: null, linkShader: false);
        Assert.Equal(MaterialRenderMode.Opaque, p.RenderMode);
        Assert.True(p.DepthWrite);
        Assert.False(p.AlphaCutout, "solid ground must not discard - that was the latent over-cut of M34");
    }
}
