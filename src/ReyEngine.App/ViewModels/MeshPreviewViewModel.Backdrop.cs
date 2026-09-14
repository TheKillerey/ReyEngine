using System;
using System.Collections.Generic;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.Meshes;
using ReyEngine.Rendering;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M727: the Character Viewer's legacy map backdrop for the Direct3D 11 renderer - built from the SAME backdrop
/// the GL viewport binds, and lit by the SAME recipe.
///
/// <para><b>Why the recipe lives here, as a static function.</b> ViewportControl resolves the backdrop's lighting
/// inline in its render loop, from its own styled properties. D3D11 needs the identical numbers, and a second
/// hand-typed copy is how two renderers drift. So the recipe is written once, as a pure function of plain
/// inputs, with every term cited to the GL line it mirrors - which is also what lets a test hold it to GL's
/// values and render the real map packs without constructing a view model.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    /// <summary>Set by the window when the D3D11 renderer could not build the backdrop pass. Only then does the
    /// M725 diffuse-only prop come back, so the map is never drawn twice and never silently absent.</summary>
    [ObservableProperty] private bool _dx11BackdropFailed;

    partial void OnDx11BackdropFailedChanged(bool value)
    {
        RebuildSceneProps();
        OnPropertyChanged(nameof(BackdropIsDiffuseOnly));
    }

    /// <summary>M729: the backdrop level's authored sun and ambient - its own terrain.inibin or sun.ini, or ReyEngine's
    /// built-in copy for a pack that ships without one (<see cref="Formats.Environment.NvrSunSettings.BuiltIn"/>).
    /// Null for a level with no authored sun. Both renderers resolve it through Services.BackdropLighting.</summary>
    [ObservableProperty] private Formats.Environment.NvrSunSettings? _backgroundSun;

    /// <summary>M729: light the backdrop with <see cref="BackgroundSun"/>. Off falls back to the reference sun.</summary>
    [ObservableProperty] private bool _backgroundUseMapSun = true;

    public bool HasBackgroundSun => BackgroundSun is not null;

    /// <summary>Where the backdrop's sun came from, for the card - a built-in copy should say that it is one.</summary>
    public string BackgroundSunSource => BackgroundSun is { } sun ? "Sun and ambient: " + sun.Source : "";

    partial void OnBackgroundSunChanged(Formats.Environment.NvrSunSettings? value)
    {
        OnPropertyChanged(nameof(HasBackgroundSun));
        OnPropertyChanged(nameof(BackgroundSunSource));
    }

    /// <summary>M729: the sun a backdrop is lit by - the one its folder shipped, else the built-in copy for its level.</summary>
    public static Formats.Environment.NvrSunSettings? BackdropSunFor(Services.MapPreviewBackground bg) =>
        bg.Sun ?? Formats.Environment.NvrSunSettings.BuiltIn(bg.MapName);

    /// <summary>M729: <see cref="Services.BackdropLighting.Defaults"/> for this map. Runs once per map, from
    /// SetBackground and BEFORE it assigns the backdrop fields, so it reads the map from <paramref name="bg"/>.</summary>
    private void ApplyBackdropLightingDefaults(Services.MapPreviewBackground bg)
    {
        var (lightsOn, vertexLight) = Services.BackdropLighting.Defaults(
            compositeModel: bg.SubmeshLightmap is not null,
            hasSun: BackdropSunFor(bg) is not null,
            lightCount: bg.Lights.Count);
        BackgroundLightsEnabled = lightsOn;
        BackgroundVertexLight = vertexLight;
    }

    private BackdropScene? _dx11Backdrop;
    private object? _dx11BackdropMeshFor, _dx11BackdropTexturesFor, _dx11BackdropMaterialsFor;

    /// <summary>
    /// The backdrop as a D3D11 scene, or null when none is loaded or an ARENA owns the backdrop - the arena's
    /// mapgeo floor already draws under D3D11 as its own prop (M665), and uploading it here too would draw it twice.
    ///
    /// <para>Cached on the mesh, the diffuse list and the material list together. M727's white-backdrop bug was a
    /// cache keyed on the mesh alone serving a build made before the textures arrived.</para>
    /// </summary>
    public BackdropScene? Dx11Backdrop
    {
        get
        {
            if (BackgroundMesh is not { } mesh || _arena is not null)
            {
                _dx11Backdrop = null;
                _dx11BackdropMeshFor = _dx11BackdropTexturesFor = _dx11BackdropMaterialsFor = null;
                return null;
            }
            var textures = BackgroundTextures;
            var materials = BackgroundMaterials;
            if (_dx11Backdrop is not null && ReferenceEquals(_dx11BackdropMeshFor, mesh)
                && ReferenceEquals(_dx11BackdropTexturesFor, textures) && ReferenceEquals(_dx11BackdropMaterialsFor, materials))
                return _dx11Backdrop;

            _dx11BackdropMeshFor = mesh;
            _dx11BackdropTexturesFor = textures;
            _dx11BackdropMaterialsFor = materials;
            return _dx11Backdrop = BuildBackdropScene(mesh, textures, BackgroundMaskTextures, BackgroundBlendTextures,
                BackgroundColor1Textures, BackgroundColor2Textures, BackgroundColor3Textures,
                BackgroundLightmapTextures, materials);
        }
    }

    /// <summary>
    /// The backdrop's per-submesh layers in GL's slot meaning. Mirrors ViewportControl's backdrop upload:
    /// slot 0 diffuse, slot 1 <c>Merge(mask, blend)</c>, slots 2/3/4 COLOR_MAP_1..3, slot 6 the composite atlas -
    /// plus the per-submesh material MapPreviewLoader derived (composite ground, alpha mode, decal clamp).
    /// </summary>
    public static BackdropScene BuildBackdropScene(MeshAsset mesh,
        IReadOnlyList<TextureImage?>? diffuse, IReadOnlyList<TextureImage?>? heightScale, IReadOnlyList<TextureImage?>? blend,
        IReadOnlyList<TextureImage?>? color1, IReadOnlyList<TextureImage?>? color2, IReadOnlyList<TextureImage?>? color3,
        IReadOnlyList<TextureImage?>? lightmap, IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>? materials)
    {
        static TextureImage? At(IReadOnlyList<TextureImage?>? list, int i) => list is not null && i < list.Count ? list[i] : null;

        var submeshes = new List<BackdropSubmesh>(mesh.SubMeshes.Count);
        for (int i = 0; i < mesh.SubMeshes.Count; i++)
        {
            var s = mesh.SubMeshes[i];
            var mat = materials is not null && i < materials.Count ? materials[i] : ViewportMeshRenderer.SubmeshMaterial.Default;
            submeshes.Add(new BackdropSubmesh(
                s.StartIndex, s.IndexCount,
                Base: At(diffuse, i),
                // GL: UploadBgLayer(Merge(BackgroundMaskTextures, BackgroundBlendTextures), 1) - the height-scale
                // map where one exists, else the four-blend BLEND_MAP. Mutually exclusive per submesh.
                Mask: At(heightScale, i) ?? At(blend, i),
                Color1: At(color1, i),
                Color2: At(color2, i),
                Color3: At(color3, i),
                Lightmap: At(lightmap, i),
                CompositeGround: mat.CompositeGround,
                AlphaMode: mat.AlphaMode,
                AlphaCutoff: mat.AlphaCutoff,
                ClampUv: mat.ClampU || mat.ClampV));
        }
        return new BackdropScene(mesh, submeshes);
    }

    /// <summary>This frame's backdrop placement and lighting, from this view model's live state.</summary>
    public BackdropFrame Dx11BackdropFrame() => ResolveBackdropFrame(
        visible: BackgroundVisible && HasBackground && _arena is null,
        world: BackdropWorld,
        lighting: Services.BackdropLighting.Resolve(
            compositeModel: BackgroundLightmapTextures is not null,
            brightness: (float)BackgroundBrightness,
            vertexLight: (float)BackgroundVertexLight,
            sun: BackgroundSun,
            useMapSun: BackgroundUseMapSun),
        lights: BackgroundLights,
        lightsEnabled: BackgroundLightsEnabled,
        lightIntensity: (float)BackgroundLightIntensity);

    /// <summary>
    /// The D3D11 frame for a resolved <see cref="Services.BackdropLighting"/>, with the placement and lights beside it.
    ///
    /// <para>M729: the lighting used to be resolved HERE, as a hand-typed mirror of ViewportControl's render loop,
    /// and the mirror was wrong exactly where GL's SetSunLighting is surprising - a zero direction there is a default
    /// sun, not no sun, and the direction it takes points toward the sun. The GL viewport now calls the same
    /// resolver, so there is one recipe and nothing left to mirror.</para>
    /// </summary>
    public static BackdropFrame ResolveBackdropFrame(bool visible, Matrix4x4 world, Services.BackdropLighting lighting,
        IReadOnlyList<PointLight>? lights, bool lightsEnabled, float lightIntensity) =>
        new(
            Visible: visible,
            World: world,
            DirectionToSun: lighting.DirectionToSun, SunColor: lighting.SunColor, SkyColor: lighting.SkyColor,
            CompositeModel: lighting.CompositeModel,
            VertexBakedScale: lighting.CompositeModel ? 0f : lighting.VertexBakedScale,
            Lights: lights,
            LightsEnabled: lightsEnabled,
            LightIntensity: lightIntensity);
}
