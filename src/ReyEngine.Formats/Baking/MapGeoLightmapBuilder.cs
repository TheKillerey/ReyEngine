using System.Buffers.Binary;
using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Baking;

/// <summary>What a lightmap-layout build produced.</summary>
public sealed class LightmapLayoutResult
{
    public int AtlasCount { get; init; }
    public int MeshesLaidOut { get; init; }
    public int GeometriesUnwrapped { get; init; }
    public int MeshesSkipped { get; init; }
    /// <summary>M163: deliberately left out (VertexDeform foliage, render-region geometry).</summary>
    public int MeshesExcluded { get; init; }
    public List<string> Warnings { get; } = new();
}

/// <summary>M147b: gives a mapgeo that has NO lightmap layout a complete one — UV2 channel, packed
/// atlas regions, and the per-mesh BakedLight reference — writing it back through
/// <see cref="MapGeoBinary"/>.
///
/// The unit of work is a unique GEOMETRY, not a mesh: instances share vertex and index buffers
/// wholesale (measured on Map11: up to 35 meshes per vertex buffer, 49 per index buffer, all using the
/// identical [0..N) index range). So each geometry is unwrapped ONCE and its rebuilt buffers are shared
/// by every instance; instances differ only in the atlas region they are assigned, which is exactly what
/// BakedLight Scale/Bias expresses.
///
/// Buffers are REBUILT rather than edited in place. A chart seam splits vertices, so vertex count
/// changes and every parallel buffer plus the index buffer must be rewritten together; producing fresh
/// buffers and repointing the meshes avoids any chance of corrupting geometry still referenced
/// elsewhere.</summary>
public static class MapGeoLightmapBuilder
{
    public sealed class Settings
    {
        /// <summary>Atlas edge length in texels.</summary>
        public int AtlasResolution { get; set; } = 2048;
        /// <summary>Texels per world unit — how much atlas each mesh earns for its size.</summary>
        public float TexelDensity { get; set; } = 0.08f;
        /// <summary>Gutter between mesh regions, in texels.</summary>
        public int Padding { get; set; } = 4;
        /// <summary>Smallest / largest region a single mesh may occupy, in texels.</summary>
        public int MinRegion { get; set; } = 8;
        public int MaxRegion { get; set; } = 512;
        public float SmoothingAngleDegrees { get; set; } = 40f;
        /// <summary>M163: material names to leave out of the layout entirely (VertexDeform grass/bushes,
        /// NO_BAKED_LIGHTING surfaces). They would consume atlas space that nothing ever samples.</summary>
        public HashSet<string> ExcludeMaterials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>M163: skip meshes assigned to a render region (v18 renderRegionHash != 0) — that
        /// geometry is swapped per game mode, so one baked atlas cannot be right for it.</summary>
        public bool SkipRenderRegionMeshes { get; set; } = true;
        /// <summary>Atlas path template; {0} is the atlas index.</summary>
        public string AtlasPathFormat { get; set; } = "ASSETS/Maps/Lightmaps/Maps/MapGeometry/Custom/{0}.tex";
    }

    /// <summary>Build and apply a lightmap layout. The map is modified in place.</summary>
    public static LightmapLayoutResult Build(MapGeoBinary map, Settings settings)
    {
        var warnings = new List<string>();
        int skipped = 0, excluded = 0;

        // ---- 1. group meshes by the geometry they share ----
        var groups = new Dictionary<string, List<MapGeoBinary.Mesh>>();
        foreach (var mesh in map.Meshes)
        {
            if (map.MeshHasLightmapUv(mesh)) continue;          // already has a UV2 channel
            if (mesh.VertexCount <= 0 || mesh.IndexCount <= 0) continue;
            if (settings.SkipRenderRegionMeshes && mesh.HasRegionHash && mesh.RegionHash != 0) { excluded++; continue; }
            if (settings.ExcludeMaterials.Count > 0
                && mesh.Submeshes.Count > 0
                && mesh.Submeshes.All(sm => settings.ExcludeMaterials.Contains(sm.Material))) { excluded++; continue; }
            string key = $"{mesh.IndexBufferId}|{string.Join(',', mesh.VertexBufferIds)}|{mesh.VertexDeclarationBase}";
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add(mesh);
        }

        // ---- 2. unwrap each unique geometry once, rebuilding its buffers ----
        var laidOut = new List<(MapGeoBinary.Mesh Mesh, float WorldExtent)>();
        foreach (var (_, instances) in groups)
        {
            var proto = instances[0];
            if (!TryReadGeometry(map, proto, out var positions, out var indices, out string why))
            {
                warnings.Add($"skipped {instances.Count} mesh(es): {why}");
                skipped += instances.Count;
                continue;
            }

            var unwrap = LightmapUnwrapper.Unwrap(positions, indices, settings.SmoothingAngleDegrees);
            if (unwrap.VertexRemap.Length > 65535)
            {
                warnings.Add($"skipped {instances.Count} mesh(es): {unwrap.VertexRemap.Length} vertices after seam splits exceeds the 16-bit index limit");
                skipped += instances.Count;
                continue;
            }
            if (unwrap.FoldedTriangles > 0)
                warnings.Add($"{unwrap.FoldedTriangles} folded triangle(s) in a geometry with {indices.Length / 3} tris — UVs overlap there");

            RebuildGeometry(map, instances, unwrap, positions.Length / 3);

            foreach (var mesh in instances)
            {
                var size = mesh.BoundsMax - mesh.BoundsMin;
                laidOut.Add((mesh, MathF.Max(size.Length(), 1f)));
            }
        }
        int geometries = groups.Count;

        // ---- 3. pack every mesh into atlas regions ----
        int atlasCount = PackIntoAtlases(laidOut, settings, out var placement);

        // ---- 4. assign each mesh its atlas + scale/bias ----
        foreach (var (mesh, atlasIndex, origin, size) in placement)
        {
            float inv = 1f / settings.AtlasResolution;
            map.SetBakedLight(mesh,
                string.Format(settings.AtlasPathFormat, atlasIndex),
                new Vector2(size.X * inv, size.Y * inv),
                new Vector2(origin.X * inv, origin.Y * inv));
        }

        // Rebuilding produced fresh buffers and orphaned the originals; drop them or the file doubles.
        map.Compact();

        var result = new LightmapLayoutResult
        {
            AtlasCount = atlasCount,
            MeshesLaidOut = placement.Count,
            GeometriesUnwrapped = geometries,
            MeshesSkipped = skipped,
            MeshesExcluded = excluded,
        };
        result.Warnings.AddRange(warnings);
        return result;
    }

