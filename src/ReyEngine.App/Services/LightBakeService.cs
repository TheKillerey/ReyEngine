using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Baking;
using ReyEngine.Formats.Lighting;
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
}

public sealed record LightBakeResult(int AtlasCount, long TotalBytes, bool WroteLightGrid, string OutputDescription);

/// <summary>M158: drives a bake from the app's live map + lighting + project state and writes the
/// results into the project's override store, keyed by the same WAD path hashes the game uses — so a
/// baked atlas simply replaces Riot's, no bin edit required.</summary>
public sealed class LightBakeService
{
    private readonly ReyProject _project;
    public LightBakeService(ReyProject project) => _project = project;

    /// <summary>Assemble the light model from the live viewport values, applying the renderer's clamps so
    /// a bake can never exceed what preview would show.</summary>
    public static BakeLighting BuildLighting(
        Vector3 sunDirectionTowardSun, Vector3 sunColor, Vector3 skyColor, float skyScale,
        float lightMapColorScale, IReadOnlyList<BakePointLight> lights,
        float lightIntensity, float lightRadiusScale, float lightPositionScale,
        BakeSettings settings)
        => BakeLighting.FromViewport(
            sunDirectionTowardSun, sunColor, skyColor, skyScale, lightMapColorScale, lights,
            lightIntensity, lightRadiusScale, lightPositionScale,
            Vector2.One, Vector2.Zero,
            sunShadows: settings.SunSamples > 0,
            pointLightShadows: settings.PointLightSamples > 0);

    /// <summary>Per-group baked-lighting flags from the map's material profiles. A group whose material
    /// sets NO_BAKED_LIGHTING is excluded from the bake so its texels are never written — matching the
    /// viewport, which does not sample the atlas for those meshes.</summary>
    public static IReadOnlyList<bool> BuildGroupFlags(
        MapGeoAsset map, IReadOnlyDictionary<string, MaterialProfile>? profiles)
    {
        var flags = new bool[map.Groups.Count];
        for (int i = 0; i < map.Groups.Count; i++)
        {
            var mat = map.Groups[i].Material;
            flags[i] = string.IsNullOrEmpty(mat)
                       || profiles is null
                       || !profiles.TryGetValue(mat, out var p)
                       || !p.NoBakedLighting;
        }
        return flags;
    }

    /// <summary>Run the bake. Each atlas is written to the override store as it finishes (streamed — a
    /// large map is dozens of atlases and holding them all would cost gigabytes), and the lightgrid last.</summary>
    public async Task<LightBakeResult> BakeAsync(
        LightBakeInputs inputs, BakeSettings settings,
        IProgress<BakeProgress>? progress = null, CancellationToken ct = default)
    {
        if (_project.OverridesDirectory is null)
            throw new InvalidOperationException("Save the project before baking — atlases are written into its override store.");

        long totalBytes = 0;
        int atlasCount = await LightBaker.BakeExistingLayoutAsync(
            inputs.Map, inputs.GroupLightmapEnabled, inputs.Lighting, settings, inputs.MapgeoPath,
            baked =>
            {
                // The mapgeo already samples this atlas by its own path; write the new bytes under that
                // path's hash so the built WAD serves them in place of Riot's.
                ulong hash = HashAlgorithms.WadPath(baked.OutputPath);
                ProjectWorkspace.StoreOverrideBytes(_project, hash, baked.TexBytes, ".tex");
                totalBytes += baked.TexBytes.Length;
                return Task.CompletedTask;
            },
            progress, ct).ConfigureAwait(false);

        bool wroteGrid = false;
        if (settings.BakeLightGrid)
        {
            var grid = await Task.Run(() => LightBaker.BakeLightGrid(inputs.Map, inputs.Lighting, settings, progress: progress, ct: ct), ct)
                                 .ConfigureAwait(false);
            string gridPath = settings.ResolveOutputFolder(inputs.MapgeoPath) + settings.LightGridFileName();
            ulong gridHash = HashAlgorithms.WadPath(gridPath);
            var gridBytes = grid.Write();
            ProjectWorkspace.StoreOverrideBytes(_project, gridHash, gridBytes, ".dat");
            totalBytes += gridBytes.Length;
            wroteGrid = true;
        }

        return new LightBakeResult(atlasCount, totalBytes, wroteGrid,
            $"{atlasCount} atlas(es){(wroteGrid ? " + lightgrid" : "")} → {settings.ResolveOutputFolder(inputs.MapgeoPath)}");
    }
}
