namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M817: the object phases of an edit (<c>apply/objects.rs</c>): creation after the override files, removal after the entry edits.
/// </summary>
internal static class ObjectPhase
{
    /// <summary>What the creation phase did: the objects it skipped, how many it created, and the outcome of their <c>set</c> edits.</summary>
    internal sealed class Created
    {
        public List<SkippedObject> Skipped { get; } = new();
        public int Objects { get; set; }
        public PhaseOutcome Sets { get; set; } = new();
    }

    /// <summary>
    /// Creates the cloned and constructed objects of one batch. Every clone reads the table as it is before the first object of the batch
    /// is created. The objects are appended in mapping order, then each one's <c>set</c> runs as the entry edits of its name.
    /// </summary>
    public static Created Create(OrderedMap<PropObject> objects, Coercer coercer, List<KeyValuePair<EntryName, ObjectEdit>> edits)
    {
        var meter = coercer.Meter;
        var created = new Created();
        var taken = KeyHash.Set();
        var built = new List<PropObject>();
        var sets = new List<KeyValuePair<EntryName, List<PropertyEdit>>>();
        foreach (var (name, edit) in edits)
        {
            if (edit is RemoveObjectEdit) continue;
            meter?.Charge(1);
            uint hash = name.ObjectHash;
            PropObject? made = null;
            ObjectSkipReason? reason = null;
            if (objects.ContainsKey(hash) || taken.Contains(hash)) reason = ObjectSkipReason.ObjectExists;
            else if (edit is CloneObjectEdit clone)
            {
                if (objects.TryGetValue(clone.Source.ObjectHash, out var source)) made = CloneAs(source, clone.Source, name, meter);
                else reason = ObjectSkipReason.SourceMissing;
            }
            else if (edit is ConstructObjectEdit construct)
            {
                uint classHash = construct.Class.ClassHash;
                if (coercer.Schema.HasClass(classHash))
                {
                    meter?.Grow(ObjectHeaderBytes);
                    made = new PropObject(hash, classHash);
                }
                else reason = ObjectSkipReason.UnknownClass;
            }

            if (made is not null)
            {
                taken.Add(hash);
                built.Add(made);
                if (edit.Properties.Count > 0) sets.Add(new(name, edit.Properties.ToList()));
            }
            else
            {
                meter?.Charge(GameDataApplier.DiagnosticWeight);
                created.Skipped.Add(new SkippedObject(name, reason!.Value));
            }
        }
        created.Objects = built.Count;
        foreach (var obj in built) objects.Set(obj.PathHash, obj);
        var phase = new EntryPhase(objects, coercer, null);
        phase.Run(sets);
        created.Sets = phase.Outcome;
        return created;
    }

    /// <summary>What an object takes beyond its properties: its class in the table, its size, its path and its property count.</summary>
    private const int ObjectHeaderBytes = 14;

    /// <summary>
    /// A copy of <paramref name="source"/> as the entry <paramref name="name"/>. A top-level hash property holding the hash of the
    /// source, and a top-level string property spelling the path of the source (compared ASCII case-insensitively), name the object itself:
    /// the copy holds the hash of the new name and the new path there. A hash-form name has no path, and a string property is left as it
    /// is where either name is hash-form. A link, a file, a nested value and a <c>0x</c> name are untouched.
    /// </summary>
    private static PropObject CloneAs(PropObject source, EntryName sourceName, EntryName name, WorkMeter? meter)
    {
        // the copy is counted before it is made: a clone of a clone of a large object is how a small edit asks for a great deal of memory
        if (meter is not null) meter.Grow(PropWalk.MeasureObject(source));
        var copy = source.Clone(meter);
        copy.PathHash = name.ObjectHash;
        bool spelled = !sourceName.IsHash && !name.IsHash;
        meter?.Charge(copy.Properties.Count);
        for (int i = 0; i < copy.Properties.Count; i++)
        {
            switch (copy.Properties.ValueAt(i))
            {
                case PropHash { Kind: PropKind.Hash } hash when hash.Value == sourceName.ObjectHash:
                    copy.Properties.SetValueAt(i, new PropHash(PropKind.Hash, name.ObjectHash));
                    break;
                case PropString text when spelled && GameDataApplier.EqualsAsciiIgnoreCase(text.Value, sourceName.Text):
                    copy.Properties.SetValueAt(i, new PropString(name.Text));
                    break;
            }
        }
        return copy;
    }

    /// <summary>Removes the objects of one batch, and returns those the table did not hold with how many it removed. Runs after the entry
    /// edits, in mapping order; the order of the rest of the table is kept.</summary>
    public static (List<SkippedObject> Skipped, int Removed) Remove(OrderedMap<PropObject> objects, List<KeyValuePair<EntryName, ObjectEdit>> edits, WorkMeter? meter = null)
    {
        var skipped = new List<SkippedObject>();
        int removed = 0;
        foreach (var (name, edit) in edits)
        {
            if (edit is not RemoveObjectEdit) continue;
            meter?.Charge(1);
            if (objects.Remove(name.ObjectHash, out int shifted))
            {
                removed++;
                meter?.Charge(shifted >> 5);
            }
            else skipped.Add(new SkippedObject(name, ObjectSkipReason.RemovalUnmatched));
        }
        return (skipped, removed);
    }
}
