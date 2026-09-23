using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Particles;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M752: an emitter's force fields - acceleration, drag, noise, orbital, attraction - added, removed and
/// edited on the live bin. Every write is checked by reading it back through VfxSystemResolver, the reader
/// the preview's simulator uses, so a wire form the game would misread fails here rather than in game.
/// </summary>
public sealed class ParticleForcesTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static string? Name(uint hash) => null;

    private static ParticleDocument Doc(params BinTreeProperty[] emitterFields)
    {
        var emitter = new BinTreeStruct(0, H("VfxEmitterDefinitionData"),
            new BinTreeProperty[] { new BinTreeString(H("emitterName"), "e") }.Concat(emitterFields).ToArray());
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, new BinTreeProperty[] { emitter }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        return ParticleDocument.Parse(ms.ToArray(), Name)!;
    }

    private static ParticleEmitterEntry Emitter(ParticleDocument doc) => doc.Systems.Single().Emitters.Single();

    /// <summary>What the preview's simulator reads from the serialized document.</summary>
    private static VfxForceFields? Resolved(ParticleDocument doc) =>
        VfxSystemResolver.ExtractAll(doc.Serialize()).Values.Single().Emitters.Single().ForceFields;

    // ===================================================== adding

    [Fact]
    public void EachKindIsAddedAtRiotsTypicalValuesAndTheSimulatorReadsThem()
    {
        var doc = Doc();
        var e = Emitter(doc);
        foreach (ParticleForceKind kind in Enum.GetValues<ParticleForceKind>()) e.AddForce(kind);

        var f = Resolved(doc)!;
        Assert.Equal(new Vector3(0, 40, 0), Assert.Single(f.Acceleration).Acceleration);
        var drag = Assert.Single(f.Drag);
        Assert.Equal((1000f, 2f), (drag.Radius, drag.Strength));
        var noise = Assert.Single(f.Noise);
        Assert.Equal((300f, 10f, 20f), (noise.Radius, noise.Frequency, noise.VelocityDelta));
        Assert.Equal(Vector3.One, noise.AxisFraction);   // left to the class default: all axes
        Assert.Equal(new Vector3(0, 1, 0), Assert.Single(f.Orbital).Direction);
        var pull = Assert.Single(f.Attraction);
        Assert.Equal((500f, 500f), (pull.Radius, pull.Acceleration));
        Assert.True(e.IsDirty);
    }

    [Fact]
    public void TheCollectionIsAPointerAndEachListHoldsEmbeddedDefinitions()
    {
        // M522's census of 5,138 shipped collections: a POINTER, and lists of EMBEDDED elements. A wrong
        // wire form is a property the client silently skips.
        var doc = Doc();
        Emitter(doc).AddForce(ParticleForceKind.Drag);
        var tree = new BinTree(new MemoryStream(doc.Serialize(), false));
        var emitter = (BinTreeStruct)((BinTreeContainer)tree.Objects[H("sys")].Properties[H("complexEmitterDefinitionData")]).Elements[0];

        var collection = emitter.Properties[H("fieldCollectionDefinition")];
        Assert.IsType<BinTreeStruct>(collection);
        Assert.IsNotType<BinTreeEmbedded>(collection);
        var list = Assert.IsAssignableFrom<BinTreeContainer>(((BinTreeStruct)collection).Properties[H("fieldDragDefinitions")]);
        Assert.Equal(BinPropertyType.Embedded, list.ElementType);
        Assert.IsType<BinTreeEmbedded>(Assert.Single(list.Elements));
        // a zero Position is left out, as Riot leaves it out
        Assert.False(((BinTreeStruct)list.Elements[0]).Properties.ContainsKey(H("Position")));
    }

    [Fact]
    public void ASecondForceOfAKindJoinsTheSameList()
    {
        var doc = Doc();
        var e = Emitter(doc);
        Assert.Equal(0, e.AddForce(ParticleForceKind.Noise));
        Assert.Equal(1, e.AddForce(ParticleForceKind.Noise));
        Assert.Equal(2, Resolved(doc)!.Noise.Count);
    }

    // ===================================================== editing

    [Fact]
    public void AValueEditReachesTheSimulator()
    {
        var doc = Doc();
        var e = Emitter(doc);
        e.AddForce(ParticleForceKind.Attraction);
        e.SetForceValue(ParticleForceKind.Attraction, 0, "Position", new Vector3(0, 200, 0));
        e.SetForceValue(ParticleForceKind.Attraction, 0, "acceleration", new Vector3(-800, 0, 0));   // a repulsor

        var pull = Assert.Single(Resolved(doc)!.Attraction);
        Assert.Equal(new Vector3(0, 200, 0), pull.Position);
        Assert.Equal(-800f, pull.Acceleration);   // signed - negative pushes, never clamped
    }

    [Fact]
    public void TheEditorReadsBackWhatItWrote()
    {
        var doc = Doc();
        var e = Emitter(doc);
        e.AddForce(ParticleForceKind.Drag);
        e.SetForceValue(ParticleForceKind.Drag, 0, "Position", new Vector3(400, 0, 0));

        var force = Assert.Single(e.Forces);
        Assert.True(force.IsPositional);
        Assert.Equal(new Vector3(400, 0, 0), force.Position);
        Assert.Equal(1000f, force.Radius);
        Assert.Equal("Drag 1", force.Title);
    }

    [Fact]
    public void EditingAValueThatHasACurveKeepsTheCurve()
    {
        // 65 of 191 shipped orbital directions are authored as curves; editing the constant must not drop one
        var curved = new BinTreeStruct(H("fieldCollectionDefinition"), H("VfxFieldCollectionDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("fieldOrbitalDefinitions"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("VfxFieldOrbitalDefinitionData"), new BinTreeProperty[]
                {
                    new BinTreeEmbedded(H("direction"), H("ValueVector3"), new BinTreeProperty[]
                    {
                        new BinTreeStruct(H("dynamics"), H("VfxAnimatedVector3fVariableData"), new BinTreeProperty[]
                        {
                            new BinTreeContainer(H("times"), BinPropertyType.F32, new BinTreeProperty[] { new BinTreeF32(0, 0), new BinTreeF32(0, 1) }),
                            new BinTreeContainer(H("values"), BinPropertyType.Vector3, new BinTreeProperty[]
                                { new BinTreeVector3(0, new Vector3(0, 2, 0)), new BinTreeVector3(0, new Vector3(0, 4, 0)) }),
                        }),
                    }),
                }),
            }),
        });
        var doc = Doc(curved);
        var e = Emitter(doc);
        var before = Assert.Single(e.Forces).Values.Single();
        Assert.True(before.HasCurve);
        Assert.Equal(new Vector3(0, 2, 0), before.Value);   // a curve-only value reads its first key

        e.SetForceValue(ParticleForceKind.Orbital, 0, "direction", new Vector3(0, 3, 0));
        var after = Assert.Single(e.Forces).Values.Single();
        Assert.True(after.HasCurve);
        Assert.Equal(new Vector3(0, 3, 0), after.Value);
    }

    [Fact]
    public void AFieldAKindDoesNotHaveIsRefused()
    {
        var doc = Doc();
        var e = Emitter(doc);
        e.AddForce(ParticleForceKind.Acceleration);
        Assert.Throws<ArgumentException>(() => e.SetForceValue(ParticleForceKind.Acceleration, 0, "radius", Vector3.One));
    }

    // ===================================================== removing

    [Fact]
    public void RemovingTheLastForceOfAKindRemovesItsListAndTheLastListTheCollection()
    {
        // Riot ships no empty container, and an empty one crashes the client at map load (M414)
        var doc = Doc();
        var e = Emitter(doc);
        e.AddForce(ParticleForceKind.Drag);
        e.AddForce(ParticleForceKind.Noise);

        e.RemoveForce(ParticleForceKind.Drag, 0);
        var f = Resolved(doc)!;
        Assert.Empty(f.Drag);
        Assert.Single(f.Noise);

        e.RemoveForce(ParticleForceKind.Noise, 0);
        Assert.Null(Resolved(doc));
        var tree = new BinTree(new MemoryStream(doc.Serialize(), false));
        var emitter = (BinTreeStruct)((BinTreeContainer)tree.Objects[H("sys")].Properties[H("complexEmitterDefinitionData")]).Elements[0];
        Assert.False(emitter.Properties.ContainsKey(H("fieldCollectionDefinition")));
    }

    [Fact]
    public void RemovingOneOfSeveralKeepsTheRestInOrder()
    {
        var doc = Doc();
        var e = Emitter(doc);
        e.AddForce(ParticleForceKind.Attraction);
        e.AddForce(ParticleForceKind.Attraction);
        e.SetForceValue(ParticleForceKind.Attraction, 1, "radius", new Vector3(777, 0, 0));
        e.RemoveForce(ParticleForceKind.Attraction, 0);
        Assert.Equal(777f, Assert.Single(Resolved(doc)!.Attraction).Radius);
    }

    [Fact]
    public void ANonExistentForceIsRefused()
    {
        var doc = Doc();
        Assert.Throws<InvalidOperationException>(() => Emitter(doc).RemoveForce(ParticleForceKind.Drag, 0));
    }

    // ===================================================== the emitter's own offset (moved by the M753 gizmo)

    [Fact]
    public void MovingAnEmitterWithoutAnOffsetCreatesOneTheSimulatorPlacesItBy()
    {
        var doc = Doc();
        var e = Emitter(doc);
        e.SetEmitterPosition(new Vector3(10, 20, 30));
        Assert.Equal(new Vector3(10, 20, 30), e.EmitterPosition);
        var def = VfxSystemResolver.ExtractAll(doc.Serialize()).Values.Single().Emitters.Single();
        Assert.Equal(new Vector3(10, 20, 30), def.EmitterPosition.Constant);
    }

    [Fact]
    public void MovingAnAnimatedEmitterShiftsItsPathWithIt()
    {
        var animated = new BinTreeEmbedded(H("EmitterPosition"), H("ValueVector3"), new BinTreeProperty[]
        {
            new BinTreeVector3(H("constantValue"), new Vector3(0, 0, 0)),
            new BinTreeStruct(H("dynamics"), H("VfxAnimatedVector3fVariableData"), new BinTreeProperty[]
            {
                new BinTreeContainer(H("times"), BinPropertyType.F32, new BinTreeProperty[] { new BinTreeF32(0, 0), new BinTreeF32(0, 1) }),
                new BinTreeContainer(H("values"), BinPropertyType.Vector3, new BinTreeProperty[]
                    { new BinTreeVector3(0, new Vector3(0, 0, 0)), new BinTreeVector3(0, new Vector3(100, 0, 0)) }),
            }),
        });
        var doc = Doc(animated);
        var e = Emitter(doc);
        e.SetEmitterPosition(new Vector3(0, 50, 0));

        var def = VfxSystemResolver.ExtractAll(doc.Serialize()).Values.Single().Emitters.Single();
        Assert.Equal(new Vector3(0, 50, 0), def.EmitterPosition.Constant);
        Assert.Equal(new[] { new Vector3(0, 50, 0), new Vector3(100, 50, 0) }, def.EmitterPosition.Values);
    }

    // ===================================================== Riot's own data

    [Fact]
    public void RiotsShippedForcesReadTheSameThroughTheEditorAndTheSimulator()
    {
        const string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
        if (!File.Exists(wad)) return;
        var db = new HashSyncService().LoadLocal(_ => { });
        using var archive = ReyEngine.Core.Wad.WadArchive.Open(wad, new WadPathResolver(db));
        int emitters = 0, forces = 0;
        foreach (var entry in archive.Entries.Where(x => x.IsResolved && x.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase)).Take(4))
        {
            byte[] bytes = archive.Extract(entry);
            var doc = ParticleDocument.Parse(bytes, Name);
            if (doc is null) continue;
            var defs = VfxSystemResolver.ExtractAll(bytes);
            foreach (var s in doc.Systems)
            {
                if (!defs.TryGetValue(s.PathHash, out var def)) continue;
                // the resolver keeps every emitter, disabled ones too, in the document's order
                var live = s.Emitters;
                if (live.Count != def.Emitters.Count) continue;
                for (int i = 0; i < live.Count; i++)
                {
                    var mine = live[i].Forces;
                    var theirs = def.Emitters[i].ForceFields;
                    int count = theirs is null ? 0
                        : theirs.Acceleration.Count + theirs.Drag.Count + theirs.Noise.Count + theirs.Orbital.Count + theirs.Attraction.Count;
                    Assert.Equal(count, mine.Count);
                    emitters++; forces += mine.Count;
                }
            }
        }
        Assert.True(emitters > 0);
    }
}
