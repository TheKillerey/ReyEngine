using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ReyEngine.Formats.Baking;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.Services;

/// <summary>Everything a bake needs, pulled from the live editor state so the baked result matches
/// exactly what the viewport is showing.</summary>
public sealed class LightBakeInputs
{
    public required MapGeoAsset Map { get; init; }
    public required string MapgeoPath { get; init; }
    public required BakeLighting Lighting { get; init; }
    /// <summary>Per-group (index-aligned with Map.Groups) baked-lighting flag; false = NO_BAKED_LIGHTING.</summary>
    public IReadOnlyList<bool>? GroupLightmapEnabled { get; init; }
    /// <summary>Per-group: may this group cast shadows? False for alpha-card foliage, which is solid
    /// geometry in the BVH and would otherwise roof the map over.</summary>
    public IReadOnlyList<bool>? GroupOccluderEnabled { get; init; }
}

public sealed record LightBakeResult(
    int AtlasCount, int ReferencedAtlasCount, int SkippedAtlasCount,
    long TotalBytes, bool WroteLightGrid, string OutputDescription);

/// <summary>M158: drives a bake from the app's live map + lighting state. WHERE each baked file lands is
/// the host's decision — passed in as <c>writeAsset</c> — so a folder project gets the atlas at its real
/// path (…/Map12/assets/maps/lightmaps/…/0.tex, which the packer hashes straight back to the chunk the
/// game reads) while a single-WAD project falls back to the hashed override store. The service just
/// bakes and hands each finished file to that writer.</summary>
public sealed class LightBakeService
{
    /// <summary>Writes one baked file. (assetPath, bytes, extension) → the on-disk path written.</summary>
    private readonly Func<string, byte[], string, string> _writeAsset;

    public LightBakeService(Func<string, byte[], string, string> writeAsset) => _writeAsset = writeAsset;

    /// <summary>Assemble the light model from the live viewport values, applying the renderer's clamps so
    /// a bake can never exceed what preview would show.</summary>
    public static BakeLighting BuildLighting(
        Vector3 sunDirectionTowardSun, Vector3 sunColor, Vector3 skyColor, float skyScale,
        float lightMapColorScale, IReadOnlyList<BakePointLight> lights,
        float lightIntensity, float lightRadiusScale, float lightPositionScale,
        Vector2 lightPositionScaleXZ, Vector2 lightPositionOffset,
        BakeSettings settings)
        => BakeLighting.FromViewport(
            sunDirectionTowardSun, sunColor, skyColor, skyScale, lightMapColorScale, lights,
            lightIntensity, lightRadiusScale, lightPositionScale,
            // M280: these two were hardcoded to identity while the master spread above was passed - so
            // the fit panel's Scale X/Z and Offset X/Z moved the preview and did nothing to the bake.
            // ResolvePosition had implemented the full transform all along; nothing ever fed it.
            lightPositionScaleXZ, lightPositionOffset,
            sunShadows: settings.SunSamples > 0,
            pointLightShadows: settings.PointLightSamples > 0,
            falloffSoftness: settings.FalloffSoftness);

    /// <summary>Per-group baked-lighting flags. A group is excluded from the bake when:
    ///  - its material sets NO_BAKED_LIGHTING — so bake coverage matches the viewport, which does not
    ///    sample the atlas for those meshes;
    ///  - its material is VertexDeform (grass, bushes, foliage) — these sway at RUNTIME, so light baked
    ///    against their rest pose would slide off the geometry as it animates;
    ///  - M163: its mesh belongs to a render region (v18 renderRegionHash != 0) — region geometry is
    ///    swapped in and out per game mode, so a single baked atlas cannot be correct for it.</summary>
    /// <summary>Per-group shadow-casting flags. Only alpha-card foliage is excluded: it is modelled as
    /// solid two-sided triangles with no alpha test, so leaving it in makes every bush an opaque wall.
    /// NO_BAKED_LIGHTING and render-region geometry DO occlude — they are real walls, they just don't
    /// receive a lightmap themselves.</summary>
    public static IReadOnlyList<bool> BuildOccluderFlags(
        MapGeoAsset map, IReadOnlyDictionary<string, MaterialProfile>? profiles)
    {
        var flags = new bool[map.Groups.Count];
        for (int i = 0; i < map.Groups.Count; i++)
        {
            var mat = map.Groups[i].Material;
            flags[i] = string.IsNullOrEmpty(mat) || profiles is null
                       || !profiles.TryGetValue(mat, out var p) || !p.IsVertexDeform;
        }
        return flags;
    }

