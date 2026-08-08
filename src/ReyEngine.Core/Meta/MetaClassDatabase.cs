using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace ReyEngine.Core.Meta;

/// <summary>One property of a meta class, resolved to a single point in history.</summary>
/// <param name="Hash">FNV-1a-32 of the property name.</param>
/// <param name="Name">Empty when the hash is still uncracked upstream.</param>
/// <param name="FieldType">The <c>ft</c> of the type tuple, e.g. "f32", "string", "struct", "container".</param>
/// <param name="KeyType">The <c>kt</c>: element type of a container/option, or map key type. Empty if unused.</param>
/// <param name="ValueType">The <c>vt</c>: map value type. Empty if unused.</param>
/// <param name="KeyHash">The <c>kh</c>: for struct/embed/link, the hash of the referenced class. Empty if unused.</param>
/// <param name="Default">The authored default as raw JSON text, or null when the dump recorded none.</param>
public readonly record struct MetaProperty(
    uint Hash, string Name, string FieldType, string KeyType, string ValueType, string KeyHash, string? Default)
{
    public bool HasName => Name.Length > 0;

    /// <summary>The referenced class hash for struct/embedded/link properties, if the tuple carries one.</summary>
    public bool TryGetReferencedClass(out uint classHash)
    {
        classHash = 0;
        return KeyHash.Length > 0 && MetaClassDatabase.TryParseHexHash(KeyHash, out classHash);
    }
}

/// <summary>One meta class, resolved to a single point in history.</summary>
public sealed class MetaClass
{
    public required uint Hash { get; init; }
    /// <summary>Empty when the hash is still uncracked upstream.</summary>
    public required string Name { get; init; }
    public required bool IsInterface { get; init; }
    public required bool IsValue { get; init; }
    /// <summary>Base class hashes, nearest first. Inherited properties live on these.</summary>
    public required IReadOnlyList<uint> Bases { get; init; }
    /// <summary>Properties declared on THIS class only - not including inherited ones. Use
    /// <see cref="MetaClassDatabase.PropertiesOf"/> for the full flattened set.</summary>
    public required IReadOnlyDictionary<uint, MetaProperty> Properties { get; init; }

    public bool HasName => Name.Length > 0;
    public override string ToString() => HasName ? Name : $"0x{Hash:x8}";
}

/// <summary>
/// <para>M367: the LeagueToolkit <c>lol-meta-classes</c> database - Riot's own <c>.bin</c> class and
/// property schema, dumped from the client every patch.</para>
///
/// <para><b>What this adds over the CommunityDragon hash lists.</b> Those give NAMES only, in one flat
/// namespace: a hash resolves to a string or it does not. This gives STRUCTURE - which properties belong to
/// which class, their declared types, their base classes, and the authored DEFAULT for each. That is the
/// difference between an editor that can only show fields a file happens to contain, and one that can show
/// every field the class actually has, correctly typed, with the value the game assumes when it is absent.
/// The recurring "absence != zero" problem in the VFX work is exactly what the defaults answer.</para>
///
/// <para><b>Everything is versioned by BUILD.</b> Each class and property carries a list of revisions with
/// a <c>from</c> (inclusive) and optional <c>to</c> (exclusive; absent means "still current"). A query
/// therefore has to name the build it is asking about, or accept <see cref="Latest"/>. Answering for the
/// wrong build is how a tool ends up showing a field that the user's patch does not have.</para>
///
/// <para>Parsed with a streaming reader rather than deserialised into objects: the file is ~3.6 MB of deeply
/// nested JSON and only a fraction of it is wanted at any one time.</para>
/// </summary>
public sealed class MetaClassDatabase
{
    private readonly Dictionary<uint, MetaClass> _classes = new();
    private readonly Dictionary<uint, string> _externalTypeNames = new();
    private readonly Dictionary<uint, string> _allNames = new();

    /// <summary>M372: every property hash a class has EVER declared, across all builds - not just the one
    /// resolved for. This is what separates "this field was removed in your patch" from "this field never
    /// existed", and those two need completely different advice.</summary>
    private readonly Dictionary<uint, HashSet<uint>> _everDeclared = new();

    /// <summary>The newest build number the dump covers. 0 when nothing is loaded.</summary>
    public int Latest { get; private set; }

    /// <summary>The build this instance was resolved for. Equals <see cref="Latest"/> unless a specific
    /// build was requested.</summary>
    public int ResolvedBuild { get; private set; }

    public int ClassCount => _classes.Count;
    public int PropertyCount { get; private set; }
    public bool IsEmpty => _classes.Count == 0;

    /// <summary>Patch string to build number, oldest first, as recorded by the dump.</summary>
    public IReadOnlyList<(string Patch, int Build)> Versions { get; private set; } = Array.Empty<(string, int)>();

    public IEnumerable<MetaClass> Classes => _classes.Values;

