namespace ReyEngine.Formats.LtkGameData;

/// <summary>Where a module is declared (<c>Origin</c>). Used for diagnostics only.</summary>
public sealed record GameDataOrigin(string Manifest, string? Source, ulong ModuleIndex);

/// <summary>Link removals and link additions.</summary>
public sealed class LinkEdit
{
    public List<LinkPath> Add { get; } = new();
    public List<LinkPath> Remove { get; } = new();
}

/// <summary>A new object of a chunk, or the removal of one (<c>ObjectEdit</c>).</summary>
public abstract class ObjectEdit
{
    /// <summary>The property edits of a creation's <c>set</c>, empty for a removal.</summary>
    public virtual IReadOnlyList<PropertyEdit> Properties => Array.Empty<PropertyEdit>();
}

/// <summary>A copy of the entry <see cref="Source"/> holds at the start of the creation phase, with <see cref="Properties"/> applied.</summary>
public sealed class CloneObjectEdit : ObjectEdit
{
    public CloneObjectEdit(EntryName source, List<PropertyEdit> properties) { Source = source; _properties = properties; }
    private readonly List<PropertyEdit> _properties;
    public EntryName Source { get; }
    public override IReadOnlyList<PropertyEdit> Properties => _properties;
}

/// <summary>An object of <see cref="Class"/> holding no property but <see cref="Properties"/>.</summary>
public sealed class ConstructObjectEdit : ObjectEdit
{
    public ConstructObjectEdit(ClassName cls, List<PropertyEdit> properties) { Class = cls; _properties = properties; }
    private readonly List<PropertyEdit> _properties;
    public ClassName Class { get; }
    public override IReadOnlyList<PropertyEdit> Properties => _properties;
}

/// <summary>The removal of the object.</summary>
public sealed class RemoveObjectEdit : ObjectEdit
{
    public static readonly RemoveObjectEdit Instance = new();
}

/// <summary>
/// One edit of a chunk (<c>Edit</c>): every binding of one batch, applied phase by phase: the override files, the object
/// creations, the entry edits, the object removals, and the dependency-list edits. Objects and entries are in mapping order.
/// </summary>
public sealed class GameDataEdit
{
    /// <summary>Override files applied in listed order, the first phase.</summary>
    public List<OverridePath> Overrides { get; } = new();

    /// <summary>Objects created or removed in the chunk, in mapping order. Creations are the second phase and removals the fourth.</summary>
    public List<KeyValuePair<EntryName, ObjectEdit>> Objects { get; } = new();

    /// <summary>The property edits of each entry of the chunk, in mapping order, the third phase.</summary>
    public List<KeyValuePair<EntryName, List<PropertyEdit>>> Entries { get; } = new();

    /// <summary>Dependency-list edits, the last phase.</summary>
    public LinkEdit Links { get; } = new();

    /// <summary>The one-entry edit an <c>entries</c> module lowers each entry to, for each chunk that declares it: the entry's property
    /// edits, and its links riding along as links of the declaring chunk (<c>Application::application</c>).</summary>
    public static GameDataEdit ForEntry(EntryName name, EntryEdit edit)
    {
        var lowered = new GameDataEdit();
        lowered.Entries.Add(new(name, edit.Properties));
        lowered.Links.Add.AddRange(edit.Links.Add);
        lowered.Links.Remove.AddRange(edit.Links.Remove);
        return lowered;
    }

    /// <summary>Every reference the edit's property edits hold, in spelled order: the <c>set</c> of each object, then each entry.</summary>
    public List<GameDataReference> References()
    {
        var found = new List<GameDataReference>();
        foreach (var (_, edit) in Objects)
            foreach (var property in edit.Properties) property.Value.CollectReferences(found);
        foreach (var (_, properties) in Entries)
            foreach (var property in properties) property.Value.CollectReferences(found);
        return found;
    }
}

/// <summary>The bindings of one bin entry (<c>EntryEdit</c>), applied in every chunk declaring it.</summary>
public sealed class EntryEdit
{
    public List<PropertyEdit> Properties { get; } = new();
    public LinkEdit Links { get; } = new();
}

