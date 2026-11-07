using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.Views;

public partial class MeshPreviewWindow
{
    private MainWindowViewModel? _committedArena;
    private int _arenaMaterialsRevision = -1;
    private bool _buildingArena;
    private int _arenaCharacterGeometry = -1;
    private List<PreviewMaterial> _arenaCharacterMaterials = new();
    private List<string> _arenaCharacterTextureKeys = new();
    private int _arenaCharacterInsertIndex = -1;   // M872: where the character's materials sit in the draw list - after the map's, before particles and props
    private bool _arenaMemoryHandedBack;
    private PropRenderSet? _arenaPropsSource, _arenaDummySource, _arenaCombinedProps;
    private VfxPlayback? _arenaVfxSource, _arenaSpellSource, _arenaCombinedVfx;

    private bool EnsureArenaDx11Scene(MeshPreviewViewModel vm)
    {
        if (vm.ArenaViewport is not { } map)
        {
            if (_committedArena is not null)
            {
                _committedArena = null;
                _dx11CommittedRevision = -1;
                _arenaCharacterMaterials.Clear();
                _arenaCharacterTextureKeys = new();
                if (_arenaCharacterGeometry >= 0) _dx11?.Renderer.ReleaseRiotMeshGeometry(_arenaCharacterGeometry);
                _arenaCharacterGeometry = -1;
            }
            return !_buildingArena;
        }
        if (_buildingArena) return false;
        if (ReferenceEquals(map, _committedArena) && _arenaMaterialsRevision == map.MaterialsRevision)
        {
            if (_dx11CommittedRevision == vm.Dx11SceneRevision) return true;
            // M872: only the champion changed. The map's ~1,600 materials and 170 textures stay where they are; building
            // them again per champion cost 9 GB of allocation and ~4 s. Swap the character's own materials, geometry and
            // textures in place instead.
            if (_dx11 is { } live) { SwapArenaCharacter(vm, live); return true; }
        }
        _buildingArena = true;
        _ = BuildArenaDx11Scene(vm, map);
        return false;
    }

    /// <summary>M872: replace the previous character's materials, geometry and pooled textures with vm.Dx11Scene's, leaving
    /// the map scene (and the particle/prop materials riding on it) untouched. Textures go through RemoveCachedTextures, which
    /// keeps any view another material still binds (M781), so a texture the map or the new character shares is never freed.</summary>
    private void SwapArenaCharacter(MeshPreviewViewModel vm, Dx11ViewportSurface surface)
    {
        var renderer = surface.Renderer;
        var old = _arenaCharacterMaterials;
        if (old.Count > 0) renderer.RemoveMaterials(m => old.Contains(m));
        _arenaCharacterMaterials = new();
        if (_arenaCharacterGeometry >= 0) renderer.ReleaseRiotMeshGeometry(_arenaCharacterGeometry);
        _arenaCharacterGeometry = -1;
        var oldKeys = _arenaCharacterTextureKeys;
        _arenaCharacterTextureKeys = new();
        if (oldKeys.Count > 0) renderer.RemoveCachedTextures(oldKeys);
        _dx11CommittedRevision = vm.Dx11SceneRevision;
        CommitArenaCharacter(vm, surface);
        // a full build has the character right after the map's materials and the particle/prop materials after it; appended at the end
        // instead, a non-depth-writing champion slice would blend after the effects and the M796 depth replay would start at another draw
        if (_arenaCharacterInsertIndex >= 0 && _arenaCharacterMaterials.Count > 0)
            renderer.MoveMaterialsTo(_arenaCharacterMaterials, _arenaCharacterInsertIndex);
        surface.HasScene = renderer.MaterialCount > 0;
        vm.ReapplyChromaPreview();
    }

