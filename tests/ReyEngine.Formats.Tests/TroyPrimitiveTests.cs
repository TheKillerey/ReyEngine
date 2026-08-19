using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M527: <c>*p-type</c> chooses the primitive class.
///
/// <para>This is the field that decides what an emitter IS - a camera-facing billboard, a
/// world-oriented quad, a ray, a mesh, a trail - and it was ignored: every non-mesh emitter got no
/// primitive at all, so 75 of the 441 paired emitters were converted as the wrong kind of thing.</para>
///
/// <para><b>The ArbitraryQuad hazard is the reason these tests are specific.</b> Writing that class
/// for every emitter was a real bug once - the renderer branches on it as
/// <c>right = uArbitraryQuad != 0 ? placedRight : uCamRight</c>, so a converted particle became a plane
/// pinned to one world direction and went edge-on as the camera moved. The lesson was "do not write it
/// unconditionally", not "never write it": gated on <c>*p-type == 1</c> it agrees with Riot 27 times
/// out of 27.</para>
/// </summary>
public sealed class TroyPrimitiveTests
{
    private const string Corpus = @"K:\LeagueSandbox\League_Sandbox_Client\DATA\Particles";

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static BinTreeProperty? Prop(IEnumerable<BinTreeProperty> props, string name)
        => props.FirstOrDefault(p => p.NameHash == H(name));

    private static IEnumerable<BinTreeProperty> Kids(BinTreeProperty? p) => p switch
    {
        BinTreeEmbedded e => e.Properties.Values,
        BinTreeStruct s => s.Properties.Values,
        BinTreeContainer c => c.Elements,
        _ => Array.Empty<BinTreeProperty>(),
    };

    /// <summary>A one-emitter legacy file whose <c>*p-type</c> holds <paramref name="type"/>, built by
    /// hand so the table can be exercised without needing a corpus file per value.</summary>
    private static TroyBinFile Legacy(int? type, string? mesh = null)
    {
        var entries = new List<(string Field, string Value)>();
        if (type is { } t) entries.Add(("*p-type", t.ToString()));
        if (mesh is not null) entries.Add(("*p-mesh", mesh));

        // strings first so their offsets are known, then the body referencing them
        var block = new List<byte>();
        var offsets = new Dictionary<string, int>();
        void Str(string v)
        {
            if (offsets.ContainsKey(v)) return;
            offsets[v] = block.Count;
            block.AddRange(System.Text.Encoding.ASCII.GetBytes(v));
            block.Add(0);
        }
        Str("emitter");
        foreach (var (_, v) in entries) Str(v);

        var keys = new List<(uint Key, int Offset)>
        {
            (TroyHash.SystemKey(TroyFields.GroupPart(1)), offsets["emitter"]),
        };
        foreach (var (field, value) in entries)
            keys.Add((TroyHash.FieldKey("emitter", field), offsets[value]));
        keys.Sort((a, b) => a.Key.CompareTo(b.Key));   // entries are strictly ascending by key

        var body = new List<byte> { 0x00, 0x10 };      // mask: bit 12 only (the string section)
        body.AddRange(BitConverter.GetBytes((ushort)keys.Count));
        foreach (var (k, _) in keys) body.AddRange(BitConverter.GetBytes(k));
        foreach (var (_, o) in keys) body.AddRange(BitConverter.GetBytes((ushort)o));

        var file = new List<byte> { 2 };
        file.AddRange(BitConverter.GetBytes((ushort)block.Count));
        file.AddRange(body);
        file.AddRange(block);

        Assert.True(TroyBinFile.TryParse(file.ToArray(), out var troy, out var error), error);
        Assert.True(troy!.HasDecodedBody);
        return troy;
    }

    private static BinTreeStruct? PrimitiveOf(TroyBinFile troy)
    {
        var result = TroyBinConverter.Convert(troy, "T", "Particles/T");
        BinTree tree;
        using (var ms = new MemoryStream(result.BinBytes)) tree = new BinTree(ms);
        var emitter = Kids(tree.Objects.Values.Single().Properties[H("complexEmitterDefinitionData")]).Single();
        return Prop(Kids(emitter), "primitive") as BinTreeStruct;
    }

