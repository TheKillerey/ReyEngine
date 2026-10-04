namespace ReyEngine.Formats.LtkGameData;

/// <summary>The category of an application diagnostic (<c>ApplyDiagnosticKind</c>). The names are the camelCase forms the serialized
/// diagnostics use.</summary>
public enum GameDataDiagnosticKind
{
    /// <summary>An override file the reader cannot supply. The file is skipped.</summary>
    OverrideUnreadable,
    /// <summary>An override file that is not a PTCH. The file is skipped.</summary>
    OverrideInvalid,
    /// <summary>One override record that does not apply to the target. The remaining records apply.</summary>
    OverrideRecordSkipped,
    /// <summary>A <c>-links</c> path the target's dependency list does not hold. The edit continues.</summary>
    LinkRemovalUnmatched,
    /// <summary>One property key whose edit does not apply. The remaining keys apply.</summary>
    PropertyEditSkipped,
    /// <summary>A property typed without the schema's answer: from the base value, or from the schema's fallback. Informational.</summary>
    SchemaFallback,
    /// <summary>A referenced entry the caller could not read. Every key naming it is skipped.</summary>
    ReferenceUnreadable,
    /// <summary>One object creation or removal that does not apply. The remaining objects apply.</summary>
    ObjectSkipped,
    /// <summary>A link edit on a PTCH target. A PTCH holds no dependency list. The edit continues.</summary>
    LinkUnsupported,
    /// <summary>An entry of a PTCH target's base the caller could not read. Every edit naming it is skipped.</summary>
    EntryUnreadable,
    Unknown,
}

/// <summary>Why an override record does not apply (<c>RecordSkipReason</c>). The client's own rule is a skip.</summary>
public enum RecordSkipReason
{
    MissingObject,
    MissingProperty,
    NullPointer,
    CannotDescend,
    NotIndexable,
    IndexOutOfRange,
    InvalidKey,
    KeyNotFound,
    TypeMismatch,
    Unknown,
}

/// <summary>Why a property edit does not apply (<c>PropertySkipReason</c>). The first nine codes are the
/// <see cref="RecordSkipReason"/> codes of a path that does not resolve or a value the tree refuses.</summary>
public enum PropertySkipReason
{
    /// <summary>The target has no object with the entry's hash.</summary>
    MissingObject,
    /// <summary>A path segment names a property the value does not have.</summary>
    MissingProperty,
    /// <summary>A path segment descends through a null pointer.</summary>
    NullPointer,
    /// <summary>A path segment descends into a value that is not a pointer or an embed.</summary>
    CannotDescend,
    /// <summary>A subscript on a value that is not a list, option, or map.</summary>
    NotIndexable,
    /// <summary>A list or option index past the end.</summary>
    IndexOutOfRange,
    /// <summary>A map key that does not convert to the map's key kind.</summary>
    InvalidKey,
    /// <summary>A map key no entry has.</summary>
    KeyNotFound,
    /// <summary>The coerced value's shape is not the base value's shape.</summary>
    TypeMismatch,
    /// <summary>A key inside a block or a <c>set</c> that is not a property path.</summary>
    InvalidPath,
    /// <summary>The property has no type: the base omits it, and neither the schema's expected nor its fallback shape answers.</summary>
    Untypable,
    /// <summary>A struct pin's class the schema does not know.</summary>
    UnknownClass,
    /// <summary>A pin whose type name is not the property's kind.</summary>
    PinMismatch,
    /// <summary>A <c>+</c> or <c>-</c> on a property that is not a list or a map.</summary>
    SignOnScalar,
    /// <summary>A <c>-</c> on a container the base omits.</summary>
    ContainerAbsent,
    /// <summary>A removal that matches no element, index, or key.</summary>
    RemovalUnmatched,
    /// <summary>A value of a kind no coercion row accepts for the property's shape.</summary>
    KindMismatch,
    /// <summary>An integer outside the range of the property's kind.</summary>
    OutOfRange,
    /// <summary>An integer an f32 does not represent exactly.</summary>
    PrecisionLoss,
    /// <summary>A list whose length is not the shape's.</summary>
    ArityMismatch,
    /// <summary>A reference whose entry the caller does not supply.</summary>
    ReferenceMissingEntry,
    /// <summary>A reference whose path the supplied entry does not resolve.</summary>
    ReferenceUnresolved,
    /// <summary>A path with a hash-form segment, on an object a PTCH target patches with a record.</summary>
    HashFormPath,
    Unknown,
}