/// <summary>One selector with its edits, its optional name, and its origin (<c>Module</c>): a <c>target</c> chunk with a list of edits,
/// or <c>entries</c>.</summary>
public sealed class GameDataModule
{
    public string? Name { get; init; }

    /// <summary>The chunk of a target module; null for an entries module.</summary>
    public TargetName? Target { get; init; }

    /// <summary>The edits of a target module, in order.</summary>
    public List<GameDataEdit>? Edits { get; init; }

    /// <summary>The entries of an entries module, in mapping order.</summary>
    public List<KeyValuePair<EntryName, EntryEdit>>? Entries { get; init; }

    public required GameDataOrigin Origin { get; init; }

    public bool IsTarget => Target is not null;

    /// <summary>Every reference the module's edits hold, in spelled order, duplicates included.</summary>
    public List<GameDataReference> References()
    {
        var found = new List<GameDataReference>();
        if (Edits is not null)
            foreach (var edit in Edits) found.AddRange(edit.References());
        if (Entries is not null)
            foreach (var (_, entry) in Entries)
                foreach (var property in entry.Properties) property.Value.CollectReferences(found);
        return found;
    }
}

/// <summary>
/// M817: a layer's modules in execution order (<c>Declarations</c>), read from the JSON document an archive carries
/// (<c>{"version": 1, "modules": [...]}</c>). The document is read the way <c>ltk_game_data</c> reads it, refusing what it refuses:
/// a duplicate key anywhere, an unknown key, an integer where a float stands or a float where an integer does, a version other
/// than 1, a module with both selectors or neither, a target with no edits, a name, path or pin of the wrong shape.
///
/// <para>A refusal is worded as the crate words it, which is how <c>serde</c> words a type error (<see cref="SerdeValue"/>) and how <c>serde_json</c> words
/// a syntax error, line and column in bytes included. Fields are read where the document spells them, so the first thing wrong in the document is the
/// one reported: a struct the derive reads (the document, a module, an origin) is also accepted as a sequence of its fields, as <c>serde</c> accepts
/// it, and the version and the selector checks run after the whole document has been read.</para>
/// </summary>
public sealed class GameDataDocument
{
    public GameDataDocument(List<GameDataModule> modules) { Modules = modules; }

    public const int SupportedVersion = 1;

    public List<GameDataModule> Modules { get; }

    /// <summary>Reads a document. <paramref name="depthLimit"/> is the nesting the JSON reader starts with (see <see cref="GameDataJson.Parse"/>).</summary>
    /// <exception cref="GameDataException">The document is refused.</exception>
    public static GameDataDocument Parse(string text, int depthLimit = GameDataJson.DefaultDepthLimit)
    {
        GameDataLiteral root;
        try { root = GameDataJson.Parse(text, depthLimit); }
        catch (GameDataJsonException e) { throw new GameDataException(GameDataErrorKind.Syntax, e.Message); }
        return FromLiteral(root);
    }

    public static bool TryParse(string text, out GameDataDocument? document, out GameDataException? error, int depthLimit = GameDataJson.DefaultDepthLimit)
    {
        try
        {
            document = Parse(text, depthLimit);
            error = null;
            return true;
        }
        catch (GameDataException e)
        {
            document = null;
            error = e;
            return false;
        }
    }

    /// <summary>One struct <c>serde</c>'s derive reads: its name, its fields in declared order, and which of them a sequence may leave out.</summary>
    private sealed record StructShape(string Name, string[] Fields, bool[] SequenceDefault);

    private static readonly StructShape DeclarationsShape = new("Declarations", new[] { "version", "modules" }, new[] { false, false });
    private static readonly StructShape ModuleShape = new("Module", new[] { "name", "target", "edits", "entries", "origin" }, new[] { true, true, true, true, false });
    private static readonly StructShape OriginShape = new("Origin", new[] { "manifest", "source", "module" }, new[] { false, false, false });

