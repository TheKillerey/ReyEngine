using ReyEngine.Formats.LtkGameData;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M819: what the GameData overlay said, per bin, in the Bin Issues window - the surface the editor already has for "these bins have problems", rather than a window of its own.
///
/// <para>One group per bin the declarations name (changed bins first), and one for what belongs to no bin; each row is a diagnostic. A kind that repeats often in one bin - a thousand skipped edits that all say the same
/// thing - is ONE row with the count and the first few messages, because a window of 1,600 rows is not read by anyone and costs seconds to build. The headline, the notes (why the schema types less than it could) and the warnings (a game
/// index that is incomplete) are the window's description; a warning that a rebuild answers has a Retry on its row.</para>
/// </summary>
public static class GameDataDiagnosticsView
{
    /// <summary>A kind that occurs this often or less in one bin is listed row by row.</summary>
    public const int ListEachUpTo = 8;

    /// <summary>The messages a collapsed row quotes.</summary>
    public const int Examples = 3;

    // how the summary words the two warnings that are about the game as a whole (see GameDataPreview): the row is titled by them
    private const string IncompleteIndex = "The game's index is incomplete";
    private const string GameUnreadable = "The game cannot be read";

    public static BinIssuesWindowViewModel Build(GameDataPreview preview, Func<GameDataBinReport, string> nameOf, Action? retry)
    {
        var summary = preview.Summary!;
        var vm = new BinIssuesWindowViewModel
        {
            BinName = "LTK GameData - " + summary.Headline,
            Description = Describe(summary),
            Status = preview.Bins.Count == 0 ? "The declarations name no bin." : "",
        };

        var general = new BinIssueGroupViewModel { BinName = "Not about one bin" };
        foreach (string warning in summary.Warnings)
        {
            bool incompleteIndex = summary.IndexUnsettled && warning.StartsWith(IncompleteIndex, StringComparison.Ordinal);
            general.Rows.Add(new BinIssueRowViewModel
            {
                Kind = "warning",
                ObjectName = incompleteIndex ? IncompleteIndex : warning.StartsWith(GameUnreadable, StringComparison.Ordinal) ? GameUnreadable : "Read the warning",
                ClassName = "LTK GameData",
                Message = warning,
                Suggestion = "A result that depends on what the game holds may be wrong until the game is read again.",
                FixLabel = retry is not null && incompleteIndex ? "Retry" : null,
                FixAsync = retry is not null && incompleteIndex ? () => { retry(); return Task.FromResult(true); } : null,
                FixDoneText = "Retrying - the mounts are rebuilt and the game is read again.",
            });
        }
        foreach (var row in Rows(preview.GeneralDiagnostics, summary)) general.Rows.Add(row);
        if (general.Rows.Count > 0) vm.Groups.Add(general);

        foreach (var bin in preview.Bins)
        {
            var group = new BinIssueGroupViewModel { BinName = $"{nameOf(bin)}  -  {State(bin)}" };
            foreach (var row in Rows(bin.Diagnostics, summary)) group.Rows.Add(row);
            vm.Groups.Add(group);
        }
        return vm;
    }

    private static string Describe(GameDataSummary summary)
    {
        var parts = new List<string>
        {
            "The mod's LTK GameData applied over the installed game, as LTK Manager installs it. Read-only: editing a changed bin comes with the next update.",
        };
        parts.AddRange(summary.Notes);
        parts.AddRange(summary.Warnings);
        return string.Join("\n\n", parts);
    }

    private static string State(GameDataBinReport bin) =>
        bin.Applied ? $"changed ({bin.Applications} application(s) from {string.Join(", ", bin.Layers)})"
        : bin.BaseKind == GameDataBaseKind.None ? "not applied: no base"
        : "not changed";

