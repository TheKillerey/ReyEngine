namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M817: the codes of <c>ltk_game_data::ErrorKind</c> a declaration document or an application can end in. A document that does not
/// load is refused whole; an application fails only for the three codes of the crate (<see cref="UnsupportedBase"/>, <see cref="Bin"/>,
/// <see cref="DependencyOverflow"/>) and for <see cref="LimitExceeded"/>, which is the engine's own, and every other outcome is a diagnostic.
/// A cancelled application is not an error of the engine: it ends with the <see cref="OperationCanceledException"/> of its token.
/// </summary>
public enum GameDataErrorKind
{
    UnsupportedVersion,
    Syntax,
    EmptyTarget,
    EmptyEntryName,
    EmptyClassName,
    EmptyModuleName,
    LinkPathLength,
    EmptyOverridePath,
    OverridePathBackslash,
    OverridePathAbsolute,
    OverridePathSegment,
    OverridePathExtension,
    OverridePathRito,
    SelectorMissing,
    SelectorConflict,
    TargetWithoutEdits,
    EntriesWithEdits,
    OverridesInEntry,
    ObjectsInEntry,
    ObjectBodyShape,
    EditsEmpty,
    UnsupportedBinding,
    ReservedBindingKey,
    EntryBodyShape,
    InvalidPropertyPath,
    StructPinShape,
    ReferenceShape,
    /// <summary>The base is not a PROP version 2 or 3.</summary>
    UnsupportedBase,
    /// <summary>The base, or the output, is a bin the codec refuses. <see cref="GameDataException.Detail"/> is the codec's statement.</summary>
    Bin,
    /// <summary>A dependency list the PROP header cannot count.</summary>
    DependencyOverflow,
    /// <summary>The application used more than <see cref="GameDataLimits"/> allow: more work, more output, or more override-file bytes. The
    /// bytes are not wrong, so this is never <see cref="Bin"/>; <see cref="GameDataException.Detail"/> says which limit.</summary>
    LimitExceeded,
}

/// <summary>
/// M817: a declaration that does not load, or a target that cannot be applied to at all. <see cref="Kind"/> is the code and
/// <see cref="Detail"/> the statement a code carries; <see cref="Message"/> is the text <c>ltk_game_data</c> writes for it, with
/// the place it is about in front.
/// </summary>
public sealed class GameDataException : Exception
{
    public GameDataException(GameDataErrorKind kind, string? detail = null, string? where = null)
        : base(Compose(kind, detail, where))
    {
        Kind = kind;
        Detail = detail;
    }

    public GameDataErrorKind Kind { get; }

    public string? Detail { get; }

    private static string Compose(GameDataErrorKind kind, string? detail, string? where)
    {
        string text = Statement(kind, detail);
        return where is null ? text : where + ": " + text;
    }

    /// <summary>The <c>#[error(...)]</c> text of a code.</summary>
    public static string Statement(GameDataErrorKind kind, string? detail) => kind switch
    {
        GameDataErrorKind.UnsupportedVersion => "unsupported declaration version",
        GameDataErrorKind.Syntax or GameDataErrorKind.Bin => detail ?? "",
        GameDataErrorKind.EmptyTarget => "expected a nonempty target",
        GameDataErrorKind.EmptyEntryName => "expected a nonempty entry name",
        GameDataErrorKind.EmptyClassName => "expected a nonempty class name",
        GameDataErrorKind.EmptyModuleName => "expected a nonempty module name",
        GameDataErrorKind.LinkPathLength => "link paths require 1 to 65535 UTF-8 bytes",
        GameDataErrorKind.EmptyOverridePath => "expected a nonempty override path",
        GameDataErrorKind.OverridePathBackslash => "override paths use forward slashes",
        GameDataErrorKind.OverridePathAbsolute => "override paths are relative",
        GameDataErrorKind.OverridePathSegment => "override paths contain no empty, `.`, or `..` segment",
        GameDataErrorKind.OverridePathExtension => "override files require the `.ptch` extension",
        GameDataErrorKind.OverridePathRito => "`.rito` override files are unsupported; convert the file to `.ptch`",
        GameDataErrorKind.SelectorMissing => "module requires target or entries",
        GameDataErrorKind.SelectorConflict => "target and entries are mutually exclusive",
        GameDataErrorKind.TargetWithoutEdits => "target requires `edits`",
        GameDataErrorKind.EntriesWithEdits => "entries and `edits` are mutually exclusive",
        GameDataErrorKind.OverridesInEntry => "overrides is not permitted inside entries",
        GameDataErrorKind.ObjectsInEntry => "objects is not permitted inside entries",
        GameDataErrorKind.ObjectBodyShape => "an object takes `clone` or `class` with `set`, or `remove: true`",
        GameDataErrorKind.EditsEmpty => "`edits` requires at least one edit",
        GameDataErrorKind.UnsupportedBinding => $"unsupported binding `{detail}`",
        GameDataErrorKind.ReservedBindingKey => $"`{detail}` is a binding keyword",
        GameDataErrorKind.EntryBodyShape => "entry body requires a mapping",
        GameDataErrorKind.InvalidPropertyPath => $"invalid property path: {detail}",
        GameDataErrorKind.StructPinShape => "a pointer or embed pin takes `class` and `set`",
        GameDataErrorKind.ReferenceShape => "a ref takes `<entry>:<property path>`",
        GameDataErrorKind.UnsupportedBase => "expected PROP version 2 or 3",
        GameDataErrorKind.DependencyOverflow => "dependency list exceeds the header's limits",
        GameDataErrorKind.LimitExceeded => detail ?? "a limit of the application was exceeded",
        _ => kind.ToString(),
    };
}
