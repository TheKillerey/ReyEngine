namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// The category of an overlay diagnostic (<c>GameDataDiagnosticKind</c> of <c>ltk_overlay</c>): the application kinds an apply reports, lowered one to one, and the kinds only the overlay
/// can report because they are about layers, chunks and the object index. The names are the camelCase forms the serialized diagnostics use.
/// </summary>
public enum GameDataOverlayDiagnosticKind
{
    /// <summary>A layer whose document does not load: every one of its modules is dropped.</summary>
    DeclarationsRejected,
    /// <summary>A target the overlay could not apply to at all: its base did not read, it is a blocked chunk, or its bytes are not a bin the engine applies to.</summary>
    TargetSkipped,
    /// <summary>An application in which every edit was skipped. The chunk is left as it was.</summary>
    NoEffect,
    /// <summary>An entry no game bin declares. Its edits are skipped.</summary>
    EntryUnresolved,
    /// <summary>An entry several game bins declare. Every one is edited. Informational.</summary>
    EntryFanOut,
    /// <summary>The object index did not load or build. An <c>entries</c> module is skipped whole; a <c>target</c> module keeps every edit but its references.</summary>
    IndexUnavailable,
    OverrideUnreadable,
    OverrideInvalid,
    OverrideRecordSkipped,
    LinkRemovalUnmatched,
    PropertyEditSkipped,
    SchemaFallback,
    ReferenceUnreadable,
    ObjectSkipped,
    /// <summary>A created object whose name a game bin other than the target declares. The object is created. Informational.</summary>
    ObjectShadowsGame,
    LinkUnsupported,
    EntryUnreadable,
    /// <summary>A missing or unrecognized category.</summary>
    Unknown,
    /// <summary>Not in <c>ltk_overlay</c>: a layer asks for more applications than <see cref="GameDataOverlayOptions.MaxApplications"/>, and the rest are not made. The guard of a package that is not trusted.</summary>
    LimitExceeded,
}

/// <summary>
/// One overlay diagnostic (<c>GameDataDiagnostic</c> of <c>ltk_overlay</c>), in the order the build produced it. The edit index is zero-based.
/// </summary>
/// <param name="Kind">The category.</param>
/// <param name="ModId">The mod the layer belongs to (<see cref="GameDataOverlayOptions.ModId"/>).</param>
/// <param name="Layer">The layer.</param>
/// <param name="Target">The authored target or entry name.</param>
/// <param name="Chunk">The chunk the diagnostic is about.</param>
/// <param name="Origin">Where the module is declared.</param>
/// <param name="Edit">The edit within the module's application.</param>
/// <param name="Record">The record of an <see cref="GameDataOverlayDiagnosticKind.OverrideRecordSkipped"/> diagnostic.</param>
/// <param name="Property">The property of a <see cref="GameDataOverlayDiagnosticKind.PropertyEditSkipped"/> diagnostic.</param>
/// <param name="Object">The object of an <see cref="GameDataOverlayDiagnosticKind.ObjectSkipped"/> diagnostic.</param>
/// <param name="Message">The statement: for an application diagnostic the one <c>ltk_game_data</c> writes for it (<see cref="GameDataOverlayDiagnostics.Describe"/>).</param>
public sealed record GameDataOverlayDiagnostic(
    GameDataOverlayDiagnosticKind Kind,
    string ModId,
    string Layer,
    string? Target,
    ulong? Chunk,
    GameDataOrigin? Origin,
    int? Edit,
    SkippedRecord? Record,
    SkippedProperty? Property,
    SkippedObject? Object,
    string Message)
{
    /// <summary>The kind as the serialized form spells it.</summary>
    public string KindName => GameDataNaming.CamelCase(Kind.ToString());
}

/// <summary>The statements the overlay writes (<c>ApplyDiagnostic</c>'s <c>Display</c>, and the texts of <c>ltk_overlay</c>'s own kinds).</summary>
public static class GameDataOverlayDiagnostics
{
    /// <summary>A count as a statement spells it, with thousands separators and in no culture: the text of a diagnostic is the same on every machine.</summary>
    internal static string Count(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The kind an application diagnostic becomes (<c>From&lt;ApplyDiagnosticKind&gt;</c>).</summary>
    public static GameDataOverlayDiagnosticKind Lower(GameDataDiagnosticKind kind) => kind switch
    {
        GameDataDiagnosticKind.LinkRemovalUnmatched => GameDataOverlayDiagnosticKind.LinkRemovalUnmatched,
        GameDataDiagnosticKind.OverrideUnreadable => GameDataOverlayDiagnosticKind.OverrideUnreadable,
        GameDataDiagnosticKind.OverrideInvalid => GameDataOverlayDiagnosticKind.OverrideInvalid,
        GameDataDiagnosticKind.OverrideRecordSkipped => GameDataOverlayDiagnosticKind.OverrideRecordSkipped,
        GameDataDiagnosticKind.PropertyEditSkipped => GameDataOverlayDiagnosticKind.PropertyEditSkipped,
        GameDataDiagnosticKind.SchemaFallback => GameDataOverlayDiagnosticKind.SchemaFallback,
        GameDataDiagnosticKind.ReferenceUnreadable => GameDataOverlayDiagnosticKind.ReferenceUnreadable,
        GameDataDiagnosticKind.ObjectSkipped => GameDataOverlayDiagnosticKind.ObjectSkipped,
        GameDataDiagnosticKind.LinkUnsupported => GameDataOverlayDiagnosticKind.LinkUnsupported,
        GameDataDiagnosticKind.EntryUnreadable => GameDataOverlayDiagnosticKind.EntryUnreadable,
        _ => GameDataOverlayDiagnosticKind.Unknown,
    };

    /// <summary>The statement of an application diagnostic: the text of its category about its path, then the lower layer's words in parentheses where it has any
    /// (<c>ApplyDiagnosticKind::message</c> and <c>ApplyDiagnostic</c>'s <c>Display</c>).</summary>
    public static string Describe(GameDataDiagnostic diagnostic)
    {
        string path = diagnostic.Path;
        string statement = diagnostic.Kind switch
        {
            GameDataDiagnosticKind.OverrideUnreadable => $"Override file cannot be read: {path}",
            GameDataDiagnosticKind.OverrideInvalid => $"Override file is not a PTCH: {path}",
            GameDataDiagnosticKind.OverrideRecordSkipped => $"Override record is skipped: {path}",
            GameDataDiagnosticKind.LinkRemovalUnmatched => $"Link removal is absent: {path}",
            GameDataDiagnosticKind.PropertyEditSkipped => $"Property edit is skipped: {path}",
            GameDataDiagnosticKind.SchemaFallback => $"Property is typed without the schema: {path}",
            GameDataDiagnosticKind.ReferenceUnreadable => $"Referenced entry cannot be read: {path}",
            GameDataDiagnosticKind.ObjectSkipped => $"Object edit is skipped: {path}",
            GameDataDiagnosticKind.LinkUnsupported => $"Link edit on a PTCH is skipped: {path}",
            GameDataDiagnosticKind.EntryUnreadable => $"Base entry cannot be read: {path}",
            _ => $"Application diagnostic: {path}",
        };
        return diagnostic.Detail is { } detail ? $"{statement} ({detail})" : statement;
    }
}
