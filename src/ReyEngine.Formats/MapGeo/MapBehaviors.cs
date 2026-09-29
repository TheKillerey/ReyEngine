using System.Buffers.Binary;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>One entry of a <see cref="MapBehavior"/>'s <c>Actions</c> list. <see cref="StartTime"/> is the
/// authored <c>startTime</c> in seconds (absent = 0), relative to the moment the behaviour's cue fires.</summary>
public abstract record MapBehaviorAction(float StartTime);

/// <summary>M797: <c>MapActionSetVisibilityFlag</c> - the game replaces the map's visibility mask with
/// <see cref="Mask"/> (REPLACES: it is not OR-ed onto the previous one). <see cref="TransitionTime"/> is the
/// authored cross-fade in seconds, when there is one; the editor does not fade.</summary>
public sealed record MapSetVisibilityAction(float StartTime, int Mask, float? TransitionTime)
    : MapBehaviorAction(StartTime);

/// <summary>M797: <c>MapActionToggleMapParticle</c> - switches the map particle placements with these NAMES on
/// or off. <c>shown</c> is absent when true (Riot omits defaults).</summary>
public sealed record MapToggleParticleAction(float StartTime, IReadOnlyList<string> ParticleNames, bool Shown)
    : MapBehaviorAction(StartTime);

/// <summary>M797: every other action, counted and never dropped: prop animations, sounds, the unnamed
/// lighting-volume and fog fades (0xa0b62126, 0x1d9354fe), and anything a later patch adds. Also a
/// <c>MapActionSetVisibilityFlag</c> or <c>MapActionToggleMapParticle</c> whose fields are not the shape this
/// reader knows - it is left unapplied rather than guessed at.</summary>
public sealed record MapOtherAction(float StartTime, uint ClassHash) : MapBehaviorAction(StartTime);

/// <summary>
/// M797: a <c>MapBehavior</c> placeable of a map materials bin - a scripted reaction the client runs when the
/// game raises its <see cref="Cue"/> (<c>ArenaSkin_Cue_BoardReady</c>, <c>ArenaSkin_Cue_PlanningLevelUp7</c>,
/// win streaks, clicks ...). TFT boards are the only maps that carry them.
///
/// <para>The behaviours that matter to the viewport are the ones that set the visibility mask, because that
/// mask is what the board's levels are: level 1 and level 7 are different masks over one mapgeo.</para>
/// </summary>
public sealed record MapBehavior(string Name, string Cue, float? CooldownInSec, IReadOnlyList<MapBehaviorAction> Actions)
{
    /// <summary>The <c>*Debug</c> / <c>*DebugBack</c> behaviours are editor-side duplicates of a real one.</summary>
    public bool IsDebug =>
        Name.Contains("Debug", StringComparison.OrdinalIgnoreCase) || Cue.Contains("Debug", StringComparison.OrdinalIgnoreCase);

    public bool SetsVisibility => Actions.Any(a => a is MapSetVisibilityAction);

    /// <summary>Actions in the order the client runs them: by start time, ties in list order (LINQ's OrderBy is
    /// stable).</summary>
    public IEnumerable<MapBehaviorAction> InRunOrder() => Actions.OrderBy(a => a.StartTime);

    /// <summary>The mask this behaviour leaves the map on: its LAST <see cref="MapSetVisibilityAction"/> by start
    /// time, ties by list order. Null when it never sets one.</summary>
    public int? EndVisibilityMask =>
        InRunOrder().OfType<MapSetVisibilityAction>().Select(a => (int?)a.Mask).LastOrDefault();

    /// <summary>Per placement name, the state this behaviour leaves it in: its last toggle by start time, ties by
    /// list order. Names are compared case-insensitively.</summary>
    public IReadOnlyDictionary<string, bool> EndParticleToggles
    {
        get
        {
            var end = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var toggle in InRunOrder().OfType<MapToggleParticleAction>())
                foreach (var name in toggle.ParticleNames) end[name] = toggle.Shown;
            return end;
        }
    }

    /// <summary>Actions the viewport does not apply: everything but the visibility mask and the particle
    /// toggles.</summary>
    public int UnappliedActionCount => Actions.Count(a => a is MapOtherAction);
}

/// <summary>M797: reads the <see cref="MapBehavior"/> placeables of a map materials bin. Never throws.</summary>
public static class MapBehaviors
{
    private static readonly uint ContainerClass = HashAlgorithms.Fnv1a("MapPlaceableContainer");
    private static readonly uint BehaviorClass = HashAlgorithms.Fnv1a("MapBehavior");
    private static readonly uint SetVisibilityClass = HashAlgorithms.Fnv1a("MapActionSetVisibilityFlag");
    private static readonly uint ToggleParticleClass = HashAlgorithms.Fnv1a("MapActionToggleMapParticle");

