using System.Globalization;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M497: reading ritobin <c>#PROP_text</c> back into a BinTree.
///
/// <para>Most of these are ROUND TRIPS through the real binary writer, because that is the only check that
/// matters for an editor: what comes back out has to be the file that went in. A parser can look correct
/// and still drop an element type, mis-nest a container, or silently zero a float.</para>
/// </summary>
public sealed class RitobinTextReaderTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static byte[] Serialize(BinTree tree)
    {
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    /// <summary>bin -> text -> bin, compared through the same binary writer so only the TEXT step is
    /// under test.</summary>
    private static void AssertRoundTrips(params BinTreeProperty[] properties)
    {
        var tree = new BinTree(
            new[] { new BinTreeObject(H("Test/Object"), H("TestClass"), properties) },
            new[] { "Some/Dependency.bin" });

        string text = RitobinText.Write(tree);
        var back = RitobinTextReader.Read(text, out var errors);

        Assert.True(errors.Count == 0, "parse errors: " + string.Join("; ", errors));
        Assert.NotNull(back);
        Assert.Equal(Serialize(tree), Serialize(back!));
    }

    [Fact]
    public void EveryScalarTypeRoundTrips()
    {
        AssertRoundTrips(
            new BinTreeBool(H("a"), true),
            new BinTreeBitBool(H("b"), false),
            new BinTreeI8(H("c"), -8),
            new BinTreeU8(H("d"), 200),
            new BinTreeI16(H("e"), -300),
            new BinTreeU16(H("f"), 60000),
            new BinTreeI32(H("g"), -100000),
            new BinTreeU32(H("h"), 4000000000),
            new BinTreeI64(H("i"), -5_000_000_000),
            new BinTreeU64(H("j"), 9_000_000_000),
            new BinTreeF32(H("k"), 1.5f),
            new BinTreeString(H("l"), "hello \"world\"\n"),
            new BinTreeHash(H("m"), 0xdeadbeef),
            new BinTreeObjectLink(H("n"), 0x12345678),
            new BinTreeNone(H("o")));
    }

    [Fact]
    public void VectorsMatricesAndColoursRoundTrip()
    {
        AssertRoundTrips(
            new BinTreeVector2(H("v2"), new Vector2(1.25f, -2.5f)),
            new BinTreeVector3(H("v3"), new Vector3(1, 2, 3)),
            new BinTreeVector4(H("v4"), new Vector4(0.1f, 0.2f, 0.3f, 1f)),
            new BinTreeMatrix44(H("m44"), Matrix4x4.CreateTranslation(new Vector3(10, 20, 30))),
            new BinTreeColor(H("rgba"), new LeagueToolkit.Core.Primitives.Color(10, 20, 30, 255)));
    }

    [Fact]
    public void ContainersMapsAndEmbedsRoundTrip()
    {
        AssertRoundTrips(
            new BinTreeContainer(H("paths"), BinPropertyType.String, new BinTreeProperty[]
            {
                new BinTreeString(0, "one"), new BinTreeString(0, "two"),
            }),
            new BinTreeUnorderedContainer(H("samplers"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("SamplerDef"), new BinTreeProperty[]
                {
                    new BinTreeString(H("TextureName"), "DiffuseTexture"),
                    new BinTreeU32(H("addressU"), 1),
                }),
            }),
            new BinTreeMap(H("macros"), BinPropertyType.String, BinPropertyType.String, new[]
            {
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                    new BinTreeString(0, "NO_BAKED_LIGHTING"), new BinTreeString(0, "1")),
            }),
            new BinTreeStruct(H("ptr"), H("PointedClass"), new BinTreeProperty[]
            {
                new BinTreeF32(H("scale"), 0.25f),
            }),
            new BinTreeOptional(H("maybe"), new BinTreeString(0, "present")));
    }

    [Fact]
    public void NestedContainersRoundTrip()
    {
        // A list of lists is where a parser that assumes one level of nesting comes apart.
        AssertRoundTrips(
            new BinTreeContainer(H("groups"), BinPropertyType.Container, new BinTreeProperty[]
            {
                new BinTreeContainer(0, BinPropertyType.U32, new BinTreeProperty[]
                {
                    new BinTreeU32(0, 1), new BinTreeU32(0, 2),
                }),
                new BinTreeContainer(0, BinPropertyType.U32, new BinTreeProperty[]
                {
                    new BinTreeU32(0, 3),
                }),
            }));
    }

    [Fact]
    public void DeeplyNestedEmbedsRoundTrip()
    {
        AssertRoundTrips(
            new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("TechniqueDef"), new BinTreeProperty[]
                {
                    new BinTreeString(H("name"), "normal"),
                    new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, H("PassDef"), new BinTreeProperty[]
                        {
                            new BinTreeObjectLink(H("shader"), H("Shaders/X")),
                            new BinTreeBool(H("blendEnable"), true),
                        }),
                    }),
                }),
            }));
    }

    [Fact]
    public void ResolvedNamesInTheTextHashBackToTheSameValues()
    {
        // The writer prints names when it can. Reading must hash them back to exactly what it started
        // from, or readable output would quietly not round-trip.
        var tree = new BinTree(
            new[] { new BinTreeObject(H("Maps/Thing"), H("StaticMaterialDef"), new BinTreeProperty[]
            {
                new BinTreeObjectLink(H("shader"), H("Shaders/StaticMesh/DefaultEnv_Flat")),
            }) }, Array.Empty<string>());

        string named = RitobinText.Write(tree, h => h switch
        {
            _ when h == H("Maps/Thing") => "Maps/Thing",
            _ when h == H("StaticMaterialDef") => "StaticMaterialDef",
            _ when h == H("shader") => "shader",
            _ when h == H("Shaders/StaticMesh/DefaultEnv_Flat") => "Shaders/StaticMesh/DefaultEnv_Flat",
            _ => null,
        });
        Assert.Contains("\"Shaders/StaticMesh/DefaultEnv_Flat\"", named);

        var back = RitobinTextReader.Read(named, out var errors);
        Assert.Empty(errors);
        Assert.Equal(Serialize(tree), Serialize(back!));
    }

    [Fact]
    public void DependenciesSurvive()
    {
        var tree = new BinTree(
            new[] { new BinTreeObject(H("O"), H("C"), Array.Empty<BinTreeProperty>()) },
            new[] { "DATA/First.bin", "DATA/Second.bin" });

        var back = RitobinTextReader.Read(RitobinText.Write(tree), out var errors);
        Assert.Empty(errors);
        Assert.Equal(new[] { "DATA/First.bin", "DATA/Second.bin" }, back!.Dependencies);
    }

    [Fact]
    public void FloatsParseInvariantEvenUnderAGermanLocale()
    {
        // Reading "1.5" with a comma-decimal culture would fail and silently yield 0 — the mirror of the
        // writer-side trap, and just as invisible.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var back = RitobinTextReader.Read("""
                #PROP_text
                type: string = "PROP"
                version: u32 = 3
                linked: list[string] = { }
                entries: map[hash,embed] = {
                  0x1 = 0x2 {
                    scale: f32 = 1.5
                    tint: vec4 = { 0.25, 0.5, 0.75, 1 }
                  }
                }
                """, out var errors);

            Assert.Empty(errors);
            var obj = back!.Objects[1u];
            Assert.Equal(1.5f, ((BinTreeF32)obj.Properties[H("scale")]).Value);
            Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 1f), ((BinTreeVector4)obj.Properties[H("tint")]).Value);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        var back = RitobinTextReader.Read("""
            #PROP_text

            # a comment about the file
            type: string = "PROP"
            version: u32 = 3
            linked: list[string] = { }
            entries: map[hash,embed] = {
              # a comment about the entry
              0xaabbccdd = SomeClass {
                value: u32 = 42   # trailing comment
              }
            }
            """, out var errors);

        Assert.Empty(errors);
        Assert.Single(back!.Objects);
        Assert.Equal(42u, ((BinTreeU32)back.Objects[0xaabbccdd].Properties[H("value")]).Value);
    }

    [Fact]
    public void ErrorsCarryLineNumbersAndNothingIsReturned()
    {
        var tree = RitobinTextReader.Read("""
            #PROP_text
            type: string = "PROP"
            version: u32 = 3
            linked: list[string] = { }
            entries: map[hash,embed] = {
              0x1 = C {
                good: u32 = 1
                bad: strng = 2
              }
            }
            """, out var errors);

        Assert.Null(tree);                       // nothing is handed back from a failed parse
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Message.Contains("strng"));
        Assert.Contains(errors, e => e.Line == 8);
    }

    [Fact]
    public void EmptyOrForeignTextIsReportedRatherThanThrowing()
    {
        Assert.Null(RitobinTextReader.Read("", out var a));
        Assert.NotEmpty(a);

        Assert.Null(RitobinTextReader.Read("just some prose", out var b));
        Assert.NotEmpty(b);
    }
}