    public bool TryGetClass(uint classHash, out MetaClass cls) => _classes.TryGetValue(classHash, out cls!);

    /// <summary>M372: did this class EVER declare this property, at any build? Walks bases like
    /// <see cref="TryGetProperty"/> does. Used to tell "your patch removed this field" (true here, absent
    /// at the resolved build) from "this field never existed" (false), which need opposite advice.</summary>
    public bool DeclaredAtAnyBuild(uint classHash, uint propertyHash)
    {
        var seen = new HashSet<uint>();
        var queue = new Queue<uint>();
        queue.Enqueue(classHash);
        while (queue.Count > 0)
        {
            uint h = queue.Dequeue();
            if (!seen.Add(h)) continue;
            if (_everDeclared.TryGetValue(h, out var ever) && ever.Contains(propertyHash)) return true;
            if (_classes.TryGetValue(h, out var cls))
                foreach (var b in cls.Bases) queue.Enqueue(b);
        }
        return false;
    }

    /// <summary>Resolve any class OR property hash to a name. Complements the CommunityDragon bin-name
    /// lists rather than replacing them - the two disagree on coverage in both directions.</summary>
    public bool TryGetName(uint hash, out string name) => _allNames.TryGetValue(hash, out name!);

    /// <summary>A property declared on this class or inherited from any base. Bases are walked
    /// breadth-first and the nearest declaration wins, which is what C++ single-dispatch does and what the
    /// dump's <c>bases</c> ordering implies.</summary>
    public bool TryGetProperty(uint classHash, uint propertyHash, out MetaProperty property)
    {
        property = default;
        var seen = new HashSet<uint>();
        var queue = new Queue<uint>();
        queue.Enqueue(classHash);
        while (queue.Count > 0)
        {
            uint h = queue.Dequeue();
            if (!seen.Add(h) || !_classes.TryGetValue(h, out var cls)) continue;
            if (cls.Properties.TryGetValue(propertyHash, out property)) return true;
            foreach (var b in cls.Bases) queue.Enqueue(b);
        }
        return false;
    }

