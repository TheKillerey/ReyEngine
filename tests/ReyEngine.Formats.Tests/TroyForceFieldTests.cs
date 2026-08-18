using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M522: converting the legacy force fields, checked against Riot's own output.
///
/// <para>All five legacy kinds survive one-for-one into the modern engine, and a census of 5,138
/// shipped <c>VfxFieldCollectionDefinitionData</c> fixes every shape: the collection is a POINTER, each
/// list a container of EMBEDDED elements, and the per-kind properties are those below. Measured over
/// the paired systems, every list length and every value agrees with Riot except attraction toward
/// another emitter, which is called out as unresolved rather than guessed.</para>
/// </summary>
public sealed class TroyForceFieldTests
{
    private const string Corpus = @"K:\LeagueSandbox\League_Sandbox_Client\DATA\Particles";

    private static TroyBinFile? Load(string name)
    {
        string path = Path.Combine(Corpus, name + ".troybin");
        if (!File.Exists(path)) return null;
        Assert.True(TroyBinFile.TryParse(File.ReadAllBytes(path), out var t, out var error), error);
        return t;
    }

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static BinTreeProperty? Prop(IEnumerable<BinTreeProperty> props, string name)
        => props.FirstOrDefault(p => p.NameHash == H(name));

    private static IEnumerable<BinTreeProperty> Kids(BinTreeProperty? p) => p switch
    {
        BinTreeEmbedded e => e.Properties.Values,
        BinTreeStruct s => s.Properties.Values,
        BinTreeContainer c => c.Elements,
        BinTreeOptional o => o.Value is null ? Array.Empty<BinTreeProperty>() : new[] { o.Value },
        _ => Array.Empty<BinTreeProperty>(),
    };

    private static BinTreeProperty Emitter(TroyBinFile troy, string name)
    {
        var result = TroyBinConverter.Convert(troy, "Test", "Particles/Test");
        BinTree tree;
        using (var ms = new MemoryStream(result.BinBytes)) tree = new BinTree(ms);
        var emitters = Kids(tree.Objects.Values.Single().Properties[H("complexEmitterDefinitionData")]);
        return emitters.Single(e => Prop(Kids(e), "emitterName") is BinTreeString s && s.Value == name);
    }

    /// <summary>The field of <paramref name="listName"/> at index 0 on that emitter.</summary>
    private static BinTreeProperty Field(TroyBinFile troy, string emitter, string listName)
    {
        var collection = Prop(Kids(Emitter(troy, emitter)), "fieldCollectionDefinition");
        Assert.NotNull(collection);
        return Kids(Prop(Kids(collection), listName)).First();
    }

    private static float? Scalar(BinTreeProperty holder, string field)
        => Prop(Kids(Prop(Kids(holder), field)), "constantValue") is BinTreeF32 f ? f.Value : null;

    private static Vector3? Vector(BinTreeProperty holder, string field)
        => Prop(Kids(Prop(Kids(holder), field)), "constantValue") is BinTreeVector3 v ? v.Value : null;

    [Fact]
    public void TheFiveKindsNameTheFiveShippedClasses()
    {
        // The converter builds the class name as "VfxField" + kind + "DefinitionData", so the enum
        // member names ARE the wire contract. Renaming one would not fail to compile - it would emit a
        // class hash the client has never heard of, and silently drop the field.
        foreach (var (kind, cls) in new[]
        {
            (TroyFieldKind.Acceleration, "VfxFieldAccelerationDefinitionData"),
            (TroyFieldKind.Attraction, "VfxFieldAttractionDefinitionData"),
            (TroyFieldKind.Drag, "VfxFieldDragDefinitionData"),
            (TroyFieldKind.Orbital, "VfxFieldOrbitalDefinitionData"),
            (TroyFieldKind.Noise, "VfxFieldNoiseDefinitionData"),
        })
            Assert.Equal(cls, "VfxField" + kind + "DefinitionData");
    }

    [Fact]
    public void TheCollectionIsAPointerHoldingContainersOfEmbeddedElements()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        // Shapes are copied from a census of 5,138 shipped collections, not inferred from our own
        // reader - which is tolerant enough to accept a shape the client rejects.
        var collection = Prop(Kids(Emitter(t, "Flame")), "fieldCollectionDefinition");
        var asStruct = Assert.IsType<BinTreeStruct>(collection);
        Assert.Equal(H("VfxFieldCollectionDefinitionData"), asStruct.ClassHash);

