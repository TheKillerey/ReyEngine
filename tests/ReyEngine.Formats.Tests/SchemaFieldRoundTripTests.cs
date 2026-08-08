using System.IO;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Particles;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// <para>M371: the end-to-end check M370 shipped without. Adding a schema field is a WRITE into a real
/// mod's .bin, and everything up to now only proved the property OBJECT was built correctly - not that it
/// survives serialization, or that writing it leaves the rest of the file intact.</para>
///
/// <para>Each test does the full loop: build a bin, parse it, add a field, serialize, RE-PARSE THE BYTES,
/// and read the value back out of the fresh tree. Read-back goes through SafeBinTree rather than the
/// document layer on purpose - asserting through the same abstraction that wrote it could pass while the
/// bytes on disk were wrong.</para>
///
/// <para>Fixtures are built in memory. A test that needed a League install would fail on a clean checkout
/// and in CI, which is exactly when a regression in a write path most needs catching.</para>
/// </summary>
public class SchemaFieldRoundTripTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // ---------------------------------------------------------------- fixtures

    /// <summary>A minimal but REAL particle bin: one VfxSystemDefinitionData carrying one
    /// VfxEmitterDefinitionData in a complexEmitterDefinitionData container, which is the shape
    /// ParticleDocument.Parse looks for.</summary>
    private static byte[] BuildParticleBin()
    {
        var emitter = new BinTreeStruct(0, H("VfxEmitterDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("emitterName"), "TestEmitter"),
            new BinTreeF32(H("rate"), 12.5f),
        });
        var system = new BinTreeObject(0xABCD1234, H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "TestSystem"),
            new BinTreeString(H("particlePath"), "Maps/Particles/Test"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct,
                new BinTreeProperty[] { emitter }),
        });
        var tree = new BinTree(new[] { system }, System.Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static byte[] BuildMaterialBin()
    {
        var material = new BinTreeObject(0x5150CAFE, H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Test/Material_inst"),
        });
        var tree = new BinTree(new[] { material }, System.Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    /// <summary>Read a property straight out of freshly parsed BYTES, bypassing the document layer that
    /// wrote it.</summary>
    private static BinTreeProperty? ReadBack(byte[] bin, uint objectHash, uint propertyHash,
        uint? insideStructOfClass = null)
    {
        var tree = SafeBinTree.Parse(bin);
        if (!tree.Objects.TryGetValue(objectHash, out var obj)) return null;
        if (insideStructOfClass is not { } cls)
            return obj.Properties.TryGetValue(propertyHash, out var direct) ? direct : null;

        foreach (var (_, p) in obj.Properties)
        {
            if (p is not BinTreeContainer c) continue;
            foreach (var el in c.Elements)
                if (el is BinTreeStruct s && s.ClassHash == cls
                    && s.Properties.TryGetValue(propertyHash, out var found))
                    return found;
        }
        return null;
    }

    // ---------------------------------------------------------------- emitter

    [Fact]
    public void EmitterFieldSurvivesSerializeAndReparse()
    {
        var doc = ParticleDocument.Parse(BuildParticleBin());
        Assert.NotNull(doc);
        var emitter = doc!.Systems.Single().Emitters.Single();

        uint alphaRef = H("alphaRef");
        Assert.DoesNotContain(alphaRef, emitter.PresentHashes);
        Assert.False(doc.IsDirty);

        // alphaRef = 5 is the real authored default from the shipping dump.
        Assert.True(emitter.TryAddDefaultProperty(alphaRef, "U8", "5", out var reason), reason);
        Assert.Null(reason);
        Assert.True(doc.IsDirty, "adding a field must mark the document dirty or Save Override skips it");

        byte[] written = doc.Serialize();
        var readBack = ReadBack(written, 0xABCD1234, alphaRef, H("VfxEmitterDefinitionData"));
        var u8 = Assert.IsType<BinTreeU8>(readBack);
        Assert.Equal(5, u8.Value);
    }

    [Fact]
    public void AddingAFieldLeavesEverythingElseIntact()
    {
        var doc = ParticleDocument.Parse(BuildParticleBin())!;
        var emitter = doc.Systems.Single().Emitters.Single();
        Assert.True(emitter.TryAddDefaultProperty(H("alphaRef"), "U8", "5", out _));

        byte[] written = doc.Serialize();

        // The pre-existing emitter fields and the system's own fields must be untouched. A write path that
        // adds a property but disturbs a neighbour is worse than one that does nothing.
        var name = ReadBack(written, 0xABCD1234, H("emitterName"), H("VfxEmitterDefinitionData"));
        Assert.Equal("TestEmitter", Assert.IsType<BinTreeString>(name).Value);
        var rate = ReadBack(written, 0xABCD1234, H("rate"), H("VfxEmitterDefinitionData"));
        Assert.Equal(12.5f, Assert.IsType<BinTreeF32>(rate).Value);
        var sysName = ReadBack(written, 0xABCD1234, H("particleName"));
        Assert.Equal("TestSystem", Assert.IsType<BinTreeString>(sysName).Value);
    }

    /// <summary>The re-parsed document must AGREE that the field is now present - otherwise the panel would
    /// keep offering to add it after a save and reload.</summary>
    [Fact]
    public void ReparsedDocumentReportsTheFieldAsPresent()
    {
        var doc = ParticleDocument.Parse(BuildParticleBin())!;
        Assert.True(doc.Systems.Single().Emitters.Single()
            .TryAddDefaultProperty(H("alphaRef"), "U8", "5", out _));

        var reloaded = ParticleDocument.Parse(doc.Serialize());
        Assert.NotNull(reloaded);
        Assert.Contains(H("alphaRef"), reloaded!.Systems.Single().Emitters.Single().PresentHashes);
        Assert.False(reloaded.IsDirty);   // a freshly loaded document has nothing pending
    }

    [Theory]
    [InlineData("F32", "0.5")]
    [InlineData("Bool", "true")]
    [InlineData("Flag", "true")]
    [InlineData("String", "\"hello\"")]
    [InlineData("Hash", "\"0xdeadbeef\"")]
    [InlineData("Vec3", "[1.0,2.0,3.0]")]
    [InlineData("I32", "-7")]
    [InlineData("U16", "212")]
    public void EverySupportedTypeSurvivesTheRoundTrip(string fieldType, string json)
    {
        var doc = ParticleDocument.Parse(BuildParticleBin())!;
        uint field = H("roundTripProbe");
        Assert.True(doc.Systems.Single().Emitters.Single()
            .TryAddDefaultProperty(field, fieldType, json, out var reason), reason);

        var back = ReadBack(doc.Serialize(), 0xABCD1234, field, H("VfxEmitterDefinitionData"));
        Assert.NotNull(back);

        // Every supported type must come back as the SAME wire type it went in as. Flag in particular:
        // a BinTreeBool would round-trip happily and be read by the game at the wrong width.
        string expected = fieldType switch
        {
            "F32" => nameof(BinTreeF32),
            "Bool" => nameof(BinTreeBool),
            "Flag" => nameof(BinTreeBitBool),
            "String" => nameof(BinTreeString),
            "Hash" => nameof(BinTreeHash),
            "Vec3" => nameof(BinTreeVector3),
            "I32" => nameof(BinTreeI32),
            "U16" => nameof(BinTreeU16),
            _ => "?",
        };
        Assert.Equal(expected, back!.GetType().Name);
    }

    // ---------------------------------------------------------------- system object

    [Fact]
    public void SystemFieldSurvivesSerializeAndReparse()
    {
        var doc = ParticleDocument.Parse(BuildParticleBin())!;
        var system = doc.Systems.Single();

        uint flags = H("flags");
        Assert.DoesNotContain(flags, system.PresentHashes);
        Assert.True(system.TryAddDefaultProperty(flags, "U16", "212", out var reason), reason);
        Assert.True(doc.IsDirty);

        var back = ReadBack(doc.Serialize(), 0xABCD1234, flags);
        Assert.Equal(212, Assert.IsType<BinTreeU16>(back).Value);
    }

    // ---------------------------------------------------------------- material

    [Fact]
    public void MaterialFieldSurvivesSerializeAndReparse()
    {
        // The resolver is NOT optional here, and this test found that out the hard way: Parse decides a
        // material is a StaticMaterialDef by asking the resolver to NAME its class hash
        // (MaterialDocument.cs:211), so with a resolver that returns null the object is skipped entirely
        // and Materials comes back empty. The app supplies ResolveBinName, so this mirrors it.
        var doc = MaterialDocument.Parse(BuildMaterialBin(),
            h => h == H("StaticMaterialDef") ? "StaticMaterialDef" : null);
        var material = doc.Materials.Single(m => m.ClassHash == H("StaticMaterialDef"));

        uint type = H("type");
        Assert.DoesNotContain(type, material.PresentHashes);
        Assert.False(material.IsDirty);

        // type = 1 is the real authored default for StaticMaterialDef.
        Assert.True(material.TryAddDefaultProperty(type, "U32", "1", out var reason), reason);
        Assert.True(material.IsDirty, "adding a field must mark the material dirty or Save skips it");

        byte[] written = doc.Serialize();
        Assert.Equal(1u, Assert.IsType<BinTreeU32>(ReadBack(written, 0x5150CAFE, type)).Value);

        // and the name it already had is still there
        Assert.Equal("Test/Material_inst",
            Assert.IsType<BinTreeString>(ReadBack(written, 0x5150CAFE, H("name"))).Value);
    }

    // ---------------------------------------------------------------- refusals, at the document layer

    [Fact]
    public void RefusesToOverwriteAFieldThatAlreadyExists()
    {
        var doc = ParticleDocument.Parse(BuildParticleBin())!;
        var emitter = doc.Systems.Single().Emitters.Single();

        // 'rate' is already authored at 12.5. Silently replacing it with a default would destroy the
        // user's value - the panel only ever offers ABSENT fields, but the write path must not rely on
        // the UI for that guarantee.
        Assert.False(emitter.TryAddDefaultProperty(H("rate"), "F32", "0.0", out var reason));
        Assert.NotNull(reason);
        Assert.False(doc.IsDirty);

        var back = ReadBack(doc.Serialize(), 0xABCD1234, H("rate"), H("VfxEmitterDefinitionData"));
        Assert.Equal(12.5f, Assert.IsType<BinTreeF32>(back).Value);
    }

    [Fact]
    public void RefusedAddLeavesTheDocumentClean()
    {
        var doc = ParticleDocument.Parse(BuildParticleBin())!;
        var emitter = doc.Systems.Single().Emitters.Single();

        Assert.False(emitter.TryAddDefaultProperty(H("someEmbed"), "Embed", "{}", out _));
        Assert.False(emitter.TryAddDefaultProperty(H("noDefault"), "F32", null, out _));
        Assert.False(doc.IsDirty, "a refused add must not leave the document looking edited");
    }
}
