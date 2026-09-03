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
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.Services;

/// <summary>A shipped map, loaded as an ARENA for the playable character: its geometry as one prop, the
/// navigation grid that says where a unit may walk and how high the ground is, and a spawn point on it.</summary>
public sealed record ArenaScene(
    string MapKey,
    PropMesh Geometry,
    NavGrid? Nav,
    Vector3 Spawn,
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    int GroupsDrawn,
    int GroupsHiddenByLayer,
    int TexturesMissing)
{
    public bool HasNavGrid => Nav is not null;
}

/// <summary>
/// M636: turn a shipped map WAD into an arena the character window can stand a champion on.
///
/// <para>The map goes in through the PROP pipeline - one <see cref="PropMesh"/> carrying the whole mapgeo
/// with one submesh per material group and that group's diffuse - because that is the one path both
/// renderers already draw a textured, depth-tested mesh through beside the champion (M628 put the practice
/// dummy on it). It is honest about what that costs: no lightmaps, no map shaders, no water, no bushes
/// swaying. The main viewport still renders a map properly; this is a floor to play on, and it says so in
/// its name.</para>
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

        // The base mapgeo: Riot names it base_srx / base / <map>_base. Take the largest resolved .mapgeo
        // under the map's own mapgeometry folder rather than guessing a name, and say which one it was.
        var geo = wad.Entries
            .Where(e => e.IsResolved
                        && e.Path.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase)
                        && e.Path.Contains("/mapgeometry/" + folder + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.UncompressedSize)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"{mapKey}: no .mapgeo under data/maps/mapgeometry/{folder}/");
        log?.Invoke($"{mapKey}: geometry {Path.GetFileName(geo.Path)} ({geo.UncompressedSize / 1_048_576.0:0.0} MB)");

        var map = MapGeoDecoder.Decode(wad.Extract(geo));

        // Materials sit beside the geometry under the same stem.
        string stem = geo.Path[..^".mapgeo".Length];
        MaterialDocument? materials = null;
        ulong binHash = HashAlgorithms.WadPath(stem + ".materials.bin");
        if (wad.TryGetEntry(binHash, out _))
        {
            try { materials = MaterialDocument.Parse(wad.Extract(binHash), resolveBinName, resolveWadPath); }
            catch (Exception ex) { log?.Invoke($"{mapKey}: materials.bin would not parse ({ex.Message}); the arena draws untextured."); }
        }
        var bindings = materials?.Materials.ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase)
                       ?? new Dictionary<string, MaterialBinding>(StringComparer.OrdinalIgnoreCase);

        // One submesh per visible material group, with that group's diffuse. Layer rule: a group that is
        // on every layer (0 / 255) or on the FIRST layer draws; the others are the map's variants (dragon
        // pits, seasonal swaps) and would draw on top of each other. MapVisibility.VisibleForMask is the
        // same rule with the axis's initial mask folded in; the arena has no axis to consult.
        var textures = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        var submeshes = new List<PropSubmesh>(map.Groups.Count);
        int hidden = 0, missing = 0;
        foreach (var g in map.Groups)
        {
            if (!(g.VisibilityFlags is 0 or 255 || (g.VisibilityFlags & 1) != 0)) { hidden++; continue; }
            if (g.IndexCount <= 0) continue;

            TextureImage? diffuse = null;
            string? path = DiffusePathFor(g, bindings);
            if (path is not null)
            {
                if (!textures.TryGetValue(path, out diffuse))
                {
                    diffuse = TryDecode(wad, path);
                    textures[path] = diffuse;
                    if (diffuse is null) missing++;
                }
            }
            submeshes.Add(new PropSubmesh(g.StartIndex, g.IndexCount, diffuse));
        }

        var geometry = new PropMesh("arena|" + mapKey, map.Positions, map.Normals, map.Uvs, map.Indices, submeshes);

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

        log?.Invoke($"{mapKey}: {submeshes.Count} group(s) drawn, {hidden} on other layers, "
                    + $"{textures.Count} texture(s), {missing} missing; spawn at ({spawn.X:0}, {spawn.Y:0}, {spawn.Z:0}).");
        return new ArenaScene(mapKey, geometry, nav, spawn, min, max, submeshes.Count, hidden, missing);
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
