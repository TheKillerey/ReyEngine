using System.Reflection;
using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M125: one problem the tolerant reader found (and worked around) while reading a malformed bin.
/// Strict readers (LeagueToolkit-based tools, and the game's own expectations) refuse such files,
/// so these are worth surfacing: the object they live in can be marked in the UI and repaired.
/// </summary>
public sealed record BinRepairIssue(
    uint ObjectPathHash, uint ObjectClassHash, string Kind, uint? FieldHash,
    string Message, string Suggestion);

/// <summary>
/// A fault-tolerant PROP/.bin reader for malformed files that LeagueToolkit's strict reader rejects
/// (e.g. a struct/object with two properties sharing one name hash — common in hand-edited or
/// old-tooling mod bins, which makes <c>new BinTree(stream)</c> throw on its internal ToDictionary).
///
/// It replicates only the container framing (header + per-object size/hash/count) and reuses
/// LeagueToolkit's own per-property reader for the value types, then de-duplicates properties
/// (last value wins) before building real <see cref="BinTree"/>/<see cref="BinTreeObject"/> objects —
/// so the rest of ReyEngine consumes the result unchanged.
/// </summary>
public static class TolerantBinReader
{
    /// <summary>M413: the one issue kind that means DATA WAS LOST. The duplicate-field/object kinds are
    /// deliberate, advertised repairs (saving writes the file back without the duplicate); this one is
    /// not a repair at all - the rest of the object was abandoned. Writers must treat it differently,
    /// so it is a shared constant rather than a string typed twice.</summary>
    public const string UnreadableDataKind = "Unreadable data";

    private static readonly Func<BinaryReader, bool, BinTreeProperty> ReadProperty = BuildReader();

    private static Func<BinaryReader, bool, BinTreeProperty> BuildReader()
    {
        var m = typeof(BinTreeProperty).GetMethod("Read",
            BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(BinaryReader), typeof(bool) }, null)
            ?? throw new MissingMethodException("LeagueToolkit BinTreeProperty.Read(BinaryReader, bool) not found.");
        return (Func<BinaryReader, bool, BinTreeProperty>)Delegate.CreateDelegate(
            typeof(Func<BinaryReader, bool, BinTreeProperty>), m);
    }

    public static BinTree Read(byte[] data) => Read(data, null);

    public static BinTree Read(byte[] data, ICollection<BinRepairIssue>? issues)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
        if (magic == "PTCH")
        {
            br.ReadUInt64();                                  // patch header (unknown)
            magic = Encoding.ASCII.GetString(br.ReadBytes(4));
        }
        if (magic != "PROP") throw new InvalidDataException($"Not a PROP bin (magic '{magic}').");

        uint version = br.ReadUInt32();

        var dependencies = new List<string>();
        if (version >= 2)
        {
            uint depCount = br.ReadUInt32();
            for (int i = 0; i < depCount; i++)
            {
                ushort len = br.ReadUInt16();
                dependencies.Add(Encoding.UTF8.GetString(br.ReadBytes(len)));
            }
        }

        uint objectCount = br.ReadUInt32();
        var classHashes = new uint[objectCount];
        for (int i = 0; i < objectCount; i++) classHashes[i] = br.ReadUInt32();

        var objects = new List<BinTreeObject>((int)objectCount);
        for (int i = 0; i < objectCount; i++)
        {
            uint size = br.ReadUInt32();
            long end = ms.Position + size;
            uint pathHash = br.ReadUInt32();
            ushort propCount = br.ReadUInt16();

            var props = new Dictionary<uint, BinTreeProperty>(propCount);
            for (int p = 0; p < propCount && ms.Position < end; p++)
            {
                BinTreeProperty prop;
                try { prop = ReadProperty(br, false); }
                catch
                {
                    issues?.Add(new BinRepairIssue(pathHash, classHashes[i], UnreadableDataKind, null,
                        $"Only {p} of {propCount} properties could be read - the rest of this object was skipped.",
                        "The unreadable tail is dropped when the bin is saved. Check this object's remaining values before relying on it."));
                    break; // give up on this object's tail; keep what parsed
                }
                if (issues is not null && props.ContainsKey(prop.NameHash))
                    issues.Add(new BinRepairIssue(pathHash, classHashes[i], "Duplicate field", prop.NameHash,
                        "The same field appears twice in this object - strict tools (LeagueToolkit-based) refuse the whole file over this.",
                        "The last value was kept. Saving this bin from ReyEngine (Repair, Save Override, Add Mesh...) writes it back without the duplicate."));
                props[prop.NameHash] = prop; // tolerant: last value wins on duplicate name
            }

            ms.Position = end; // resync to the object boundary regardless
            objects.Add(new BinTreeObject(pathHash, classHashes[i], props.Values));
        }

        // The BinTree ctor keys objects by path-hash (ToDictionary) — so duplicate object hashes (also seen in
        // old-tooling / hand-edited bins, e.g. some bloom.materials.bin copies) would throw. De-dupe here too,
        // last object wins, so those bins load instead of failing outright.
        var byHash = new Dictionary<uint, BinTreeObject>(objects.Count);
        foreach (var o in objects)
        {
            if (issues is not null && byHash.ContainsKey(o.PathHash))
                issues.Add(new BinRepairIssue(o.PathHash, o.ClassHash, "Duplicate object", null,
                    "Two objects in this bin share the same path hash - strict tools refuse the whole file over this.",
                    "The last object was kept. Saving this bin from ReyEngine writes it back with only that one."));
            byHash[o.PathHash] = o;
        }
        return new BinTree(byHash.Values, dependencies);
    }
}

