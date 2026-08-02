using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M199 (tier 5.2). The headline case is <see cref="EditingOneOfTwoPlacementsSharingAMatrixLeavesTheOtherAlone"/>:
/// that is the bug the byte-signature locator had, reproduced on a synthetic bin that mirrors the shape
/// measured in Map11's base.materials.bin, where 1,450 of 30,628 shipped placements share a transform.
/// </summary>
public class MapPlaceableWriterTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const uint ContainerHash = 0x5000u;
    private const uint KeyA = 0xAAAAAAAAu;
    private const uint KeyB = 0xBBBBBBBBu;

    /// <summary>Two placements with the SAME transform - the case that defeats a byte-signature locator.</summary>
    private static byte[] BuildBin(Matrix4x4 shared)
    {
        BinTreeStruct Particle(string name, uint system) => new(0, H("MapParticle"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), name),
            new BinTreeMatrix44(H("transform"), shared),
            new BinTreeObjectLink(H("system"), system),
        });

        var items = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, new[]
        {
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, KeyA), Particle("runeTimer", 0x1111u)),
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, KeyB), Particle("rubbleDust", 0x2222u)),
        });
        var container = new BinTreeObject(ContainerHash, H("MapPlaceableContainer"), new BinTreeProperty[] { items });
        // a second, unrelated object so "nothing else changed" has something to be true about
        var other = new BinTreeObject(0x6000u, H("MapSunProperties"), new BinTreeProperty[]
        {
            new BinTreeF32(H("lightMapColorScale"), 2f),
        });

        using var ms = new MemoryStream();
        new BinTree(new[] { container, other }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    /// <summary>Fields are read tolerantly: one of the cases under test is a placement that has no
    /// transform at all, and a helper that assumes one turns that into a test bug rather than a result.</summary>
    private static (string Name, Matrix4x4? Transform, uint? System)? Read(byte[] bin, uint key)
    {
        var tree = new BinTree(new MemoryStream(bin, false));
        if (!tree.Objects.TryGetValue(ContainerHash, out var c)) return null;
        if (c.Properties[H("items")] is not BinTreeMap map) return null;
        foreach (var e in map)
            if (e.Key is BinTreeHash kh && kh.Value == key && e.Value is BinTreeStruct s)
                return (s.Properties.TryGetValue(H("name"), out var n) && n is BinTreeString ns ? ns.Value : "",
                        s.Properties.TryGetValue(H("transform"), out var t) && t is BinTreeMatrix44 tm ? tm.Value : null,
                        s.Properties.TryGetValue(H("system"), out var y) && y is BinTreeObjectLink yl ? yl.Value : null);
        return null;
    }

    [Fact]
    public void EditingOneOfTwoPlacementsSharingAMatrixLeavesTheOtherAlone()
    {
        var shared = Matrix4x4.CreateTranslation(100f, 0f, 200f);
        var bin = BuildBin(shared);
        var movedTo = Matrix4x4.CreateTranslation(999f, 5f, 999f);

        var outBytes = MapPlaceableWriter.WriteEdits(bin,
            new[] { new MapPlacementEdit(new MapPlacementId(ContainerHash, KeyB)) { Transform = movedTo } },
            out var err);

        Assert.Null(err);
        Assert.NotNull(outBytes);
        // B moved...
        Assert.Equal(movedTo, Read(outBytes!, KeyB)!.Value.Transform!.Value);
        // ...and A, which has byte-identical transform bytes, did NOT.
        Assert.Equal(shared, Read(outBytes!, KeyA)!.Value.Transform!.Value);
    }

    [Fact]
    public void APlacementWithNoTransformIsStillAddressable()
    {
        // 2 shipped placements carry no transform, so a signature locator can never find them.
        var items = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, new[]
        {
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, KeyA),
                new BinTreeStruct(0, H("MapParticle"), new BinTreeProperty[] { new BinTreeString(H("name"), "noTransform") })),
        });
        var container = new BinTreeObject(ContainerHash, H("MapPlaceableContainer"), new BinTreeProperty[] { items });
        using var ms = new MemoryStream();
        new BinTree(new[] { container }, Array.Empty<string>()).Write(ms);

        var outBytes = MapPlaceableWriter.WriteEdits(ms.ToArray(),
            new[] { new MapPlacementEdit(new MapPlacementId(ContainerHash, KeyA)) { Name = "renamed" } },
            out var err);

        Assert.Null(err);
        Assert.Equal("renamed", Read(outBytes!, KeyA)!.Value.Name);
    }

    [Fact]
    public void RetintAndRelinkAndRenameAllPersist()
    {
        var bin = BuildBin(Matrix4x4.Identity);
        var outBytes = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(new MapPlacementId(ContainerHash, KeyA))
            {
                ColorModulate = new Vector4(1f, 0.5f, 0.25f, 0.75f),
                SystemLink = 0xFEEDu,
                Name = "retinted",
            },
        }, out var err);

        Assert.Null(err);
        var a = Read(outBytes!, KeyA)!.Value;
        Assert.Equal("retinted", a.Name);
        Assert.Equal(0xFEEDu, a.System);

        var tree = new BinTree(new MemoryStream(outBytes!, false));
        var map = (BinTreeMap)tree.Objects[ContainerHash].Properties[H("items")];
        var s = (BinTreeStruct)map.Single(e => ((BinTreeHash)e.Key).Value == KeyA).Value;
        Assert.Equal(new Vector4(1f, 0.5f, 0.25f, 0.75f), ((BinTreeVector4)s.Properties[H("colorModulate")]).Value);
    }

    [Fact]
    public void VisibilityZeroDisablesWithoutRemovingThePlacement()
    {
        var bin = BuildBin(Matrix4x4.Identity);
        var outBytes = MapPlaceableWriter.WriteEdits(bin,
            new[] { new MapPlacementEdit(new MapPlacementId(ContainerHash, KeyA)) { VisibilityFlags = 0 } },
            out var err);

        Assert.Null(err);
        Assert.NotNull(Read(outBytes!, KeyA));
        var tree = new BinTree(new MemoryStream(outBytes!, false));
        var map = (BinTreeMap)tree.Objects[ContainerHash].Properties[H("items")];
        var placement = (BinTreeStruct)map.Single(e => ((BinTreeHash)e.Key).Value == KeyA).Value;
        Assert.Equal(0, ((BinTreeU8)placement.Properties[H("mVisibilityFlags")]).Value);
    }

    [Fact]
    public void PropSkinEditChangesOnlyNestedSkinPath()
    {
        var characterData = new BinTreeEmbedded(H("characterData"), H("CharacterData"), new BinTreeProperty[]
        {
            new BinTreeString(H("characterRecord"), "Characters/SRU_Baron/CharacterRecords/Root"),
            new BinTreeString(H("skin"), "Characters/SRU_Baron/Skins/Skin0"),
        });
        var prop = new BinTreeStruct(0, H("MapCharacter"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Baron"),
            new BinTreeMatrix44(H("transform"), Matrix4x4.Identity),
            characterData,
        });
        var items = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, new[]
        {
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, KeyA), prop),
        });
        using var source = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(ContainerHash, H("MapPlaceableContainer"), new BinTreeProperty[] { items }) },
            Array.Empty<string>()).Write(source);

        const string replacement = "Characters/SRU_Baron/Skins/Skin4";
        var outBytes = MapPlaceableWriter.WriteEdits(source.ToArray(),
            new[] { new MapPlacementEdit(new MapPlacementId(ContainerHash, KeyA)) { Skin = replacement } }, out var err);

        Assert.Null(err);
        var tree = new BinTree(new MemoryStream(outBytes!, false));
        var outputItems = (BinTreeMap)tree.Objects[ContainerHash].Properties[H("items")];
        var outputProp = (BinTreeStruct)outputItems.Single().Value;
        var outputCharacter = outputProp.Properties.Values.OfType<BinTreeStruct>()
            .Single(s => s.Properties.ContainsKey(H("characterRecord")));
        Assert.Equal(replacement, ((BinTreeString)outputCharacter.Properties[H("skin")]).Value);
        Assert.Equal("Characters/SRU_Baron/CharacterRecords/Root",
            ((BinTreeString)outputCharacter.Properties[H("characterRecord")]).Value);
    }

    [Fact]
    public void RemoveDropsOnlyTheTargetedPlacement()
    {
        var bin = BuildBin(Matrix4x4.Identity);
        var outBytes = MapPlaceableWriter.WriteEdits(bin,
            new[] { new MapPlacementEdit(new MapPlacementId(ContainerHash, KeyA)) { Remove = true } },
            out var err);

        Assert.Null(err);
        Assert.Null(Read(outBytes!, KeyA));
        Assert.NotNull(Read(outBytes!, KeyB));
    }

    [Fact]
    public void UnrelatedObjectsSurviveTheRewriteUntouched()
    {
        var bin = BuildBin(Matrix4x4.Identity);
        var outBytes = MapPlaceableWriter.WriteEdits(bin,
            new[] { new MapPlacementEdit(new MapPlacementId(ContainerHash, KeyA)) { Name = "x" } },
            out _);

        var tree = new BinTree(new MemoryStream(outBytes!, false));
        Assert.Equal(2f, ((BinTreeF32)tree.Objects[0x6000u].Properties[H("lightMapColorScale")]).Value);
    }

    [Fact]
    public void AnUnknownPlacementIsRefusedRatherThanSilentlyIgnored()
    {
        var bin = BuildBin(Matrix4x4.Identity);
        var outBytes = MapPlaceableWriter.WriteEdits(bin,
            new[] { new MapPlacementEdit(new MapPlacementId(ContainerHash, 0xDEADu)) { Name = "nope" } },
            out var err);

        Assert.Null(outBytes);
        Assert.NotNull(err);
    }

    [Fact]
    public void PartialLocationReportsWhatWasMissed()
    {
        var bin = BuildBin(Matrix4x4.Identity);
        var outBytes = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(new MapPlacementId(ContainerHash, KeyA)) { Name = "ok" },
            new MapPlacementEdit(new MapPlacementId(ContainerHash, 0xDEADu)) { Name = "missing" },
        }, out var err);

        Assert.NotNull(outBytes);          // the locatable edit still lands
        Assert.NotNull(err);               // but the caller is told
        Assert.Contains("1 of 2", err);
    }

    // ---- M206: clone -----------------------------------------------------------------------------

    [Fact]
    public void CloningAddsANewPlacementAndLeavesTheSourceIntact()
    {
        var bin = BuildBin(Matrix4x4.CreateTranslation(7f, 8f, 9f));
        var tree = new BinTree(new MemoryStream(bin, false));
        uint newKey = MapPlaceableWriter.NewItemKey(tree, new MapPlacementId(ContainerHash, KeyA));

        var outBytes = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(new MapPlacementId(ContainerHash, newKey))
            {
                CloneOf = new MapPlacementId(ContainerHash, KeyA),
                Name = "copy",
                Transform = Matrix4x4.CreateTranslation(100f, 0f, 100f),
            },
        }, out var err);

        Assert.Null(err);
        var clone = Read(outBytes!, newKey);
        Assert.NotNull(clone);
        Assert.Equal("copy", clone!.Value.Name);
        Assert.Equal(Matrix4x4.CreateTranslation(100f, 0f, 100f), clone.Value.Transform!.Value);
        // the clone inherited the source's system link without being told to
        Assert.Equal(0x1111u, clone.Value.System);

        // ...and the source is untouched
        var source = Read(outBytes!, KeyA);
        Assert.Equal("runeTimer", source!.Value.Name);
        Assert.Equal(Matrix4x4.CreateTranslation(7f, 8f, 9f), source.Value.Transform!.Value);
    }

    [Fact]
    public void ACloneWithNoOtherVerbsIsAFaithfulCopy()
    {
        var shared = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        var bin = BuildBin(shared);
        var tree = new BinTree(new MemoryStream(bin, false));
        uint newKey = MapPlaceableWriter.NewItemKey(tree, new MapPlacementId(ContainerHash, KeyB));

        var outBytes = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(new MapPlacementId(ContainerHash, newKey))
                { CloneOf = new MapPlacementId(ContainerHash, KeyB) },
        }, out var err);

        Assert.Null(err);
        var a = Read(outBytes!, KeyB)!.Value;
        var b = Read(outBytes!, newKey)!.Value;
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Transform, b.Transform);
        Assert.Equal(a.System, b.System);
    }

    [Fact]
    public void NewItemKeyNeverCollidesAndIsStable()
    {
        var tree = new BinTree(new MemoryStream(BuildBin(Matrix4x4.Identity), false));
        var src = new MapPlacementId(ContainerHash, KeyA);
        uint k1 = MapPlaceableWriter.NewItemKey(tree, src);
        uint k2 = MapPlaceableWriter.NewItemKey(tree, src);

        Assert.Equal(k1, k2);                 // stable: saving twice must not churn the key
        Assert.NotEqual(0u, k1);
        Assert.NotEqual(KeyA, k1);
        Assert.NotEqual(KeyB, k1);
    }

    [Fact]
    public void CloningOntoAKeyThatAlreadyExistsIsRefused()
    {
        var bin = BuildBin(Matrix4x4.Identity);
        var outBytes = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            // KeyB is taken - overwriting it would silently destroy a placement
            new MapPlacementEdit(new MapPlacementId(ContainerHash, KeyB))
                { CloneOf = new MapPlacementId(ContainerHash, KeyA) },
        }, out var err);

        Assert.Null(outBytes);
        Assert.NotNull(err);
    }

    [Fact]
    public void CloningAMissingSourceIsRefused()
    {
        var bin = BuildBin(Matrix4x4.Identity);
        var outBytes = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(new MapPlacementId(ContainerHash, 0x1234u))
                { CloneOf = new MapPlacementId(ContainerHash, 0xDEADu) },
        }, out var err);

        Assert.Null(outBytes);
        Assert.NotNull(err);
    }

    [Fact]
    public void NoEditsIsANoOpNotARewrite()
    {
        var bin = BuildBin(Matrix4x4.Identity);
        Assert.Same(bin, MapPlaceableWriter.WriteEdits(bin, Array.Empty<MapPlacementEdit>(), out var err));
        Assert.Null(err);
    }
}