    /// <summary>
    /// A derived struct read from a value (<c>deserialize_struct</c>): a mapping, whose keys are read in the order the document spells them, an unknown one
    /// refused where it stands; or a sequence, whose elements are the fields in declared order, with the fields marked <c>default</c> optional at the end
    /// and any element past the last field refused. Either way the first problem in the document is the one reported, and a field that is missing from a
    /// mapping is left to the caller.
    /// </summary>
    private static void ReadStruct(GameDataLiteral literal, StructShape shape, Action<int, GameDataLiteral> read)
    {
        switch (literal)
        {
            case GdMapping map:
                foreach (var (key, value) in map.Entries)
                {
                    int index = Array.IndexOf(shape.Fields, key);
                    if (index < 0) throw SerdeValue.UnknownField(key, shape.Fields);
                    read(index, value);
                }
                break;
            case GdList list:
                for (int i = 0; i < shape.Fields.Length; i++)
                {
                    if (i < list.Items.Count) read(i, list.Items[i]);
                    else if (!shape.SequenceDefault[i]) throw SerdeValue.InvalidLength(i, $"struct {shape.Name} with {shape.Fields.Length} elements");
                }
                if (list.Items.Count > shape.Fields.Length) throw SerdeValue.InvalidLength(list.Items.Count, "fewer elements in array");
                break;
            default:
                throw SerdeValue.InvalidType(literal, $"struct {shape.Name}");
        }
    }

    /// <summary>The document a parsed JSON value is.</summary>
    /// <exception cref="GameDataException">The document is refused; a literal nested past what the stack holds is refused as <see cref="GameDataErrorKind.Syntax"/>.</exception>
    public static GameDataDocument FromLiteral(GameDataLiteral root)
    {
        try
        {
            return ReadDocument(root);
        }
        catch (InsufficientExecutionStackException)
        {
            throw new GameDataException(GameDataErrorKind.Syntax, "recursion limit exceeded");
        }
    }

    private static GameDataDocument ReadDocument(GameDataLiteral root)
    {
        uint? version = null;
        List<GameDataModule>? modules = null;
        ReadStruct(root, DeclarationsShape, (index, value) =>
        {
            if (index == 0) version = SerdeValue.ReadU32(value);
            else modules = SerdeValue.ReadSequence(value).Select(ReadModule).ToList();
        });
        if (version is null) throw SerdeValue.MissingField("version");
        if (modules is null) throw SerdeValue.MissingField("modules");

        // Declarations::validate, after every module has been read
        if (version != SupportedVersion) throw new GameDataException(GameDataErrorKind.UnsupportedVersion);
        // an `entries` module with no entry is valid and applies nothing; a target module needs an edit
        for (int index = 0; index < modules.Count; index++)
            if (modules[index].Edits is { Count: 0 })
                throw new GameDataException(GameDataErrorKind.EditsEmpty, null, $"{modules[index].Origin.Manifest}: module {index}");
        return new GameDataDocument(modules);
    }

    // ------------------------------------------------------------------------------------------ the module (document.rs Module)

    private static GameDataModule ReadModule(GameDataLiteral literal)
    {
        string? moduleName = null;
        TargetName? targetName = null;
        List<GameDataEdit>? editList = null;
        List<KeyValuePair<EntryName, EntryEdit>>? entryList = null;
        GameDataOrigin? moduleOrigin = null;

        // a field is read where the document spells it, so the first thing wrong in the document is the first thing reported
        ReadStruct(literal, ModuleShape, (index, value) =>
        {
            switch (index)
            {
                case 0:
                    if (value is GdNull) break;
                    string name = SerdeValue.ReadString(value);
                    if (name.Length == 0) throw new GameDataException(GameDataErrorKind.EmptyModuleName, null, "name");
                    moduleName = name;
                    break;
                case 1:
                    if (value is not GdNull) targetName = new TargetName(SerdeValue.ReadString(value));
                    break;
                case 2:
                    if (value is GdNull) break;
                    editList = SerdeValue.ReadSequence(value).Select(ReadEdit).ToList();
                    break;
                case 3:
                    if (value is GdNull) break;
                    if (value is not GdMapping map) throw SerdeValue.InvalidType(value, "a map");
                    entryList = new List<KeyValuePair<EntryName, EntryEdit>>(map.Count);
                    foreach (var (key, body) in map.Entries)
                    {
                        var entryName = ToEntryName(key);
                        entryList.Add(new(entryName, ReadEntryEdit(body)));
                    }
                    break;
                default:
                    moduleOrigin = ReadOrigin(value);
                    break;
            }
        });
        if (moduleOrigin is null) throw SerdeValue.MissingField("origin");
        string where = $"{moduleOrigin.Manifest}: module {moduleOrigin.ModuleIndex}";

        // SelectorKey::one, then the pairing with `edits`
        if (targetName is not null && entryList is not null) throw new GameDataException(GameDataErrorKind.SelectorConflict, null, where);
        if (targetName is null && entryList is null) throw new GameDataException(GameDataErrorKind.SelectorMissing, null, where);
        if (targetName is not null && editList is null) throw new GameDataException(GameDataErrorKind.TargetWithoutEdits, null, where);
        if (entryList is not null && editList is not null) throw new GameDataException(GameDataErrorKind.EntriesWithEdits, null, where);

        return new GameDataModule { Name = moduleName, Target = targetName, Edits = editList, Entries = entryList, Origin = moduleOrigin };
    }

