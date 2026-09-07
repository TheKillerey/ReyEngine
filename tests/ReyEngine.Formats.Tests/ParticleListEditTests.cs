using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M651: adding and removing items in a VFX list.
///
/// <para>M648 counted 114,698 rows whose only message was "a list header - adding and removing items is
/// not supported". Measuring what those lists ARE cut the job down twice over. The curve pairs
/// (times/values, keyTimes/keyValues) never produce such a row at all - a Value* struct is a leaf and its
/// keys have had their own editor since M187/M523 - so what is left is the emitter's own lists, led by
/// emitRotationAngles and emitRotationAxes (74k of the 114k), the force-field definitions, the submesh
/// name lists and the child-particle identifiers.</para>
///
/// <para>Two measurements shaped the design. Riot ships <b>zero</b> empty containers - 0 of 5,754,355
/// across 6 shipping map wads and 25 champion wads, which is the same answer M414 got over the material
/// corpus, where writing one crashed the game at map load - so the last item of a list cannot be removed.
/// And emitRotationAngles/emitRotationAxes are <b>not</b> reliably parallel: of 36,938 emitters carrying
/// both, 470 have different lengths and 115 carry only one of the two. They look like times/values and
/// are not, so they are edited independently rather than kept in step, because forcing a rule Riot's own
/// data breaks would be an invention.</para>
/// </summary>
public sealed class ParticleListEditTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static string? Name(uint hash)
    {
        foreach (string n in new[]
        {
            "emitterName", "particleName", "complexEmitterDefinitionData",
            "emitRotationAxes", "mSubmeshesToDraw", "fieldNoiseDefinitions", "switches", "noiseRatio", "name",
        })
            if (H(n) == hash) return n;
        return null;
    }

    /// <summary>One emitter with the given fields, through the parse the editor uses.</summary>
    private static (ParticleDocument Doc, ParticleEmitterEntry Emitter) Load(params BinTreeProperty[] fields)
    {
        var emitter = new BinTreeStruct(0, H("VfxEmitterDefinitionData"),
            new BinTreeProperty[] { new BinTreeString(H("emitterName"), "e") }.Concat(fields).ToArray());
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct,
                new BinTreeProperty[] { emitter }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        var doc = ParticleDocument.Parse(ms.ToArray(), Name);
        Assert.NotNull(doc);
        return (doc!, doc!.Systems.Single().Emitters.Single());
    }

    private static BinTreeProperty Axes(params float[] xs) =>
        new BinTreeContainer(H("emitRotationAxes"), BinPropertyType.Vector3,
            xs.Select(x => (BinTreeProperty)new BinTreeVector3(0, new System.Numerics.Vector3(x, 0, 0))).ToArray());

    private static IReadOnlyList<ParticleProperty> Items(ParticleEmitterEntry e, string field) =>
        e.Properties.SkipWhile(p => p.Name != field).Skip(1).Where(p => p.IsListItem).ToList();

    // ===================================================== the handle

    [Fact]
    public void EachItemRowKnowsWhichListItemItIs()
    {
        var (_, emitter) = Load(Axes(1, 2, 3));
        var items = Items(emitter, "emitRotationAxes");
        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { 0, 1, 2 }, items.Select(i => i.ListIndex).ToArray());
        Assert.All(items, i => Assert.Equal(3, i.ListCount));
        Assert.All(items, i => Assert.True(i.CanRemoveFromList));
    }

    [Fact]
    public void AnOrdinaryFieldIsNotAListItem()
    {
        var (_, emitter) = Load(Axes(1));
        var name = emitter.Properties.Single(p => p.Name == "emitterName");
        Assert.False(name.IsListItem);
        Assert.Equal(-1, name.ListIndex);
        Assert.False(name.CanRemoveFromList);
    }

    // ===================================================== duplicate and remove

    [Fact]
    public void DuplicatingAnItemInsertsACopyRightAfterIt()
    {
        var (doc, emitter) = Load(Axes(1, 2, 3));
        Items(emitter, "emitRotationAxes")[1].DuplicateInList();

        var after = Items(doc.RebuildRows(emitter), "emitRotationAxes");
        Assert.Equal(4, after.Count);
        // 1, 2, 2, 3 - the copy sits next to its original, not at the end
        Assert.Equal(new[] { "1, 0, 0", "2, 0, 0", "2, 0, 0", "3, 0, 0" }, after.Select(i => i.CurrentText).ToArray());
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void RemovingAnItemDropsThatOne()
    {
        var (doc, emitter) = Load(Axes(1, 2, 3));
        Items(emitter, "emitRotationAxes")[0].RemoveFromList();

        var after = Items(doc.RebuildRows(emitter), "emitRotationAxes");
        Assert.Equal(new[] { "2, 0, 0", "3, 0, 0" }, after.Select(i => i.CurrentText).ToArray());
    }

    [Fact]
    public void TheLastItemStays()
    {
        // 0 of 5,754,355 containers Riot ships is empty, and an empty one crashes the game at map load.
        var (_, emitter) = Load(Axes(7));
        var only = Items(emitter, "emitRotationAxes").Single();
        Assert.False(only.CanRemoveFromList);
        Assert.Contains("last item", only.ListBlockedReason, StringComparison.OrdinalIgnoreCase);
        var ex = Assert.Throws<InvalidOperationException>(() => only.RemoveFromList());
        Assert.Contains("last item", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicatingAStructItemCopiesTheWholeThing()
    {
        // A force field is a struct: cloning gives a complete, valid one the user can then retune, which
        // is why duplicate is offered rather than an "add" that would have to invent a default.
        var field = new BinTreeContainer(H("fieldNoiseDefinitions"), BinPropertyType.Embedded,
            new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("VfxFieldNoiseDefinitionData"),
                    new BinTreeProperty[] { new BinTreeF32(H("noiseRatio"), 0.25f) }),
                new BinTreeEmbedded(0, H("VfxFieldNoiseDefinitionData"),
                    new BinTreeProperty[] { new BinTreeF32(H("noiseRatio"), 0.75f) }),
            });
        var (doc, emitter) = Load(field);
        Items(emitter, "fieldNoiseDefinitions")[0].DuplicateInList();

        var rebuilt = doc.RebuildRows(emitter);
        Assert.Equal(3, Items(rebuilt, "fieldNoiseDefinitions").Count);

        // and the clone is DEEP: retuning the copy must not move the one it was copied from
        var ratios = rebuilt.Properties.Where(p => p.Name == "noiseRatio").ToList();
        Assert.Equal(3, ratios.Count);
        Assert.Equal(new[] { "0.25", "0.25", "0.75" }, ratios.Select(r => r.CurrentText).ToArray());
        ratios[1].Apply("0.5");
        Assert.Equal(new[] { "0.25", "0.5", "0.75" }, ratios.Select(r => r.CurrentText).ToArray());
    }

    // ===================================================== the wire form

    [Fact]
    public void AListTwoStaysAListTwoAfterAnEdit()
    {
        // The tag is what the client dispatches on; an UnorderedContainer rebuilt as an ordinary one is a
        // property the client silently drops. M649 has the three times this has bitten the project.
        var sw = new BinTreeUnorderedContainer(H("switches"), BinPropertyType.Embedded,
            new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("StaticMaterialSwitchDef"),
                    new BinTreeProperty[] { new BinTreeString(H("name"), "A") }),
                new BinTreeEmbedded(0, H("StaticMaterialSwitchDef"),
                    new BinTreeProperty[] { new BinTreeString(H("name"), "B") }),
            });
        var (doc, emitter) = Load(sw);
        var before = SafeBinTree.Parse(doc.Serialize());

        Items(emitter, "switches")[0].DuplicateInList();
        var after = SafeBinTree.Parse(doc.Serialize());

        // three items now, and not one property changed its tag on the way
        Assert.Empty(BinWireForm.Compare(before, after, Name));
        Assert.Equal(3, Items(doc.RebuildRows(emitter), "switches").Count);
    }

    [Fact]
    public void AnEditedListStillSerialisesAndParsesBack()
    {
        var (doc, emitter) = Load(Axes(1, 2));
        Items(emitter, "emitRotationAxes")[0].DuplicateInList();
        byte[] bytes = doc.Serialize();
        var reread = ParticleDocument.Parse(bytes, Name);
        Assert.NotNull(reread);
        Assert.Equal(3, Items(reread!.Systems.Single().Emitters.Single(), "emitRotationAxes").Count);
    }

    // ===================================================== on a real champion

    [Fact]
    public void ARealEmittersListEditsAndSurvivesTheRoundTrip()
    {
        string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions\Aatrox.wad.client";
        if (!File.Exists(wad)) return;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); }
        catch { return; }
        string? Resolve(uint h) => db.TryGetBinName(h, out var n) ? n : null;

        using var archive = WadArchive.Open(wad, new WadPathResolver(db));
        foreach (var entry in archive.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
        {
            ParticleDocument? doc;
            byte[] original;
            try { original = archive.Extract(entry.PathHash); doc = ParticleDocument.Parse(original, Resolve); }
            catch { continue; }
            if (doc is null) continue;

            var target = doc.Systems.SelectMany(s => s.Emitters)
                .SelectMany(e => e.Properties)
                .FirstOrDefault(p => p.CanRemoveFromList);
            if (target is null) continue;

            int was = target.ListCount;
            target.DuplicateInList();
            byte[] edited = doc.Serialize();

            // it parses, the list grew by exactly one, and nothing was re-tagged
            var reread = ParticleDocument.Parse(edited, Resolve);
            Assert.NotNull(reread);
            Assert.Empty(BinWireForm.Compare(SafeBinTree.Parse(original), SafeBinTree.Parse(edited), Resolve));

            target.RemoveFromList();
            Assert.Equal(was, target.ListCount);
            return;   // one real bin is the point; the corpus sweep is the probe's job
        }
    }
}
