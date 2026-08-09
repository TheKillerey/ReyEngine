using System.Buffers.Binary;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M413. A <c>0x82</c> struct is NULLABLE: class hash 0 means null and nothing follows it, four bytes
/// total. A writer that emits the long form (<c>class 0, size 2, propCount 0</c>) adds six stray bytes,
/// and every byte after the first null in that container is then misread - which crashed Map453 at load.
///
/// <para>The fixture is hand-assembled rather than produced by corrupting a written file, so the two
/// encodings are stated explicitly and the test cannot drift with the library's writer. Both forms are
/// built from one description, which is what makes "repair the broken one and get the good one back,
/// byte for byte" a meaningful assertion.</para>
/// </summary>
public class BinNullStructRepairTests
{
    private const uint ContainerClass = 0xb25c0a3f;   // MapPlaceableContainer, the real-world victim
    private const uint ItemsField = 0x3a79338f;       // its "items" map
    private const uint ItemClass = 0x5289f880;
    private const uint ValueField = 0x0dba4cb3;

    /// <summary>One object holding items = map&lt;hash, struct&gt; with null, real, null - the exact shape
    /// Map453 has. <paramref name="longNulls"/> picks the broken encoding.</summary>
    private static byte[] BuildBin(bool longNulls)
    {
        var entries = new MemoryStream();
        var w = new BinaryWriter(entries);

        void Null(uint key)
        {
            w.Write(key);
            w.Write(0u);                       // class hash 0 == null
            if (!longNulls) return;
            w.Write(2u);                       // the six bytes the format says are NOT here
            w.Write((ushort)0);
        }

        w.Write(0xAAAA0001u); w.Write(0u); if (longNulls) { w.Write(2u); w.Write((ushort)0); }
        // a real entry between the nulls, so a desync corrupts something with content
        w.Write(0xBBBB0002u);
        w.Write(ItemClass);
        w.Write(11u);                          // size covers propCount(2) + one F32 property(4+1+4 = 9)
        w.Write((ushort)1);
        w.Write(ValueField); w.Write((byte)10); w.Write(1.5f);
        Null(0xCCCC0003u);
        w.Flush();
        byte[] entryBytes = entries.ToArray();

        var body = new MemoryStream();
        var b = new BinaryWriter(body);
        b.Write(ItemsField);
        b.Write((byte)0x86);                   // Map
        b.Write((byte)0x11);                   // key: Hash
        b.Write((byte)0x82);                   // value: struct (NULLABLE)
        b.Write((uint)(entryBytes.Length + 4));// size covers count + entries
        b.Write(3u);
        b.Write(entryBytes);
        b.Flush();
        byte[] bodyBytes = body.ToArray();

        var file = new MemoryStream();
        var f = new BinaryWriter(file);
        f.Write(new[] { 'P', 'R', 'O', 'P' });
        f.Write(3u);                           // version
        f.Write(0u);                           // dependencies
        f.Write(1u);                           // object count
        f.Write(ContainerClass);
        f.Write((uint)(bodyBytes.Length + 6)); // size = pathHash(4) + propCount(2) + properties
        f.Write(0x0fa61e5bu);                  // path hash
        f.Write((ushort)1);
        f.Write(bodyBytes);
        f.Flush();
        return file.ToArray();
    }

    private static byte[] Good => BuildBin(longNulls: false);
    private static byte[] Broken => BuildBin(longNulls: true);

    // ---- the fixture is genuinely the defect ----

    /// <summary>If the strict reader accepted the "broken" bytes the rest of these tests would prove
    /// nothing, so this states the premise instead of assuming it.</summary>
    [Fact]
    public void TheBrokenFixtureIsRejectedByTheStrictReaderAndTheGoodOneIsNot()
    {
        _ = new BinTree(new MemoryStream(Good, writable: false));
        Assert.ThrowsAny<Exception>(() => new BinTree(new MemoryStream(Broken, writable: false)));
        Assert.Equal(Good.Length + 12, Broken.Length);   // two nulls x six stray bytes
    }

    // ---- the repair ----

    [Fact]
    public void RepairTurnsTheBrokenFormBackIntoTheGoodOneByteForByte()
    {
        Assert.True(BinNullStructRepair.TryRepair(Broken, out byte[]? repaired, out var result));
        Assert.NotNull(repaired);
        Assert.Equal(2, result.NullsFixed);
        Assert.Equal(1, result.ObjectsFixed);
        Assert.Equal(0, result.ObjectsUnreadable);
        Assert.Equal(Good, repaired);
    }

