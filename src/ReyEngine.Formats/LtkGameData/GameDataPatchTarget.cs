namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M817: application of edits over a PTCH (<c>apply/patch.rs</c>): every edit lowered to the patch's own deletions, objects and records.
/// The edits run over a view: the objects the patch holds, and the game's copy of each other object an edit names with the patch's
/// records laid over it. An object the patch holds takes an edit in place. Every other object takes one record per settled property key.
/// </summary>
internal static class PatchTarget
{
    public static GameDataApplyResult Apply(
        byte[] baseBytes,
        IReadOnlyList<GameDataEdit> edits,
        ReadOverrideFile readOverride,
        ReadGameEntry readEntry,
        IGameDataSchema schema,
        WorkMeter meter)
    {
        // a PTCH target is decoded like an override file, and is counted like one
        meter.Override(baseBytes.Length);
        PtchBin patch;
        try { patch = PropCodec.ReadPtch(baseBytes, meter); }
        catch (PropDecodeException e) { throw new GameDataException(GameDataErrorKind.Bin, e.Message); }

        var diagnostics = new List<GameDataDiagnostic>();
        var references = GameDataApplier.ResolveReferences(edits, readEntry, diagnostics, meter);
        var coercer = new Coercer(schema, references, meter);

        var view = new View(patch, meter);
        var applied = new GameDataApplied();
        for (int index = 0; index < edits.Count; index++)
        {
            meter.Check();
            var edit = edits[index];
            foreach (var path in edit.Overrides)
            {
                var file = GameDataApplier.ReadOverrideFileOf(path, readOverride, index, diagnostics, meter);
                if (file is null) continue;
                // the skipped records of a merged file are not reported, and every record counts
                applied.Records += file.Patches.Count;
                applied.Objects += file.Objects.Count + file.Deleted.Count;
                view.Merge(file);
            }

            view.Load(edit, readEntry, index, diagnostics);

            var before = view.Keys();
            var created = ObjectPhase.Create(view.Bin, coercer, edit.Objects);
            applied.Objects += created.Objects;
            GameDataApplier.ReportObjects(diagnostics, index, created.Skipped, meter);
            view.OwnNew(before);

            var phase = new EntryPhase(view.Bin, coercer, view.Owned);
            phase.Run(edit.Entries);
            view.Record(phase.Records);
            applied.Properties += GameDataApplier.ReportProperties(diagnostics, index, created.Sets, meter);
            applied.Properties += GameDataApplier.ReportProperties(diagnostics, index, phase.Outcome, meter);

            before = view.Keys();
            var (skipped, removed) = ObjectPhase.Remove(view.Bin, edit.Objects, meter);
            applied.Objects += removed;
            GameDataApplier.ReportObjects(diagnostics, index, skipped, meter);
            view.DropMissing(before);

            // a PTCH holds no dependency list
            foreach (var path in edit.Links.Add.Concat(edit.Links.Remove))
            {
                meter.Charge(GameDataApplier.DiagnosticWeight);
                diagnostics.Add(new GameDataDiagnostic(GameDataDiagnosticKind.LinkUnsupported, index, path.Text));
            }
        }

        meter.Check();
        if (!applied.TreeChanged) return new GameDataApplyResult((byte[])baseBytes.Clone(), Array.Empty<string>(), applied, diagnostics, meter.Work);
        return new GameDataApplyResult(PropCodec.WritePtch(view.IntoPatch(), meter), Array.Empty<string>(), applied, diagnostics, meter.Work);
    }

    /// <summary>
    /// The records a view holds, in the order they were taken, and which of them still count. A record is dropped by being marked, not
    /// by being cut out of a list, and every record is found by the object it patches, so a record costs what the records of its own object
    /// cost and no more.
    /// </summary>
    private sealed class Records
    {
        private sealed class Held
        {
            public Held(PatchRecord record) { Record = record; }

