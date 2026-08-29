using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M596: a ported decal has to blend the way Riot's decals blend, and the source factor is the half that
/// goes wrong silently.
///
/// <para>Riot's own decals in Map453's <c>jade_container.materials.bin</c> - the control, sitting in the
/// same file as the ported ones - author <c>srcColorBlendFactor</c> / <c>srcAlphaBlendFactor</c> = 6
/// (SourceAlpha) with <c>dst</c> = 7 (OneMinusSourceAlpha), 16 of 16 that blend at all. That is ordinary
/// alpha blending.</para>
///
/// <para>Leaving the source factor OUT is not "unset": the format reads absence as One, which is
/// premultiplied blending. Run against a texture that is not premultiplied, the transparent part of a
/// decal stops being transparent and ADDS its colour to the ground - so the decal plane shows up as a
/// flat hard-edged rectangle of colour instead of the art it carries. That is precisely how it was
/// reported: two pink rectangles lying on the ground of a ported map, where the unmodified map has
/// none.</para>
///
/// <para>The trap is that absence is the RIGHT answer elsewhere - Riot expresses One by omission and
/// never writes <c>srcColorBlendFactor = 1</c> in 9,800 shipped passes - so nothing about a missing field
/// looks wrong on inspection. Only the comparison against Riot's decals says which it should be.</para>
/// </summary>
public sealed class PortedDecalBlendStateTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    /// <summary>Riot's blend-factor enum: 6 = SourceAlpha, 7 = OneMinusSourceAlpha.</summary>
    private const int SourceAlpha = 6;
    private const int OneMinusSourceAlpha = 7;

    private static byte[] EmptyBin() => Write(
        new BinTreeObject(0x100u, H("MapSunProperties"), Array.Empty<BinTreeProperty>()));

    private static byte[] Write(params BinTreeObject[] objects)
    {
        using var stream = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(stream);
        return stream.ToArray();
    }

    private static LeagueShaderDef Shader() => new(
        "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest", "StaticMesh",
        new() { new ShaderTextureDef("DiffuseTexture", "ASSETS/Shared/Materials/white.tex") }, new(), new());

    /// <summary>Every pass property of the one material the bin holds.</summary>
    private static Dictionary<string, BinTreeProperty> Pass(byte[] bin, string material)
    {
        var obj = SafeBinTree.Parse(bin).Objects[H(material)];
        var techniques = Assert.IsAssignableFrom<BinTreeContainer>(obj.Properties[H("techniques")]);
        var technique = Assert.IsAssignableFrom<BinTreeStruct>(Assert.Single(techniques.Elements));
        var passes = Assert.IsAssignableFrom<BinTreeContainer>(technique.Properties[H("passes")]);
        var pass = Assert.IsAssignableFrom<BinTreeStruct>(Assert.Single(passes.Elements));

        var byName = new Dictionary<string, BinTreeProperty>(StringComparer.Ordinal);
        foreach (string field in new[]
                 {
                     "blendEnable", "srcColorBlendFactor", "srcAlphaBlendFactor",
                     "dstColorBlendFactor", "dstAlphaBlendFactor",
                 })
            if (pass.Properties.TryGetValue(H(field), out var value)) byName[field] = value;
        return byName;
    }

    private static byte[] Decal(string name = "LegacyPort/map1/Decal_test") =>
        MapMaterialFactory.CreateFromShader(EmptyBin(), name, Shader(), out string? error,
            new Dictionary<string, string> { ["__diffuse__"] = "assets/maps/legacy/decal.dds" },
            blendEnable: true, sourceBlendFactor: SourceAlpha, destinationBlendFactor: OneMinusSourceAlpha)
        ?? throw new Xunit.Sdk.XunitException($"the factory refused to build a decal: {error}");

    [Fact]
    public void APortedDecalAuthorsSourceAlphaRatherThanLeavingItAbsent()
    {
        var pass = Pass(Decal(), "LegacyPort/map1/Decal_test");

        Assert.True(pass.ContainsKey("srcColorBlendFactor"),
            "srcColorBlendFactor is missing, which the format reads as One - the decal will add its "
            + "colour to the ground instead of blending against it");
        Assert.Equal((uint)SourceAlpha, Assert.IsType<BinTreeU32>(pass["srcColorBlendFactor"]).Value);
    }

    /// <summary>M415: the alpha half is not optional - a pass carrying only the colour factor is a blend
    /// equation Riot never ships, and it rendered the ported terrain invisible.</summary>
    [Fact]
    public void BothHalvesOfTheBlendEquationAreAuthored()
    {
        var pass = Pass(Decal(), "LegacyPort/map1/Decal_test");

        Assert.Equal((uint)SourceAlpha, Assert.IsType<BinTreeU32>(pass["srcColorBlendFactor"]).Value);
        Assert.Equal((uint)SourceAlpha, Assert.IsType<BinTreeU32>(pass["srcAlphaBlendFactor"]).Value);
        Assert.Equal((uint)OneMinusSourceAlpha, Assert.IsType<BinTreeU32>(pass["dstColorBlendFactor"]).Value);
        Assert.Equal((uint)OneMinusSourceAlpha, Assert.IsType<BinTreeU32>(pass["dstAlphaBlendFactor"]).Value);
        Assert.True(Assert.IsType<BinTreeBool>(pass["blendEnable"]).Value);
    }

    /// <summary>The ported decal must match Riot's own decals field for field, because they are drawn by
    /// the same shader over the same ground in the same bin.</summary>
    [Fact]
    public void APortedDecalBlendsExactlyAsRiotsOwnDecalsDo()
    {
        var pass = Pass(Decal(), "LegacyPort/map1/Decal_test");

        Assert.Equal(
            new (string, uint)[]
            {
                ("srcColorBlendFactor", SourceAlpha), ("srcAlphaBlendFactor", SourceAlpha),
                ("dstColorBlendFactor", OneMinusSourceAlpha), ("dstAlphaBlendFactor", OneMinusSourceAlpha),
            },
            new[] { "srcColorBlendFactor", "srcAlphaBlendFactor", "dstColorBlendFactor", "dstAlphaBlendFactor" }
                .Select(f => (f, Assert.IsType<BinTreeU32>(pass[f]).Value)).ToArray());
    }

    /// <summary>The control: a non-decal role asks for no blending, and must then author no blend factors
    /// at all rather than an explicit One. Riot writes 0 of 9,800 passes with srcColorBlendFactor = 1.</summary>
    [Fact]
    public void AnOrdinarySurfaceStillAuthorsNoBlendFactorsAtAll()
    {
        var plain = MapMaterialFactory.CreateFromShader(EmptyBin(), "LegacyPort/map1/Normal_plain", Shader(),
            out string? error, new Dictionary<string, string> { ["__diffuse__"] = "assets/maps/legacy/rock.dds" },
            blendEnable: false, sourceBlendFactor: null, destinationBlendFactor: null);

        Assert.Null(error);
        var pass = Pass(plain!, "LegacyPort/map1/Normal_plain");
        Assert.False(pass.ContainsKey("srcColorBlendFactor"));
        Assert.False(pass.ContainsKey("srcAlphaBlendFactor"));
        Assert.False(pass.ContainsKey("dstColorBlendFactor"));
        Assert.False(pass.ContainsKey("dstAlphaBlendFactor"));
        Assert.False(Assert.IsType<BinTreeBool>(pass["blendEnable"]).Value);
    }

    /// <summary>Re-porting over an existing material must not quietly inherit the old pass state. The
    /// factory drops and rebuilds the object, so a second port has to produce the same blend equation as
    /// the first - otherwise a re-port silently changes how the map draws.</summary>
    [Fact]
    public void RePortingADecalRebuildsTheSameBlendEquation()
    {
        const string name = "LegacyPort/map1/Decal_test";
        var once = Decal(name);

        var twice = MapMaterialFactory.CreateFromShader(once, name, Shader(), out string? error,
            new Dictionary<string, string> { ["__diffuse__"] = "assets/maps/legacy/decal.dds" },
            replaceExisting: true,
            blendEnable: true, sourceBlendFactor: SourceAlpha, destinationBlendFactor: OneMinusSourceAlpha);

        Assert.Null(error);
        Assert.Equal(Pass(once, name).Keys.OrderBy(k => k, StringComparer.Ordinal),
                     Pass(twice!, name).Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal((uint)SourceAlpha,
            Assert.IsType<BinTreeU32>(Pass(twice!, name)["srcColorBlendFactor"]).Value);
    }
}