/// <summary>Why an object creation or removal does not apply (<c>ObjectSkipReason</c>).</summary>
public enum ObjectSkipReason
{
    /// <summary>The target holds an object of the created name, or the batch creates it twice.</summary>
    ObjectExists,
    /// <summary>The clone source is absent from the target at the start of the creation phase.</summary>
    SourceMissing,
    /// <summary>A constructed class the schema does not know.</summary>
    UnknownClass,
    /// <summary>A removed object the target does not hold.</summary>
    RemovalUnmatched,
    Unknown,
}

/// <summary>One override record that does not apply. The record index is zero-based within its file.</summary>
public sealed record SkippedRecord(int Index, uint Object, string Property, RecordSkipReason Reason);

/// <summary>One property edit that does not apply: the entry the edit addresses, as spelled, and why.</summary>
public sealed record SkippedProperty(EntryName Entry, PropertySkipReason Reason);

/// <summary>One object creation or removal that does not apply: the object, as spelled, and why.</summary>
public sealed record SkippedObject(EntryName Name, ObjectSkipReason Reason);

/// <summary>An application diagnostic (<c>ApplyDiagnostic</c>). The edit index is zero-based.</summary>
/// <param name="Path">The link path, the override path, the object name, or the signed property key the diagnostic is about.</param>
/// <param name="Detail">What a lower layer said, when it said something the codes do not carry: the reader's error of an unreadable
/// override, the decoder's of an invalid one, the patch error of a skipped record, the error of an unreadable entry.</param>
public sealed record GameDataDiagnostic(
    GameDataDiagnosticKind Kind,
    int Edit,
    string Path,
    SkippedRecord? Record = null,
    SkippedProperty? Property = null,
    SkippedObject? Object = null,
    string? Detail = null)
{
    /// <summary>The kind as the serialized form spells it.</summary>
    public string KindName => GameDataNaming.CamelCase(Kind.ToString());
}

/// <summary>What an application changed, counted across every edit (<c>Applied</c>).</summary>
public sealed class GameDataApplied
{
    /// <summary>Override records that applied, over every override file of every edit.</summary>
    public int Records { get; internal set; }
    /// <summary>Objects an override file added to, replaced in, or deleted from the target, and objects an <c>objects</c> binding created or removed.</summary>
    public int Objects { get; internal set; }
    /// <summary>Property keys whose patch landed. One key is one patch, whatever its sign.</summary>
    public int Properties { get; internal set; }
    /// <summary>Dependencies added to the target.</summary>
    public int LinksAdded { get; internal set; }
    /// <summary>Dependencies removed from the target.</summary>
    public int LinksRemoved { get; internal set; }

    /// <summary>Whether any edit took effect. False means the returned bytes carry nothing the caller declared.</summary>
    public bool Any => Records > 0 || Objects > 0 || Properties > 0 || LinksAdded > 0 || LinksRemoved > 0;

    /// <summary>Whether the object tree was reached: only then is a target written from its decoded tree.</summary>
    internal bool TreeChanged => Records > 0 || Objects > 0 || Properties > 0;
}

/// <summary>The outcome of one application: the target's bytes, its dependency list, what the edits changed, and every nonfatal
/// outcome (<c>ApplyResult</c>).</summary>
public sealed class GameDataApplyResult
{
    internal GameDataApplyResult(byte[] bytes, IReadOnlyList<string> dependencies, GameDataApplied applied, IReadOnlyList<GameDataDiagnostic> diagnostics, long workUsed = 0)
    {
        Bytes = bytes;
        Dependencies = dependencies;
        Applied = applied;
        Diagnostics = diagnostics;
        WorkUsed = workUsed;
    }

    /// <summary>The units of work the application used (see <see cref="GameDataLimits"/>): what a caller sets its own limit against.</summary>
    public long WorkUsed { get; }

    /// <summary>The target after every edit: a PROP for a PROP target and a PTCH for a PTCH one.</summary>
    public byte[] Bytes { get; }

    /// <summary>The dependency spellings of <see cref="Bytes"/>, retained base entries included. A PTCH holds none.</summary>
    public IReadOnlyList<string> Dependencies { get; }

    public GameDataApplied Applied { get; }

    /// <summary>Every diagnostic in apply order.</summary>
    public IReadOnlyList<GameDataDiagnostic> Diagnostics { get; }

    /// <summary>Whether any edit took effect.</summary>
    public bool Changed => Applied.Any;
}

internal static class GameDataNaming
{
    public static string CamelCase(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
