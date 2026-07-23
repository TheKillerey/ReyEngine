using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Environment;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.Meshes;
using ReyEngine.Rendering;

namespace ReyEngine.App.Services;

/// <summary>M88: the fully-resolved backdrop for the character preview — an old-format (NVR) map room
/// as render-ready geometry + per-submesh diffuse + its dynamic point lights, all shifted so the chosen
/// spawn anchor sits at the world origin (where the previewed character stands).</summary>
public sealed record MapPreviewBackground(
    string MapName,
    MeshAsset Mesh,
    IReadOnlyList<TextureImage?> SubmeshTextures,
    IReadOnlyList<TextureImage?> SubmeshBlend,     // M89: four-blend ground layers
    IReadOnlyList<TextureImage?> SubmeshColor1,
    IReadOnlyList<TextureImage?> SubmeshColor2,
    IReadOnlyList<TextureImage?> SubmeshColor3,
    IReadOnlyList<bool> SubmeshDoubleSided,
    IReadOnlyList<PointLight> Lights,
    int MeshCount,
    int MissingTextures,
    // M142: per-submesh preview material for the SUBJECT (legacy-map) viewer — double-sided from the NVR,
    // alpha cutout for foliage/decals, CompositeGround on height-blend ground submeshes.
    IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>? SubmeshMaterials = null,
    // M142.2: mask slot = terrainHeightScale (per-layer height scales, planar over the level rect) and
    // lightmap slot = compositeColorMap (baked light/colour, modulates the height-blended detail 2X).
    // Both only on the flagged ground submeshes; null when the map has no height-blend ground.
    IReadOnlyList<TextureImage?>? SubmeshMask = null,
    IReadOnlyList<TextureImage?>? SubmeshLightmap = null);

/// <summary>M88: loads a legacy League <c>LEVELS/&lt;Map&gt;</c> folder as a character-preview backdrop.
/// Reads <c>Scene/room.nvr</c>, resolves diffuse from <c>Scene/Textures/</c>, loads <c>Light.dat</c>, and
/// anchors the scene to a team spawn so the character stands on the ground in-frame. All file access is
/// read-only; nothing here writes to the game install.</summary>
public static class MapPreviewLoader
{
    /// <summary>True when <paramref name="mapFolder"/> looks like a legacy map (has Scene/room.nvr).</summary>
    public static bool IsNvrMapFolder(string? mapFolder) =>
        !string.IsNullOrEmpty(mapFolder) && File.Exists(Path.Combine(mapFolder, "Scene", "room.nvr"));