            public PatchRecord Record { get; }

            public bool Live { get; set; } = true;
        }

        private readonly List<Held> _all = new();
        private readonly Dictionary<uint, List<Held>> _byObject = KeyHash.Map<List<Held>>();

        public Records(IEnumerable<PatchRecord> records)
        {
            foreach (var record in records) Add(record);
        }

        public void Add(PatchRecord record)
        {
            var held = new Held(record);
            _all.Add(held);
            if (!_byObject.TryGetValue(record.ObjectHash, out var list)) _byObject[record.ObjectHash] = list = new List<Held>();
            list.Add(held);
        }

        /// <summary>The live records of one object, in order.</summary>
        public List<PatchRecord> Of(uint hash)
        {
            var found = new List<PatchRecord>();
            if (_byObject.TryGetValue(hash, out var list))
                foreach (var held in list)
                    if (held.Live) found.Add(held.Record);
            return found;
        }

        /// <summary>Drops every record of an object.</summary>
        public void RemoveObject(uint hash, WorkMeter meter)
        {
            if (!_byObject.Remove(hash, out var list)) return;
            meter.Charge(list.Count);
            foreach (var held in list) held.Live = false;
        }

        /// <summary>Drops every record of an object at <paramref name="identity"/> or under it.</summary>
        public void RemoveCovered(uint hash, string identity, WorkMeter meter)
        {
            if (!_byObject.TryGetValue(hash, out var list)) return;
            meter.Charge(list.Count);
            list.RemoveAll(held =>
            {
                if (!held.Live) return true;
                if (!Covers(identity, held.Record.Identity)) return false;
                held.Live = false;
                return true;
            });
        }

        public List<PatchRecord> ToList()
        {
            var records = new List<PatchRecord>(_all.Count);
            foreach (var held in _all)
                if (held.Live) records.Add(held.Record);
            return records;
        }

        /// <summary>Whether the property at identity <paramref name="held"/> is the one at <paramref name="path"/> or lies under it.</summary>
        private static bool Covers(string path, string held)
        {
            if (!held.StartsWith(path, StringComparison.Ordinal)) return false;
            return held.Length == path.Length || held[path.Length] is '.' or '[' or '{';
        }
    }

    private sealed class View
    {
        private readonly PtchBin _patch;
        private readonly WorkMeter _meter;
        private readonly HashSet<uint> _asked = KeyHash.Set();
        private readonly HashSet<uint> _deleted;
        private readonly Records _records;

        public View(PtchBin patch, WorkMeter meter)
        {
            _patch = patch;
            _meter = meter;
            Bin = patch.Objects;
            for (int i = 0; i < Bin.Count; i++) Owned.Add(Bin.KeyAt(i));
            _deleted = KeyHash.Set(patch.Deleted);
            _records = new Records(patch.Patches);
        }

        /// <summary>The objects the patch holds, then the game's copy of each other object an edit names, with the patch's records laid over it.</summary>
        public OrderedMap<PropObject> Bin { get; }

        /// <summary>The objects the patch holds.</summary>
        public HashSet<uint> Owned { get; } = KeyHash.Set();

        /// <summary>The keys of the view's objects, in order.</summary>
        public List<uint> Keys()
        {
            _meter.Charge(Bin.Count + 1);
            return Bin.Keys.ToList();
        }

        /// <summary>Takes an override file's deletions, objects and records into the patch, and lays them over the view. A record over an
        /// object the view has not read applies when it is read.</summary>
        public void Merge(PtchBin file)
        {
            _meter.Charge(file.Objects.Count + file.Deleted.Count + file.Patches.Count + 1);
            for (int i = 0; i < file.Objects.Count; i++) Owned.Add(file.Objects.KeyAt(i));
            _patch.Deleted.AddRange(file.Deleted);
            _deleted.UnionWith(file.Deleted);
            foreach (var record in file.Patches) _records.Add(new PatchRecord(record.ObjectHash, record.Path, record.Value.Clone(0, _meter)));
            OverrideApplier.Apply(file, Bin, _meter);
        }

