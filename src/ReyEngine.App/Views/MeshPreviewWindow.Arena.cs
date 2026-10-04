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
                if (_arenaCharacterGeometry >= 0) _dx11?.Renderer.ReleaseRiotMeshGeometry(_arenaCharacterGeometry);
                _arenaCharacterGeometry = -1;
            }
            return !_buildingArena;
        }
        if (_buildingArena) return false;
        if (ReferenceEquals(map, _committedArena) && _arenaMaterialsRevision == map.MaterialsRevision
            && _dx11CommittedRevision == vm.Dx11SceneRevision) return true;
        _buildingArena = true;
        _ = BuildArenaDx11Scene(vm, map);
        return false;
    }

    private async Task BuildArenaDx11Scene(MeshPreviewViewModel vm, MainWindowViewModel map)
    {
        try
        {
            if (_dx11 is not { } surface) return;
            if (_arenaCharacterGeometry >= 0) surface.Renderer.ReleaseRiotMeshGeometry(_arenaCharacterGeometry);
            _arenaCharacterGeometry = -1;
            _arenaCharacterMaterials.Clear();
            var report = await map.BuildDx11SceneAsync(surface.Renderer);
            if (_dx11Closed || !ReferenceEquals(vm.ArenaViewport, map)) return;
            _arenaMaterialsRevision = map.MaterialsRevision;
            _dx11CommittedRevision = vm.Dx11SceneRevision;
            if (vm.Dx11Scene is { } character)
            {
                _arenaCharacterGeometry = surface.Renderer.CreateRiotMeshGeometry(character.Mesh);
                if (_arenaCharacterGeometry >= 0)
                    _arenaCharacterMaterials = Dx11CharacterScene.CommitSlices(surface.Renderer, character,
                        m => m.RiotMeshGeometryId = _arenaCharacterGeometry);
            }
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
        surface.PreparePropScene = map.PreparePropDx11Scene;
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
