using System.Text.RegularExpressions;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M797: one game state of a TFT board - "Board ready (level 1)", "Level 7" - as the board's own
/// <see cref="MapBehavior"/>s define it.
///
/// <para><b>What a stage is.</b> The visibility mask the client is on once the behaviour has run, and the
/// per-name particle toggles it leaves behind. Both are CUMULATIVE over the chain: level 7 is board-ready's end
/// state with level 7's own actions run on top, so a particle board-ready switched on stays on unless level 7
/// says otherwise, while the mask is simply the last one set.</para>
/// </summary>
/// <param name="Level">The N of <c>LevelUpN</c>; null for board-ready.</param>
/// <param name="Mask">The visibility mask after the chain - the value the last
/// <c>MapActionSetVisibilityFlag</c> wrote. Content is on when its flags share a bit with it
/// (<see cref="MapVisibility.VisibleForStage"/>).</param>
/// <param name="ParticleToggles">Placement name -> shown, after the whole chain. A name absent from the
/// dictionary was never touched and keeps its authored state.</param>
/// <param name="Chain">The behaviours run to reach this stage, in order, selected one last.</param>
/// <param name="UnappliedActionCount">Actions in the chain the viewport does not emulate: prop animations,
/// sounds, lighting-volume and fog fades.</param>
public sealed record MapBoardStage(
    string Label, string BehaviorName, string Cue, int? Level, int Mask,
    IReadOnlyDictionary<string, bool> ParticleToggles, IReadOnlyList<string> Chain, int UnappliedActionCount);

/// <summary>M797: a board's stages, and what the lighting needs to follow them.</summary>
public sealed class MapBoardStageSet
{
    public static readonly MapBoardStageSet Empty = new(Array.Empty<MapBoardStage>(), Array.Empty<MapLightingVolume>(), null);

    public MapBoardStageSet(IReadOnlyList<MapBoardStage> stages, IReadOnlyList<MapLightingVolume> volumes, MapSunProperties? globalSun)
    {
        Stages = stages;
        Volumes = volumes;
        GlobalSun = globalSun;
    }

    public IReadOnlyList<MapBoardStage> Stages { get; }
    public IReadOnlyList<MapLightingVolume> Volumes { get; }
    public MapSunProperties? GlobalSun { get; }
    public bool HasStages => Stages.Count > 0;

    /// <summary>The lighting volume that lights the board at the stage's mask: the one volume a lone-volume board
    /// has (M207), or the one the mask turns on (M785's rule, asked with the stage's mask instead of the map's
    /// initial one). Null when the board's own global sun lights it - the mask does not decide between volumes.</summary>
    public MapLightingVolume? VolumeFor(MapBoardStage stage) => MapLighting.VolumeForMask(Volumes, stage.Mask);

    /// <summary>The sun the stage lights the board with: <see cref="MapLighting.SunForMask"/>.</summary>
    /// <param name="startSun">The lighting the board opened with. When given, the stage keeps every field of it that a
    /// lighting volume does not model and takes only the nine a volume does (<see cref="MapLighting.WithVolumeFields"/>),
    /// so a stage view never disagrees with the Start view about a value neither of them authors.</param>
    public MapSunProperties? SunFor(MapBoardStage stage, MapSunProperties? startSun = null)
    {
        var lit = MapLighting.SunForMask(Volumes, GlobalSun, stage.Mask);
        return lit is null ? null : MapLighting.WithVolumeFields(startSun, lit);
    }
}