    private static GameDataOrigin ReadOrigin(GameDataLiteral literal)
    {
        string? manifest = null, source = null;
        ulong? module = null;
        ReadStruct(literal, OriginShape, (index, value) =>
        {
            switch (index)
            {
                case 0: manifest = SerdeValue.ReadString(value); break;
                case 1: source = value is GdNull ? null : SerdeValue.ReadString(value); break;
                default: module = SerdeValue.ReadUsize(value); break;
            }
        });
        if (manifest is null) throw SerdeValue.MissingField("manifest");
        if (module is null) throw SerdeValue.MissingField("module");
        return new GameDataOrigin(manifest, source, module.Value);
    }

    // ------------------------------------------------------------------------------------------ bodies (document.rs Bindings)

    /// <summary>The bindings of one body mapping, read key by key: a binding keyword to its field, any other key to <c>rest</c>.</summary>
    private sealed class Bindings
    {
        public List<string>? Overrides;
        public GameDataLiteral? Objects;
        public List<LinkPath>? AddLinks;
        public List<LinkPath>? RemoveLinks;
        public readonly List<KeyValuePair<string, GameDataLiteral>> Rest = new();
    }

    private static Bindings ReadBindings(GameDataLiteral literal)
    {
        var fields = Fields(literal, "a mapping of bindings");
        var bindings = new Bindings();
        foreach (var (key, value) in fields)
        {
            switch (BindingKeywords.Of(key))
            {
                case BindingKeyword.Overrides:
                    bindings.Overrides = SerdeValue.ReadSequence(value).Select(SerdeValue.ReadString).ToList();
                    break;
                case BindingKeyword.Objects:
                    bindings.Objects = value;
                    break;
                case BindingKeyword.AddLinks:
                    if (bindings.AddLinks is not null) throw Shape("a body holds one of `links` and `+links`, once");
                    bindings.AddLinks = ReadLinks(value);
                    break;
                case BindingKeyword.RemoveLinks:
                    bindings.RemoveLinks = ReadLinks(value);
                    break;
                default:
                    bindings.Rest.Add(new(key, value));
                    break;
            }
        }
        return bindings;
    }

    private static List<LinkPath> ReadLinks(GameDataLiteral value) => SerdeValue.ReadSequence(value).Select(item => new LinkPath(SerdeValue.ReadString(item))).ToList();

    private static void FillLinks(LinkEdit links, Bindings bindings)
    {
        if (bindings.AddLinks is not null) links.Add.AddRange(bindings.AddLinks);
        if (bindings.RemoveLinks is not null) links.Remove.AddRange(bindings.RemoveLinks);
    }

    /// <summary>An edit of a target body (<c>TryFrom&lt;Bindings&gt; for Edit</c>).</summary>
    private static GameDataEdit ReadEdit(GameDataLiteral literal)
    {
        var bindings = ReadBindings(literal);
        var edit = new GameDataEdit();
        if (bindings.Overrides is not null)
            foreach (string path in bindings.Overrides) edit.Overrides.Add(new OverridePath(path));
        if (bindings.Objects is not null) edit.Objects.AddRange(ReadObjects(bindings.Objects));
        foreach (var (key, value) in bindings.Rest)
        {
            // a key that is neither a binding keyword nor an entry name: only a target body tells it from an entry name, by the `/`
            var name = ToEntryName(key);
            if (!name.Text.Contains('/') && !name.IsHash) throw new GameDataException(GameDataErrorKind.UnsupportedBinding, key, key);
            if (value is not GdMapping body) throw new GameDataException(GameDataErrorKind.EntryBodyShape, null, $"entry {name.Text}");
            edit.Entries.Add(new(name, ReadProperties(body, name.Text)));
        }
        FillLinks(edit.Links, bindings);
        return edit;
    }