    /// <summary>Load and fully resolve the backdrop. Runs on a background thread (heavy: ~1M verts +
    /// ~130 DDS decodes). Throws on a missing/corrupt room.nvr; texture/light gaps are tolerated.</summary>
    public static MapPreviewBackground Load(string mapFolder)
    {
        string sceneDir = Path.Combine(mapFolder, "Scene");
        string nvrPath = Path.Combine(sceneDir, "room.nvr");
        var nvr = NvrEnvironment.Load(File.ReadAllBytes(nvrPath));

        // Anchor: prefer a team spawn's CentralPoint; else the ground under the map's XZ center.
        Vector3 anchor = ReadSpawnAnchor(sceneDir) ?? GroundCenter(nvr.Mesh);

        var src = nvr.Mesh;

        // M142: Map10 height-blended ground. Its ground materials use a null_black placeholder BLEND_MAP
        // (Riot height-blends the 4 tile layers by their alpha rather than by an authored RGB mask), and
        // the game bakes that per-pixel blend into ONE ground atlas (compositeColorMap.dds). The atlas is
        // a PLANAR WORLD-SPACE bake over the level's footprint — the __LevelSize_.SCB quad ([0..15398]² on
        // Map10; verified by splatting the ground verts onto the atlas). Texcoord7 is the four-blend MASK
        // UV and does NOT map onto the composite, so write planar composite UVs (unshifted world XZ over
        // the level rect) into the flagged ground vertices' 2nd UV set, which the renderer samples.
        bool[]? ground = null;             // per-submesh: height-blend ground flag
        float[]? lmUvOverride = null;
        TextureImage? heightScale = null;  // M142.2: per-layer height-scale map (same planar rect)
        var composite = LoadComposite(mapFolder, sceneDir);
        if (composite is not null && ReadLevelRect(sceneDir) is { } rect)
        {
            var flags = new bool[src.SubMeshes.Count];
            bool anyGround = false;
            for (int i = 0; i < flags.Length; i++)
                anyGround |= flags[i] = IsHeightBlendGround(nvr.SubmeshBlendTextures[i]);
            if (anyGround)
            {
                ground = flags;
                // M142.2: terrainHeightScale.dds — the painted per-layer height scales, planar over the
                // SAME level rect (verified: ground-vs-paint coverage is best under the LevelSize mapping).
                heightScale = LoadHeightScale(mapFolder);
                lmUvOverride = src.LightmapUvs is { } lm0 ? (float[])lm0.Clone() : new float[src.VertexCount * 2];
                for (int i = 0; i < flags.Length; i++)
                {
                    if (!flags[i]) continue;
                    var s = src.SubMeshes[i];
                    int end = s.StartIndex + s.IndexCount;
                    for (int k = s.StartIndex; k < end; k++)
                    {
                        int v = (int)src.Indices[k];
                        lmUvOverride[2 * v] = (src.Positions[3 * v] - rect.MinX) / rect.SizeX;
                        lmUvOverride[2 * v + 1] = (src.Positions[3 * v + 2] - rect.MinZ) / rect.SizeZ;
                    }
                }
            }
        }

        // Shift geometry so the anchor is at the origin (character stands here).
        var pos = (float[])src.Positions.Clone();
        for (int i = 0; i + 2 < pos.Length; i += 3)
        {
            pos[i] -= anchor.X; pos[i + 1] -= anchor.Y; pos[i + 2] -= anchor.Z;
        }
        var shifted = new MeshAsset
        {
            Positions = pos,
            Normals = src.Normals,
            Uvs = src.Uvs,
            Colors = src.Colors,             // M89: baked ground shading — dropping this killed the Ground slider
            // M89: four-blend mask UV — dropping this killed the mask blend. M142: on height-blend maps the
            // flagged ground vertices instead carry planar composite UVs (world XZ over the level rect).
            LightmapUvs = lmUvOverride ?? src.LightmapUvs,
            Indices = src.Indices,
            SubMeshes = src.SubMeshes,
            VertexCount = src.VertexCount,
            BoundsMin = src.BoundsMin - anchor,
            BoundsMax = src.BoundsMax - anchor,
        };

        // Resolve textures (cached per file name; missing → null fallback).
        var texIndex = BuildTextureIndex(sceneDir);
        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        int missing = 0;
        TextureImage? Resolve(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (cache.TryGetValue(name, out var img)) return img;
            img = null;
            if (texIndex.TryGetValue(name, out var full) || texIndex.TryGetValue(Path.GetFileName(name), out full))
            {
                try { img = TextureDecoder.Decode(File.ReadAllBytes(full)); } catch { img = null; }
            }
            cache[name] = img;
            return img;
        }
        TextureImage?[] ResolveAll(IReadOnlyList<string?> names)
        {
            var arr = new TextureImage?[names.Count];
            for (int i = 0; i < names.Count; i++) arr[i] = Resolve(names[i]);
            return arr;
        }

        var subTex = ResolveAll(nvr.SubmeshDiffuseTextures);
        var subBlend = ResolveAll(nvr.SubmeshBlendTextures);
        var subColor1 = ResolveAll(nvr.SubmeshColor1Textures);
        var subColor2 = ResolveAll(nvr.SubmeshColor2Textures);
        var subColor3 = ResolveAll(nvr.SubmeshColor3Textures);
        for (int i = 0; i < subTex.Length; i++)
            if (nvr.SubmeshDiffuseTextures[i] is null || subTex[i] is null) missing++;

        // M142.3: per-submesh SUBJECT materials — double-sided from the NVR, alpha CUTOUT wherever the
        // diffuse has fully transparent texels (foliage, webs, grass tufts, ground decals — NVR draws
        // them cut out; opaque they read as solid boxes), CompositeGround for the height-blend ground.
        var alphaCache = new Dictionary<TextureImage, bool>(ReferenceEqualityComparer.Instance);
        bool HasCutoutAlpha(TextureImage? t)
        {
            if (t is null) return false;
            if (alphaCache.TryGetValue(t, out bool cut)) return cut;
            long clear = 0, total = 0;
            var px = t.Rgba;
            for (int p = 3; p < px.Length; p += 64)   // every 16th pixel — plenty for a yes/no
            {
                total++;
                if (px[p] < 16) clear++;
            }
            alphaCache[t] = cut = total > 0 && clear * 100 > total;   // >1% fully transparent texels
            return cut;
        }
        var mats = new ViewportMeshRenderer.SubmeshMaterial[subTex.Length];
        TextureImage?[]? subMask = null, subLightmap = null;
        bool detail = ground is not null && heightScale is not null;   // M142.2: real height blend possible
        if (detail) { subMask = new TextureImage?[subTex.Length]; subLightmap = new TextureImage?[subTex.Length]; }
        for (int i = 0; i < mats.Length; i++)
        {
            bool g = ground is not null && ground[i];
            // M142.6: decals (decalVersion3 / decal_*.dds) project their texture ONCE but their UVs run
            // outside [0,1]; with GL_REPEAT that wraps into hard tile seams (mud path, tower/step decals).
            // Clamp their UVs so the out-of-range border shows the (transparent) edge texel — cut out by the
            // alpha pass — and the decal paints once. Ground/rubble map within [0,1] and are left tiling.
            bool decal = !g &&
                ((nvr.SubmeshDiffuseTextures[i]?.StartsWith("decal", StringComparison.OrdinalIgnoreCase) ?? false)
                 || (src.SubMeshes[i].Material?.Contains("decal", StringComparison.OrdinalIgnoreCase) ?? false));
            mats[i] = ViewportMeshRenderer.SubmeshMaterial.Default with
            {
                CompositeGround = g,
                DoubleSided = nvr.SubmeshDoubleSided[i],
                AlphaMode = !g && (decal || HasCutoutAlpha(subTex[i])) ? 1 : 0,
                AlphaCutoff = 0.25f,
                ClampU = decal,
                ClampV = decal,
            };
            if (!g) continue;
            if (detail) { subMask![i] = heightScale; subLightmap![i] = composite; }
            else subTex[i] = composite;   // no height-scale map: show the baked composite directly
        }

        // Lights: legacy Light.dat, shifted by the same anchor.
        var lights = Array.Empty<PointLight>() as IReadOnlyList<PointLight>;
        string lightPath = Path.Combine(mapFolder, "Light.dat");
        if (File.Exists(lightPath))
        {
            try
            {
                lights = LightDatFile.Parse(File.ReadAllBytes(lightPath))
                    .Select(l => l with { Position = l.Position - anchor }).ToList();
            }
            catch { /* lights are optional */ }
        }

        return new MapPreviewBackground(
            new DirectoryInfo(mapFolder).Name, shifted, subTex, subBlend, subColor1, subColor2, subColor3,
            nvr.SubmeshDoubleSided, lights, nvr.MeshCount, missing, mats, subMask, subLightmap);
    }