/// <summary>M797: builds the stages of a TFT board out of its <see cref="MapBehavior"/>s.</summary>
public static class MapBoardStages
{
    private static readonly Regex LevelUp = new(@"LevelUp(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The stages of a materials bin, with the lighting volumes to follow them. <see cref="MapBoardStageSet.Empty"/>
    /// for a bin with no stage behaviour, which is every map but the TFT boards. A bin that cannot have one - it
    /// does not write both class hashes (<see cref="MapBehaviors.MayHaveStages"/>) - costs a byte scan and no
    /// parse.</summary>
    public static MapBoardStageSet Load(byte[]? materialsBin)
    {
        if (!MapBehaviors.MayHaveStages(materialsBin)) return MapBoardStageSet.Empty;
        var stages = Build(MapBehaviors.Extract(materialsBin));
        if (stages.Count == 0 || materialsBin is null) return MapBoardStageSet.Empty;
        var global = MapSunProperties.Extract(materialsBin);
        return new MapBoardStageSet(stages, MapLighting.Extract(materialsBin, global).Volumes, global);
    }

    /// <summary>
    /// The stages are the board-ready behaviour and the <c>LevelUpN</c> behaviours, when they set the visibility
    /// mask: board-ready first, then the levels by N. Debug behaviours (<c>*Debug</c>, <c>*DebugBack</c>) are
    /// editor-side duplicates and never count.
    ///
    /// <para><b>Measured</b> over every materials bin in the installed map WADs (Map11, Map12, Map22, Map30, Map453:
    /// 201 bins, 81 of them with behaviours, 32 with a board-ready or level-up behaviour that sets the mask). What
    /// else writes a mask are EVENTS that play over whatever stage is on - WinStreak / WinStreakEnd (2 boards),
    /// Set8's cat triggers, EsportsEnd, the last-drop and samurai transitions - and they are not offered as
    /// stages: a win streak is not a level, and "cumulative" means nothing for a trigger.</para>
    ///
    /// <para><b>Several behaviours for one level.</b> <c>LevelUp7</c> (a bare cue, no actions on most boards) and
    /// <c>LevelUp7Planning</c> (the transformation) only both set a mask on choncc; set10_kda has a
    /// <c>ReconnectLevelUp6</c> beside its <c>PlanningLevelUp6</c>. The planning behaviour is the one every board
    /// has, so it wins; otherwise the one with more actions.</para>
    /// </summary>
    public static IReadOnlyList<MapBoardStage> Build(IReadOnlyList<MapBehavior> behaviors)
    {
        ArgumentNullException.ThrowIfNull(behaviors);
        var ordered = behaviors
            .Where(b => !b.IsDebug && b.EndVisibilityMask is not null)
            .Select(b => (Behavior: b, Level: LevelOf(b, out bool boardReady), BoardReady: boardReady))
            .Where(x => x.BoardReady || x.Level is not null)
            .GroupBy(x => x.BoardReady ? -1 : x.Level!.Value)
            .Select(g => g
                .OrderByDescending(x => IsPlanning(x.Behavior))
                .ThenByDescending(x => x.Behavior.Actions.Count)
                .ThenBy(x => x.Behavior.Name, StringComparer.Ordinal)
                .First())
            .OrderBy(x => x.BoardReady ? -1 : x.Level!.Value)
            .ToList();

        var stages = new List<MapBoardStage>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            int mask = 0;
            int unapplied = 0;
            var toggles = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var chain = new List<string>(i + 1);
            for (int k = 0; k <= i; k++)
            {
                var b = ordered[k].Behavior;
                mask = b.EndVisibilityMask ?? mask;
                foreach (var (name, shown) in b.EndParticleToggles) toggles[name] = shown;
                unapplied += b.UnappliedActionCount;
                chain.Add(b.Name);
            }
            var (behavior, level, boardReady) = ordered[i];
            stages.Add(new MapBoardStage(boardReady ? "Board ready (level 1)" : $"Level {level}", behavior.Name, behavior.Cue,
                boardReady ? null : level, mask, toggles, chain, unapplied));
        }
        return stages;
    }

    private static bool IsPlanning(MapBehavior b) =>
        b.Name.Contains("Planning", StringComparison.OrdinalIgnoreCase) || b.Cue.Contains("Planning", StringComparison.OrdinalIgnoreCase);

    /// <summary>The N of a <c>LevelUpN</c> behaviour (from its cue, else its name); null for anything else.
    /// <paramref name="boardReady"/> is true for the board-ready behaviour.</summary>
    private static int? LevelOf(MapBehavior b, out bool boardReady)
    {
        boardReady = b.Name.Contains("BoardReady", StringComparison.OrdinalIgnoreCase)
                     || b.Cue.Contains("BoardReady", StringComparison.OrdinalIgnoreCase);
        if (boardReady) return null;
        foreach (string text in new[] { b.Cue, b.Name })
            if (LevelUp.Match(text) is { Success: true } m && int.TryParse(m.Groups["n"].Value, out int n))
                return n;
        return null;
    }
}
