using System;
using System.Collections.Generic;
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
            // M868: minimal overhead, no distracting map effects. The Character window stages the champion on
            // the bare terrain: no placed props (and so no prop animations), no map particles, no distance
            // fog. These are the viewport's defaults for a freshly constructed MainWindowViewModel except
            // fog, which defaults on, so it is turned off here. Nothing in the Character window turns them back on.
            arena.ShowFog = false;
            // M872: while Direct3D 11 is the preview, the stage map skips what only the editor's map view and the GL viewport use
            // (GL texture decode, map audio banks, ray index, a copy of the mapgeo bytes). See LoadDeferredMapTexturesAsync.
            arena._leanArena = MeshPreview.UseDx11Preview;
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
            return arena;
        }
        catch { arena.ReleaseArenaViewport(); throw; }
    }

    public Formats.MapGeo.NavGrid? ArenaNavGrid => _navGrid;

    // M872: set on a stage viewport opened under Direct3D 11; see CreateArenaViewport.
    private bool _leanArena;
    private Func<IReadOnlyList<Core.Decoding.TextureImage?>>? _deferredMapTextures;

    private Task? _deferredLoad;

    /// <summary>M872: this viewport was opened lean (under Direct3D 11), so its backdrop must be built again once the OpenGL textures exist.</summary>
    public bool IsLeanStage => _leanArena;

    /// <summary>M872: this stage viewport skipped its OpenGL textures and has not made them yet (true while the decode runs, and again
    /// after a failed one, so the next switch to OpenGL retries).</summary>
    public bool NeedsDeferredMapTextures => _deferredMapTextures is not null;

    /// <summary>M872: hand address space the runtime keeps committed back to the OS after a decode that filled the large object heap
    /// with textures. Blocking and compacting; call it once per big build, never per frame.</summary>
    internal static void HandBackLargeObjectMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>M872: make the OpenGL textures a lean stage viewport skipped (the user unticked Direct3D 11). Idempotent.</summary>
    public Task LoadDeferredMapTexturesAsync() => _deferredLoad ??= LoadDeferredCoreAsync();

    private async Task LoadDeferredCoreAsync()
    {
        try
        {
            if (_deferredMapTextures is not { } build) return;
            var textures = await Task.Run(() => { var t = build(); HandBackLargeObjectMemory(); return t; });
            CurrentModelTextures = textures;
            ClearSecondaryTextures();
            PublishMapMaterialLayers();
            _deferredMapTextures = null;   // only after it worked: a failure leaves it for the next switch to OpenGL
        }
        finally { _deferredLoad = null; }
    }

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