    /// <summary>Does the bin write this class hash anywhere? A pointer's class hash is written verbatim, so an
    /// absent hash proves no instance of the class exists - a byte scan (about 5 ms for all 26 of Summoner's
    /// Rift's materials bins, 37 MB) that lets a caller skip a parse.</summary>
    public static bool Names(byte[]? bin, uint classHash)
    {
        if (bin is not { Length: > 8 }) return false;
        Span<byte> needle = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, classHash);
        return bin.AsSpan().IndexOf(needle) >= 0;
    }

    /// <summary>Could this bin carry a board stage? A stage is a <c>MapBehavior</c> that sets the visibility mask,
    /// so the bin must write BOTH class hashes. Measured over the installed game's 201 materials bins: Summoner's
    /// Rift (26 bins) and Map453 name neither and cost only the scan; Map12's bloom (one behaviour), the three
    /// Arena maps and 39 of the 159 Map22 bins name <c>MapBehavior</c> but set no mask, so the second hash spares
    /// them the parse (50 ms for bloom, under a millisecond for an arena).</summary>
    public static bool MayHaveStages(byte[]? bin) => Names(bin, BehaviorClass) && Names(bin, SetVisibilityClass);

    /// <summary>Every <c>MapBehavior</c> in the bin, in file order. A bin that never mentions the class - every
    /// map but the TFT boards and a few others - is rejected by a byte scan before any parse
    /// (<see cref="Names"/>).</summary>
    public static IReadOnlyList<MapBehavior> Extract(byte[]? materialsBin)
    {
        var result = new List<MapBehavior>();
        if (!Names(materialsBin, BehaviorClass)) return result;
        if (materialsBin is null) return result;

        BinTree tree;
        try { tree = SafeBinTree.Parse(materialsBin); }
        catch { return result; }

        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != ContainerClass) continue;
            if (Field(o.Properties, "items") is not BinTreeMap items) continue;
            foreach (var entry in items)
            {
                if (entry.Value is not BinTreeStruct s || s.ClassHash != BehaviorClass) continue;
                result.Add(Read(s));
            }
        }
        return result;
    }

    private static MapBehavior Read(BinTreeStruct s)
    {
        var actions = new List<MapBehaviorAction>();
        if (Field(s.Properties, "Actions") is BinTreeContainer list)
            foreach (var element in list.Elements)
                actions.Add(element is BinTreeStruct action ? ReadAction(action) : new MapOtherAction(0f, 0u));

        return new MapBehavior(
            (Field(s.Properties, "name") as BinTreeString)?.Value ?? "",
            (Field(s.Properties, "Cue") as BinTreeString)?.Value ?? "",
            Field(s.Properties, "CooldownInSec") is BinTreeF32 cooldown ? cooldown.Value : null,
            actions);
    }

    private static MapBehaviorAction ReadAction(BinTreeStruct a)
    {
        float start = Field(a.Properties, "startTime") is BinTreeF32 t ? t.Value : 0f;

        if (a.ClassHash == SetVisibilityClass && Field(a.Properties, "VisibilityFlags") is { } flags && Integer(flags) is { } mask)
            return new MapSetVisibilityAction(start, mask,
                Field(a.Properties, "TransitionTime") is BinTreeF32 fade ? fade.Value : null);

        if (a.ClassHash == ToggleParticleClass && Field(a.Properties, "MapParticleName") is BinTreeContainer names)
        {
            bool? shown = Field(a.Properties, "shown") switch
            {
                null => true,   // Riot omits a true bool
                BinTreeBool b => b.Value,
                BinTreeBitBool b => b.Value,
                _ => null,
            };
            if (shown is { } known)
                return new MapToggleParticleAction(start,
                    names.Elements.OfType<BinTreeString>().Select(n => n.Value).ToList(), known);
        }

        return new MapOtherAction(start, a.ClassHash);
    }

    private static int? Integer(BinTreeProperty p) => p switch
    {
        BinTreeU8 v => v.Value,
        BinTreeU16 v => v.Value,
        BinTreeU32 v when v.Value <= int.MaxValue => (int)v.Value,
        BinTreeI8 v => v.Value,
        BinTreeI16 v => v.Value,
        BinTreeI32 v => v.Value,
        _ => null,
    };

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> props, string name)
        => props.TryGetValue(HashAlgorithms.Fnv1a(name), out var p) ? p
         : props.TryGetValue(HashAlgorithms.Fnv1aRaw(name), out var q) ? q : null;
}