        /// <summary>Reads the game's copy of every object <paramref name="edit"/> names that the view lacks, with the patch's records laid
        /// over it. A record the copy does not fit is left out, as the client leaves it out.</summary>
        public void Load(GameDataEdit edit, ReadGameEntry readEntry, int index, List<GameDataDiagnostic> diagnostics)
        {
            var names = new List<EntryName>();
            foreach (var (name, _) in edit.Entries) names.Add(name);
            foreach (var (name, _) in edit.Objects) names.Add(name);
            foreach (var (_, objectEdit) in edit.Objects)
                if (objectEdit is CloneObjectEdit clone) names.Add(clone.Source);

            foreach (var name in names)
            {
                _meter.Charge(1);
                uint hash = name.ObjectHash;
                if (Bin.ContainsKey(hash) || _deleted.Contains(hash) || !_asked.Add(hash)) continue;

                var read = readEntry(name);
                _meter.Check();
                if (read.Error is not null)
                {
                    _meter.Charge(GameDataApplier.DiagnosticWeight);
                    diagnostics.Add(new GameDataDiagnostic(GameDataDiagnosticKind.EntryUnreadable, index, name.Text, Detail: read.Error));
                    continue;
                }
                if (read.Object is null) continue;

                // the object is the caller's, and the view edits its own
                _meter.Grow(PropWalk.MeasureObject(read.Object));
                var obj = read.Object.Clone(_meter);
                foreach (var record in _records.Of(hash))
                    PropWalk.PatchClient(obj, record.Path, record.Value.Clone(0, _meter), out _, _meter);
                Bin.Set(hash, obj);
            }
        }

        /// <summary>Takes every object the view gained since <paramref name="before"/> as the patch's own. A created object the patch deletes
        /// is no longer deleted.</summary>
        public void OwnNew(List<uint> before)
        {
            var known = KeyHash.Set(before);
            _meter.Charge(Bin.Count + 1);
            foreach (uint hash in Bin.Keys.ToList())
            {
                if (known.Contains(hash)) continue;
                Owned.Add(hash);
                if (_deleted.Remove(hash))
                {
                    _meter.Charge(_patch.Deleted.Count);
                    _patch.Deleted.RemoveAll(deleted => deleted == hash);
                }
            }
        }

        /// <summary>Drops every object the view lost since <paramref name="before"/>: from the patch's objects where the patch holds it, into
        /// its deletions where the game does. Its records go with it.</summary>
        public void DropMissing(List<uint> before)
        {
            _meter.Charge(before.Count);
            foreach (uint hash in before)
            {
                if (Bin.ContainsKey(hash)) continue;
                if (!Owned.Remove(hash))
                {
                    _patch.Deleted.Add(hash);
                    _deleted.Add(hash);
                }
                _records.RemoveObject(hash, _meter);
            }
        }

        /// <summary>Appends each record, dropping every held record of its object at its path or under it. The new value was settled over the
        /// view, which those records had already shaped.</summary>
        public void Record(List<PatchRecord> records)
        {
            foreach (var record in records)
            {
                _meter.Charge(1);
                _records.RemoveCovered(record.ObjectHash, record.Identity, _meter);
                _records.Add(record);
            }
        }

        public PtchBin IntoPatch()
        {
            var objects = new OrderedMap<PropObject>();
            for (int i = 0; i < Bin.Count; i++)
                if (Owned.Contains(Bin.KeyAt(i))) objects.Set(Bin.KeyAt(i), Bin.ValueAt(i));
            _patch.Objects = objects;
            _patch.Patches = _records.ToList();
            var seen = KeyHash.Set();
            _patch.Deleted.RemoveAll(hash => !seen.Add(hash));
            return _patch;
        }
    }
}
