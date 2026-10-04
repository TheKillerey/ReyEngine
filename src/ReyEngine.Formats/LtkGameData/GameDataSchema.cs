using System.Globalization;
using System.IO.Hashing;
using System.Text.Json;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>A property type (<c>Shape</c>): a kind, and the kinds a container carries. The key is a map's key kind; the item is a
/// container's or an option's item kind, or a map's value kind.</summary>
public readonly record struct GameDataShape(PropKind Kind, PropKind? Key = null, PropKind? Item = null)
{
    /// <summary>The shape of a kind with no key and no item.</summary>
    public static GameDataShape Bare(PropKind kind) => new(kind);

    /// <summary><c>Shape::of</c>: the shape of a value in a base tree.</summary>
    public static GameDataShape Of(PropValue value)
    {
        var shape = value.Shape;
        return new GameDataShape(shape.Kind, shape.Key, shape.Item);
    }
}

/// <summary>
/// M817: the class schema of the installed patch (<c>ltk_game_data::Schema</c>). It answers the shape of a field on a class, and
/// whether a class exists. <see cref="Expected"/> returning null is "the schema says nothing", never a mismatch: the base value's shape
/// types a property the base holds, and <see cref="Fallback"/> types one the base omits.
/// </summary>
public interface IGameDataSchema
{
    /// <summary>The shape of <paramref name="field"/> on <paramref name="cls"/>. The class is always the actual class of the holder, or the pinned class.</summary>
    GameDataShape? Expected(uint cls, uint field);

    /// <summary>The best-known shape of a field for a build the schema does not describe. Asked only where <see cref="Expected"/> answers
    /// null and the base omits the property; a property typed through it is reported as <c>SchemaFallback</c>.</summary>
    GameDataShape? Fallback(uint cls, uint field) => null;

    /// <summary>Whether the schema knows the class.</summary>
    bool HasClass(uint cls);
}

/// <summary>The schema that says nothing: every property is typed from the base, and no class is known. A struct pin whose class the
/// base value already carries applies, the base being the attestation; a pin on any other class is refused with <c>UnknownClass</c>.</summary>
public sealed class NoSchema : IGameDataSchema
{
    public static readonly NoSchema Instance = new();

    private NoSchema() { }

    public GameDataShape? Expected(uint cls, uint field) => null;

    public bool HasClass(uint cls) => false;
}

