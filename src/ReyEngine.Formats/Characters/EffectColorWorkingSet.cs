using System.Numerics;
using LeagueToolkit.Core.Meta;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Characters;

/// <summary>What one evaluation of the working set changed: the playback model of every system whose colours moved, and what was written to the working trees.</summary>
public sealed record EffectColorPreview(IReadOnlyDictionary<uint, VfxSystemDefinition> Changed, EffectColorApplyResult Result);

/// <summary>
/// M826: the live preview of an effect colour recolour. It holds a WORKING COPY of each bin that carries a system the skin uses (the bin as the project serves it)
/// beside Riot's untouched copy, and evaluates the same <see cref="SkinEffectColors.Apply"/> the save runs - then reads the systems it touched back through
/// <see cref="VfxSystemResolver.ParseSystemObject"/>. So the preview is exactly what saving would play, not a second implementation of the recolour, and it cannot
/// compound: every value is derived from Riot's tree, however often the sliders move.
///
/// <para><b>Letting go.</b> A field the preview wrote and that is no longer asked for (the person switched it off, or the sliders went back to no change) is put back to the value the
/// project held when the preview first touched it. That baseline is the project as it was when the set was made or last <see cref="Rebase"/>d: a save or a revert changes the project,
/// so the card rebases the set after one.</para>
///
/// <para>Thread-safe in the one way the card needs: an evaluation and a rebase never overlap. The trees are private to this object; nothing it does reaches a file.</para>
/// </summary>
public sealed class EffectColorWorkingSet
{
    private sealed class Entry
    {
        public required string Bin { get; init; }
        public required BinTree Riot { get; init; }
        public required HashSet<uint> Systems { get; init; }
        public required BinTree Work { get; set; }
    }

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<uint, Entry> _bySystem = new();
    private readonly HashSet<EffectColorKey> _touched = new();
    private readonly Dictionary<EffectColorKey, Vector4[]> _baseline = new();

    /// <param name="bins">Each bin that holds a system of the skin: its WAD path, the bytes the project serves, Riot's untouched bytes.</param>
    /// <param name="systems">The systems of interest per bin path (the ones the skin reaches); null: every system in it.</param>
    public EffectColorWorkingSet(IEnumerable<(string Bin, byte[] Current, byte[] Riot)> bins, IReadOnlyDictionary<string, ISet<uint>>? systems = null)
    {
        foreach (var (bin, current, riot) in bins)
        {
            var work = SafeBinTree.Parse(current);
            var riotTree = SafeBinTree.Parse(riot);
            var ids = work.Objects.Values.Where(o => o.ClassHash == VfxColorReader.SystemClass).Select(o => o.PathHash).ToHashSet();
            if (systems is not null && systems.TryGetValue(bin, out var wanted)) ids.IntersectWith(wanted);
            var entry = new Entry { Bin = bin, Work = work, Riot = riotTree, Systems = ids };
            _entries.Add(entry);
            foreach (uint id in ids) _bySystem.TryAdd(id, entry);
        }
    }

    /// <summary>The systems this set can recolour.</summary>
    public IReadOnlyCollection<uint> Systems => _bySystem.Keys;

    /// <summary>The playback model of one system as the working tree holds it now; null when the set does not hold it.</summary>
    public VfxSystemDefinition? Definition(uint system)
    {
        lock (_gate)
            return _bySystem.TryGetValue(system, out var entry) && entry.Work.Objects.TryGetValue(system, out var o) ? VfxSystemResolver.ParseSystemObject(o) : null;
    }

    /// <summary>Apply a recolour to the working trees (see <see cref="SkinEffectColors.Apply"/> for the arguments) and return the new playback model of the systems it changed.</summary>
    public EffectColorPreview Apply(ColorTransform transform, IReadOnlyCollection<EffectColorKey> recolour, IReadOnlyCollection<EffectColorKey> restore,
        ColorTransform? recipe = null, IReadOnlyCollection<EffectColorKey>? recipeKeys = null)
    {
        lock (_gate)
        {
            var edits = new List<EffectColorEdit>();
            var changedSystems = new HashSet<uint>();
            var wanted = new HashSet<EffectColorKey>(recolour);
            wanted.UnionWith(restore);

            // let go of what is no longer asked for: back to the project's value
            var release = _touched.Where(k => !wanted.Contains(k)).ToList();
            foreach (var entry in _entries)
            {
                var mine = release.Where(k => entry.Systems.Contains(k.System)).ToList();
                if (mine.Count > 0) changedSystems.UnionWith(SkinEffectColors.Restore(entry.Work, _baseline, mine));
            }
            foreach (var key in release) { _touched.Remove(key); _baseline.Remove(key); }

            // A field the saved recipe owns that is asked to go back to Riot's: whether the recipe still holds it is a question about what the PROJECT holds (that is what saving judges), not
            // about what an earlier evaluation of this set wrote over it. So those fields are first put back to the project's value, and the check below reads that.
            var recolourSet = recolour.ToHashSet();
            var judged = restore.Where(k => _touched.Contains(k) && _baseline.ContainsKey(k) && !recolourSet.Contains(k)).ToList();
            if (judged.Count > 0)
                foreach (var entry in _entries)
                {
                    var mine = judged.Where(k => entry.Systems.Contains(k.System)).ToList();
                    if (mine.Count > 0) changedSystems.UnionWith(SkinEffectColors.Restore(entry.Work, _baseline, mine));
                }

            // remember what the project holds for the fields about to be written
            foreach (var entry in _entries)
            {
                var fresh = wanted.Where(k => entry.Systems.Contains(k.System) && !_baseline.ContainsKey(k)).ToList();
                if (fresh.Count == 0) continue;
                foreach (var (key, values) in SkinEffectColors.Capture(entry.Work, fresh)) _baseline[key] = values;
            }
            _touched.UnionWith(wanted);

            foreach (var entry in _entries)
            {
                var mine = recolour.Where(k => entry.Systems.Contains(k.System)).ToList();
                var give = restore.Where(k => entry.Systems.Contains(k.System)).ToList();
                if (mine.Count == 0 && give.Count == 0) continue;
                var result = SkinEffectColors.Apply(entry.Work, entry.Riot, transform, mine, give, recipe,
                    recipeKeys?.Where(k => entry.Systems.Contains(k.System)).ToList());
                edits.AddRange(result.Edits);
                changedSystems.UnionWith(result.WrittenSystems);
            }

            var changed = new Dictionary<uint, VfxSystemDefinition>();
            foreach (uint system in changedSystems)
                if (_bySystem.TryGetValue(system, out var e) && e.Work.Objects.TryGetValue(system, out var o) && VfxSystemResolver.ParseSystemObject(o) is { } def)
                    changed[system] = def;
            return new EffectColorPreview(changed, new EffectColorApplyResult(edits));
        }
    }

    /// <summary>The project's bins changed (a save, a revert): read them again as the new baseline. Returns the systems the preview had touched, whose playback model the caller should
    /// read again (<see cref="Definition"/>) because the baseline they stand on moved.</summary>
    public IReadOnlyCollection<uint> Rebase(Func<string, byte[]?> readCurrent)
    {
        lock (_gate)
        {
            var touched = _touched.Select(k => k.System).ToHashSet();
            foreach (var entry in _entries)
            {
                if (readCurrent(entry.Bin) is not { } bytes) continue;
                entry.Work = SafeBinTree.Parse(bytes);
            }
            _touched.Clear();
            _baseline.Clear();
            return touched;
        }
    }
}
