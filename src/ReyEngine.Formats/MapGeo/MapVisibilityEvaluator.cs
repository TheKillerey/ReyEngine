namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M843: everything that decides whether a mapgeo mesh is drawn, as one value: the layer picked on each Map Visibility axis
/// (axis field hash -> layer bit, 0 = All), the events switched on, a TFT board stage's exact mask (null = none) and whether
/// render-region meshes show.
/// </summary>
public sealed record VisibilitySelection(
    IReadOnlyDictionary<uint, int> Axes,
    IReadOnlySet<string> Events,
    int? StageMask,
    bool RenderRegions)
{
    public static readonly VisibilitySelection Empty = new(
        new Dictionary<uint, int>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), null, false);
}

/// <summary>
/// M843: the ONE function that decides whether a mapgeo group (and so a mesh) is drawn. The editor's
/// <c>ApplyMapVisibility</c> (both viewports) calls it, and the Blender link's <c>visibility_eval</c> calls it, so Blender
/// draws exactly the meshes this function says (the editor's per-mesh eye / disable / delete state is not modelled). The rules themselves stay in <see cref="MapVisibilityResolver"/> and
/// <see cref="MapVisibility"/>: dragon bits decide (M771), a visibility controller overrides the mask, an event shows its
/// content only while ticked (M802), a board stage replaces the primary axis (M797), and render-region meshes show only with
/// regions on (M104).
/// </summary>
public static class MapVisibilityEvaluator
{
    /// <summary>Is a draw group with these (effective) flags and controller shown?</summary>
    public static bool Visible(MapVisibilityResolver resolver, int flags, uint controllerHash, uint regionHash, VisibilitySelection selection)
    {
        if (!resolver.IsVisible(flags, controllerHash, selection.Axes, selection.StageMask, selection.Events)) return false;
        return selection.RenderRegions || regionHash == 0;
    }

    /// <summary>
    /// Per draw group of <paramref name="map"/>, in group order. A group of a mesh reads the mesh's EFFECTIVE flags and
    /// controller (pending layer edits preview live); <paramref name="hiddenByUser"/> is the editor's own per-mesh eye /
    /// disable / delete state, which the link does not model (null for none).
    /// </summary>
    public static bool[] GroupsVisible(MapGeoAsset map, MapVisibilityResolver resolver, VisibilitySelection selection,
        Func<int, bool>? hiddenByUser = null)
    {
        var meshByIndex = new Dictionary<int, MapGeoMesh>();
        foreach (var m in map.Meshes) meshByIndex[m.Index] = m;
        var visible = new bool[map.Groups.Count];
        for (int i = 0; i < visible.Length; i++)
        {
            var g = map.Groups[i];
            int flags = g.VisibilityFlags;
            uint ctrl = g.ControllerHash;
            uint region = 0;
            if (g.MeshIndex >= 0 && meshByIndex.TryGetValue(g.MeshIndex, out var src))
            { flags = src.EffectiveVisibility; ctrl = src.EffectiveController; region = src.RegionHash; }
            bool v = resolver.IsVisible(flags, ctrl, selection.Axes, selection.StageMask, selection.Events);
            if (hiddenByUser is not null && hiddenByUser(g.MeshIndex)) v = false;
            if (v && !selection.RenderRegions && g.MeshIndex >= 0 && region != 0) v = false;
            visible[i] = v;
        }
        return visible;
    }

    /// <summary>
    /// The indices (<see cref="MapGeoMesh.Index"/>) of the meshes drawn under <paramref name="selection"/>: a mesh is drawn
    /// when any of its draw groups is, and a mesh with no group by its own flags.
    /// </summary>
    public static IReadOnlyList<int> VisibleMeshIndices(MapGeoAsset map, MapVisibilityResolver resolver, VisibilitySelection selection)
    {
        var groups = GroupsVisible(map, resolver, selection);
        var drawn = new HashSet<int>();
        var withGroups = new HashSet<int>();
        for (int i = 0; i < groups.Length; i++)
        {
            int mesh = map.Groups[i].MeshIndex;
            if (mesh < 0) continue;
            withGroups.Add(mesh);
            if (groups[i]) drawn.Add(mesh);
        }
        var result = new List<int>();
        foreach (var m in map.Meshes)
        {
            bool visible = withGroups.Contains(m.Index)
                ? drawn.Contains(m.Index)
                : Visible(resolver, m.EffectiveVisibility, m.EffectiveController, m.RegionHash, selection);
            if (visible) result.Add(m.Index);
        }
        return result;
    }
}