/// <summary>A game content build, <c>&lt;major&gt;.&lt;minor&gt;.&lt;build&gt;</c> (<c>GameBuild</c>). The third number is the content build a
/// schema revision is keyed on.</summary>
public readonly record struct GameBuild(uint Major, uint Minor, uint Content) : IComparable<GameBuild>
{
    /// <summary>The patch a player names, <c>16.19</c>.</summary>
    public string Patch => $"{Major}.{Minor}";

    public int CompareTo(GameBuild other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Content.CompareTo(other.Content);
    }

    public override string ToString() => $"{Major}.{Minor}.{Content}";

    /// <summary>Reads a build, ignoring anything after a <c>+</c>: <c>16.19.8230722+branch.releases-16-19.content.release</c>. Three
    /// numbers are needed; a part after them is ignored.</summary>
    public static bool TryParse(string text, out GameBuild build)
    {
        build = default;
        string[] parts = text.Split('+')[0].Split('.');
        if (parts.Length < 3) return false;
        if (!RustParse.TryParseInteger(parts[0], PropKind.U32, out ulong major)
            || !RustParse.TryParseInteger(parts[1], PropKind.U32, out ulong minor)
            || !RustParse.TryParseInteger(parts[2], PropKind.U32, out ulong content))
            return false;
        build = new GameBuild((uint)major, (uint)minor, (uint)content);
        return true;
    }

    /// <summary>The content build of an install: the <c>version</c> of <c>content-metadata.json</c> in its <c>Game</c> directory. Null when
    /// the file is absent or holds no build.</summary>
    public static GameBuild? ReadInstalled(string gameDirectory)
    {
        try
        {
            string path = Path.Combine(gameDirectory, "content-metadata.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                && TryParse(version.GetString()!, out var build))
                return build;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return null;
    }
}

/// <summary>
/// M817: LTK Manager's meta schema database (<c>data/meta/meta.db.json</c>, <c>formatVersion</c> 1) read the way its
/// <c>MetaSchema</c> reads it (<c>meta_schema.rs</c>, v1.21.0), and kept apart from <c>MetaClassDatabase</c>, whose queries are an
/// editor's. The rules a declaration's typing depends on (the first two <c>MetaClassDatabase</c> reads the same way since M817):
///
/// <list type="bullet">
/// <item><b><c>to</c> is inclusive.</b> A revision covers a build when <c>from &lt;= build</c> and <c>to</c> is absent or
/// <c>build &lt;= to</c>; 785 property revisions and 234 class revisions of the database hold for exactly one build.</item>
/// <item><b>The first revision in file order that covers the build answers</b>, not the newest.</item>
/// <item><b>A class exists at any build.</b> <see cref="HasClass"/> asks only whether the database holds the class; the class's
/// own properties answer whether or not a class revision covers the build, and only its bases come from the revisions that do.</item>
/// <item><b>Bases are walked depth first</b> in the order a revision lists them, the class itself first, to depth 16.</item>
/// </list>
/// </summary>
public sealed class LtkMetaSchema
{
    private const int BaseDepth = 16;

    private sealed class ClassRevision
    {
        public uint From;
        public uint? To;
        public uint[] Bases = Array.Empty<uint>();
        public bool Covers(uint build) => build >= From && (To is null || build <= To);
    }

    private sealed class Revision
    {
        public uint From;
        public uint? To;
        /// <summary>Null for a type name this reader does not map: a revision the lookup declines to answer rather than one it answers wrongly.</summary>
        public GameDataShape? Shape;
        public bool Covers(uint build) => build >= From && (To is null || build <= To);
    }

    private sealed class ParsedClass
    {
        public readonly List<ClassRevision> Bases = new();
        public readonly Dictionary<uint, List<Revision>> Properties = KeyHash.Map<List<Revision>>();
    }

    private readonly Dictionary<uint, ParsedClass> _classes = KeyHash.Map<ParsedClass>();

    private LtkMetaSchema(uint latest, string identity) { Latest = latest; Identity = identity; }

    /// <summary>The newest build any revision names.</summary>
    public uint Latest { get; }

    /// <summary>A fingerprint of the bytes the database was read from: two databases with the same identity type every property alike. A cache of what was worked out with a schema is keyed by it.</summary>
    public string Identity { get; }

    public int ClassCount => _classes.Count;

    /// <summary>Whether the database describes <paramref name="build"/> at all: a build past its newest revision is one it cannot speak about.</summary>
    public bool Describes(uint build) => build <= Latest;

    /// <summary>Whether the database holds <paramref name="cls"/> at any build, named or not.</summary>
    public bool HasClass(uint cls) => _classes.ContainsKey(cls);

    /// <summary>The schema read at <paramref name="build"/>: its content build, or null where the install did not say.</summary>
    public IGameDataSchema At(uint? build) => new LtkPatchSchema(this, build is { } b && Describes(b) ? b : null);

    public IGameDataSchema At(GameBuild? build) => At(build?.Content);

    /// <summary>The most a database file may hold, 64 MiB: about seventeen times the one LTK Manager keeps today (3.8 MB).</summary>
    public const long MaxFileBytes = 64L << 20;

    /// <summary>
    /// Reads the database at <paramref name="path"/>. The read is bounded (<see cref="MaxFileBytes"/>: a file that is not a database cannot take the memory of the editor), and the file is opened so that its owner can go on
    /// replacing it - LTK Manager updates its cache by renaming a new file over the old one, which fails while a reader holds the file without allowing it to be deleted.
    /// </summary>
    /// <exception cref="FormatException">The file is not a database this reader knows.</exception>
    /// <exception cref="IOException">The file is larger than <see cref="MaxFileBytes"/> or cannot be read.</exception>
    public static LtkMetaSchema Load(string path) =>
        Parse(BoundedFile.Read(path, MaxFileBytes, FileShare.ReadWrite | FileShare.Delete, "schema reader"));

    /// <summary>Reads a database from its JSON.</summary>
    /// <exception cref="FormatException">The bytes are not a database this reader knows: JSON that is malformed or nested too deeply, or whose shape
    /// is not a database's (an object where a number is wanted, a number where an object is). It is the only exception bad content raises.</exception>
    public static LtkMetaSchema Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("meta schema database: expected an object");
            uint format = ReadUInt(root, "formatVersion");
            if (format != 1) throw new FormatException($"meta schema database is format version {format}, and this build reads 1");
            var schema = new LtkMetaSchema(ReadUInt(root, "latest"), $"{XxHash64.HashToUInt64(json):x16}:{json.Length}");
            if (!root.TryGetProperty("classes", out var classes) || classes.ValueKind != JsonValueKind.Object)
                throw new FormatException("meta schema database: missing `classes`");
            foreach (var cls in classes.EnumerateObject())
            {
                if (!TryParseHash(cls.Name, out uint classHash)) continue;
                schema._classes[classHash] = ReadClass(cls.Value);
            }
            return schema;
        }
        catch (JsonException e)
        {
            throw new FormatException("meta schema database: " + e.Message, e);
        }
    }

    /// <summary>A database whose JSON is well formed and whose shape is not what the reader needs. Asking a value that is not an object for one of
    /// its properties would be an <see cref="InvalidOperationException"/>; the reader answers it as it answers every other fault.</summary>
    private static FormatException NotAnObject(string what) => new($"meta schema database: {what} is not an object");

    private static ParsedClass ReadClass(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw NotAnObject("a class");
        var parsed = new ParsedClass();
        if (element.TryGetProperty("revisions", out var revisions) && revisions.ValueKind == JsonValueKind.Array)
        {
            foreach (var revision in revisions.EnumerateArray())
            {
                if (revision.ValueKind != JsonValueKind.Object) throw NotAnObject("a class revision");
                var bases = new List<uint>();
                if (revision.TryGetProperty("bases", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var item in list.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && TryParseHash(item.GetString()!, out uint hash)) bases.Add(hash);
                parsed.Bases.Add(new ClassRevision { From = ReadUInt(revision, "from"), To = ReadOptionalUInt(revision, "to"), Bases = bases.ToArray() });
            }
        }
        if (element.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (!TryParseHash(property.Name, out uint field)) continue;
                if (property.Value.ValueKind != JsonValueKind.Object) throw NotAnObject("a property");
                var list = new List<Revision>();
                if (property.Value.TryGetProperty("revisions", out var revisions2) && revisions2.ValueKind == JsonValueKind.Array)
                {
                    foreach (var revision in revisions2.EnumerateArray())
                    {
                        if (revision.ValueKind != JsonValueKind.Object) throw NotAnObject("a property revision");
                        var slots = new List<string>();
                        if (revision.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array)
                            foreach (var slot in type.EnumerateArray())
                                slots.Add(slot.ValueKind == JsonValueKind.String ? slot.GetString()! : throw new FormatException("meta schema database: a type slot is not a string"));
                        list.Add(new Revision { From = ReadUInt(revision, "from"), To = ReadOptionalUInt(revision, "to"), Shape = ShapeWritten(slots) });
                    }
                }
                parsed.Properties[field] = list;
            }
        }
        return parsed;
    }

    private static uint ReadUInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint number)
            ? number
            : throw new FormatException($"meta schema database: `{name}` is not a u32");

    private static uint? ReadOptionalUInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw NotAnObject("a revision");
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint number)
            ? number
            : throw new FormatException($"meta schema database: `{name}` is not a u32");
    }

    /// <summary>The hash a database key writes: unpadded hex, with or without a <c>0x</c>.</summary>
    private static bool TryParseHash(string key, out uint hash)
    {
        string digits = key.StartsWith("0x", StringComparison.Ordinal) ? key[2..] : key;
        return uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash);
    }

    // ------------------------------------------------------------------------------------------ types

    /// <summary>The slot the database writes where a type has nothing to say.</summary>
    private const string EmptySlot = "0x0";

    /// <summary>Every type name the database writes, beside the kind <c>ltk_meta</c> calls it.</summary>
    private static readonly Dictionary<string, PropKind> Names = new(StringComparer.Ordinal)
    {
        ["None"] = PropKind.None, ["Bool"] = PropKind.Bool, ["I8"] = PropKind.I8, ["U8"] = PropKind.U8, ["I16"] = PropKind.I16,
        ["U16"] = PropKind.U16, ["I32"] = PropKind.I32, ["U32"] = PropKind.U32, ["I64"] = PropKind.I64, ["U64"] = PropKind.U64,
        ["F32"] = PropKind.F32, ["Vec2"] = PropKind.Vector2, ["Vec3"] = PropKind.Vector3, ["Vec4"] = PropKind.Vector4,
        ["Mtx44"] = PropKind.Matrix44, ["Color"] = PropKind.Color, ["String"] = PropKind.String, ["Hash"] = PropKind.Hash,
        ["File"] = PropKind.WadChunkLink, ["List"] = PropKind.Container, ["List2"] = PropKind.UnorderedContainer,
        ["Pointer"] = PropKind.Struct, ["Embed"] = PropKind.Embedded, ["Link"] = PropKind.ObjectLink, ["Option"] = PropKind.Optional,
        ["Map"] = PropKind.Map, ["Flag"] = PropKind.BitBool,
    };

    /// <summary><c>Shape::written</c>: the kind from slot 0, a map's key from slot 1 (a list writes its fixed size there, which is a
    /// count and not a kind), and the item or value kind from slot 2. Null where a slot the shape needs is not a type name.</summary>
    private static GameDataShape? ShapeWritten(List<string> slots)
    {
        string? Slot(int index) => index < slots.Count && slots[index] != EmptySlot ? slots[index] : null;

        if (Slot(0) is not { } first || !Names.TryGetValue(first, out var kind)) return null;
        PropKind? key = null;
        if (kind == PropKind.Map && Slot(1) is { } written)
        {
            if (!Names.TryGetValue(written, out var keyKind)) return null;
            key = keyKind;
        }
        PropKind? item = null;
        if (Slot(2) is { } valueName)
        {
            if (!Names.TryGetValue(valueName, out var itemKind)) return null;
            item = itemKind;
        }
        return new GameDataShape(kind, key, item);
    }

    // ------------------------------------------------------------------------------------------ queries

    /// <summary>
    /// <c>MetaSchema::expected</c>: what the game expects <paramref name="field"/> of <paramref name="cls"/> to hold at
    /// <paramref name="build"/>. The type is the one the class or a base of it declares: the class first, then its bases from the class
    /// revisions covering the build, in the order they are listed. Returns false when no class answers; <paramref name="shape"/> is null
    /// where the answering revision names a type this reader does not map.
    /// </summary>
    public bool TryExpected(uint cls, uint field, uint build, out GameDataShape? shape)
    {
        shape = null;
        return Walk(cls, field, build, 0, ref shape);
    }

    private bool Walk(uint cls, uint field, uint build, int depth, ref GameDataShape? shape)
    {
        if (_classes.TryGetValue(cls, out var parsed) && parsed.Properties.TryGetValue(field, out var revisions))
        {
            foreach (var revision in revisions)
            {
                if (!revision.Covers(build)) continue;
                shape = revision.Shape;
                return true;
            }
        }
        if (depth == BaseDepth || parsed is null) return false;
        foreach (var classRevision in parsed.Bases)
        {
            if (!classRevision.Covers(build)) continue;
            foreach (uint baseClass in classRevision.Bases)
                if (Walk(baseClass, field, build, depth + 1, ref shape)) return true;
        }
        return false;
    }
}

/// <summary>
/// M817: the meta schema at one game build, as the game-data engine reads it (<c>PatchSchema</c>). A field answers with the type its
/// class or a base of it declares. A build the database does not describe answers no type and knows every class: undescribed means
/// <see cref="Expected"/> is null and <see cref="HasClass"/> is true. There is no fallback shape.
/// </summary>
public sealed class LtkPatchSchema : IGameDataSchema
{
    private readonly LtkMetaSchema _schema;
    private readonly uint? _build;

    internal LtkPatchSchema(LtkMetaSchema schema, uint? build)
    {
        _schema = schema;
        _build = build;
    }

    /// <summary>The content build the schema is read at; null where the database does not describe the install's.</summary>
    public uint? Build => _build;

    public GameDataShape? Expected(uint cls, uint field) =>
        _build is { } build && _schema.TryExpected(cls, field, build, out var shape) ? shape : null;

    public bool HasClass(uint cls) => _build is null || _schema.HasClass(cls);
}
