using System.Globalization;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Meta;

/// <summary>One file of a mod's package: the layer and the WAD folder it ships in, its path inside that WAD,
/// and where the bytes live.</summary>
public sealed record DeclarationFile(string Layer, string WadFolder, string RelPath, string AbsPath);

/// <summary>
/// M823: the two bins a project's copy of a bin is declared between when the imported GameData also targets that bin.
/// </summary>
/// <param name="Game">The game's bin with the modules that run in front of the new one applied to it - the bin LTK has when the new module runs.</param>
/// <param name="Mod">The project's copy with the same modules applied to it: the bin the module has to produce.</param>
public sealed record DeclarationBaseline(byte[] Game, byte[] Mod);

/// <summary>
/// M823: tells the planner where a project bin that an imported package's GameData also targets stands in the install order.
///
/// <para>A project that holds a whole bin and a GameData module for it has LTK's "mod copy as base" case: the module runs over the project's copy. Declared against the raw game
/// the copy would be diffed without the modules, and the module that results would run on top of the package's own modules - the package's effects applied twice, or reverted. So the
/// copy is declared between the game's bin and the copy, BOTH with the modules that run in front of the new one applied: what it states is only what the copy changes beyond what the
/// package's modules (and the edits kept on top of them) already do.</para>
/// </summary>
public interface IDeclarationBaselines
{
    /// <summary>The pair for a project bin, or null when the GameData does not target it (it is declared against the game's bin as ever).</summary>
    /// <param name="file">The project's file: its layer says which modules run in front of its module.</param>
    /// <param name="chunk">The bin's path hash.</param>
    /// <param name="game">The game's bin, as the planner reads it.</param>
    /// <param name="mod">The project's copy.</param>
    DeclarationBaseline? For(DeclarationFile file, ulong chunk, byte[] game, byte[] mod);
}

/// <summary>A game bin declared as changes, with the layer and project path it came from.</summary>
/// <param name="Chunk">The bin's first module. M814: its only one.</param>
public sealed record DeclaredBin(string Layer, string WadFolder, string RelPath, DeclaredChunk Chunk)
{
    /// <summary>M815: every module the bin became, in the order they apply. A bin the diff declared is one module
    /// (<see cref="Chunk"/>); a forced map skin's bin is the references and then, when the project holds more than the
    /// switch, the values that follow them - two modules of one target, which LTK applies in this order.</summary>
    public IReadOnlyList<DeclaredChunk> Chunks { get; init; } = [Chunk];
}

/// <summary>
/// M814: which of a project's bins ship as declarations and which still ship as files. The one answer behind
/// both Send to LTK Manager (<c>game_data.yaml</c>) and Export .fantome (<c>Layers.&lt;name&gt;.GameData</c>).
/// </summary>
public sealed class DeclarationPlan
{
    /// <summary>What still ships as files, in the order given: everything that is not a bin, a bin with no
    /// game copy (new content) and a bin that cannot be declared.</summary>
    public required IReadOnlyList<DeclarationFile> Kept { get; init; }

    /// <summary>The files that do not ship as files because a declaration (or the game's own copy) stands
    /// for them: a declared bin, an unchanged bin, and a copy of a bin another folder of the layer already
    /// declared.</summary>
    public required IReadOnlyList<DeclarationFile> Dropped { get; init; }

    /// <summary>Each layer's declared chunks, ordered by target so one project always declares in one order
    /// (the layer name compares without regard to case).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<DeclaredChunk>> Modules { get; init; }

    /// <summary>Every declared bin, in the order the files were given.</summary>
    public required IReadOnlyList<DeclaredBin> DeclaredBins { get; init; }

    /// <summary>One line per bin that ships whole: <c>folder/path: why</c>.</summary>
    public required IReadOnlyList<string> Whole { get; init; }

    /// <summary>The bins the game has no copy of: new content, or a project with no Riot reference WAD set up to
    /// compare with. They ship as files; listed so a setting that declared nothing can say why.</summary>
    public required IReadOnlyList<string> NoGameCopy { get; init; }

