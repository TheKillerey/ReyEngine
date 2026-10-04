using System;
using System.Linq;
using System.Threading.Tasks;
using ReyEngine.App.Services;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Wad;

namespace ReyEngine.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // An arena owns map state independently of the editor's open map, but loads and renders it through
    // the very same map pipeline. Project mounts stay ahead of the selected map's Riot fallback.
    private async Task<MainWindowViewModel> CreateArenaViewport(string wad)
    {
        var arena = new MainWindowViewModel();
        arena._regularAutoSaveTimer?.Stop();
        try
        {
            arena.Project = Project;
            arena.LoadWad(wad);
            arena._showPropMeshes = true;
            if (ProjectMode)
            {
                arena.ProjectMode = true;
                arena.BuildMounts();
                arena._mounts!.AddFallback(new WadMount(WadArchive.Open(wad, _resolver),
                    AssetSourceKind.RiotReference, editable: false));
            }
            var geo = arena._archive!.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Path.EndsWith("/base_srx.mapgeo", StringComparison.OrdinalIgnoreCase) ? 0
                    : e.Path.EndsWith("/base.mapgeo", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenByDescending(e => e.UncompressedSize).FirstOrDefault()
                ?? throw new InvalidOperationException("No map geometry found.");
            if (arena.TryResolveEntry(geo.PathHash, out var entry)) await arena.LoadMapGeoAsync(entry);
            if (arena.CurrentMesh is null) throw new InvalidOperationException("The arena map could not be loaded.");
            arena.PlayAllParticles = true;
            await arena.RefreshPropMeshesAsync();
            return arena;
        }
        catch { arena.ReleaseArenaViewport(); throw; }
    }

    public Formats.MapGeo.NavGrid? ArenaNavGrid => _navGrid;

    public MapPreviewBackground ArenaBackground(string mapName)
    {
        int n = CurrentMesh!.SubMeshes.Count;
        var empty = new Core.Decoding.TextureImage?[n];
        var mats = CurrentModelSubmeshMaterials;
        return new MapPreviewBackground(mapName, CurrentMesh, CurrentModelTextures ?? empty, empty,
            CurrentModelGradientTextures ?? empty, CurrentModelEmissiveTextures ?? empty, CurrentModelMatCapTextures ?? empty,
            Enumerable.Range(0, n).Select(i => mats is not null && i < mats.Count && mats[i].DoubleSided).ToArray(),
            DynamicLights ?? Array.Empty<Formats.Lighting.PointLight>(), n, 0, mats,
            CurrentModelMaskTextures, CurrentModelLightmapTextures);
    }

    public void ReleaseArenaViewport()
    {
        _regularAutoSaveTimer?.Stop();
        _autoSaveTimer?.Stop();
        Dx11RebindGrassTintPair = null;
        CancelGameData();
        StopProjectWatchers();
        _mounts?.Dispose(); _mounts = null;
        _archive?.Dispose(); _archive = null;
    }
}
