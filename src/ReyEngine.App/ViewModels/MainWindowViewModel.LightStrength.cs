using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M901: the strength of the map's own point lights (<c>MapDynamicPointLight</c> in materials.bin). The panel holds a COPY of each
/// light; this ties the copy back to its bin entry so the inspector's Strength is written to that entry's <c>intensityScale</c> by
/// Save to Mod (see SaveParticleMoves) and to nothing else. Lights with no entry - Light.dat, editor-made - are untouched.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>A placed light's strength moved: Save to Mod is offered (or withdrawn when it is back at the authored value).</summary>
    internal void NotifyPlacedLightStrengthChanged() => RefreshPlacementDirtyFlag();

    /// <summary>Placed lights whose strength differs from the bin's.</summary>
    private List<PointLightViewModel> EditedPlacedLights() => EditableLights.Where(l => l.StrengthEdited).ToList();

    /// <summary>
    /// M901: tie each panel light to the <c>MapDynamicPointLight</c> it came from, so its strength can be written to that entry's
    /// <c>intensityScale</c> and nothing else. The panel is a copy (the project's saved lighting record replaces the imported lights
    /// on every open), so the link is re-made by matching: same position and radius, else a name that is unique on both sides.
    /// A light that matches nothing - a Light.dat light, an editor-made one - stays unlinked and keeps the old Light.dat behaviour.
    /// </summary>
    /// <returns>How many lights were linked.</returns>
    public int LinkPlacedLights(IReadOnlyList<PlacedPointLight> placed)
    {
        foreach (var l in EditableLights) l.ClearPlacementLink();
        var free = EditableLights.ToList();
        var unmatched = new List<PlacedPointLight>();
        int linked = 0, pending = 0;

        void Link(PointLightViewModel vm, PlacedPointLight p)
        {
            vm.LinkToPlacement(p.Id, p.Light.IntensityScale);
            free.Remove(vm); linked++;
            if (vm.StrengthEdited) pending++;
        }

        foreach (var p in placed)
        {
            var vm = free.FirstOrDefault(v =>
                System.Numerics.Vector3.Distance(v.Position, p.Light.Position) < 1f && Math.Abs(v.Radius - p.Light.Radius) < 1f);
            if (vm is not null) Link(vm, p); else unmatched.Add(p);
        }
        foreach (var p in unmatched)
        {
            if (string.IsNullOrWhiteSpace(p.Light.Name)) continue;
            if (unmatched.Count(o => o.Light.Name == p.Light.Name) != 1) continue;
            var byName = free.Where(v => v.Name == p.Light.Name).ToList();
            if (byName.Count == 1) Link(byName[0], p);
        }
        if (pending > 0)
            _log.Info("Lights", $"{pending} placed light(s) carry a strength that differs from the map's - Save to Mod writes it.");
        RefreshPlacementDirtyFlag();
        return linked;
    }

    /// <summary>Read the open map's point-light placements and link the panel to them. Failure leaves the lights unlinked (the
    /// safe answer: a strength edit then simply is not offered for saving).</summary>
    private void LinkPlacedLightsFromMap(string? mapGeoPath)
    {
        try
        {
            if (string.IsNullOrEmpty(mapGeoPath) || !TryResolveMaterialsBin(mapGeoPath, out var binEntry)) return;
            LinkPlacedLights(DynamicPointLights.ReadPlacements(ReadAsset(binEntry.PathHash)));
        }
        catch (Exception ex) { _log.Warn("Lights", "Placed lights could not be linked to the map: " + ex.Message); }
    }

    /// <summary>Forget every link (the port re-keyed the placements) and withdraw any pending strength edit from Save to Mod.</summary>
    private void ClearPlacedLightLinks()
    {
        foreach (var l in EditableLights) l.ClearPlacementLink();
        RefreshPlacementDirtyFlag();
    }
}