    /// <summary>M815: one line per forced map skin the plan met - what was declared by reference, or why nothing was.</summary>
    public IReadOnlyList<string> SwitchNotes { get; init; } = Array.Empty<string>();

    /// <summary>M815: how many values of the plan are references to the installed game (<see cref="DeclRef"/>).</summary>
    public int References { get; init; }

    public int Declared { get; init; }
    public int Unchanged { get; init; }
    public int Properties { get; init; }
    public int ObjectsAdded { get; init; }
    public int ObjectsRemoved { get; init; }

    /// <summary>The summary line and up to 20 reasons, levelled for a log (0 success, 1 info). The text is what
    /// Send to LTK Manager has always logged when <paramref name="sent"/> is "sent".</summary>
    public IReadOnlyList<(int Level, string Line)> Report(string sent = "sent")
    {
        var report = new List<(int, string)>
        {
            (0, $"Declarations: {Declared} game bin(s) {sent} as changes ({Properties} propert(ies), {ObjectsAdded} object(s) added, "
                + $"{ObjectsRemoved} removed), {Unchanged} unchanged bin(s) not {sent}, {Whole.Count} {sent} whole."),
        };
        foreach (var note in SwitchNotes) report.Add((1, "  " + note));
        foreach (var w in Whole.Take(20)) report.Add((1, $"  {sent} whole - " + w));
        if (Whole.Count > 20) report.Add((1, $"  ... and {Whole.Count - 20} more {sent} whole."));
        if (NoGameCopy.Count > 0)
            report.Add((1, $"  {NoGameCopy.Count} bin(s) have no game copy to compare with and are {sent} as files: new content, "
                + "or the project has no Riot reference WAD for them."));
        return report;
    }
}

