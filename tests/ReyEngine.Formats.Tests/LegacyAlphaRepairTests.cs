using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M528: repairing a map that was ALREADY ported with the alpha test on everything.
///
/// <para>Fixing the porter is the right place for the rule, but a map ported before the fix carries the
/// damage in its materials.bin, and re-porting is not always what someone wants to do to a project they
/// have since edited by hand. This applies the same measured rule to an existing document.</para>
///
/// <para>The tests below are mostly about what it must NOT do: a repair that fires on a texture it
/// could not read, or on a genuine cutout, is a guess wearing a measurement's clothes.</para>
/// </summary>
public sealed class LegacyAlphaRepairTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>A one-material bin whose first pass points at <paramref name="shader"/>.</summary>
    private static MaterialDocument Document(string shader, string texture = "assets/ground.tex")
    {
        var pass = new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
        {
            new BinTreeObjectLink(H("shader"), H(shader)),
        });
        var technique = new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Default"),
            new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[] { pass }),
        });

        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), "Ground"),
            new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[] { technique }),
        };
        if (texture.Length > 0)
            props.Add(new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded,
                new BinTreeProperty[]
                {
                    new BinTreeEmbedded(0, 0x0904b150, new BinTreeProperty[]
                    {
                        new BinTreeString(H("TextureName"), "DiffuseTexture"),
                        new BinTreeString(H("texturePath"), texture),
                    }),
                }));

        using var ms = new MemoryStream();
        new BinTree(
            new[] { new BinTreeObject(H("Ground"), H("StaticMaterialDef"), props) },
            Array.Empty<string>()).Write(ms);

        var doc = MaterialDocument.Parse(ms.ToArray(), Name);
        Assert.NotNull(doc);
        return doc!;
    }

    /// <summary>The host's hash dictionary. The SHADER PATHS matter as much as the field names: the
    /// pass stores an objlink, so without them RenderShader reads back as a bare hash and the repair
    /// cannot recognise what it is looking at.</summary>
    private static string? Name(uint hash)
    {
        foreach (string n in new[]
        {
            "StaticMaterialDef", "name", "techniques", "passes", "shader", "samplerValues",
            "TextureName", "texturePath", "StaticMaterialTechniqueDef", "StaticMaterialPassDef",
            LegacyAlphaRepair.AlphaTestedShader, LegacyAlphaRepair.SolidShader,
            "Shaders/StaticMesh/VertexDeform", "Shaders/StaticMesh/4TextureBlend_WorldProjected",
        })
            if (H(n) == hash) return n;
        return null;
    }

    private static Func<string, (LegacyAlphaKind, double)?> Always(LegacyAlphaKind kind, double discarded = 0.5)
        => _ => (kind, discarded);

    [Fact]
    public void AGradientAlphaMovesOffTheAlphaTestedShader()
    {
        // The case that ate the ground: alpha holds gloss, so the test removes half the surface.
        var doc = Document(LegacyAlphaRepair.AlphaTestedShader);
        var result = LegacyAlphaRepair.Repair(doc, Always(LegacyAlphaKind.Gradient, 0.5));

        Assert.Equal(1, result.Changed);
        Assert.Equal(LegacyAlphaRepair.SolidShader, doc.Materials[0].RenderShader);
        Assert.Contains("gradient", result.Rows[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARealCutoutIsLeftAlone()
    {
        var doc = Document(LegacyAlphaRepair.AlphaTestedShader);
        var result = LegacyAlphaRepair.Repair(doc, Always(LegacyAlphaKind.Cutout));

        Assert.Equal(0, result.Changed);
        Assert.Equal(LegacyAlphaRepair.AlphaTestedShader, doc.Materials[0].RenderShader);
    }

    [Fact]
    public void AnUnreadableTextureIsLeftAloneRatherThanGuessedAt()
    {
        // Failing towards "change nothing" is the whole point: the repair's authority comes from having
        // measured the texture, so with no measurement it has none.
        var doc = Document(LegacyAlphaRepair.AlphaTestedShader);
        var result = LegacyAlphaRepair.Repair(doc, _ => null);

        Assert.Equal(0, result.Changed);
        Assert.Equal(LegacyAlphaRepair.AlphaTestedShader, doc.Materials[0].RenderShader);
        Assert.Contains("could not be read", result.Rows[0].Reason);
    }

    [Fact]
    public void AMaterialWithNoDiffuseIsLeftAlone()
    {
        var doc = Document(LegacyAlphaRepair.AlphaTestedShader, texture: "");
        var result = LegacyAlphaRepair.Repair(doc, Always(LegacyAlphaKind.Gradient));

        Assert.Equal(0, result.Changed);
        Assert.Contains("no diffuse texture", result.Rows[0].Reason);
    }

    [Fact]
    public void MaterialsOnOtherShadersAreNotTouchedAtAll()
    {
        // The repair only ever moves things OFF the alpha-tested shader. A material already on the solid
        // one, or on a grass or terrain shader, is none of its business.
        foreach (string shader in new[]
                 {
                     LegacyAlphaRepair.SolidShader,
                     "Shaders/StaticMesh/VertexDeform",
                     "Shaders/StaticMesh/4TextureBlend_WorldProjected",
                 })
        {
            var doc = Document(shader);
            var result = LegacyAlphaRepair.Repair(doc, Always(LegacyAlphaKind.Gradient));
            Assert.Empty(result.Rows);
            Assert.Equal(shader, doc.Materials[0].RenderShader);
        }
    }

    [Fact]
    public void TheChangeSurvivesTheRoundTrip()
    {
        // A shader swap that only lives in memory would repair nothing. This is the same objlink the
        // game resolves, so it has to be in the bytes.
        var doc = Document(LegacyAlphaRepair.AlphaTestedShader);
        LegacyAlphaRepair.Repair(doc, Always(LegacyAlphaKind.Gradient));

        var reloaded = MaterialDocument.Parse(doc.Serialize(), Name);
        Assert.NotNull(reloaded);
        Assert.Equal(LegacyAlphaRepair.SolidShader, reloaded!.Materials[0].RenderShader);
    }
}
