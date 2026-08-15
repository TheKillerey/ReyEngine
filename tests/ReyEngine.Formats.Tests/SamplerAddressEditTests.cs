using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M493: editing sampler address modes (addressU/V/W) on a material.
///
/// <para>The distinction these pin down is ABSENT vs 0. They behave identically at runtime — 0 is the
/// schema default, Wrap — but they are different bytes, and Riot uses both deliberately: censused over the
/// nine shipped map WADs in M490, 4,726 samplers author no address field at all while 8,667 author
/// addressW=1 alone. An editor that collapsed the two could never reproduce what the game ships, and a
/// round trip would silently rewrite every untouched sampler.</para>
/// </summary>
public sealed class SamplerAddressEditTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);
    private const uint SamplerClass = 0x0904b150;   // StaticMaterialShaderSamplerDef

    /// <summary>One material, one sampler. <paramref name="address"/> null = no address field authored.</summary>
    private static byte[] Bin(int? address = null)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("TextureName"), "DiffuseTexture"),
            new BinTreeString(H("texturePath"), "assets/test/diffuse.tex"),
        };
        if (address is { } a)
        {
            props.Add(new BinTreeU32(H("addressU"), (uint)a));
            props.Add(new BinTreeU32(H("addressV"), (uint)a));
            props.Add(new BinTreeU32(H("addressW"), (uint)a));
        }

        var material = new BinTreeObject(H("Test/Material"), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Test/Material"),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded,
                new BinTreeProperty[] { new BinTreeEmbedded(0, SamplerClass, props) }),
        });

        using var stream = new MemoryStream();
        new BinTree(new[] { material }, Array.Empty<string>()).Write(stream);
        return stream.ToArray();
    }

    private static TextureSlot Slot(byte[] bytes, out MaterialDocument doc)
    {
        doc = MaterialDocument.Parse(bytes, _ => null);
        return doc.Materials.Single().Slots.Single();
    }

    [Fact]
    public void ReadsAuthoredValuesAndReportsAbsentAsNull()
    {
        var withNone = Slot(Bin(), out _);
        Assert.True(withNone.CanEditAddress);
        Assert.Null(withNone.AddressU);
        Assert.Null(withNone.AddressV);
        Assert.Null(withNone.AddressW);

        var withClamp = Slot(Bin(1), out _);
        Assert.Equal(1, withClamp.AddressU);
        Assert.Equal(1, withClamp.AddressV);
        Assert.Equal(1, withClamp.AddressW);
    }

    [Fact]
    public void AddsTheFieldAsU32WhichIsTheOnlyTypeRiotShips()
    {
        // The wire type is the load-bearing part: a narrower integer is a property the client reads at the
        // wrong width, which is the silent-skip failure of M416/M476 and invisible to our own reader.
        var slot = Slot(Bin(), out var doc);

        Assert.True(slot.SetAddress(TextureSlot.AddressAxis.U, 1));
        Assert.True(slot.SetAddress(TextureSlot.AddressAxis.V, 1));
        Assert.True(doc.Materials.Single().IsDirty);

        var sampler = ReadSampler(doc.Serialize());
        Assert.Equal(1u, Assert.IsType<BinTreeU32>(sampler.Properties[H("addressU")]).Value);
        Assert.Equal(1u, Assert.IsType<BinTreeU32>(sampler.Properties[H("addressV")]).Value);
        Assert.False(sampler.Properties.ContainsKey(H("addressW")));   // untouched axis stays absent
    }

    [Fact]
    public void RemovingWritesNoFieldRatherThanAZero()
    {
        var slot = Slot(Bin(1), out var doc);

        Assert.True(slot.SetAddress(TextureSlot.AddressAxis.U, null));

        var sampler = ReadSampler(doc.Serialize());
        Assert.False(sampler.Properties.ContainsKey(H("addressU")));
        // An explicit 0 must remain expressible and distinct from removal.
        Assert.True(slot.SetAddress(TextureSlot.AddressAxis.V, 0));
        sampler = ReadSampler(doc.Serialize());
        Assert.Equal(0u, Assert.IsType<BinTreeU32>(sampler.Properties[H("addressV")]).Value);
    }

    [Fact]
    public void UntouchedMaterialIsNotDirtyAndRoundTripsWithoutGainingFields()
    {
        // The regression this guards: reading a sampler must not itself author anything. If parsing wrote a
        // default 0, every material in every bin would come back changed.
        var original = Bin();
        var slot = Slot(original, out var doc);

        Assert.False(slot.IsDirty);
        Assert.False(doc.Materials.Single().IsDirty);

        var sampler = ReadSampler(doc.Serialize());
        foreach (string field in new[] { "addressU", "addressV", "addressW" })
            Assert.False(sampler.Properties.ContainsKey(H(field)));
    }

    [Fact]
    public void RevertRestoresTheOriginalAddressState()
    {
        var slot = Slot(Bin(1), out var doc);
        slot.SetAddress(TextureSlot.AddressAxis.U, 2);
        slot.SetAddress(TextureSlot.AddressAxis.W, null);
        Assert.True(slot.IsDirty);

        slot.Revert();

        Assert.False(slot.IsDirty);
        Assert.Equal(1, slot.AddressU);
        Assert.Equal(1, slot.AddressW);
    }

    [Fact]
    public void UndoCommandRestoresAbsenceNotZero()
    {
        // Undo has to be able to put the field back to "not present", which a plain int could not express.
        var slot = Slot(Bin(), out var doc);
        var command = new SamplerAddressEditCommand(doc, slot, TextureSlot.AddressAxis.U, null, 1, null);

        command.Execute();
        Assert.Equal(1, slot.AddressU);

        command.Undo();
        Assert.Null(slot.AddressU);
        var sampler = ReadSampler(doc.Serialize());
        Assert.False(sampler.Properties.ContainsKey(H("addressU")));
    }

    private static BinTreeStruct ReadSampler(byte[] bytes)
    {
        var tree = SafeBinTree.Parse(bytes);
        var material = tree.Objects[H("Test/Material")];
        var container = Assert.IsType<BinTreeUnorderedContainer>(material.Properties[H("samplerValues")]);
        return Assert.Single(container.Elements.OfType<BinTreeStruct>());
    }
}