    /// <summary>Read a mesh's local positions + triangle indices out of its buffers.</summary>
    private static bool TryReadGeometry(MapGeoBinary map, MapGeoBinary.Mesh mesh,
        out float[] positions, out int[] indices, out string why)
    {
        positions = Array.Empty<float>(); indices = Array.Empty<int>(); why = "";

        // Locate the Position element across the mesh's consecutive declaration run.
        int bufIdx = -1, offset = 0, stride = 0;
        for (int i = 0; i < mesh.VertexBufferIds.Count; i++)
        {
            var decl = map.Declarations[mesh.VertexDeclarationBase + i];
            int o = 0;
            foreach (var (name, format) in decl.Elements)
            {
                if (name == MapGeoBinary.ElemPosition && format == MapGeoBinary.FmtXYZ_Float32)
                { bufIdx = i; offset = o; stride = decl.Stride; break; }
                o += MapGeoBinary.FormatSize(format);
            }
            if (bufIdx >= 0) break;
        }
        if (bufIdx < 0) { why = "no XYZ_Float32 Position element"; return false; }

        var vb = map.VertexBuffers[mesh.VertexBufferIds[bufIdx]];
        if (stride <= 0 || (long)mesh.VertexCount * stride > vb.Data.Length)
        { why = "vertex buffer smaller than vertexCount * stride"; return false; }

        positions = new float[mesh.VertexCount * 3];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            int o = v * stride + offset;
            positions[v * 3] = BitConverter.ToSingle(vb.Data, o);
            positions[v * 3 + 1] = BitConverter.ToSingle(vb.Data, o + 4);
            positions[v * 3 + 2] = BitConverter.ToSingle(vb.Data, o + 8);
        }

