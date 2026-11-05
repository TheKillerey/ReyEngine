using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>The two hashes that select a mapgeo scene bucket grid.</summary>
public readonly record struct MapBucketGridKey(uint ControllerHash, uint RegionHash);

/// <summary>One 20-byte bucket descriptor in a BucketedGeometry scene graph.</summary>
public sealed record MapBucketCell(
    float MaxStickOutX,
    float MaxStickOutZ,
    uint StartIndex,
    uint BaseVertex,
    ushort InsideFaceCount,
    ushort StickingOutFaceCount);

/// <summary>
/// A complete, format-neutral BucketedGeometry payload. Indices are local to each cell's
/// <see cref="MapBucketCell.BaseVertex"/> and cells are stored in Z-major, then X-major order.
/// </summary>
public sealed class MapBucketGridData
{
    public required MapBucketGridKey Key { get; init; }
    public required float MinX { get; init; }
    public required float MinZ { get; init; }
    public required float MaxX { get; init; }
    public required float MaxZ { get; init; }
    public required float MaxStickOutX { get; init; }
    public required float MaxStickOutZ { get; init; }
    public required float BucketSizeX { get; init; }
    public required float BucketSizeZ { get; init; }
    public required ushort BucketsPerSide { get; init; }
    public required IReadOnlyList<Vector3> Vertices { get; init; }
    public required IReadOnlyList<ushort> Indices { get; init; }
    public required IReadOnlyList<MapBucketCell> Buckets { get; init; }
    public required IReadOnlyList<byte> FaceVisibilityFlags { get; init; }
}

/// <summary>
/// Recreates League's X/Z culling grids from the map's current world-space triangles.
/// Meshes are grouped by controller + render-region hash. Each face is stored ONCE per grid, in the
/// bucket that holds the min-X/min-Z corner of its X/Z box (see <see cref="LocateFace"/>), and carries
/// its source mesh's visibility byte.
///
/// <para>M848: this used to copy a face into EVERY bucket it overlapped. The client draws planar
/// projection VFX (<c>VfxPrimitivePlanarProjection</c>, the Jade turret shots' <c>projected</c> emitter)
/// onto these grid triangles, not onto the render meshes, and it paints each copy additively. On Old
/// Summoner's Rift the copies averaged 1.62 per face, far more for big ground triangles, and the shots
/// drew bright, stepped washes with hard edges along the bucket lines. Riot stores each face once, in its
/// min-corner bucket. That holds for every one of the 13,970,431 faces in the grids of the 202 mapgeos Riot
/// ships in Maps/Shipping, base_srx's 323,296 among them. A grid rebuilt with Riot's rule drew the
/// projection correctly in game (2026-10-25).</para>
/// </summary>
public static class MapBucketGridBuilder
{
    public const float HeightRangeMin = -120f;
    public const float HeightRangeMax = 5000f;
    /// <summary>M848: about Riot's cell size. Riot's Map11 master grid is 128 x 128 buckets of ~120 units.
    /// Riot's formula for the bucket count of its smaller grids is NOT known (it ships 128, 32, 25, 12,
    /// 33, ... for extents that ceil(extent / 120) does not reproduce); ceil(larger extent / 120) is what
    /// was verified in game. Was 500.</summary>
    public const float TargetBucketSize = 120f;
    public const int MinimumBucketsPerSide = 4;
    public const int MaximumBucketsPerSide = 256;

    private const float BoundsPadding = 0.001f;
    private const float MinimumBucketSize = 0.01f;

