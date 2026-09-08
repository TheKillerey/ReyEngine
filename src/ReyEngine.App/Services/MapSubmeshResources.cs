using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Rendering;

namespace ReyEngine.App.Services;

/// <summary>
/// M665: everything a mapgeo group needs to be DRAWN, resolved once and owned by nobody.
///
/// <para>This is <c>MainWindowViewModel.BuildMapTextures</c> with its side effects taken out. That method
/// resolved the diffuse, the baked lightmap, the flow/terrain layers and the per-group render state, and
/// then wrote all of it straight into the main window's own map fields. It was therefore unusable by
/// anything that is not the map viewport - which is why the ARENA grew a second, poorer path through the
/// prop pipeline: diffuse only, one global alpha cutout, no per-material blend or two-sidedness and no
/// baked light at all. Measured on Map30, that flattened 3 blended, 2 alpha-cutout and 2 two-sided groups
/// and left 6 of 21 groups' lightmaps unbound; on Map11, 78, 137 and 72.</para>
///
/// <para>The caller supplies the texture loader, so the map viewport keeps reading through its mounts and
/// overrides while the arena reads out of the map's own WAD. Nothing here touches view-model state.</para>
/// </summary>
public sealed record MapSubmeshResources(
    TextureImage?[] Diffuse,
    TextureImage?[] Lightmaps,
    /// <summary>Slot 1: flow map, or the terrain RGB blend mask.</summary>
    TextureImage?[] FlowMasks,
    /// <summary>Slot 2: flow normal, or the terrain middle layer.</summary>
    TextureImage?[] FlowGradients,
    /// <summary>Slot 3 (emissive slot, reused inside the terrain branch).</summary>
    TextureImage?[] TerrainTops,
    /// <summary>Slot 4 (matcap slot, reused inside the terrain branch).</summary>
    TextureImage?[] TerrainExtras,
    ViewportMeshRenderer.SubmeshMaterial[] Materials,
    int LightmapGroups, int FlowGroups, int TerrainGroups, int BakedPaintGroups, int UniqueTextures,
    Vector4 TerrainWorldTransform)
{
    public bool HasLightmaps => LightmapGroups > 0;
    public bool HasFlowOrTerrain => FlowGroups + TerrainGroups > 0;

    /// <summary>
    /// Resolve every group of <paramref name="map"/>.
    /// </summary>
    /// <param name="materialToTexture">Material name → diffuse path, from MapGeoMaterialResolver.</param>
    /// <param name="profilesByName">Material name → profile, from MaterialProfiles.ForMapMaterials.</param>
    /// <param name="mapGeoPath">The .mapgeo's own path; only needed for the world-projected terrain mask.</param>
    /// <param name="loadTexture">Decodes a texture path. Called once per distinct path.</param>
    /// <param name="onFlowGroup">Optional diagnostic hook for the first few flowmap-water groups.</param>
    public static MapSubmeshResources Build(
        MapGeoAsset map,
        IReadOnlyDictionary<string, string> materialToTexture,
        IReadOnlyDictionary<string, MaterialProfile> profilesByName,
        string? mapGeoPath,
        Func<string, TextureImage?> loadTexture,
        Action<string, MaterialProfile, TextureImage?, TextureImage?>? onFlowGroup = null)
    {
        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        TextureImage? Load(string path)
        {
            if (cache.TryGetValue(path, out var hit)) return hit;
            return cache[path] = loadTexture(path);
        }

        int n = map.Groups.Count;
        var result = new TextureImage?[n];
        var lightmaps = new TextureImage?[n];
        var flowMaps = new TextureImage?[n];
        var flowNormals = new TextureImage?[n];
        var terrainTops = new TextureImage?[n];
        var terrainExtras = new TextureImage?[n];
        var submeshMats = new ViewportMeshRenderer.SubmeshMaterial[n];

        var terrainWorldTransform = MapGeoMaterialResolver.TerrainBlendWorldTransformFor(map,
            profilesByName.Where(x => x.Value.TerrainWorldProjectedMask).Select(x => x.Key));
        // Per-mesh mirrored (negative-determinant) flag, for the two-sided/mirrored render state (M34).
        var mirroredByMesh = map.Meshes.ToDictionary(mm => mm.Index, mm => mm.IsMirrored);
        int lmGroups = 0, flowGroups = 0, terrainGroups = 0, bakedPaintGroups = 0;

        for (int i = 0; i < n; i++)
        {
            var group = map.Groups[i];
            var matName = group.Material;
            string? Override(params string[] names)
            {
                foreach (string name in names)
                    if (group.TextureOverrides.TryGetValue(name, out string? value)
                        && !string.IsNullOrWhiteSpace(value)) return value;
                return null;
            }
            if (materialToTexture.TryGetValue(matName, out var path))
                result[i] = Load(path);
            if (profilesByName.TryGetValue(matName, out var prof))
            {
                submeshMats[i] = ToSubmeshMaterial(prof, terrainWorldTransform);

                // Shader 0xe25b830f: load the opaque terrain splat layers. Renderer slots are deliberately
                // reused because regular emissive/matcap effects are disabled inside the terrain branch.
                if (prof.IsTerrainBlend)
                {
                    string? bottom = Override("Bottom_Texture") ?? prof.TerrainBottomPath;
                    string? middle = Override("Middle_Texture") ?? prof.TerrainMiddlePath;
                    string? top = Override("Top_Texture") ?? prof.TerrainTopPath;
                    string? extras = Override("Extras_Texture") ?? prof.TerrainExtrasPath;
                    if (!string.IsNullOrEmpty(bottom)) result[i] = Load(bottom);
                    string? terrainMask = prof.TerrainMaskPath;
                    if (prof.TerrainWorldProjectedMask && !string.IsNullOrEmpty(mapGeoPath))
                        terrainMask = MapGeoMaterialResolver.TerrainBlendTexturePathFor(mapGeoPath);
                    if (!string.IsNullOrEmpty(terrainMask)) flowMaps[i] = Load(terrainMask);
                    if (!string.IsNullOrEmpty(middle)) flowNormals[i] = Load(middle);
                    if (!string.IsNullOrEmpty(top)) terrainTops[i] = Load(top);
                    if (!string.IsNullOrEmpty(extras)) terrainExtras[i] = Load(extras);
                    terrainGroups++;
                }

                // M44 flowmap river water: load the Flow_Map + Flowing_Normal textures into the mask/gradient
                // slots the water shader samples (slots 1/2). Falls back to a flat animated look if missing.
                if (prof.IsFlowmap)
                {
                    if (!string.IsNullOrEmpty(prof.FlowMapPath)) flowMaps[i] = Load(prof.FlowMapPath);
                    if (!string.IsNullOrEmpty(prof.FlowNormalPath)) flowNormals[i] = Load(prof.FlowNormalPath);
                    flowGroups++;
                    if (flowGroups <= 3) onFlowGroup?.Invoke(matName, prof, flowMaps[i], flowNormals[i]);
                }
            }
            else submeshMats[i] = ViewportMeshRenderer.SubmeshMaterial.Default;

            // Mapgeo v17+ can replace any authored material sampler per mesh. Legacy ports depend on
            // this to share one material per shader role while retaining every object's own texture.
            // Apply this after the material profile so the mesh override remains authoritative.
            if (Override("DiffuseTexture", "Diffuse_Texture", "_MainTex") is { } diffuseOverride)
                result[i] = Load(diffuseOverride);

            if (mirroredByMesh.TryGetValue(group.MeshIndex, out var mir) && mir)
                submeshMats[i] = submeshMats[i] with { Mirrored = true };

            // M319/M320: DefaultEnv_Flat_BakedTerrain deliberately points its material sampler at
            // black.tex. The actual atlas is a per-MESH BAKED_DIFFUSE_TEXTURE override in mapgeo.
            // Its final UV is decoded separately from raw Texcoord7 and selected by UsesBakedPaint;
            // ordinary material UV transforms continue to operate on Texcoord0.
            var bakedPaintPath = group.BakedPaintTexture;
            if (!string.IsNullOrEmpty(bakedPaintPath))
            {
                var bakedPaint = Load(bakedPaintPath);
                if (bakedPaint is not null) result[i] = bakedPaint;
                submeshMats[i] = submeshMats[i] with { UsesBakedPaint = true };
                if (bakedPaint is not null) bakedPaintGroups++;
            }

            // Baked lightmap: the group's BakedLight atlas (mesh already carries the uv7*scale+bias UVs).
            var lmPath = group.LightmapTexture;
            if (!string.IsNullOrEmpty(lmPath)) { lightmaps[i] = Load(lmPath); if (lightmaps[i] is not null) lmGroups++; }
        }

        return new MapSubmeshResources(result, lightmaps, flowMaps, flowNormals, terrainTops, terrainExtras,
            submeshMats, lmGroups, flowGroups, terrainGroups, bakedPaintGroups,
            cache.Values.Count(v => v is not null), terrainWorldTransform);
    }

    /// <summary>
    /// The renderer's per-submesh state for one material profile — moved here verbatim from
    /// MainWindowViewModel (M665) so the map viewport, the champion preview and the arena cannot drift
    /// apart. That view model still calls it, through a one-line forwarder.
    /// </summary>
    public static ViewportMeshRenderer.SubmeshMaterial ToSubmeshMaterial(MaterialProfile p,
        System.Numerics.Vector4 terrainWorldMaskTransform = default) =>
        new(p.UsesRim, p.UsesSpecular, p.UvScale, p.UvOffset, p.UvRotationDegrees,
            AlphaMode: p.RenderMode switch
            {
                MaterialRenderMode.Cutout => 1,
                MaterialRenderMode.Transparent => 2,
                MaterialRenderMode.TransparentCutout => 3,
                _ => 0,
            },
            DoubleSided: p.DoubleSided,
            Tint: p.Tint,
            TintTextured: p.TintTextured,
            AlphaCutoff: p.AlphaCutoff ?? 0.35f,
            ClampU: p.ClampU,
            ClampV: p.ClampV,
            IsFlowmap: p.IsFlowmap,
            FlowSpeed: p.FlowSpeed,
            FlowStrength: p.FlowStrength,
            FlowTile: p.FlowTile,
            ColorInside: p.ColorInside,
            ColorOutside: p.ColorOutside,
            WaterAlpha: p.WaterAlpha,
            IsTerrainBlend: p.IsTerrainBlend,
            TerrainBottomTiling: p.TerrainBottomTiling,
            TerrainMiddleTiling: p.TerrainMiddleTiling,
            TerrainTopTiling: p.TerrainTopTiling,
            TerrainExtrasTiling: p.TerrainExtrasTiling,
            TerrainWorldScale: p.TerrainWorldScale,
            TerrainMaskMultipliers: new System.Numerics.Vector3(
                p.TerrainRMaskMultiplier, p.TerrainGMaskMultiplier, p.TerrainBMaskMultiplier),
            TerrainWorldProjectedMask: p.TerrainWorldProjectedMask,
            TerrainWorldMaskTransform: terrainWorldMaskTransform,
            TerrainBlendPowers: p.TerrainBlendPowers,
            TerrainUseTop: p.TerrainUseTop,
            TerrainUseExtras: p.TerrainUseExtras,
            TerrainUseAlphaOverlay: p.TerrainUseAlphaOverlay,
            TerrainOverlayRange: p.TerrainOverlayRange,
            UsesGrassTint: p.UsesGrassTint,    // M78
            EmissiveColor: p.EmissiveColor,       // M376: additive glow, gated on the intensity below
            EmissiveIntensity: p.EmissiveIntensity,
            NoBakedLighting: p.NoBakedLighting,   // M150: shaderMacros NO_BAKED_LIGHTING
            DisableDepthFog: p.DisableDepthFog,   //           DISABLE_DEPTH_FOG
            SrcBlendFactor: p.SrcBlendFactor,
            DstBlendFactor: p.DstBlendFactor,
            IsPbrLighting: p.IsPbrShader);   // M458: Mantis submeshes run Riot's GGX BRDF for point lights
}
