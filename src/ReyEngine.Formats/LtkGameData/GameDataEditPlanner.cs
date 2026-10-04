using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueToolkit.Core.Meta;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>What an edit on top of a mod's GameData comes to.</summary>
public enum GameDataEditKind
{
    /// <summary>The edited bin is the bin the GameData makes: there is nothing to declare, and an edit kept for the bin is taken away.</summary>
    Unchanged,

    /// <summary>The difference is a module that LTK applies to the GameData's bin and gets the edited bin from.</summary>
    Declared,

    /// <summary>The edit cannot be kept as a declaration that gives the same bin on LTK's side, and <see cref="GameDataEditOutcome.Reason"/> says why.</summary>
    Refused,
}

/// <summary>
/// The result of <see cref="GameDataEditPlanner.Plan"/>.
/// </summary>
/// <param name="Reason">Why the edit is refused, worded for a person; null otherwise.</param>
/// <param name="ModuleText">The module as JSON (<c>target</c>, <c>edits</c>, <c>origin</c>), when <see cref="Kind"/> is Declared.</param>
/// <param name="Expected">The bytes the engine makes of the GameData's bin and the module: what the preview serves for the bin once the module is kept. Declared only.</param>
/// <param name="NeedsSchema">True when LTK would skip an edit for want of the class schema (a property or a struct class it cannot type): the edit is expressible, and goes through where the schema describes the game build.</param>
public sealed record GameDataEditOutcome(
    GameDataEditKind Kind, string? Reason, string? ModuleText = null, byte[]? Expected = null,
    int Properties = 0, int ObjectsAdded = 0, int ObjectsRemoved = 0, int LinksChanged = 0, bool NeedsSchema = false)
{
    public bool IsRefused => Kind == GameDataEditKind.Refused;
}