    /// <summary>The edits of an entry body (<c>TryFrom&lt;Bindings&gt; for EntryEdit</c>).</summary>
    private static EntryEdit ReadEntryEdit(GameDataLiteral literal)
    {
        var bindings = ReadBindings(literal);
        if (bindings.Overrides is not null) throw new GameDataException(GameDataErrorKind.OverridesInEntry, null, "overrides");
        if (bindings.Objects is not null) throw new GameDataException(GameDataErrorKind.ObjectsInEntry, null, "objects");
        var entry = new EntryEdit();
        entry.Properties.AddRange(ReadProperties(new GdMapping(bindings.Rest), null));
        FillLinks(entry.Links, bindings);
        return entry;
    }

    private static List<PropertyEdit> ReadProperties(GdMapping body, string? entry)
    {
        var edits = new List<PropertyEdit>(body.Count);
        foreach (var (key, value) in body.Entries)
        {
            try { edits.Add(PropertyEdit.Parse(key, value)); }
            catch (GameDataException e) when (entry is not null) { throw new GameDataException(e.Kind, e.Detail, $"entry {entry}: {key}"); }
        }
        return edits;
    }

    private static EntryName ToEntryName(string key)
    {
        if (EntryName.TryCreate(key, out var name, out var error)) return name!;
        throw EntryName.Refusal(error!.Value, key);
    }

    // ------------------------------------------------------------------------------------------ objects (document.rs objects_of)

    private static List<KeyValuePair<EntryName, ObjectEdit>> ReadObjects(GameDataLiteral value)
    {
        if (value is not GdMapping objects) throw new GameDataException(GameDataErrorKind.ObjectBodyShape, null, "objects");
        var result = new List<KeyValuePair<EntryName, ObjectEdit>>(objects.Count);
        foreach (var (key, body) in objects.Entries)
        {
            var name = ToEntryName(key);
            result.Add(new(name, ReadObject(body, name.Text)));
        }
        return result;
    }

    private static ObjectEdit ReadObject(GameDataLiteral body, string name)
    {
        GameDataException Bad() => new(GameDataErrorKind.ObjectBodyShape, null, $"entry {name}: objects");
        if (body is not GdMapping mapping) throw Bad();

        string? clone = null, cls = null;
        GameDataLiteral? set = null, remove = null;
        foreach (var (key, value) in mapping.Entries)
        {
            switch (key)
            {
                case "clone": clone = value is GdString c ? c.Value : throw Bad(); break;
                case "class": cls = value is GdString k ? k.Value : throw Bad(); break;
                case "set": set = value; break;
                case "remove": remove = value; break;
                default: throw Bad();
            }
        }

        List<PropertyEdit> Properties()
        {
            if (set is null) return new List<PropertyEdit>();
            if (set is GdMapping map) return ReadProperties(map, name);
            throw Bad();
        }

        if (clone is not null && cls is null && remove is null)
        {
            if (!EntryName.TryCreate(clone, out var source, out var error)) throw EntryName.Refusal(error!.Value, clone, name);
            return new CloneObjectEdit(source!, Properties());
        }
        if (clone is null && cls is not null && remove is null)
        {
            if (cls.Length == 0) throw new GameDataException(GameDataErrorKind.EmptyClassName, null, $"entry {name}: class");
            return new ConstructObjectEdit(new ClassName(cls), Properties());
        }
        if (clone is null && cls is null && remove is GdBool { Value: true } && set is null) return RemoveObjectEdit.Instance;
        throw Bad();
    }

    // ------------------------------------------------------------------------------------------ helpers

    private static IReadOnlyList<KeyValuePair<string, GameDataLiteral>> Fields(GameDataLiteral literal, string expected) =>
        literal is GdMapping mapping ? mapping.Entries : throw SerdeValue.InvalidType(literal, expected);

    private static GameDataException Shape(string detail) => new(GameDataErrorKind.Syntax, detail);
}
