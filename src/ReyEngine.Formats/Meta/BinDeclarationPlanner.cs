using System.Globalization;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Meta;

/// <summary>One file of a mod's package: the layer and the WAD folder it ships in, its path inside that WAD,
/// and where the bytes live.</summary>
public sealed record DeclarationFile(string Layer, string WadFolder, string RelPath, string AbsPath);

/// <summary>A game bin declared as changes, with the layer and project path it came from.</summary>
public sealed record DeclaredBin(string Layer, string WadFolder, string RelPath, DeclaredChunk Chunk);

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
    public static DeclarationPlan Plan(
        IEnumerable<DeclarationFile> files, Func<ulong, string, byte[]?> readRiot, IDeclarationNames names)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(readRiot);
        ArgumentNullException.ThrowIfNull(names);

        var kept = new List<DeclarationFile>();
        var dropped = new List<DeclarationFile>();
        var modules = new Dictionary<string, List<DeclaredChunk>>(StringComparer.OrdinalIgnoreCase);
        var declaredBins = new List<DeclaredBin>();
        var seen = new Dictionary<string, (byte[] Bytes, bool ShipsWhole, string Where)>(StringComparer.Ordinal);
        var whole = new List<string>();
        var noCopy = new List<string>();
        int declared = 0, unchanged = 0, props = 0, added = 0, removed = 0;

        foreach (var f in files)
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

            var chunk = BinDeclarations.Convert(target, riot, mod, names);
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
            chunk = chunk with { Label = f.RelPath.Replace('\\', '/') };
            if (!modules.TryGetValue(f.Layer, out var list)) modules[f.Layer] = list = new List<DeclaredChunk>();
            list.Add(chunk);
            declaredBins.Add(new DeclaredBin(f.Layer, f.WadFolder, f.RelPath, chunk));
            dropped.Add(f);
            declared++; props += chunk.Properties; added += chunk.ObjectsAdded; removed += chunk.ObjectsRemoved;
        }

        var ordered = new Dictionary<string, IReadOnlyList<DeclaredChunk>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (layer, list) in modules)
            ordered[layer] = list.OrderBy(c => c.Target, StringComparer.Ordinal).ToList();

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
        };
    }
}