        var list = Assert.IsType<BinTreeContainer>(Prop(Kids(collection), "fieldDragDefinitions"));
        Assert.Equal(BinPropertyType.Embedded, list.ElementType);
        Assert.IsType<BinTreeEmbedded>(list.Elements.Single());
    }

    [Fact]
    public void OnlyTheKindsTheEmitterActuallyUsesGetAList()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        // An empty container crashes the client at map load (M414), so a kind with no fields must be
        // absent rather than present-and-empty. Flame pulls accel, attract, drag and orbit - not noise.
        var collection = Prop(Kids(Emitter(t, "Flame")), "fieldCollectionDefinition");
        Assert.NotNull(Prop(Kids(collection), "fieldAccelerationDefinitions"));
        Assert.NotNull(Prop(Kids(collection), "fieldAttractionDefinitions"));
        Assert.NotNull(Prop(Kids(collection), "fieldDragDefinitions"));
        Assert.NotNull(Prop(Kids(collection), "fieldOrbitalDefinitions"));
        Assert.Null(Prop(Kids(collection), "fieldNoiseDefinitions"));

        Assert.All(Kids(collection), list => Assert.NotEmpty(Kids(list)));
    }

    [Fact]
    public void AnEmitterWithNoFieldsGetsNoCollection()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        Assert.Empty(t.Emitters.Single(e => e.Name == "Flat").FieldReferences!);
        Assert.Null(Prop(Kids(Emitter(t, "Flat")), "fieldCollectionDefinition"));
    }

    [Fact]
    public void DragCarriesStrengthAndRadius()
    {
        var t = Load("DestroyedBuilding_idle");
        if (t is null) return;

        // [SparksDrag] f-drag=6, f-radius=1000 -> Riot's fieldDragDefinitions[0] strength 6, radius 1000
        var drag = Field(t, "sparkburst3", "fieldDragDefinitions");
        Assert.Equal(6f, Scalar(drag, "strength"));
        Assert.Equal(1000f, Scalar(drag, "radius"));

        // f-pos is (0,0,0) here and Riot writes no Position - measured on the paired systems, and the
        // reason only 222 of 848 shipped drag fields carry one.
        Assert.Equal(Vector3.Zero, t.ForceFields.Single(f => f.Name == "SparksDrag").Position);
        Assert.Null(Prop(Kids(drag), "Position"));
    }

    [Fact]
    public void AttractionReadsAccelAsAScalarAndAccelerationReadsItAsAVector()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        // The same field name, two types, told apart only by which reference named the section:
        // [FlameAttract] f-accel=100 is a pull rate, [FlameAccel] f-accel=(0,300,0) is a vector.
        var attract = Field(t, "Flame", "fieldAttractionDefinitions");
        Assert.Equal(100f, Scalar(attract, "acceleration"));
        Assert.Equal(200f, Scalar(attract, "radius"));
        Assert.Equal(new Vector3(0, 50, 0), Vector(attract, "Position"));

        var accel = Field(t, "Flame", "fieldAccelerationDefinitions");
        Assert.Equal(new Vector3(0, 300, 0), Vector(accel, "acceleration"));
        Assert.Null(Scalar(accel, "acceleration"));
    }

    [Fact]
    public void OrbitalCarriesItsDirection()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        Assert.Equal(new Vector3(0, 1, 0),
            Vector(Field(t, "Flame", "fieldOrbitalDefinitions"), "direction"));
    }

    [Fact]
    public void NoiseFrequencyIsTheReciprocalOfThePeriod()
    {
        var t = Load("SRU_Forest_Polen_Needles_01");
        if (t is null) return;

        // Riot's rule, not the format's, and exact every time it was checked: period 0.5 -> frequency 2,
        // 0.25 -> 4, 0.4 -> 2.5. Copying the period straight across would make every noise field run at
        // the wrong rate without looking broken.
        Assert.Equal(0.5f, t.ForceFields.Single(f => f.Name == "PolenNoise").Period);

        var noise = Field(t, "SRU_Polen_Needles", "fieldNoiseDefinitions");
        Assert.Equal(2f, Scalar(noise, "frequency"));
        Assert.Equal(1940f, Scalar(noise, "radius"));
        Assert.Equal(20f, Scalar(noise, "velocityDelta"));
    }

    [Fact]
    public void NoiseAxisFractionComesFromTheFileAndIsAPlainVector()
    {
        var t = Load("SRU_Forest_Polen_Needles_01");
        if (t is null) return;

        // *f-axisfrac, and a PLAIN vec3 rather than a ValueVector3 wrapper - 3,612 of 3,635 shipped
        // noise fields carry it that way. Riot's converted value for this one is (2,1,0).
        Assert.Equal(new Vector3(2, 1, 0), t.ForceFields.Single(f => f.Name == "PolenNoise").AxisFraction);

        var axis = Prop(Kids(Field(t, "SRU_Polen_Needles", "fieldNoiseDefinitions")), "axisFraction");
        Assert.Equal(new Vector3(2, 1, 0), Assert.IsType<BinTreeVector3>(axis).Value);
    }

    [Fact]
    public void ASilentAxisFractionBecomesTheIdentity()
    {
        var t = Load("DestroyedBuilding_idle");
        if (t is null) return;

        // Riot writes (1,1,1) in 5 of 5 cases where the legacy file has no f-axisfrac. Unlike
        // particleLinger's 10 - a behavioural value, so deliberately not copied - (1,1,1) is the
        // identity for a per-axis weighting and cannot change how the effect renders.
        Assert.Null(t.ForceFields.Single(f => f.Name == "smokenoise").AxisFraction);

        var axis = Prop(Kids(Field(t, "smoke", "fieldNoiseDefinitions")), "axisFraction");
        Assert.Equal(Vector3.One, Assert.IsType<BinTreeVector3>(axis).Value);
    }
}