/// <summary>
/// M823: an edit of a bin the imported GameData targets, as the declaration that reproduces it.
///
/// <para><b>The difference.</b> <c>B</c> is the bin as LTK makes it from the game (or the mod's own copy of it) and the package's GameData alone - what the editor was
/// showing, less the edits kept on top. <c>E</c> is the bin the editor holds. The declaration is <see cref="BinDeclarations"/>' literal diff of the two: one module of values,
/// which names no value it does not change, so it keeps applying when Riot's next patch changes everything else. It goes into the layer after the package's own modules, so at
/// install it turns <c>B</c> into <c>E</c>.</para>
///
/// <para><b>The proof.</b> A declaration the diff writes may still not give <c>E</c> on LTK's side: a property the class schema cannot type is skipped, a value of another kind is refused, an
/// embedded struct may not change class. So the module is APPLIED to <c>B</c> with the engine LTK's own crate is ported to (<see cref="GameDataApplier"/>, with the class schema
/// the preview uses) and the result is compared with <c>E</c>, structurally (<see cref="BinTreeEquivalence"/>: LTK writes the canonical PROP version 3, so the bytes may differ). Only a module
/// that gives <c>E</c> is kept; any other is refused with the reason, and nothing is written. There is no fallback to a whole copy of the bin: LTK would apply the package's
/// declarations to it again.</para>
/// </summary>
public static class GameDataEditPlanner
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="target">How the module names the chunk: its path, or the sixteen digits of its hash (<see cref="BinDeclarations.TargetOf"/>).</param>
    /// <param name="imported"><c>B</c>: the bin the package's GameData makes (<see cref="GameDataPreview.TryReadImportedOnly"/>).</param>
    /// <param name="edited"><c>E</c>: the bin the editor wrote.</param>
    /// <param name="names">Plaintext for the hashes the declaration spells.</param>
    /// <param name="schema">Types the properties the bin does not hold: the preview's (<see cref="GameDataSetup.Options"/>).</param>
    /// <param name="limits">What the proof may cost; <see cref="GameDataLimits.Default"/> when null.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static GameDataEditOutcome Plan(
        string target, byte[] imported, byte[] edited, IDeclarationNames names, IGameDataSchema schema, GameDataLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(imported);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(schema);

        if (IsPatch(imported)) return Refuse("the bin is a PTCH override, not a PROP bin, and a declaration cannot be made of an edit to one");
        if (IsPatch(edited)) return Refuse("the edited file is a PTCH override, not a PROP bin");

        BinTree before, after;
        try
        {
            before = SafeBinTree.Parse(imported, out var beforeIssues);
            if (beforeIssues.Count > 0) return Refuse($"the bin LTK makes of the GameData parses lossily ({beforeIssues.Count} issue(s)), so what is changed cannot be told from what is lost");
            after = SafeBinTree.Parse(edited, out var afterIssues);
            if (afterIssues.Count > 0) return Refuse($"the edited bin parses lossily ({afterIssues.Count} issue(s)), so saving it would declare what was lost");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse($"a bin did not parse: {ex.Message}");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var declared = BinDeclarations.ConvertTrees(target, before, after, names);
        if (declared.WhyNot is { } why) return Refuse(why);
        if (declared.Unchanged) return new GameDataEditOutcome(GameDataEditKind.Unchanged, null);

        string moduleText;
        try
        {
            var modules = (JsonArray)BinDeclarations.GameDataDocument(new[] { declared })["modules"]!;
            moduleText = modules[0]!.ToJsonString(Compact);
        }
        catch (BinDeclarations.Refused ex) { return Refuse(ex.Message); }

        // the proof: LTK's engine applies the module to B, and what it makes must hold the data of E
        byte[] applied;
        try
        {
            var document = GameDataDocument.Parse("{\"version\":1,\"modules\":[" + moduleText + "]}");
            var result = GameDataApplier.Apply(
                imported, document.Modules[0].Edits!, _ => GameDataBytesRead.Failed("a declaration of values names no file"), _ => GameDataEntryRead.None,
                schema, limits ?? GameDataLimits.Default, cancellationToken);
            var skipped = result.Diagnostics.Where(d => d.Kind != GameDataDiagnosticKind.SchemaFallback).ToList();
            if (skipped.Count > 0)
            {
                bool needsSchema = skipped.Any(d => d.Property?.Reason is PropertySkipReason.Untypable or PropertySkipReason.UnknownClass
                                               || d.Object?.Reason is ObjectSkipReason.UnknownClass);
                return Refuse($"LTK would skip {skipped.Count} of its edits: {string.Join("; ", skipped.Take(3).Select(Why))}" + (skipped.Count > 3 ? "; ..." : ""), needsSchema);
            }
            applied = result.Bytes;
        }
        catch (Exception ex) when (ex is GameDataException or PropDecodeException)
        {
            return Refuse($"LTK cannot apply it: {ex.Message}");
        }

        BinTree made;
        try
        {
            made = SafeBinTree.Parse(applied, out var madeIssues);
            if (madeIssues.Count > 0) return Refuse($"the bin LTK would make parses lossily ({madeIssues.Count} issue(s))");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Refuse($"the bin LTK would make does not parse: {ex.Message}");
        }
        if (BinTreeEquivalence.FirstDifference(after, made, names) is { } difference)
            return Refuse($"LTK would not give the edited bin from this declaration: {difference}");

        return new GameDataEditOutcome(GameDataEditKind.Declared, null, moduleText, applied,
            declared.Properties, declared.ObjectsAdded, declared.ObjectsRemoved, declared.LinksChanged);
    }

    private static GameDataEditOutcome Refuse(string reason, bool needsSchema = false) => new(GameDataEditKind.Refused, reason, NeedsSchema: needsSchema);

    /// <summary>What LTK says of a skipped edit, with the reason it gives (<c>Untypable</c>, <c>UnknownClass</c>, <c>KindMismatch</c>...).</summary>
    private static string Why(GameDataDiagnostic d) =>
        GameDataOverlayDiagnostics.Describe(d) + (d.Property is { } p ? $" [{p.Reason}]" : d.Object is { } o ? $" [{o.Reason}]" : d.Record is { } r ? $" [{r.Reason}]" : "");

    private static bool IsPatch(byte[] bytes) => bytes.Length >= 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'T' && bytes[2] == (byte)'C' && bytes[3] == (byte)'H';
}