    [Theory]
    [InlineData(1, "VfxPrimitiveArbitraryQuad")]
    [InlineData(2, "VfxPrimitiveRay")]
    [InlineData(4, "VfxPrimitiveCameraTrail")]
    [InlineData(7, "VfxPrimitivePlanarProjection")]
    public void TheTypeSelectsTheClass(int type, string expected)
    {
        var prim = PrimitiveOf(Legacy(type));
        Assert.NotNull(prim);
        Assert.Equal(H(expected), prim!.ClassHash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void TypeZeroAndAbsentBothMeanTheCameraFacingBillboard(int? type)
    {
        // The billboard is the ABSENCE of the property, not a class. 157 paired emitters land here and
        // Riot writes nothing on every one of them.
        Assert.Null(PrimitiveOf(Legacy(type)));
    }

    [Fact]
    public void ArbitraryQuadIsWrittenOnlyForTypeOne()
    {
        // The regression guard. Writing this class unconditionally pinned every converted particle to a
        // world direction; it is correct for type 1 and for nothing else.
        Assert.Equal(H("VfxPrimitiveArbitraryQuad"), PrimitiveOf(Legacy(1))!.ClassHash);
        foreach (int other in new[] { 0, 2, 3, 4, 7 })
        {
            var prim = PrimitiveOf(Legacy(other));
            if (prim is null) continue;
            Assert.NotEqual(H("VfxPrimitiveArbitraryQuad"), prim.ClassHash);
        }
        Assert.Null(PrimitiveOf(Legacy(null)));
    }

    [Fact]
    public void TheEmptyClassesAreWrittenEmpty()
    {
        // ArbitraryQuad and Ray carry no properties in any of the 42,382 shipped instances, so an empty
        // struct is the shipped shape rather than a stub.
        Assert.Empty(PrimitiveOf(Legacy(1))!.Properties);
        Assert.Empty(PrimitiveOf(Legacy(2))!.Properties);
    }

    [Fact]
    public void AMeshPathAloneStillMakesAMeshParticle()
    {
        // p-type 3 and a named mesh agree on 207 of 209 paired emitters, so either signal is enough -
        // and the mesh emitters that predate this table must not lose their primitive.
        var prim = PrimitiveOf(Legacy(null, "DATA/Particles/rock.scb"));
        Assert.NotNull(prim);
        Assert.Equal(H("VfxPrimitiveMesh"), prim!.ClassHash);

        var mMesh = Prop(prim.Properties.Values, "mMesh");
        Assert.NotNull(mMesh);
        Assert.Equal("ASSETS/Legacy/Particles/rock.scb",
            ((BinTreeString)Prop(Kids(mMesh), "mSimpleMeshName")!).Value);
    }

    [Fact]
    public void TheParityHarnessComparesTheClassItself()
    {
        // Without this the harness could not see a wrong primitive at all: two of the five classes are
        // EMPTY, so a struct compared only through its leaves scores a perfect match however wrong the
        // class is. Adding the check turned up 5,803 further comparisons on the paired corpus.
        static BinTreeObject System(string cls)
        {
            var emitter = new BinTreeStruct(0, H("VfxEmitterDefinitionData"), new BinTreeProperty[]
            {
                new BinTreeString(H("emitterName"), "e"),
                new BinTreeStruct(H("primitive"), H(cls), Array.Empty<BinTreeProperty>()),
            });
            return new BinTreeObject(H("s"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
            {
                new BinTreeString(H("particleName"), "s"),
                new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct,
                    new BinTreeProperty[] { emitter }),
            });
        }

        // the resolver matters: without it the path is the hex hash rather than "primitive"
        static string? Name(uint h) => h == H("primitive") ? "primitive" : null;

        var same = new TroyConversionParity.Accumulator();
        TroyConversionParity.Compare(System("VfxPrimitiveRay"), System("VfxPrimitiveRay"), same, Name);
        Assert.Equal(1, same.Build().Field("primitive.@class")!.Agreed);

        var differs = new TroyConversionParity.Accumulator();
        TroyConversionParity.Compare(System("VfxPrimitiveRay"), System("VfxPrimitiveArbitraryQuad"), differs, Name);
        var field = differs.Build().Field("primitive.@class")!;
        Assert.Equal(1, field.Differed);
    }
}