    private static IEnumerable<BinIssueRowViewModel> Rows(IReadOnlyList<GameDataOverlayDiagnostic> diagnostics, GameDataSummary summary)
    {
        foreach (var kind in diagnostics.GroupBy(d => (d.Kind, Reason(d))))
        {
            var items = kind.ToList();
            // a property typed without the schema is the one reason an undescribed build gives for every property it types that way: one row says it, whatever the count
            if (items.Count <= ListEachUpTo && kind.Key.Kind != GameDataOverlayDiagnosticKind.SchemaFallback)
                foreach (var d in items) yield return One(d, summary);
            else
                yield return Collapsed(kind.Key.Kind, kind.Key.Item2, items, summary);
        }
    }

    private static string? Reason(GameDataOverlayDiagnostic d) =>
        d.Property?.Reason.ToString() ?? d.Object?.Reason.ToString() ?? d.Record?.Reason.ToString();

    private static BinIssueRowViewModel One(GameDataOverlayDiagnostic d, GameDataSummary summary) => new()
    {
        Kind = d.KindName,
        ObjectName = d.Property?.Entry.Text ?? d.Object?.Name.Text ?? d.Target ?? "(the layer)",
        ClassName = Where(d),
        FieldName = Reason(d),
        Message = d.Message,
        Suggestion = Hint(d.Kind, Reason(d), summary),
    };

    private static BinIssueRowViewModel Collapsed(GameDataOverlayDiagnosticKind kind, string? reason, List<GameDataOverlayDiagnostic> items, GameDataSummary summary) => new()
    {
        Kind = items[0].KindName,
        ObjectName = $"{items.Count:N0} x {items[0].KindName}",
        ClassName = "layer " + string.Join(", ", items.Select(i => i.Layer).Distinct()),
        FieldName = reason,
        Message = "For example: " + string.Join(" | ", items.Take(Examples).Select(i => Truncate(i.Message, 170))),
        Suggestion = Hint(kind, reason, summary),
    };

    private static string Where(GameDataOverlayDiagnostic d) =>
        $"layer {d.Layer}" + (d.Origin is { } origin ? $", module {origin.ModuleIndex}" : "");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private static string Hint(GameDataOverlayDiagnosticKind kind, string? reason, GameDataSummary summary) => kind switch
    {
        GameDataOverlayDiagnosticKind.PropertyEditSkipped when reason == nameof(PropertySkipReason.Untypable) =>
            "The class schema does not type this property (the bin does not hold it, and the schema does not describe this game build), so the edit is skipped. LTK Manager skips it too until its own schema describes the build.",
        GameDataOverlayDiagnosticKind.PropertyEditSkipped => "This edit does not apply to the bin as the game has it, so it is skipped; the rest of the module still applies.",
        GameDataOverlayDiagnosticKind.NoEffect => "Every edit of this module was skipped, so the bin is as it was.",
        GameDataOverlayDiagnosticKind.TargetSkipped => "The bin could not be changed at all: its base did not read, it is not a bin, or a limit was reached.",
        GameDataOverlayDiagnosticKind.EntryUnresolved => summary.IndexUnsettled
            ? "No bin of the game's index declares this entry - but the index is incomplete, so that may be wrong."
            : "No game bin declares this entry, so its edits are skipped.",
        GameDataOverlayDiagnosticKind.EntryFanOut => "Informational: the entry is declared in several bins and every one is edited.",
        GameDataOverlayDiagnosticKind.DeclarationsRejected => "LTK Manager refuses the whole layer, so none of its modules apply.",
        GameDataOverlayDiagnosticKind.IndexUnavailable => "The game's object index could not be read; entries are skipped and references cannot resolve.",
        GameDataOverlayDiagnosticKind.LinkRemovalUnmatched => "A link the module removes is not in the bin.",
        GameDataOverlayDiagnosticKind.ObjectSkipped => "An object the module creates or removes could not be: it exists already, its source is missing, or it is not there to remove.",
        GameDataOverlayDiagnosticKind.SchemaFallback => "Informational: the property was typed without the schema.",
        GameDataOverlayDiagnosticKind.ObjectShadowsGame => "Informational: a game bin declares the object the module creates; both exist.",
        _ => "",
    };
}