/// <summary>
/// M757 + M814: turn every project bin that overrides a GAME bin into declarations against the game's copy.
///
/// <para>A declared or unchanged bin leaves the file list; one that cannot be declared stays in it and the
/// plan says why. A bin with no game copy is new content and ships as it always did. The same bin in two WAD
/// folders of one layer declares once when the copies are identical; two different copies cannot, and both
/// ship whole.</para>
///
/// <para>M814 moved this out of the main window's view model so Send to LTK Manager and Export .fantome run the
/// same code over the same inputs and cannot give a project two different sets of declarations. Two things
/// changed with it, both for the better of both callers: the modules of a layer are ordered by target (the file
/// system's enumeration order used to decide), and an identical second copy of a bin whose first copy ships
/// whole ships too - dropping it left the second WAD without an override it carried.</para>
/// </summary>
public static class BinDeclarationPlanner
{
    /// <param name="files">The project's package files, layer by layer.</param>
    /// <param name="readRiot">The game's untouched copy of a chunk: given its hash and its project path, the
    /// bytes, or null when the game has no such chunk (or it cannot be read).</param>
    /// <param name="names">Plaintext for the hashes a declaration spells.</param>
    /// <param name="mapSkins">M815: the project's recorded map skin switches. Given, a forced map skin's shipping bin and its source
    /// container are declared BY REFERENCE to the game's source slot (<see cref="MapSkinDeclarations"/>); null leaves every bin to the
    /// diff, as M814 had it.</param>
    /// <param name="baselines">M823: where a bin the imported GameData also targets stands in the install order (<see cref="IDeclarationBaselines"/>). Null declares every bin against the game's, as it was.</param>
    public static DeclarationPlan Plan(
        IEnumerable<DeclarationFile> files, Func<ulong, string, byte[]?> readRiot, IDeclarationNames names,
        MapSkinDeclarationOptions? mapSkins = null, IDeclarationBaselines? baselines = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(readRiot);
        ArgumentNullException.ThrowIfNull(names);

        var fileList = files as IReadOnlyList<DeclarationFile> ?? files.ToList();
        var switches = mapSkins is null ? null : MapSkinDeclarations.Read(fileList, readRiot, mapSkins);

        var kept = new List<DeclarationFile>();
        var dropped = new List<DeclarationFile>();
        var layerModules = new Dictionary<string, List<DeclaredChunk>>(StringComparer.OrdinalIgnoreCase);
        var declaredBins = new List<DeclaredBin>();
        var seen = new Dictionary<string, (byte[] Bytes, bool ShipsWhole, string Where)>(StringComparer.Ordinal);
        var whole = new List<string>();
        var noCopy = new List<string>();
        int declared = 0, unchanged = 0, props = 0, added = 0, removed = 0;

        foreach (var f in fileList)
        {
            if (!f.RelPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) { kept.Add(f); continue; }

            string stem = Path.GetFileNameWithoutExtension(f.RelPath);
            ulong hash = !f.RelPath.Contains('/') && stem.Length == 16
                         && ulong.TryParse(stem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex : HashAlgorithms.WadPath(f.RelPath);

            byte[]? riot = null;
            try { riot = readRiot(hash, f.RelPath); } catch { /* an unreadable game copy is no game copy */ }
            if (riot is null) { noCopy.Add($"{f.WadFolder}/{f.RelPath}"); kept.Add(f); continue; }   // not a game bin: new content ships as a file

            byte[] mod = File.ReadAllBytes(f.AbsPath);
            string target = BinDeclarations.TargetOf(f.RelPath, hash);
            string where = $"{f.WadFolder}/{f.RelPath}";

            // the same bin in two WAD folders of one layer declares once; two different copies cannot
            string key = f.Layer.ToLowerInvariant() + "\u0000" + target;
            if (seen.TryGetValue(key, out var first))
            {
                if (!first.Bytes.AsSpan().SequenceEqual(mod))
                {
                    whole.Add($"{where}: two different copies in one layer");
                    kept.Add(f);
                }
                else if (first.ShipsWhole)
                {
                    // the first copy ships whole, so this identical copy has to as well: it is the one
                    // that reaches its own WAD
                    whole.Add($"{where}: the same bin as {first.Where}, which ships whole");
                    kept.Add(f);
                }
                else dropped.Add(f);
                continue;
            }

            // M823: a bin the imported GameData also targets is declared between the game's bin and the project's, both with the modules in front of the new one applied
            var baseline = baselines?.For(f, hash, riot, mod);

            // M815: a forced map skin is declared by reference, then by value for whatever the project holds beyond it
            var switched = switches?.Declare(f.RelPath, target, riot, mod, names, baseline);
            var chunk = switched?[0] ?? BinDeclarations.Convert(target, baseline?.Game ?? riot, baseline?.Mod ?? mod, names);
            if (chunk.Unchanged)
            {
                seen[key] = (mod, false, where);
                unchanged++;
                dropped.Add(f);
                continue;
            }
            if (!chunk.Declared)
            {
                seen[key] = (mod, true, where);
                whole.Add($"{where}: {chunk.WhyNot}");
                kept.Add(f);
                continue;
            }

            seen[key] = (mod, false, where);
            string label = f.RelPath.Replace('\\', '/');
            var labelled = (switched ?? new[] { chunk }).Select(c => c with { Label = label }).ToList();
            if (!layerModules.TryGetValue(f.Layer, out var list)) layerModules[f.Layer] = list = new List<DeclaredChunk>();
            list.AddRange(labelled);
            declaredBins.Add(new DeclaredBin(f.Layer, f.WadFolder, f.RelPath, labelled[0]) { Chunks = labelled });
            dropped.Add(f);
            declared++;
            foreach (var c in labelled) { props += c.Properties; added += c.ObjectsAdded; removed += c.ObjectsRemoved; }
        }

        var ordered = new Dictionary<string, IReadOnlyList<DeclaredChunk>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (layer, list) in layerModules)
            ordered[layer] = list.OrderBy(c => c.Target, StringComparer.Ordinal).ToList();   // stable: the modules of one bin keep their apply order

        return new DeclarationPlan
        {
            Kept = kept,
            Dropped = dropped,
            Modules = ordered,
            DeclaredBins = declaredBins,
            Whole = whole,
            NoGameCopy = noCopy,
            Declared = declared,
            Unchanged = unchanged,
            Properties = props,
            ObjectsAdded = added,
            ObjectsRemoved = removed,
            SwitchNotes = switches?.Notes.ToList() ?? new List<string>(),
            References = switches?.References ?? 0,
        };
    }
}