/// <summary>Parse a .bin with LeagueToolkit, transparently falling back to the tolerant reader when
/// the file is malformed (so old-tooling / hand-edited mod bins still load).</summary>
public static class SafeBinTree
{
    /// <summary>M413: what a tolerant parse could not read. Attached to the tree it produced, because a
    /// caller that saves is usually far from the caller that parsed - and the whole failure mode here was
    /// data being lost between those two points with nobody looking at the returned issue list.</summary>
    public sealed record LossyParse(int ObjectsAffected, IReadOnlyList<BinRepairIssue> Issues);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BinTree, LossyParse> Lossy = new();

    public static BinTree Parse(byte[] data) => Parse(data, out _);

    /// <summary>M125: like <see cref="Parse(byte[])"/>, but reports what the tolerant fallback had to
    /// repair. An empty list means the file is well-formed (the strict parser accepted it).</summary>
    public static BinTree Parse(byte[] data, out IReadOnlyList<BinRepairIssue> issues)
    {
        try
        {
            var tree = new BinTree(new MemoryStream(data, writable: false));
            issues = Array.Empty<BinRepairIssue>();
            return tree;
        }
        catch { }

        // M413: before giving up on the strict reader, try the ONE malformation that is fully
        // reconstructible - nullable structs written in the long embedded form. Repairing the bytes and
        // re-running the STRICT parser keeps every property, where the tolerant fallback below would
        // abandon the rest of each affected object. Measured on Map453: 1080 placements kept instead of
        // silently dropped.
        if (BinNullStructRepair.TryRepair(data, out byte[]? repaired, out var repair) && repaired is not null)
        {
            try
            {
                var tree = new BinTree(new MemoryStream(repaired, writable: false));
                issues =
                [
                    new BinRepairIssue(0, 0, "Non-canonical null struct", null,
                        $"{repair.NullsFixed:n0} null struct(s) across {repair.ObjectsFixed} object(s) were written in the "
                        + "long form (class hash 0 followed by a size and field count). The format omits those six bytes, so "
                        + "the game and every strict tool misread everything after the first one - this file would crash at map load.",
                        "They were rewritten to the canonical form and the whole file then parsed strictly, so nothing was lost. "
                        + "Save the bin to make the fix permanent."),
                ];
                return tree;
            }
            catch { }   // repair did not produce a strictly-readable file; fall through
        }

        var list = new List<BinRepairIssue>();
        var tolerant = TolerantBinReader.Read(data, list);
        issues = list;
        int lost = list.Count(i => i.Kind == TolerantBinReader.UnreadableDataKind);
        if (lost > 0) Lossy.AddOrUpdate(tolerant, new LossyParse(lost, list));
        return tolerant;
    }

    /// <summary>Did this tree come from a parse that could NOT read everything? Duplicate fields and
    /// duplicate objects do not count - those are deliberate repairs the UI advertises.</summary>
    public static bool IsLossy(BinTree tree, out LossyParse? info) => Lossy.TryGetValue(tree, out info);

    /// <summary>
    /// Refuse to serialise a tree whose parse dropped data. Writing one back replaces a file that still
    /// holds the unread bytes with a file that does not - the loss becomes permanent, and silently.
    /// </summary>
    public static void ThrowIfLossy(BinTree tree, string what)
    {
        if (!Lossy.TryGetValue(tree, out var info) || info is null) return;
        throw new InvalidDataException(
            $"Refusing to save {what}: {info.ObjectsAffected} object(s) in this bin could not be fully read, "
            + "so saving would permanently delete everything that failed to parse. "
            + "Repair or replace the source file first. First problem: " + info.Issues[0].Message);
    }
}