    /// <summary>
    /// M410: the height range is now a PARAMETER, not a constant.
    ///
    /// <para>Worth being precise about what it does, because the name invites a wrong mental model: the
    /// bucket grid has NO height. It is a 2D X/Z culling grid - MapBucketGridData carries
    /// MinX/MinZ/MaxX/MaxZ and BucketsPerSide and nothing about Y, and the shipped header has no height
    /// field either. This range is a triangle REJECT FILTER applied before the grid is built: a triangle
    /// whose entire Y span falls outside it is dropped from the bake. Widening it bakes more geometry
    /// into the grid; it does not make the grid taller, because there is no such thing.</para>
    ///
    /// <para>Triangles that STRADDLE a boundary are kept whole - there is no clipping - so the effective
    /// volume is slightly larger than the range in both directions.</para>
    /// </summary>
    /// <param name="heightMin">Absolute world Y, not relative to the map's bounds.</param>
    /// <param name="meshSelection">M848: which meshes go in. Riot leaves VertexDeform meshes without lightmap
    /// UVs out (see <see cref="MapBucketGridMeshSelection"/>). Null keeps every mesh, which was the only
    /// behaviour before.</param>
    public static IReadOnlyList<MapBucketGridData> Rebuild(MapGeoAsset asset,
        float targetBucketSize = TargetBucketSize,
        float heightMin = HeightRangeMin, float heightMax = HeightRangeMax,
        MapBucketGridMeshSelection? meshSelection = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!float.IsFinite(targetBucketSize) || targetBucketSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetBucketSize));
        if (!float.IsFinite(heightMin) || !float.IsFinite(heightMax))
            throw new ArgumentOutOfRangeException(nameof(heightMin), "height range must be finite");
        // Inverted rather than clamped: an inverted range silently bakes NOTHING, and a grid that is
        // empty because two numbers were typed the wrong way round is indistinguishable on screen from
        // one that failed to build.
        if (heightMin > heightMax)
            throw new ArgumentOutOfRangeException(nameof(heightMin),
                $"height range is inverted: min {heightMin} is above max {heightMax}");

        HashSet<int> excludedMeshes = (meshSelection ?? MapBucketGridMeshSelection.All).ExcludedMeshes(asset);
        var trianglesByGrid = new Dictionary<MapBucketGridKey, List<Triangle>>();
        foreach (var group in asset.Groups)
        {
            if (group.MeshIndex < 0 || group.MeshIndex >= asset.Meshes.Count)
                throw new InvalidDataException($"Mapgeo group references invalid mesh {group.MeshIndex}.");
            if (excludedMeshes.Contains(group.MeshIndex))
                continue;
            if (group.StartIndex < 0 || group.IndexCount < 0
                || group.StartIndex > asset.Indices.Length - group.IndexCount)
                throw new InvalidDataException($"Mapgeo group for mesh {group.MeshIndex} has an invalid index range.");
            if (group.IndexCount % 3 != 0)
                throw new InvalidDataException($"Mapgeo group for mesh {group.MeshIndex} is not triangle-aligned.");

            MapGeoMesh mesh = asset.Meshes[group.MeshIndex];
            var key = new MapBucketGridKey(mesh.ControllerHash, mesh.RegionHash);
            if (!trianglesByGrid.TryGetValue(key, out var triangles))
            {
                triangles = new List<Triangle>();
                trianglesByGrid.Add(key, triangles);
            }

            int end = group.StartIndex + group.IndexCount;
            for (int i = group.StartIndex; i < end; i += 3)
            {
                uint a = asset.Indices[i];
                uint b = asset.Indices[i + 1];
                uint c = asset.Indices[i + 2];
                Vector3 pa = PositionAt(asset, a);
                Vector3 pb = PositionAt(asset, b);
                Vector3 pc = PositionAt(asset, c);
                float minY = MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y));
                float maxY = MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y));
                if (maxY < heightMin || minY > heightMax)
                    continue;

                triangles.Add(new Triangle(a, b, c, unchecked((byte)mesh.VisibilityFlags)));
            }
        }

        return trianglesByGrid
            .Where(pair => pair.Value.Count > 0)
            .OrderBy(pair => pair.Key == default ? 0 : 1)
            .ThenBy(pair => pair.Key.ControllerHash)
            .ThenBy(pair => pair.Key.RegionHash)
            .Select(pair => BuildGrid(asset, pair.Key, pair.Value, targetBucketSize))
            .ToArray();
    }

    /// <summary>
    /// M411: the volume the next bake would actually cover, for the viewport preview.
    ///
    /// <para>X/Z are DERIVED - the union of every grid's snapped extent, exactly as BuildGrid computes
    /// it, so the preview cannot drift from the bake. Y is the height FILTER range, which is authored.
    /// Drawn together they answer the real question ("what gets baked?") without implying the X/Z is
    /// editable, because it is not: the grid's rectangle comes from the geometry that survives.</para>
    ///
    /// <para>Null when nothing survives the filter - which is itself the useful answer, and is why an
    /// inverted range throws in Rebuild rather than silently producing this.</para>
    /// </summary>
    public static (Vector3 Min, Vector3 Max)? ComputeBakeBounds(MapGeoAsset asset,
        float targetBucketSize = TargetBucketSize,
        float heightMin = HeightRangeMin, float heightMax = HeightRangeMax,
        MapBucketGridMeshSelection? meshSelection = null)
    {
        var grids = Rebuild(asset, targetBucketSize, heightMin, heightMax, meshSelection);
        if (grids.Count == 0) return null;

        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var g in grids)
        {
            minX = MathF.Min(minX, g.MinX); minZ = MathF.Min(minZ, g.MinZ);
            maxX = MathF.Max(maxX, g.MaxX); maxZ = MathF.Max(maxZ, g.MaxZ);
        }
        return (new Vector3(minX, heightMin, minZ), new Vector3(maxX, heightMax, maxZ));
    }

    private static MapBucketGridData BuildGrid(MapGeoAsset asset, MapBucketGridKey key, List<Triangle> triangles, float targetBucketSize)
    {
        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        foreach (Triangle triangle in triangles)
        {
            AccumulateBounds(PositionAt(asset, triangle.A), ref minX, ref minZ, ref maxX, ref maxZ);
            AccumulateBounds(PositionAt(asset, triangle.B), ref minX, ref minZ, ref maxX, ref maxZ);
            AccumulateBounds(PositionAt(asset, triangle.C), ref minX, ref minZ, ref maxX, ref maxZ);
        }

        maxX += BoundsPadding;
        maxZ += BoundsPadding;
        float rangeX = maxX - minX;
        float rangeZ = maxZ - minZ;
        int side = Math.Clamp(
            (int)MathF.Ceiling(MathF.Max(rangeX, rangeZ) / targetBucketSize),
            MinimumBucketsPerSide,
            MaximumBucketsPerSide);

        float bucketSizeX = MathF.Max(rangeX / side, MinimumBucketSize);
        float bucketSizeZ = MathF.Max(rangeZ / side, MinimumBucketSize);
        maxX = minX + bucketSizeX * side;
        maxZ = minZ + bucketSizeZ * side;

        // M848: one bucket per face - the one holding its X/Z min corner (Riot's rule, see LocateFace).
        var facesByBucket = new List<BucketFace>?[checked(side * side)];
        foreach (Triangle triangle in triangles)
        {
            var (x, z, inside) = LocateFace(
                PositionAt(asset, triangle.A), PositionAt(asset, triangle.B), PositionAt(asset, triangle.C),
                minX, minZ, bucketSizeX, bucketSizeZ, side);
            int cellIndex = z * side + x;
            (facesByBucket[cellIndex] ??= new List<BucketFace>()).Add(new BucketFace(triangle, inside));
        }

        var vertices = new List<Vector3>();
        var indices = new List<ushort>();
        var visibility = new List<byte>();
        var buckets = new MapBucketCell[facesByBucket.Length];
        float gridStickOutX = 0f, gridStickOutZ = 0f;

        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int cellIndex = z * side + x;
            List<BucketFace>? cellFaces = facesByBucket[cellIndex];
            uint startIndex = checked((uint)indices.Count);
            if (cellFaces is null || cellFaces.Count == 0)
            {
                buckets[cellIndex] = new(0f, 0f, startIndex, 0, 0, 0);
                continue;
            }

            var inside = cellFaces.Where(face => face.Inside).ToArray();
            var sticking = cellFaces.Where(face => !face.Inside).ToArray();
            if (inside.Length > ushort.MaxValue || sticking.Length > ushort.MaxValue)
                throw new InvalidDataException($"Bucket [{x},{z}] exceeds the mapgeo ushort face-count limit.");

            uint baseVertex = checked((uint)vertices.Count);
            // M842: keyed by POSITION, not by the source vertex index. The grid stores positions only, so two
            // source vertices at the same place (a UV or normal seam, two meshes meeting) are the same grid
            // vertex - Riot's own grids are welded like this (base_srx: 196,057 grid vertices for 192,509
            // faces, where the source meshes split them into ~2.3 per triangle).
            var localVertices = new Dictionary<Vector3, ushort>();
            foreach (BucketFace face in inside.Concat(sticking))
            {
                AddIndex(face.Triangle.A);
                AddIndex(face.Triangle.B);
                AddIndex(face.Triangle.C);
                visibility.Add(face.Triangle.Visibility);
            }

            // M848: only the +X/+Z edges can be crossed - the min corner always lies in this bucket.
            float right = minX + (x + 1) * bucketSizeX;
            float top = minZ + (z + 1) * bucketSizeZ;
            float stickOutX = 0f, stickOutZ = 0f;
            foreach (BucketFace face in sticking)
            {
                AccumulateStickOut(PositionAt(asset, face.Triangle.A));
                AccumulateStickOut(PositionAt(asset, face.Triangle.B));
                AccumulateStickOut(PositionAt(asset, face.Triangle.C));
            }

            gridStickOutX = MathF.Max(gridStickOutX, stickOutX);
            gridStickOutZ = MathF.Max(gridStickOutZ, stickOutZ);
            buckets[cellIndex] = new(
                stickOutX, stickOutZ, startIndex, baseVertex,
                checked((ushort)inside.Length), checked((ushort)sticking.Length));

            void AddIndex(uint globalIndex)
            {
                Vector3 position = PositionAt(asset, globalIndex);
                if (!localVertices.TryGetValue(position, out ushort localIndex))
                {
                    if (localVertices.Count > ushort.MaxValue)
                        throw new InvalidDataException($"Bucket [{x},{z}] exceeds the mapgeo ushort vertex-index limit.");
                    localIndex = checked((ushort)localVertices.Count);
                    localVertices.Add(position, localIndex);
                    vertices.Add(position);
                }
                indices.Add(localIndex);
            }

            void AccumulateStickOut(Vector3 point)
            {
                if (point.X > right) stickOutX = MathF.Max(stickOutX, point.X - right);
                if (point.Z > top) stickOutZ = MathF.Max(stickOutZ, point.Z - top);
            }
        }

        if (visibility.Count != indices.Count / 3)
            throw new InvalidDataException("Generated bucket visibility count does not match its face count.");

        return new MapBucketGridData
        {
            Key = key,
            MinX = minX,
            MinZ = minZ,
            MaxX = maxX,
            MaxZ = maxZ,
            MaxStickOutX = gridStickOutX,
            MaxStickOutZ = gridStickOutZ,
            BucketSizeX = bucketSizeX,
            BucketSizeZ = bucketSizeZ,
            BucketsPerSide = checked((ushort)side),
            Vertices = vertices,
            Indices = indices,
            Buckets = buckets,
            FaceVisibilityFlags = visibility,
        };
    }

    private static Vector3 PositionAt(MapGeoAsset asset, uint index)
    {
        if (index >= asset.VertexCount)
            throw new InvalidDataException($"Mapgeo index {index} exceeds vertex count {asset.VertexCount}.");
        int offset = checked((int)index * 3);
        var result = new Vector3(asset.Positions[offset], asset.Positions[offset + 1], asset.Positions[offset + 2]);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z))
            throw new InvalidDataException($"Mapgeo vertex {index} is not finite.");
        return result;
    }

    private static void AccumulateBounds(Vector3 point, ref float minX, ref float minZ, ref float maxX, ref float maxZ)
    {
        minX = MathF.Min(minX, point.X);
        minZ = MathF.Min(minZ, point.Z);
        maxX = MathF.Max(maxX, point.X);
        maxZ = MathF.Max(maxZ, point.Z);
    }

    /// <summary>
    /// M848: the one bucket Riot stores a face in, and whether it counts as inside it.
    ///
    /// <list type="number">
    ///   <item>The bucket is the one holding the min-X/min-Z corner of the face's X/Z bounding box.</item>
    ///   <item>Inside means all three vertices lie in that bucket. The min corner always does, so this is
    ///   the max corner not passing the bucket's +X/+Z edges. Otherwise the face is sticking out, and it
    ///   counts toward that bucket's MaxStickOutX/Z.</item>
    /// </list>
    ///
    /// <para>Measured first on Riot's Map11 base_srx.mapgeo, then over every grid of all 202 mapgeos
    /// Riot ships in Maps/Shipping. The bucket is <c>floor((min - gridMin) / bucketSize)</c> in single
    /// precision, and that reproduces Riot's placement of every face, with 0 exceptions. The inside flag
    /// is the max corner falling in the same bucket by the same formula. That matches every Riot face
    /// except a handful whose max corner sits exactly on a +X/+Z edge or one float step before it, where
    /// Riot says "sticking out". Those are the same few faces repeated across the Summoner's Rift
    /// variants, 82 hits in all, and no epsilon reproduces them: Riot evidently classified them from
    /// positions before they were rounded to the stored floats. Correcting the floor against the edges
    /// (<c>min + i * size</c>) was tried and disagreed with Riot on 74 faces.</para>
    ///
    /// <para>Public so a test can hold Riot's own grids to it: given Riot's header, this function must put
    /// every Riot face where Riot put it.</para>
    /// </summary>
    public static (int X, int Z, bool Inside) LocateFace(Vector3 a, Vector3 b, Vector3 c,
        float gridMinX, float gridMinZ, float bucketSizeX, float bucketSizeZ, int bucketsPerSide)
    {
        if (bucketsPerSide <= 0) throw new ArgumentOutOfRangeException(nameof(bucketsPerSide));
        float faceMinX = MathF.Min(a.X, MathF.Min(b.X, c.X));
        float faceMinZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
        float faceMaxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
        float faceMaxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
        int x = Math.Clamp(CellOf(faceMinX, gridMinX, bucketSizeX), 0, bucketsPerSide - 1);
        int z = Math.Clamp(CellOf(faceMinZ, gridMinZ, bucketSizeZ), 0, bucketsPerSide - 1);
        bool inside = CellOf(faceMaxX, gridMinX, bucketSizeX) == x
            && CellOf(faceMaxZ, gridMinZ, bucketSizeZ) == z;
        return (x, z, inside);
    }

    /// <summary>Unclamped, so a max corner past the grid's last edge reads as a later bucket. Large
    /// values saturate rather than wrap.</summary>
    private static int CellOf(float value, float min, float size)
    {
        float q = MathF.Floor((value - min) / size);
        return q >= int.MaxValue ? int.MaxValue : q <= int.MinValue ? int.MinValue : (int)q;
    }

    private readonly record struct Triangle(uint A, uint B, uint C, byte Visibility);
    private readonly record struct BucketFace(Triangle Triangle, bool Inside);
}