        var ib = map.IndexBuffers[mesh.IndexBufferId];
        if (ib.Data.Length < mesh.IndexCount * 2) { why = "index buffer shorter than indexCount"; return false; }
        indices = new int[mesh.IndexCount];
        for (int i = 0; i < mesh.IndexCount; i++)
        {
            int idx = BinaryPrimitives.ReadUInt16LittleEndian(ib.Data.AsSpan(i * 2));
            if (idx >= mesh.VertexCount) { why = $"index {idx} out of range for {mesh.VertexCount} vertices"; return false; }
            indices[i] = idx;
        }
        return true;
    }

    /// <summary>Rebuild every buffer of a geometry through the unwrap's vertex remap, append the uv7
    /// buffer, and repoint all its instances at the new buffers.</summary>
    private static void RebuildGeometry(MapGeoBinary map, List<MapGeoBinary.Mesh> instances,
        UnwrapResult unwrap, int sourceVertexCount)
    {
        var proto = instances[0];
        int newVertexCount = unwrap.VertexRemap.Length;

        // New vertex buffers: one per original, each vertex copied from its source through the remap.
        var newVbIds = new List<int>(proto.VertexBufferIds.Count);
        for (int i = 0; i < proto.VertexBufferIds.Count; i++)
        {
            var src = map.VertexBuffers[proto.VertexBufferIds[i]];
            int stride = map.Declarations[proto.VertexDeclarationBase + i].Stride;
            var data = new byte[(long)newVertexCount * stride <= int.MaxValue ? newVertexCount * stride : 0];
            for (int v = 0; v < newVertexCount; v++)
            {
                int srcV = unwrap.VertexRemap[v];
                if ((srcV + 1) * stride <= src.Data.Length)
                    Array.Copy(src.Data, srcV * stride, data, v * stride, stride);
            }
            newVbIds.Add(map.VertexBuffers.Count);
            map.VertexBuffers.Add(new MapGeoBinary.VertexBuffer
            { Data = data, HasVisibility = src.HasVisibility, Visibility = src.Visibility });
        }

        // New index buffer (rewritten against the new vertices).
        var protoIb = map.IndexBuffers[proto.IndexBufferId];
        var idxData = new byte[unwrap.Indices.Length * 2];
        for (int i = 0; i < unwrap.Indices.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(idxData.AsSpan(i * 2), (ushort)unwrap.Indices[i]);
        int newIbId = map.IndexBuffers.Count;
        map.IndexBuffers.Add(new MapGeoBinary.IndexBuffer
        { Data = idxData, HasVisibility = protoIb.HasVisibility, Visibility = protoIb.Visibility });

        // The uv7 buffer + a fresh CONSECUTIVE declaration run (clones + the uv7 declaration), because a
        // mesh resolves its buffer i through declarations[base + i].
        var uvData = new byte[unwrap.Uvs.Length * 8];
        for (int i = 0; i < unwrap.Uvs.Length; i++)
        {
            BitConverter.TryWriteBytes(uvData.AsSpan(i * 8), unwrap.Uvs[i].X);
            BitConverter.TryWriteBytes(uvData.AsSpan(i * 8 + 4), unwrap.Uvs[i].Y);
        }
        int uvBufId = map.VertexBuffers.Count;
        map.VertexBuffers.Add(new MapGeoBinary.VertexBuffer
        {
            Data = uvData,
            HasVisibility = map.VertexBuffers[proto.VertexBufferIds[0]].HasVisibility,
            Visibility = map.VertexBuffers[proto.VertexBufferIds[0]].Visibility,
        });

        int newDeclBase = map.Declarations.Count;
        for (int i = 0; i < proto.VertexBufferIds.Count; i++)
        {
            var src = map.Declarations[proto.VertexDeclarationBase + i];
            map.Declarations.Add(new MapGeoBinary.VertexDeclaration
            { Usage = src.Usage, Elements = new(src.Elements), Padding = (byte[])src.Padding.Clone() });
        }
        map.Declarations.Add(new MapGeoBinary.VertexDeclaration
        {
            Usage = 0,
            Elements = { (MapGeoBinary.LightmapUvElement, MapGeoBinary.FmtXY_Float32) },
            Padding = new byte[8 * 14],
        });

        // Repoint every instance. They share the rebuilt geometry; only their atlas region will differ.
        foreach (var mesh in instances)
        {
            mesh.VertexBufferIds = new List<int>(newVbIds) { uvBufId };
            mesh.VertexDeclarationBase = newDeclBase;
            mesh.VertexCount = newVertexCount;
            mesh.IndexBufferId = newIbId;
            mesh.IndexCount = unwrap.Indices.Length;
            // Submesh ranges are relative to this mesh's own index buffer and the triangle order is
            // unchanged, so only the vertex bounds need refreshing.
            foreach (var sub in mesh.Submeshes)
            {
                sub.MinVertex = 0;
                sub.MaxVertex = newVertexCount - 1;
            }
        }
    }

    /// <summary>Shelf-pack every mesh into fixed-size atlases, sizing each region by the mesh's world
    /// extent so texel density is comparable across the map.</summary>
    private static int PackIntoAtlases(
        List<(MapGeoBinary.Mesh Mesh, float WorldExtent)> meshes, Settings s,
        out List<(MapGeoBinary.Mesh Mesh, int Atlas, Vector2 Origin, Vector2 Size)> placement)
    {
        placement = new();
        int res = Math.Max(64, s.AtlasResolution);
        int pad = Math.Max(0, s.Padding);

        // Largest first keeps the shelves tight.
        foreach (var (mesh, extent) in meshes.OrderByDescending(m => m.WorldExtent))
        {
            int size = (int)MathF.Round(extent * s.TexelDensity);
            size = Math.Clamp(size, Math.Max(2, s.MinRegion), Math.Min(s.MaxRegion, res - 2 * pad));
            placement.Add((mesh, -1, Vector2.Zero, new Vector2(size, size)));
        }

        int atlas = 0, x = pad, y = pad, shelf = 0;
        for (int i = 0; i < placement.Count; i++)
        {
            var (mesh, _, _, size) = placement[i];
            int w = (int)size.X, h = (int)size.Y;
            if (x + w + pad > res) { x = pad; y += shelf + pad; shelf = 0; }
            if (y + h + pad > res) { atlas++; x = pad; y = pad; shelf = 0; }
            placement[i] = (mesh, atlas, new Vector2(x, y), size);
            x += w + pad;
            shelf = Math.Max(shelf, h);
        }
        return placement.Count == 0 ? 0 : atlas + 1;
    }
}
