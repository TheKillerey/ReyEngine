using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>One diagnostic of the entry phase, before its edit index is known.</summary>
internal sealed record PhaseReport(GameDataDiagnosticKind Kind, string Path, SkippedProperty? Property, string? Detail);

/// <summary>The entry phase's reports and how many property keys it set.</summary>
internal sealed class PhaseOutcome
{
    public List<PhaseReport> Reports { get; } = new();

    public int Properties { get; set; }
}

/// <summary>
/// M817: the entry-edit phase (<c>apply/entries.rs</c>): property edits lowered to one patch per property key. A block on a struct is
/// flattened to leaf edits; leaves are grouped by path; each key's set, removals and additions produce one value. Every skip is a report.
/// </summary>
internal sealed class EntryPhase
{
    private enum Typing { Schema, Base, Fallback }

    /// <summary>A property edit with block descent applied.</summary>
    private sealed record Leaf(PropertyPath Path, Sign Sign, GameDataLiteral Value);

    /// <summary>The leaf edits of one property key.</summary>
    private sealed class Group
    {
        public Group(PropertyPath path) { Path = path; }

        public PropertyPath Path { get; }
        public GameDataLiteral? Set { get; private set; }
        public List<GameDataLiteral> Removals { get; } = new();
        public List<GameDataLiteral> Additions { get; } = new();

        public void Push(Leaf leaf)
        {
            switch (leaf.Sign)
            {
                case Sign.Set: Set = leaf.Value; break;
                case Sign.Add: Additions.Add(leaf.Value); break;
                default: Removals.Add(leaf.Value); break;
            }
        }

        /// <summary>The sign of the first operation, the one a resolution report names.</summary>
        public Sign FirstSign => Set is not null ? Sign.Set : Removals.Count == 0 ? Sign.Add : Sign.Remove;
    }

    /// <summary>Where a leaf edit lands: the property's shape and its base value.</summary>
    private readonly record struct Site(GameDataShape Shape, PropValue? Base, Typing Typing);

    private readonly OrderedMap<PropObject> _objects;
    private readonly IGameDataSchema _schema;
    private readonly Coercer _coercer;
    private readonly HashSet<uint>? _owned;

    public EntryPhase(OrderedMap<PropObject> objects, Coercer coercer, HashSet<uint>? owned)
    {
        _objects = objects;
        _schema = coercer.Schema;
        _coercer = coercer;
        _owned = owned;
    }

    private WorkMeter? Meter => _coercer.Meter;

    public PhaseOutcome Outcome { get; } = new();

    /// <summary>The record each settled key of an object outside the owned set lowers to, in settle order (a PTCH target).</summary>
    public List<PatchRecord> Records { get; } = new();

    /// <summary>Runs the entry edits of one batch.</summary>
    public void Run(IEnumerable<KeyValuePair<EntryName, List<PropertyEdit>>> entries)
    {
        foreach (var (name, edits) in entries) Entry(name, edits);
    }