    public static IReadOnlyList<bool> BuildGroupFlags(
        MapGeoAsset map, IReadOnlyDictionary<string, MaterialProfile>? profiles,
        bool skipVertexDeform = true, bool skipRenderRegions = true)
    {
        var regionOf = map.Meshes.ToDictionary(m => m.Index, m => m.RegionHash);
        var flags = new bool[map.Groups.Count];
        for (int i = 0; i < map.Groups.Count; i++)
        {
            var g = map.Groups[i];
            bool ok = true;
            if (!string.IsNullOrEmpty(g.Material) && profiles is not null
                && profiles.TryGetValue(g.Material, out var p))
            {
                if (p.NoBakedLighting) ok = false;
                if (skipVertexDeform && p.IsVertexDeform) ok = false;
            }
            if (ok && skipRenderRegions && regionOf.TryGetValue(g.MeshIndex, out var region) && region != 0)
                ok = false;
            flags[i] = ok;
        }
        return flags;
    }

    /// <summary>Run the bake. Each atlas is handed to the writer as it finishes (streamed — a large map
    /// is dozens of atlases and holding them all would cost gigabytes), and the lightgrid last.</summary>
    public async Task<LightBakeResult> BakeAsync(
        LightBakeInputs inputs, BakeSettings settings,
        IProgress<BakeProgress>? progress = null, CancellationToken ct = default)
    {
        long totalBytes = 0;
        var atlasSummary = await LightBaker.BakeExistingLayoutAsync(
            inputs.Map, inputs.GroupLightmapEnabled, inputs.GroupOccluderEnabled,
            inputs.Lighting, settings, inputs.MapgeoPath,
            baked =>
            {
                _writeAsset(baked.OutputPath, baked.TexBytes, ".tex");
                totalBytes += baked.TexBytes.Length;
                return Task.CompletedTask;
            },
            progress, ct).ConfigureAwait(false);

        bool wroteGrid = false;
        if (settings.BakeLightGrid)
        {
            var grid = await Task.Run(() => LightBaker.BakeLightGrid(inputs.Map, inputs.Lighting, settings,
                                          inputs.GroupOccluderEnabled, progress: progress, ct: ct), ct)
                                 .ConfigureAwait(false);
            string gridPath = settings.ResolveOutputFolder(inputs.MapgeoPath) + settings.LightGridFileName();
            var gridBytes = grid.Write();
            _writeAsset(gridPath, gridBytes, ".dat");
            totalBytes += gridBytes.Length;
            wroteGrid = true;
        }

        string atlasDescription = atlasSummary.SkippedAtlases == 0
            ? $"{atlasSummary.BakedAtlases} atlas(es)"
            : $"{atlasSummary.BakedAtlases} of {atlasSummary.ReferencedAtlases} atlas(es); {atlasSummary.SkippedAtlases} skipped";
        return new LightBakeResult(
            atlasSummary.BakedAtlases, atlasSummary.ReferencedAtlases, atlasSummary.SkippedAtlases,
            totalBytes, wroteGrid,
            $"{atlasDescription}{(wroteGrid ? " + lightgrid" : "")} → {settings.ResolveOutputFolder(inputs.MapgeoPath)}");
    }
}