    private void CommitArenaCharacter(MeshPreviewViewModel vm, Dx11ViewportSurface surface)
    {
        if (vm.Dx11Scene is not { } character) return;
        _arenaCharacterGeometry = surface.Renderer.CreateRiotMeshGeometry(character.Mesh);
        if (_arenaCharacterGeometry < 0) return;
        _arenaCharacterMaterials = Dx11CharacterScene.CommitSlices(surface.Renderer, character,
            m => m.RiotMeshGeometryId = _arenaCharacterGeometry);
        // the keys the slices bound, as the renderer's ordinal pool holds them (the scene's own dictionary is case-insensitive)
        _arenaCharacterTextureKeys = character.Slices.SelectMany(sl => sl.Textures).Select(t => t.Key).Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task BuildArenaDx11Scene(MeshPreviewViewModel vm, MainWindowViewModel map)
    {
        try
        {
            if (_dx11 is not { } surface) return;
            if (_arenaCharacterGeometry >= 0) surface.Renderer.ReleaseRiotMeshGeometry(_arenaCharacterGeometry);
            _arenaCharacterGeometry = -1;
            _arenaCharacterMaterials.Clear();
            _arenaCharacterTextureKeys = new();
            var report = await map.BuildDx11SceneAsync(surface.Renderer);
            if (_dx11Closed || !ReferenceEquals(vm.ArenaViewport, map)) return;
            // M872: the build decodes every texture of the map into the large object heap and drops them once they are on the GPU. The
            // runtime keeps that address space committed (8 GB of working set for 1.5 GB of live data), so hand it back - once per window
            // session (a later rebuild is a map edit), and not for a window that has closed.
            if (!_arenaMemoryHandedBack)
            {
                _arenaMemoryHandedBack = true;
                MainWindowViewModel.HandBackLargeObjectMemory();
            }
            _arenaMaterialsRevision = map.MaterialsRevision;
            _arenaCharacterInsertIndex = surface.Renderer.MaterialCount;
            _dx11CommittedRevision = vm.Dx11SceneRevision;
            CommitArenaCharacter(vm, surface);
            surface.HasScene = surface.Renderer.MaterialCount > 0;
            surface.SceneReport = report;
            surface.NotifySceneRebuilt();
            _committedArena = map;
            map.Dx11RebindGrassTintPair = (fromPath, fromTex, toPath, toTex) =>
                !_dx11Closed && ReferenceEquals(vm.ArenaViewport, map)
                    ? Dx11SceneBuilder.RebindGrassTintPair(surface.Renderer,
                        fromPath, fromTex.Rgba, fromTex.Width, fromTex.Height,
                        toPath, toTex.Rgba, toTex.Width, toTex.Height)
                    : 0;
            vm.LogDx11?.Invoke("Arena", report);
        }
        catch (Exception ex) { vm.ArenaStatus = ex.Message; vm.LogDx11?.Invoke("Arena", ex.ToString()); }
        finally { _buildingArena = false; }
    }

    private void ApplyArenaDx11Frame(MeshPreviewViewModel vm)
    {
        if (_dx11 is not { } surface || vm.ArenaViewport is not { } map) return;
        surface.World = Matrix4x4.Identity;
        surface.BonePalette = null;
        var bones = vm.CurrentBonePalette();
        var instances = new[] { vm.ModelWorld };
        foreach (var mat in _arenaCharacterMaterials)
        {
            mat.BonePalette = bones;
            mat.CharacterInstances = instances;
        }
        surface.ApplyGroupVisibility(map.CurrentModelSubmeshVisible);
        surface.ApplyGroupMirrored(map.CurrentModelSubmeshMirrored);
        surface.MapSun = map.CurrentSunProperties;
        surface.FogEnabled = map.ShowFog;
        surface.ScreenFog = map.CurrentPostFog;
        surface.LightmapScale = map.CurrentLightmapScale;
        surface.Lights = map.DynamicLights;
        surface.ShowDynamicLights = map.ShowDynamicLights;
        surface.DynamicLightIntensity = map.DynamicLightIntensity;
        surface.DynamicLightRadiusScale = map.DynamicLightRadiusScale;
        surface.LightFalloffSoftness = map.LightFalloffSoftness;
        surface.DynamicLightPositionScale = map.DynamicLightPositionScale;
        surface.DynamicLightScaleX = map.DynamicLightScaleX;
        surface.DynamicLightScaleZ = map.DynamicLightScaleZ;
        surface.DynamicLightOffsetX = map.DynamicLightOffsetX;
        surface.DynamicLightOffsetZ = map.DynamicLightOffsetZ;
        surface.Wireframe = map.ShowWireframe;
        surface.DebugMode = map.PreviewMode;
        map.TickGrassTransition();
        surface.GrassInterp = map.GrassInterp;
        surface.SortByPipeline = !map.ClientDepthRules;
        surface.Bloom = map.ShowBloom;
        surface.Shadows = map.ShowSunShadows;
        surface.CullBackFaces = map.CullBackfaces;
        surface.AnimateTime = map.AnimationsPlaying;
        surface.Backdrop = null;
        surface.ApplySkybox(map.CurrentSkybox);
        surface.ShaderCache = map.Dx11ShaderCache;
        // M874: a summoned unit lives in the CHAMPION's wad, which the stage viewport (Map11 only) cannot read: its textures would not be found and bind as white.
        // Those meshes are prepared by the main view model's own reader (vm.PrepareOwnPropScene); every other prop, the map's and the dummy, by the map's.
        surface.PreparePropScene = mesh => vm.IsOwnPropMesh(mesh) && vm.PrepareOwnPropScene is { } own ? own(mesh) : map.PreparePropDx11Scene(mesh);
        surface.PropLightingAt = map.PropLightingAt;
        surface.PlayPropAnimations = map.PlayPropAnimations;
        UpdateArenaPlayback(vm, map);
        surface.PropMeshes = _arenaCombinedProps;
        surface.ParticlePlayback = _arenaCombinedVfx;
    }

    private void UpdateArenaPlayback(MeshPreviewViewModel vm, MainWindowViewModel map)
    {
        if (!ReferenceEquals(_arenaPropsSource, map.CurrentPropMeshes) || !ReferenceEquals(_arenaDummySource, vm.SceneProps))
        {
            _arenaPropsSource = map.CurrentPropMeshes; _arenaDummySource = vm.SceneProps;
            _arenaCombinedProps = new PropRenderSet((map.CurrentPropMeshes?.Instances ?? Array.Empty<PropInstanceData>())
                .Concat(vm.SceneProps?.Instances ?? Array.Empty<PropInstanceData>()).ToArray());
        }
        if (!ReferenceEquals(_arenaVfxSource, map.CurrentParticlePlayback) || !ReferenceEquals(_arenaSpellSource, vm.Playback))
        {
            _arenaVfxSource = map.CurrentParticlePlayback; _arenaSpellSource = vm.Playback;
            _arenaCombinedVfx = VfxPlaybackSim.Combine(map.CurrentParticlePlayback, vm.Playback);
        }
        if (!vm.UseDx11Preview)
        {
            map.TickGrassTransition();
            PreviewViewport.Skybox = map.CurrentSkybox;
            PreviewViewport.PropMeshes = _arenaCombinedProps;
            PreviewViewport.ParticlePlayback = _arenaCombinedVfx;
            PreviewViewport.PlayPropAnimations = map.PlayPropAnimations;
            PreviewViewport.RequestNextFrameRendering();
        }
    }
}