    /// <summary>The property a path names, independent of how the path spells it: the segments joined by <c>.</c>, each the field hash and the
    /// subscript as written. A name hashes ASCII case-insensitively, so <c>mFoo</c>, <c>MFoo</c> and the hash itself are one property. A
    /// <c>{key}</c> is compared as its literal text: <c>{"Key"}</c> and <c>{"key"}</c> are two, even where the map's key kind makes them one.</summary>
    public static string Identity(PropertyPath path)
    {
        var text = new StringBuilder();
        var segments = path.Segments;
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0) text.Append('.');
            var segment = segments[i];
            text.Append(LtkHash.Hash32Of(segment.Name).ToString("x8", CultureInfo.InvariantCulture));
            if (segment.Index is { } index) text.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
            else if (segment.Key is { } key) text.Append('{').Append(key.ToString()).Append('}');
        }
        return text.ToString();
    }

    private void Skip(EntryName entry, string path, PropertySkipReason reason)
    {
        Meter?.Charge(GameDataApplier.DiagnosticWeight);
        Outcome.Reports.Add(new PhaseReport(GameDataDiagnosticKind.PropertyEditSkipped, path, new SkippedProperty(entry, reason), null));
    }

    private void Entry(EntryName name, List<PropertyEdit> edits)
    {
        Meter?.Charge(edits.Count + 1);
        uint hash = name.ObjectHash;
        if (!_objects.TryGetValue(hash, out var obj))
        {
            foreach (var edit in edits) Skip(name, edit.Key, PropertySkipReason.MissingObject);
            return;
        }

        // every edit is flattened against the object as it stands before this entry's edits
        var leaves = new List<Leaf>();
        foreach (var edit in edits) Flatten(name, obj, null, edit, leaves);

        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        var order = new List<Group>();
        foreach (var leaf in leaves)
        {
            Meter?.Charge(1);
            string identity = Identity(leaf.Path);
            if (!groups.TryGetValue(identity, out var group))
            {
                group = new Group(leaf.Path);
                groups[identity] = group;
                order.Add(group);
            }
            group.Push(leaf);
        }

        foreach (var group in order)
        {
            var failure = Settle(obj, group);
            if (failure is null) Outcome.Properties++;
            else Skip(name, failure.Value.Sign.Text() + group.Path.Text, failure.Value.Reason);
        }
    }

    /// <summary>Appends the leaf edits of <paramref name="edit"/> under <paramref name="prefix"/>: the edit itself, or the edits of its block
    /// when the base at its path is a struct.</summary>
    private void Flatten(EntryName name, PropObject obj, PropertyPath? prefix, PropertyEdit edit, List<Leaf> leaves)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        Meter?.Charge(1);
        var path = prefix is null ? edit.Path : PropertyPath.Join(prefix, edit.Path);
        string Key() => edit.Sign.Text() + path.Text;

        // a reference is a one-key mapping, so it would descend as a block and have `ref` read as a field name; it names a value instead
        if (edit.Value is GdMapping block && edit.Value.Pinned() is null && edit.Value.ReferenceText() is null)
        {
            PropValue? baseValue = PropWalk.Resolve(obj, path, out var resolved, Meter) is null ? resolved : null;
            if (baseValue is PropStruct { Kind: PropKind.Struct, ClassHash: 0 })
            {
                Skip(name, Key(), PropertySkipReason.NullPointer);
                return;
            }
            if (baseValue is PropStruct)
            {
                if (edit.Sign != Sign.Set)
                {
                    Skip(name, Key(), PropertySkipReason.SignOnScalar);
                    return;
                }
                foreach (var (innerKey, value) in block.Entries)
                {
                    if (!PropertyEdit.TryParse(innerKey, value, out var inner))
                    {
                        Skip(name, path.Text + "." + innerKey, PropertySkipReason.InvalidPath);
                        continue;
                    }
                    Flatten(name, obj, path, inner!, leaves);
                }
                return;
            }
        }
        leaves.Add(new Leaf(path, edit.Sign, edit.Value));
    }

    /// <summary>Types the property at <paramref name="path"/> of the object.</summary>
    private bool Locate(PropObject obj, PropertyPath path, out Site site, out PropertySkipReason reason)
    {
        site = default;
        reason = default;
        var segments = path.Segments;
        var last = segments[^1];
        uint cls = obj.ClassHash;
        OrderedMap<PropValue> properties = obj.Properties;
        if (segments.Count > 1)
        {
            var error = PropWalk.Resolve(obj, path.Prefix(segments.Count - 1), out var parent, Meter);
            if (error is not null) { reason = error.PropertyReason; return false; }
            switch (parent)
            {
                case PropStruct { Kind: PropKind.Struct, ClassHash: 0 }:
                    reason = PropertySkipReason.NullPointer;
                    return false;
                case PropStruct holder:
                    cls = holder.ClassHash;
                    properties = holder.Properties;
                    break;
                default:
                    reason = PropertySkipReason.CannotDescend;
                    return false;
            }
        }

        uint field = LtkHash.Hash32Of(last.Name);
        if (last.HasSubscript)
        {
            // the shape of an element is that of the existing element; no schema is consulted
            var error = PropWalk.Resolve(obj, path, out var element, Meter);
            if (error is not null) { reason = error.PropertyReason; return false; }
            site = new Site(GameDataShape.Of(element!), element, Typing.Schema);
            return true;
        }

        properties.TryGetValue(field, out var existing);
        if (_schema.Expected(cls, field) is { } expected)
        {
            site = new Site(expected, existing, Typing.Schema);
            return true;
        }
        if (existing is not null)
        {
            site = new Site(GameDataShape.Of(existing), existing, Typing.Base);
            return true;
        }
        if (_schema.Fallback(cls, field) is not { } fallback)
        {
            reason = PropertySkipReason.Untypable;
            return false;
        }
        site = new Site(fallback, null, Typing.Fallback);
        return true;
    }

    /// <summary>Computes and sets the value of one property key; the sign and reason of the failure, if any.</summary>
    private (Sign Sign, PropertySkipReason Reason)? Settle(PropObject obj, Group group)
    {
        if (!Locate(obj, group.Path, out var site, out var locateReason)) return (group.FirstSign, locateReason);
        if (site.Typing != Typing.Schema) ReportFallback(group.Path);

        _coercer.FellBack = false;
        var written = Write(obj, group, site);
        if (site.Typing == Typing.Schema && _coercer.FellBack) ReportFallback(group.Path);
        return written;
    }

    /// <summary>Reports the property key at <paramref name="path"/> as typed without the schema's answer.</summary>
    private void ReportFallback(PropertyPath path)
    {
        Meter?.Charge(GameDataApplier.DiagnosticWeight);
        Outcome.Reports.Add(new PhaseReport(GameDataDiagnosticKind.SchemaFallback, path.Text, null, null));
    }

    /// <summary>Computes the value of one located property key and patches it into the object.</summary>
    private (Sign Sign, PropertySkipReason Reason)? Write(PropObject obj, Group group, Site site)
    {
        PropValue? current = site.Base;
        if (group.Set is { } set)
        {
            var value = _coercer.Coerce(set, site.Shape, site.Base);
            if (!value.IsOk) return (Sign.Set, value.Reason);
            current = value.Value;
        }

        if (group.Removals.Count > 0 || group.Additions.Count > 0)
        {
            var open = Contained.Of(current, site.Shape, out var openReason, Meter);
            Contained contained;
            if (openReason is { } refusal) return (group.FirstSign, refusal);
            if (open is not null)
            {
                if (open.Shape != site.Shape) return (group.FirstSign, PropertySkipReason.TypeMismatch);
                contained = open;
            }
            else if (group.Removals.Count == 0)
            {
                var empty = Contained.Empty(site.Shape, out var emptyReason);
                if (empty is null) return (Sign.Add, emptyReason);
                contained = empty;
            }
            else return (Sign.Remove, PropertySkipReason.ContainerAbsent);

            foreach (var removal in group.Removals)
            {
                var reason = contained.Remove(removal, _coercer, site.Shape);
                if (reason is { } failed) return (Sign.Remove, failed);
            }
            foreach (var addition in group.Additions)
            {
                var reason = contained.Add(addition, _coercer, site.Shape);
                if (reason is { } failed) return (Sign.Add, failed);
            }
            current = contained.IntoValue();
        }

        var settled = current!;
        bool recorded = _owned is not null && !_owned.Contains(obj.PathHash);
        if (recorded && group.Path.Segments.Any(segment => LtkHash.IsHashForm(segment.Name)))
            return (group.FirstSign, PropertySkipReason.HashFormPath);

        var pathError = PropWalk.ValuePathOf(obj, group.Path, out var at, Meter);
        if (pathError is not null) return (group.FirstSign, pathError.PropertyReason);
        var copy = recorded ? settled.Clone(0, Meter) : null;
        var failure = PropWalk.PatchAt(obj, at, settled, out _, Meter);
        if (failure is not null) return (group.FirstSign, failure.PropertyReason);
        if (copy is not null) Records.Add(new PatchRecord(obj.PathHash, group.Path, copy));
        return null;
    }

    /// <summary>The elements of a list or the entries of a map, open for removals and additions.</summary>
    private sealed class Contained
    {
        private readonly bool _unordered;
        private readonly PropKind? _key;
        private readonly PropKind _item;
        private readonly bool _isMap;
        private readonly List<PropValue> _items = new();
        private readonly List<KeyValuePair<PropValue, PropValue>> _entries = new();

        private Contained(bool isMap, bool unordered, PropKind? key, PropKind item)
        {
            _isMap = isMap;
            _unordered = unordered;
            _key = key;
            _item = item;
        }

        /// <summary>The container <paramref name="current"/> holds, null for an absent property of container shape (with no reason) or when
        /// the site is no container (with <see cref="PropertySkipReason.SignOnScalar"/>). The container is a copy: the held value is not edited.</summary>
        public static Contained? Of(PropValue? current, GameDataShape shape, out PropertySkipReason? reason, WorkMeter? meter)
        {
            reason = null;
            if (shape.Kind is not (PropKind.Container or PropKind.UnorderedContainer or PropKind.Map))
            {
                reason = PropertySkipReason.SignOnScalar;
                return null;
            }
            switch (current)
            {
                case null:
                    return null;
                case PropList list:
                {
                    var open = new Contained(false, list.Kind == PropKind.UnorderedContainer, null, list.ItemKind);
                    meter?.Charge(list.Items.Count + 1);
                    open._items.AddRange(list.Items);
                    return open;
                }
                case PropMapValue map:
                {
                    var open = new Contained(true, false, map.KeyKind, map.ValueKind);
                    meter?.Charge(map.Entries.Count + 1);
                    open._entries.AddRange(map.Entries);
                    return open;
                }
                default:
                    reason = PropertySkipReason.SignOnScalar;
                    return null;
            }
        }

        /// <summary>The shape the container holds.</summary>
        public GameDataShape Shape => _isMap
            ? new GameDataShape(PropKind.Map, _key, _item)
            : new GameDataShape(_unordered ? PropKind.UnorderedContainer : PropKind.Container, null, _item);

        /// <summary>The empty container of <paramref name="shape"/>, for <c>+</c> on a property the base omits.</summary>
        public static Contained? Empty(GameDataShape shape, out PropertySkipReason reason)
        {
            reason = PropertySkipReason.Untypable;
            if (shape.Item is not { } item) return null;
            if (shape.Kind == PropKind.Map)
            {
                if (shape.Key is not { } key) return null;
                return new Contained(true, false, key, item);
            }
            return new Contained(false, shape.Kind == PropKind.UnorderedContainer, null, item);
        }

        /// <summary>Removes what <paramref name="removal"/> names: elements by value or by index, entries by key.</summary>
        public PropertySkipReason? Remove(GameDataLiteral removal, Coercer coercer, GameDataShape shape)
        {
            if (!_isMap && _item is PropKind.Struct or PropKind.Embedded)
            {
                if (removal is not GdList indices) return PropertySkipReason.KindMismatch;
                coercer.Meter?.Charge(indices.Items.Count + 1);
                var positions = new List<int>(Math.Min(indices.Items.Count, PropLimits.MaxReserveBytes / 4));
                foreach (var index in indices.Items)
                {
                    if (index is not GdInteger integer) return PropertySkipReason.KindMismatch;
                    if (integer.Value < 0 || integer.Value >= _items.Count) return PropertySkipReason.RemovalUnmatched;
                    positions.Add((int)integer.Value);
                }
                positions.Sort();
                for (int i = positions.Count - 1; i >= 0; i--)
                {
                    // a duplicated index is removed once
                    if (i + 1 < positions.Count && positions[i] == positions[i + 1]) continue;
                    coercer.Meter?.Charge(1 + ((_items.Count - positions[i]) >> 5));
                    _items.RemoveAt(positions[i]);
                }
                return null;
            }

            if (!_isMap)
            {
                var coerced = coercer.Coerce(removal, shape, null);
                if (!coerced.IsOk) return coerced.Reason;
                foreach (var element in ListItems(coerced.Value!))
                {
                    coercer.Meter?.Charge(_items.Count + 1);
                    int before = _items.Count;
                    _items.RemoveAll(candidate => PropValue.Equal(candidate, element));
                    if (_items.Count == before) return PropertySkipReason.RemovalUnmatched;
                }
                return null;
            }

            if (removal is not GdList keys) return PropertySkipReason.KindMismatch;
            foreach (var spelled in keys.Items)
            {
                string text;
                switch (spelled)
                {
                    case GdString s: text = s.Value; break;
                    case GdInteger n: text = n.Value.ToString(CultureInfo.InvariantCulture); break;
                    case GdBool b: text = b.Value ? "true" : "false"; break;
                    default: return PropertySkipReason.KindMismatch;
                }
                var wanted = coercer.Key(text, _key!.Value);
                if (!wanted.IsOk) return wanted.Reason;
                coercer.Meter?.Charge(_entries.Count + 1);
                int before = _entries.Count;
                _entries.RemoveAll(candidate => PropValue.Equal(candidate.Key, wanted.Value));
                if (_entries.Count == before) return PropertySkipReason.RemovalUnmatched;
            }
            return null;
        }

        /// <summary>Appends the elements of <paramref name="addition"/>, or adds or replaces its entries by key.</summary>
        public PropertySkipReason? Add(GameDataLiteral addition, Coercer coercer, GameDataShape shape)
        {
            var added = coercer.Coerce(addition, shape, null);
            if (!added.IsOk) return added.Reason;
            if (!_isMap)
            {
                var more = ListItems(added.Value!);
                coercer.Meter?.Charge(more.Count + 1);
                _items.AddRange(more);
                return null;
            }
            if (added.Value is not PropMapValue map) return PropertySkipReason.KindMismatch;
            foreach (var entry in map.Entries)
            {
                coercer.Meter?.Charge(_entries.Count + 1);
                int at = _entries.FindIndex(candidate => PropValue.Equal(candidate.Key, entry.Key));
                if (at >= 0) _entries[at] = new(_entries[at].Key, entry.Value);
                else _entries.Add(entry);
            }
            return null;
        }

        public PropValue IntoValue()
        {
            if (_isMap) return new PropMapValue(_key!.Value, _item, _entries);
            return new PropList(_unordered ? PropKind.UnorderedContainer : PropKind.Container, _item, _items);
        }

        private static List<PropValue> ListItems(PropValue value) => value is PropList list ? list.Items : new List<PropValue>();
    }
}
