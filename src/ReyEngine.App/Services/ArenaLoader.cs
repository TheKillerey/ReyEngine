using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.Services;

/// <summary>A shipped map, loaded as an ARENA for the playable character: the map itself as the
/// viewport's BACKDROP, the navigation grid that says where a unit may walk and how high the ground is,
/// and a spawn point on it.</summary>
public sealed record ArenaScene(
    string MapKey,
    MapPreviewBackground Background,
    /// <summary>M665: the same floor as a plain prop, for the D3D11 host, which has no backdrop channel.
    /// Diffuse only - what the GL viewport drew before this milestone. Exactly one of the two is used per
    /// renderer, never both, or the floor draws twice.</summary>
    PropMesh Dx11Geometry,
    NavGrid? Nav,
    Vector3 Spawn,
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    int GroupsDrawn,
    int GroupsHiddenByLayer,
    int TexturesMissing,
    int LightmappedGroups)
{
    public bool HasNavGrid => Nav is not null;
}

/// <summary>
/// M636: turn a shipped map WAD into an arena the character window can stand a champion on.
///
/// <para>M665: the map goes in as the viewport's BACKDROP, which is a second full
/// <c>ViewportMeshRenderer</c> - same shader, same six texture slots, same per-submesh material state as
/// the map viewport - and is drawn before the champion. It used to go through the PROP pipeline, which
/// carries one diffuse per material group and nothing else, and the cost of that was measured rather than
/// guessed: on Map30 three blended, two alpha-cutout and two two-sided groups drew as flat opaque and 6 of
/// 21 groups' baked lightmaps were never bound; on Map11, 78, 137 and 72. The resolving is shared with the
/// map viewport through <see cref="MapSubmeshResources"/>, so the two cannot drift.</para>
///
/// <para>Still not the map viewport: no map shaders of Riot's own, no grass tint, and the sun comes from
/// the preview's Bright slider rather than the map's MapSunProperties.</para>
///
/// <para>Read-only throughout: the map WAD is opened from the game install and nothing is written.</para>
/// </summary>
public static class ArenaLoader
{
    /// <summary>The maps a player can choose from: every <c>Maps/Shipping/&lt;name&gt;.wad.client</c> that
    /// is not a locale WAD and not the shared Common WAD, by key (Map11, Map12, ...).</summary>
    public static IReadOnlyList<string> AvailableMaps(string? gameDirectory)
    {
        var final = GameReferenceLibrary.FindFinalDirectory(gameDirectory);
        if (final is null) return Array.Empty<string>();
        string dir = Path.Combine(final, "Maps", "Shipping");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.wad.client")
            .Select(f => Path.GetFileName(f))
            .Where(f => f.Count(c => c == '.') == 2                          // Map11.wad.client, not Map11.en_US.wad.client
                        && !f.StartsWith("Common", StringComparison.OrdinalIgnoreCase))
            .Select(f => f[..f.IndexOf('.')])
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? WadPathFor(string? gameDirectory, string mapKey)
    {
        var final = GameReferenceLibrary.FindFinalDirectory(gameDirectory);
        if (final is null) return null;
        string p = Path.Combine(final, "Maps", "Shipping", mapKey + ".wad.client");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Heavy: decodes the whole mapgeo and every diffuse it references. Run it off the UI thread.
    /// Throws when the WAD or its base mapgeo cannot be found; texture gaps are tolerated and counted.</summary>
    public static ArenaScene Load(string wadPath, IHashResolver resolver, Func<uint, string?> resolveBinName,
        Func<ulong, string?> resolveWadPath, Action<string>? log = null)
    {
        string mapKey = Path.GetFileName(wadPath);
        mapKey = mapKey[..mapKey.IndexOf('.')];
        string folder = mapKey.ToLowerInvariant();

        using var wad = WadArchive.Open(wadPath, resolver);

        // Which mapgeo: a map WAD is a family of them. Map11 ships 26 - base_srx (the Rift as played),
        // base, bloom, 10year, arcade, a22, boba_srs, trueshot, contentcapture... - so "the largest" is
        // the wrong rule: it picked trueshot (110 MB) over base_srx (91.8 MB). The playable one is
        // base_srx, then base, and only then the largest resolved file, for maps that name theirs otherwise.
        var candidates = wad.Entries
            .Where(e => e.IsResolved
                        && e.Path.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase)
                        && e.Path.Contains("/mapgeometry/" + folder + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var geo = candidates.FirstOrDefault(e => e.Path.EndsWith("/base_srx.mapgeo", StringComparison.OrdinalIgnoreCase))
                  ?? candidates.FirstOrDefault(e => e.Path.EndsWith("/base.mapgeo", StringComparison.OrdinalIgnoreCase))
                  ?? candidates.OrderByDescending(e => e.UncompressedSize).FirstOrDefault()
                  ?? throw new InvalidOperationException($"{mapKey}: no .mapgeo under data/maps/mapgeometry/{folder}/");
        log?.Invoke($"{mapKey}: geometry {Path.GetFileName(geo.Path)} ({geo.UncompressedSize / 1_048_576.0:0.0} MB), "
                    + $"{candidates.Count} variant(s) in the WAD");

        var map = MapGeoDecoder.Decode(wad.Extract(geo));

        // Materials sit beside the geometry under the same stem.
        string stem = geo.Path[..^".mapgeo".Length];
        byte[]? materialsBin = null;
        ulong binHash = HashAlgorithms.WadPath(stem + ".materials.bin");
        if (wad.TryGetEntry(binHash, out _))
        {
            try { materialsBin = wad.Extract(binHash); }
            catch (Exception ex) { log?.Invoke($"{mapKey}: materials.bin would not read ({ex.Message}); the arena draws untextured."); }
        }

        // M665: resolved exactly as the map viewport resolves it - diffuse, the baked lightmap, the
        // flow/terrain layers and the per-group render state - through the shared builder.
        var names = map.Groups.Select(g => g.Material).Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var materialToTexture = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new Dictionary<string, MaterialProfile>(StringComparer.OrdinalIgnoreCase);
        if (materialsBin is not null)
        {
            try { materialToTexture = MapGeoMaterialResolver.Resolve(materialsBin, names, resolveWadPath); }
            catch (Exception ex) { log?.Invoke($"{mapKey}: material->texture resolve failed ({ex.Message})."); }
            try { profiles = MaterialProfiles.ForMapMaterials(materialsBin, names, resolveBinName, resolveWadPath); }
            catch (Exception ex) { log?.Invoke($"{mapKey}: material profiles failed ({ex.Message})."); }
        }

        int missing = 0;
        var res = MapSubmeshResources.Build(map, materialToTexture, profiles, geo.Path, path =>
        {
            var img = TryDecode(wad, path);
            if (img is null) missing++;
            return img;
        });

        // Layer rule: a group that is on every layer (0 / 255) or on the FIRST layer draws; the others are
        // the map's variants (dragon pits, seasonal swaps) and would draw on top of each other.
        // MapVisibility.VisibleForMask is the same rule with the axis's initial mask folded in; the arena
        // has no axis to consult.
        var keep = new List<int>(map.Groups.Count);
        int hidden = 0;
        for (int i = 0; i < map.Groups.Count; i++)
        {
            var g = map.Groups[i];
            if (!(g.VisibilityFlags is 0 or 255 || (g.VisibilityFlags & 1) != 0)) { hidden++; continue; }
            if (g.IndexCount <= 0) continue;
            keep.Add(i);
        }

        // Filtered together, so submesh N of the mesh and entry N of every layer array stay the same group.
        T[] Pick<T>(T[] all) => keep.Select(i => all[i]).ToArray();
        var mesh = new MeshAsset
        {
            Positions = map.Positions,
            Normals = map.Normals,
            Uvs = map.Uvs,
            Colors = map.Colors,
            LightmapUvs = map.LightmapUvs,       // the baked-light UV set - without it nothing lights
            BakedPaintUvs = map.BakedPaintUvs,
            Indices = map.Indices,
            VertexCount = map.VertexCount,
            SubMeshes = keep.Select(i => new SubMeshInfo(map.Groups[i].Material,
                map.Groups[i].StartIndex, map.Groups[i].IndexCount, 0)).ToList(),
            BoundsMin = map.BoundsMin,
            BoundsMax = map.BoundsMax,
        };

        // The D3D11 host has no backdrop, so it still gets a prop - built from the same kept groups and
        // the same resolved diffuse, so the two renderers at least draw the same geometry and textures.
        var dx11Geometry = new PropMesh("arena|" + mapKey, map.Positions, map.Normals, map.Uvs, map.Indices,
            keep.Select(i => new PropSubmesh(map.Groups[i].StartIndex, map.Groups[i].IndexCount, res.Diffuse[i]))
                .ToList());

        var mats = Pick(res.Materials);
        var lightmaps = Pick(res.Lightmaps);
        int lit = lightmaps.Count(t => t is not null);
        var background = new MapPreviewBackground(
            MapName: mapKey,
            Mesh: mesh,
            SubmeshTextures: Pick(res.Diffuse),
            SubmeshBlend: new TextureImage?[keep.Count],   // mapgeo has no NVR four-blend layer
            SubmeshColor1: Pick(res.FlowGradients),        // slot 2: flow normal / terrain middle
            SubmeshColor2: Pick(res.TerrainTops),          // slot 3
            SubmeshColor3: Pick(res.TerrainExtras),        // slot 4
            SubmeshDoubleSided: mats.Select(m => m.DoubleSided).ToList(),
            Lights: Array.Empty<PointLight>(),
            MeshCount: keep.Count,
            MissingTextures: missing,
            SubmeshMaterials: mats,
            SubmeshMask: Pick(res.FlowMasks),              // slot 1: flow map / terrain blend mask
            SubmeshLightmap: lightmaps);

        // The navgrid, opportunistically - a map that ships none still loads as a floor at y = 0.
        NavGrid? nav = null;
        ulong navHash = HashAlgorithms.WadPath(NavGrid.PathFor(folder));
        if (wad.TryGetEntry(navHash, out _))
        {
            if (NavGrid.TryParse(wad.Extract(navHash), out nav, out string? why) && nav is not null)
                log?.Invoke($"{mapKey}: navgrid {nav.CountX}x{nav.CountZ} cells of {nav.CellSize:0}"
                            + (nav.HasHeights ? " with ground heights." : ", no ground heights."));
            else log?.Invoke($"{mapKey}: navgrid present but unreadable{(why is null ? "" : " - " + why)}.");
        }
        else log?.Invoke($"{mapKey}: no navgrid; movement is unrestricted on a flat floor.");

        var (min, max) = Bounds(map.Positions);
        var spawn = SpawnFor(nav, min, max);

        log?.Invoke($"{mapKey}: {keep.Count} group(s) drawn, {hidden} on other layers, "
                    + $"{res.UniqueTextures} texture(s), {missing} missing, {lit} with baked light"
                    + (res.TerrainGroups > 0 ? $", {res.TerrainGroups} terrain-blend" : "")
                    + (res.FlowGroups > 0 ? $", {res.FlowGroups} water" : "")
                    + $"; spawn at ({spawn.X:0}, {spawn.Y:0}, {spawn.Z:0}).");
        return new ArenaScene(mapKey, background, dx11Geometry, nav, spawn, min, max,
            keep.Count, hidden, missing, lit);
    }

    /// <summary>Near the low-X / low-Z corner - the blue fountain on Summoner's Rift and the Abyss - snapped
    /// to the nearest walkable cell when there is a grid to ask.</summary>
    private static Vector3 SpawnFor(NavGrid? nav, Vector3 min, Vector3 max)
    {
        if (nav is null)
            return new Vector3(min.X + (max.X - min.X) * 0.5f, 0f, min.Z + (max.Z - min.Z) * 0.5f);

        var guess = new Vector3(nav.Min.X + (nav.Max.X - nav.Min.X) * 0.08f, 0f,
                                nav.Min.Z + (nav.Max.Z - nav.Min.Z) * 0.08f);
        var snapped = NavGridPath.SnapToWalkable(nav, guess, maxRadiusCells: 40);
        return new Vector3(snapped.X, NavGridPath.GroundHeight(nav, snapped), snapped.Z);
    }

    private static string? DiffusePathFor(MapGeoGroup group, IReadOnlyDictionary<string, MaterialBinding> bindings)
    {
        if (!bindings.TryGetValue(group.Material, out var b)) return null;
        var slot = b.Slots.FirstOrDefault(s => s.SamplerName.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)
                                               && !string.IsNullOrWhiteSpace(s.Path))
                   ?? b.Slots.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Path));
        if (slot?.Path is not { Length: > 0 } path) return null;
        // v17+ per-mesh overrides replace the material's own sampler for that mesh.
        if (group.TextureOverrides.TryGetValue(slot.SamplerName, out var over) && !string.IsNullOrWhiteSpace(over))
            return over;
        return path;
    }

    private static TextureImage? TryDecode(WadArchive wad, string path)
    {
        try
        {
            ulong h = BinTexturePath.HashOfReference(path);
            if (!wad.TryGetEntry(h, out _)) return null;
            return TextureDecoder.Decode(wad.Extract(h));
        }
        catch { return null; }
    }

    private static (Vector3 Min, Vector3 Max) Bounds(float[] positions)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        if (min.X > max.X) return (Vector3.Zero, Vector3.Zero);
        return (min, max);
    }
}
