using ReyEngine.Core.Decoding;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M806: level props - every other "LevelProp_" <c>GdsMapObject</c> (<see cref="MapLevelProp"/>): Summoner's Rift's critters
/// (snails, birds, lizards, antler mice, the duck, the stags, the gromp and dragon props) and its esports banner platforms
/// and walls, Bilgewater's boats, ropes, lanterns and sharks, the Howling Abyss's poros, chains and shopkeepers, the TFT
/// Freljord boards' poro. Before M806 the editor read none of them (M805 drew only the sponsor banners).
///
/// <para><b>Drawn like the placed props</b> (<c>MapAnimatedProp</c>), into the same published set (<c>CurrentPropMeshes</c>),
/// which both viewports draw and which stays the one list of what stands on the map:</para>
/// <list type="bullet">
/// <item>only while Props is on (<c>ShowPropMeshes</c>);</item>
/// <item>under a board stage, by the stage's exact rule on their <c>mVisibilityFlags</c> (M798, <c>StageShowsProp</c>);
/// flags 0 hides one as it hides a disabled prop; and the Map Visibility layer dropdown does not filter them - it never
/// filtered a prop - so the two sru_gromp_prop per Summoner's Rift skin that author 128 show at every layer;</item>
/// <item>the event gate (M802) on their own or their chunk's controller, as the banners and a particle's sound - no shipped
/// level prop has either (all live in listed, ungated chunks);</item>
/// <item>the skin's idle, or the clip a <c>GDSMapObjectAnimationInfo</c> names, played with Anim as any prop's.</item>
/// </list>
/// <para><b>Read-only.</b> Not listed in the outliner, no marker, not pickable, never written: the prop list's editing verbs
/// (move, skin, disable, delete, appear-after) write fields a <c>GdsMapObject</c> does not have. Decoded once per map, off the
/// UI thread, the first time Props is on with one to show.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The open map's level props as its materials bin places them; empty on every map without one.</summary>
    private IReadOnlyList<MapLevelProp> _mapLevelProps = Array.Empty<MapLevelProp>();

    /// <summary>The level props decoded into prop instances - one per level prop that resolved - parallel to
    /// <see cref="_levelPropInstanceOwners"/>. Empty until the first build for this map has finished.</summary>
    private IReadOnlyList<PropInstanceData> _levelPropInstances = Array.Empty<PropInstanceData>();
    private IReadOnlyList<MapLevelProp> _levelPropInstanceOwners = Array.Empty<MapLevelProp>();

    /// <summary>The level prop instances the last published prop set carried, to republish only when that changes.</summary>
    private IReadOnlyList<PropInstanceData> _publishedLevelProps = Array.Empty<PropInstanceData>();

    /// <summary>The decode for <see cref="_mapLevelProps"/>, started the first time one is shown; null before.</summary>
    private Task? _levelPropBuild;

    /// <summary>A map opened (the load extracts its level props), a map tab returned, or the viewport was cleared (null).
    /// Decodes nothing: that waits for Props.</summary>
    private void SetMapLevelProps(IReadOnlyList<MapLevelProp>? props)
    {
        _mapLevelProps = props ?? Array.Empty<MapLevelProp>();
        _levelPropInstances = Array.Empty<PropInstanceData>();
        _levelPropInstanceOwners = Array.Empty<MapLevelProp>();
        _levelPropBuild = null;   // a build still running for the previous map drops its result (see BuildLevelPropsAsync)
    }

    /// <summary>Does the open map show this level prop now? The placed props' rules: Props on, not disabled (flags 0), the
    /// board stage's exact rule while one is on - and the event gate when it or its chunk names a controller. Without the
    /// map's controllers (a load still under way) a gated one is not shown.</summary>
    private bool LevelPropShown(MapLevelProp prop) =>
        ShowPropMeshes
        && prop.VisibilityFlags != 0
        && (_activeBoardStage is not { } stage || MapVisibility.VisibleForStage(prop.VisibilityFlags, stage.Mask))
        && (prop.VisibilityControllerHash == 0 || (_mapControllers is not null && EventAllows(prop.VisibilityControllerHash)));

    /// <summary>The decoded level prop instances shown now.</summary>
    private List<PropInstanceData> ShownLevelPropInstances()
    {
        var shown = new List<PropInstanceData>();
        for (int i = 0; i < _levelPropInstances.Count && i < _levelPropInstanceOwners.Count; i++)
            if (LevelPropShown(_levelPropInstanceOwners[i])) shown.Add(_levelPropInstances[i]);
        return shown;
    }

    /// <summary>Called from <see cref="ApplyMapVisibility"/> (an event, a board stage, a map load, a tab return) and when
    /// Props is switched: start the decode the first time a level prop is shown, otherwise republish the prop set when the
    /// level props it should carry differ from the ones it does (which also drops a previous map's).</summary>
    private void RefreshLevelProps()
    {
        if (_levelPropBuild is null && _mapLevelProps.Any(LevelPropShown))
        {
            _levelPropBuild = BuildLevelPropsAsync();
            return;
        }
        if (_levelPropBuild is { IsCompleted: false }) return;   // its completion publishes what is shown by then
        if (!ShownLevelPropInstances().SequenceEqual(_publishedLevelProps, ReferenceEqualityComparer.Instance))
            PublishAddedMeshPreview();
    }

    /// <summary>Decode the open map's level props off the UI thread, then publish them with the props.</summary>
    private async Task BuildLevelPropsAsync()
    {
        var props = _mapLevelProps;
        string? mapPath = _currentMapEntry?.Path;
        LevelPropBuild built;
        try { built = await Task.Run(() => BuildLevelPropInstances(props, mapPath)); }
        catch (Exception ex) { _log.Warn("Props", "Level props could not be decoded: " + ex.Message); return; }
        if (!ReferenceEquals(props, _mapLevelProps)) return;   // another map opened (or the viewport cleared) meanwhile
        _levelPropInstances = built.Instances;
        _levelPropInstanceOwners = built.Owners;
        _log.Info("Props", built.Summary);
        PublishAddedMeshPreview();
    }

    private sealed record LevelPropBuild(IReadOnlyList<PropInstanceData> Instances, IReadOnlyList<MapLevelProp> Owners, string Summary);

    /// <summary>One prop mesh per (character, clip), shared by its placements; one instance per level prop. Runs off the UI
    /// thread. The same decode as a placed prop's (<c>TryBuildPropMesh</c>): mesh, the skin's materials (with the map's
    /// shipping bin for any material only it holds - no shipped level prop skin needs one), skeleton, idle, skin scale.</summary>
    private LevelPropBuild BuildLevelPropInstances(IReadOnlyList<MapLevelProp> props, string? mapPath)
    {
        var host = LoadHostMaterials(props.Select(p => p.Skin), mapPath, out string hostNote);
        var meshes = new Dictionary<string, PropMesh?>(StringComparer.OrdinalIgnoreCase);
        var texCache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        var instances = new List<PropInstanceData>();
        var owners = new List<MapLevelProp>();
        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int failed = 0;
        foreach (var prop in props)
        {
            if (prop.Skin.Length == 0) { failed++; continue; }
            string? clip = prop.IdleAnimation.Length > 0 ? prop.IdleAnimation : null;
            string key = clip is null ? prop.Skin : prop.Skin + "|" + clip;
            if (!meshes.TryGetValue(key, out var mesh))
                meshes[key] = mesh = TryBuildPropMesh(prop.Skin, texCache, clip, host);
            if (mesh is null) { failed++; unresolved.Add(prop.CharacterName); continue; }
            instances.Add(PropInstanceData.Place(mesh, prop.Transform));
            owners.Add(prop);
        }

        var drawn = meshes.Values.Where(m => m is not null).Select(m => m!).ToList();
        var characters = owners.Select(p => p.CharacterName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var still = drawn.Where(m => !m.CanAnimate).Select(m => m.Key.Split('/')[1]).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        string summary = $"Level props: {instances.Count} of {props.Count} drawn as their characters' Skin0 - {characters.Count} character(s)"
            + (characters.Count > 0 ? ": " + string.Join(", ", characters) : "")
            + (failed > 0 ? $"; {failed} could not be resolved and are not drawn{(unresolved.Count > 0 ? $" ({string.Join(", ", unresolved)})" : "")}" : "")
            + (still.Count > 0 ? $"; {still.Count} character(s) have no idle clip and stand in bind pose ({string.Join(", ", still)})" : "")
            + $"{hostNote}. Read-only: not listed in the outliner or pickable.";
        return new LevelPropBuild(instances, owners, summary);
    }
}