    /// <summary>Every property of a class INCLUDING inherited ones, nearest declaration winning. This is the
    /// set an editor should offer, not <c>MetaClass.Properties</c>.</summary>
    public IReadOnlyList<MetaProperty> PropertiesOf(uint classHash)
    {
        var result = new Dictionary<uint, MetaProperty>();
        var seen = new HashSet<uint>();
        var queue = new Queue<uint>();
        queue.Enqueue(classHash);
        while (queue.Count > 0)
        {
            uint h = queue.Dequeue();
            if (!seen.Add(h) || !_classes.TryGetValue(h, out var cls)) continue;
            foreach (var (ph, p) in cls.Properties) result.TryAdd(ph, p);   // nearest wins
            foreach (var b in cls.Bases) queue.Enqueue(b);
        }
        return result.Values.OrderBy(p => p.HasName ? p.Name : $"~0x{p.Hash:x8}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Load and resolve for a build. <paramref name="build"/> null means "latest". Never throws on
    /// a malformed or absent file - returns an empty database, because a missing optional dictionary must
    /// degrade to "no extra names" rather than take the app down.</summary>
    public static MetaClassDatabase Load(string path, int? build = null, Action<string>? log = null)
    {
        var db = new MetaClassDatabase();
        if (!File.Exists(path)) { log?.Invoke($"meta database not found at {path}"); return db; }
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
            db.Populate(doc.RootElement, build, log);
        }
        catch (Exception ex) { log?.Invoke($"meta database could not be read: {ex.Message}"); }
        return db;
    }

    private void Populate(JsonElement root, int? wantBuild, Action<string>? log)
    {
        if (root.TryGetProperty("latest", out var latestEl) && latestEl.TryGetInt32(out int latest))
            Latest = latest;

        if (root.TryGetProperty("versions", out var versionsEl) && versionsEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<(string, int)>();
            foreach (var v in versionsEl.EnumerateArray())
            {
                string patch = v.TryGetProperty("patch", out var p) ? p.GetString() ?? "" : "";
                int b = v.TryGetProperty("build", out var bEl) && bEl.TryGetInt32(out int bi) ? bi : 0;
                list.Add((patch, b));
            }
            Versions = list;
        }

        ResolvedBuild = wantBuild ?? Latest;

        if (root.TryGetProperty("externalTypeNames", out var ext) && ext.ValueKind == JsonValueKind.Object)
            foreach (var e in ext.EnumerateObject())
                if (TryParseHexHash(e.Name, out uint h) && e.Value.GetString() is { Length: > 0 } n)
                {
                    _externalTypeNames[h] = n;
                    _allNames[h] = n;
                }

        if (!root.TryGetProperty("classes", out var classes) || classes.ValueKind != JsonValueKind.Object)
        {
            log?.Invoke("meta database has no 'classes' object");
            return;
        }

        int props = 0;
        foreach (var classProp in classes.EnumerateObject())
        {
            if (!TryParseHexHash(classProp.Name, out uint classHash)) continue;
            var el = classProp.Value;

            string name = el.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            if (name.Length > 0) _allNames[classHash] = name;

            // Pick the revision covering the requested build. Absent 'to' means "still current".
            bool isInterface = false, isValue = false;
            var bases = new List<uint>();
            if (el.TryGetProperty("revisions", out var revs) && revs.ValueKind == JsonValueKind.Array)
            {
                if (PickRevision(revs, ResolvedBuild) is { } rev)
                {
                    isInterface = rev.TryGetProperty("interface", out var i) && i.ValueKind == JsonValueKind.True;
                    isValue = rev.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
                    if (rev.TryGetProperty("bases", out var basesEl) && basesEl.ValueKind == JsonValueKind.Array)
                        foreach (var b in basesEl.EnumerateArray())
                            if (b.GetString() is { } bs && TryParseHexHash(bs, out uint bh)) bases.Add(bh);
                }
                else continue;   // the class does not exist at this build at all
            }

            var properties = new Dictionary<uint, MetaProperty>();
            if (el.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var pj in propsEl.EnumerateObject())
                {
                    if (!TryParseHexHash(pj.Name, out uint propHash)) continue;
                    string pName = pj.Value.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
                    if (pName.Length > 0) _allNames.TryAdd(propHash, pName);

                    // Recorded BEFORE the build filter below, deliberately: a property that exists only in
                    // older revisions still counts as "this class used to have it".
                    if (!_everDeclared.TryGetValue(classHash, out var ever))
                        _everDeclared[classHash] = ever = new HashSet<uint>();
                    ever.Add(propHash);

                    if (!pj.Value.TryGetProperty("revisions", out var pRevs)
                        || pRevs.ValueKind != JsonValueKind.Array) continue;
                    if (PickRevision(pRevs, ResolvedBuild) is not { } pRev) continue;   // absent at this build

                    string ft = "", kt = "", vt = "", kh = "";
                    if (pRev.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Array)
                    {
                        int idx = 0;
                        foreach (var part in t.EnumerateArray())
                        {
                            string s = part.ValueKind == JsonValueKind.String ? part.GetString() ?? "" : "";
                            switch (idx++)
                            {
                                case 0: ft = s; break;
                                case 1: kt = s; break;
                                case 2: vt = s; break;
                                case 3: kh = s; break;
                            }
                        }
                    }
                    // Kept as raw JSON text rather than a parsed value: defaults are typed by ft and range
                    // over every bin type, so parsing here would mean a second, lossy type system.
                    string? def = pRev.TryGetProperty("default", out var d) && d.ValueKind != JsonValueKind.Null
                        ? d.GetRawText()
                        : null;

                    properties[propHash] = new MetaProperty(propHash, pName, ft, kt, vt, kh, def);
                    props++;
                }
            }

            _classes[classHash] = new MetaClass
            {
                Hash = classHash, Name = name, IsInterface = isInterface, IsValue = isValue,
                Bases = bases, Properties = properties,
            };
        }
        PropertyCount = props;
        // InvariantCulture deliberately: this string reaches the UI log, and on a German system the
        // default would render 4,533 as "4.533", which reads as four-point-five.
        log?.Invoke(string.Format(CultureInfo.InvariantCulture,
            "meta classes: {0:n0} class(es), {1:n0} property revision(s) at build {2} (latest {3}).",
            _classes.Count, props, ResolvedBuild, Latest));
    }

    /// <summary>The revision covering <paramref name="build"/>: from &lt;= build &lt; to, with an absent
    /// 'to' meaning still current. Null when the entity did not exist at that build.</summary>
    private static JsonElement? PickRevision(JsonElement revisions, int build)
    {
        JsonElement? best = null;
        int bestFrom = int.MinValue;
        foreach (var rev in revisions.EnumerateArray())
        {
            int from = rev.TryGetProperty("from", out var f) && f.TryGetInt32(out int fi) ? fi : 0;
            bool hasTo = rev.TryGetProperty("to", out var t) && t.TryGetInt32(out int ti);
            int to = hasTo ? t.GetInt32() : int.MaxValue;
            if (build < from || build >= to) continue;
            if (from >= bestFrom) { bestFrom = from; best = rev; }
        }
        return best;
    }

    /// <summary>Parse the dump's hex hash strings ("0x1003c990"). Tolerates a missing 0x prefix.</summary>
    public static bool TryParseHexHash(string s, out uint hash)
    {
        hash = 0;
        if (string.IsNullOrEmpty(s)) return false;
        var span = s.AsSpan();
        if (span.Length > 2 && (span[1] == 'x' || span[1] == 'X')) span = span[2..];
        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
    }
}