    /// <summary>M142: a submesh is height-blend ground when its four-blend BLEND_MAP is the null_black
    /// placeholder — Riot's marker that the ground is height-blended (by tile alpha) rather than
    /// mask-blended by an authored RGB texture (Map8/Map1 use real masks and are left alone).</summary>
    private static bool IsHeightBlendGround(string? blendName) =>
        blendName is not null && blendName.Contains("null_black", StringComparison.OrdinalIgnoreCase);

    /// <summary>M142: the world-space rect the composite atlas covers — the level's __LevelSize_.SCB
    /// marker quad (a Riot-standard flat square under the map). Null when absent/degenerate, which
    /// disables the composite ground entirely (a wrong rect would look worse than the base tiles).</summary>
    private static (float MinX, float MinZ, float SizeX, float SizeZ)? ReadLevelRect(string sceneDir)
    {
        string p = Path.Combine(sceneDir, "__LevelSize_.SCB");
        if (!File.Exists(p)) return null;
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(p), writable: false);
            var m = LeagueToolkit.Core.Mesh.StaticMesh.ReadBinary(ms);
            if (m.Vertices.Count == 0) return null;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var v in m.Vertices)
            {
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Z < minZ) minZ = v.Z;
                if (v.Z > maxZ) maxZ = v.Z;
            }
            return maxX - minX > 1f && maxZ - minZ > 1f ? (minX, minZ, maxX - minX, maxZ - minZ) : null;
        }
        catch { return null; }
    }

    /// <summary>M142.2: the painted per-layer height-scale map (terrainHeightScale.dds at the map root) —
    /// R/G/B/A scale layers 0..3, planar over the SAME level rect as the composite. Null when absent.</summary>
    private static TextureImage? LoadHeightScale(string mapFolder)
    {
        string p = Path.Combine(mapFolder, "terrainHeightScale.dds");
        if (!File.Exists(p)) return null;
        try { return TextureDecoder.Decode(File.ReadAllBytes(p)); } catch { return null; }
    }

    /// <summary>M142: load the map's baked ground atlas (compositeColorMap.dds) if it ships with the map;
    /// checked at the map root and under Scene/ (and Scene/Textures/). Null when absent or undecodable.</summary>
    private static TextureImage? LoadComposite(string mapFolder, string sceneDir)
    {
        foreach (var p in new[]
        {
            Path.Combine(mapFolder, "compositeColorMap.dds"),
            Path.Combine(sceneDir, "compositeColorMap.dds"),
            Path.Combine(sceneDir, "Textures", "compositeColorMap.dds"),
        })
        {
            if (!File.Exists(p)) continue;
            try { return TextureDecoder.Decode(File.ReadAllBytes(p)); } catch { return null; }
        }
        return null;
    }

    /// <summary>Case-insensitive file-name → full-path index of Scene/Textures (and the Scene root).</summary>
    private static Dictionary<string, string> BuildTextureIndex(string sceneDir)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { Path.Combine(sceneDir, "Textures"), sceneDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
                index.TryAdd(Path.GetFileName(f), f);
        }
        return index;
    }

    /// <summary>Parse a team-spawn .sco's <c>CentralPoint</c> as the character stand point.</summary>
    private static Vector3? ReadSpawnAnchor(string sceneDir)
    {
        foreach (var name in new[] { "__Spawn_T1.sco", "__Spawn_T2.sco" })
        {
            string p = Path.Combine(sceneDir, name);
            if (!File.Exists(p)) continue;
            try
            {
                foreach (var line in File.ReadLines(p))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("CentralPoint", StringComparison.OrdinalIgnoreCase)) continue;
                    var nums = t[(t.IndexOf('=') + 1)..]
                        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (nums.Length >= 3
                        && float.TryParse(nums[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                        && float.TryParse(nums[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                        && float.TryParse(nums[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                        return new Vector3(x, y, z);
                }
            }
            catch { /* fall through to the next candidate */ }
        }
        return null;
    }

    /// <summary>Fallback anchor: the map's XZ center at the lowest vertex found near that center (ground).</summary>
    private static Vector3 GroundCenter(MeshAsset mesh)
    {
        var c = mesh.Center;
        float cx = c.X, cz = c.Z;
        float radiusSq = 600f * 600f;
        float groundY = float.MaxValue;
        var p = mesh.Positions;
        for (int i = 0; i + 2 < p.Length; i += 3)
        {
            float dx = p[i] - cx, dz = p[i + 2] - cz;
            if (dx * dx + dz * dz <= radiusSq && p[i + 1] < groundY) groundY = p[i + 1];
        }
        if (groundY == float.MaxValue) groundY = mesh.BoundsMin.Y;
        return new Vector3(cx, groundY, cz);
    }
}