    /// <summary>The control that matters most: a file that is ALREADY canonical must come back untouched.
    /// A repairer that rewrites correct files is worse than none, because it would corrupt Riot's.</summary>
    [Fact]
    public void RepairDeclinesAFileThatIsAlreadyCanonical()
    {
        Assert.False(BinNullStructRepair.TryRepair(Good, out byte[]? repaired, out var result));
        Assert.Null(repaired);
        Assert.Equal(0, result.NullsFixed);
    }

    [Fact]
    public void RepairDeclinesSomethingThatIsNotABin()
        => Assert.False(BinNullStructRepair.TryRepair("not a bin at all, really"u8.ToArray(), out _, out _));

    // ---- what SafeBinTree does with it ----

    /// <summary>The whole point: the broken file loads with EVERY entry intact. Before M413 the tolerant
    /// fallback kept the object and dropped its items map, silently deleting every placement in it.</summary>
    [Fact]
    public void SafeBinTreeLoadsTheBrokenFileWithoutLosingAnything()
    {
        var tree = SafeBinTree.Parse(Broken, out var issues);

        var o = Assert.Single(tree.Objects).Value;
        var map = Assert.IsType<BinTreeMap>(Assert.Single(o.Properties).Value);
        Assert.Equal(3, map.Count());
        Assert.Equal(2, map.Count(kv => kv.Value is BinTreeStruct { ClassHash: 0 }));
        var real = map.Single(kv => kv.Value is BinTreeStruct { ClassHash: ItemClass }).Value;
        Assert.Equal(1.5f, Assert.IsType<BinTreeF32>(Assert.IsType<BinTreeStruct>(real).Properties[ValueField]).Value);

        var issue = Assert.Single(issues);
        Assert.Equal("Non-canonical null struct", issue.Kind);
        Assert.Contains("crash at map load", issue.Message);
    }

    /// <summary>And saving it produces the canonical bytes, so the fix sticks.</summary>
    [Fact]
    public void SavingARepairedTreeWritesTheCanonicalNullForm()
    {
        var tree = SafeBinTree.Parse(Broken);
        using var ms = new MemoryStream();
        tree.Write(ms);
        byte[] saved = ms.ToArray();

        Assert.Equal(Good.Length, saved.Length);
        _ = new BinTree(new MemoryStream(saved, writable: false));   // strictly readable
    }

    /// <summary>A clean file must not be reported as repaired - the issue list drives UI warnings.</summary>
    [Fact]
    public void SafeBinTreeReportsNothingForACanonicalFile()
    {
        _ = SafeBinTree.Parse(Good, out var issues);
        Assert.Empty(issues);
    }

    // ---- the guard for corruption this cannot repair ----

    /// <summary>An object whose property carries an unknown type byte is not the null defect, so the
    /// tolerant reader still has to abandon its tail. That tree must refuse to be written back: the
    /// source file still holds those bytes, and saving would replace them with nothing.</summary>
    [Fact]
    public void ATreeThatLostDataRefusesToBeSaved()
    {
        byte[] bad = Good.ToArray();
        int typeByte = Array.IndexOf(bad, (byte)0x86);       // the map's type byte
        Assert.True(typeByte > 0);
        bad[typeByte] = 0x7f;                               // no such property type

        var tree = SafeBinTree.Parse(bad, out var issues);
        Assert.Contains(issues, i => i.Kind == TolerantBinReader.UnreadableDataKind);
        Assert.True(SafeBinTree.IsLossy(tree, out var info));
        Assert.Equal(1, info!.ObjectsAffected);

        var ex = Assert.Throws<InvalidDataException>(() => SafeBinTree.ThrowIfLossy(tree, "this bin"));
        Assert.Contains("permanently delete", ex.Message);
    }

    /// <summary>A tree from a clean parse must NOT be blocked - the guard has to stay silent in the
    /// normal case or every save in the app starts throwing.</summary>
    [Fact]
    public void ACleanTreeSavesWithoutComplaint()
    {
        var tree = SafeBinTree.Parse(Good);
        Assert.False(SafeBinTree.IsLossy(tree, out _));
        SafeBinTree.ThrowIfLossy(tree, "this bin");
        var repaired = SafeBinTree.Parse(Broken);
        Assert.False(SafeBinTree.IsLossy(repaired, out _));   // repaired is not lossy either
        SafeBinTree.ThrowIfLossy(repaired, "this bin");
    }
}
