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
/// Meshes are grouped by controller + render-region hash; each face is copied into every
/// 2D bucket it overlaps and carries its source mesh's visibility byte.
/// </summary>
public static class MapBucketGridBuilder
{
    public const float HeightRangeMin = -120f;
    public const float HeightRangeMax = 5000f;
    public const float TargetBucketSize = 500f;
    public const int MinimumBucketsPerSide = 4;
    public const int MaximumBucketsPerSide = 256;

    private const float BoundsPadding = 0.001f;
    private const float MinimumBucketSize = 0.01f;
    private const float IntersectionEpsilon = 0.0001f;

    public static IReadOnlyList<MapBucketGridData> Rebuild(MapGeoAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var trianglesByGrid = new Dictionary<MapBucketGridKey, List<Triangle>>();
        foreach (var group in asset.Groups)
        {
            if (group.MeshIndex < 0 || group.MeshIndex >= asset.Meshes.Count)
                throw new InvalidDataException($"Mapgeo group references invalid mesh {group.MeshIndex}.");
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
                if (maxY < HeightRangeMin || minY > HeightRangeMax)
                    continue;

                triangles.Add(new Triangle(a, b, c, unchecked((byte)mesh.VisibilityFlags)));
            }
        }

        return trianglesByGrid
            .Where(pair => pair.Value.Count > 0)
            .OrderBy(pair => pair.Key == default ? 0 : 1)
            .ThenBy(pair => pair.Key.ControllerHash)
            .ThenBy(pair => pair.Key.RegionHash)
            .Select(pair => BuildGrid(asset, pair.Key, pair.Value))
            .ToArray();
    }

    private static MapBucketGridData BuildGrid(MapGeoAsset asset, MapBucketGridKey key, List<Triangle> triangles)
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
            (int)MathF.Ceiling(MathF.Max(rangeX, rangeZ) / TargetBucketSize),
            MinimumBucketsPerSide,
            MaximumBucketsPerSide);

        float bucketSizeX = MathF.Max(rangeX / side, MinimumBucketSize);
        float bucketSizeZ = MathF.Max(rangeZ / side, MinimumBucketSize);
        maxX = minX + bucketSizeX * side;
        maxZ = minZ + bucketSizeZ * side;

        var facesByBucket = new List<BucketFace>?[checked(side * side)];
        foreach (Triangle triangle in triangles)
        {
            Vector3 a = PositionAt(asset, triangle.A);
            Vector3 b = PositionAt(asset, triangle.B);
            Vector3 c = PositionAt(asset, triangle.C);
            float triMinX = MathF.Min(a.X, MathF.Min(b.X, c.X));
            float triMaxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
            float triMinZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
            float triMaxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));

            int startX = Math.Max(0, CellOf(triMinX, minX, bucketSizeX) - 1);
            int endX = Math.Min(side - 1, CellOf(triMaxX, minX, bucketSizeX) + 1);
            int startZ = Math.Max(0, CellOf(triMinZ, minZ, bucketSizeZ) - 1);
            int endZ = Math.Min(side - 1, CellOf(triMaxZ, minZ, bucketSizeZ) + 1);

            for (int z = startZ; z <= endZ; z++)
            for (int x = startX; x <= endX; x++)
            {
                float left = minX + x * bucketSizeX;
                float bottom = minZ + z * bucketSizeZ;
                float right = left + bucketSizeX;
                float top = bottom + bucketSizeZ;
                if (!TriangleOverlapsRectangle(a, b, c, left, bottom, right, top))
                    continue;

                bool inside = PointInsideRectangle(a, left, bottom, right, top)
                    && PointInsideRectangle(b, left, bottom, right, top)
                    && PointInsideRectangle(c, left, bottom, right, top);
                int cellIndex = z * side + x;
                (facesByBucket[cellIndex] ??= new List<BucketFace>()).Add(new BucketFace(triangle, inside));
            }
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
            var localVertices = new Dictionary<uint, ushort>();
            foreach (BucketFace face in inside.Concat(sticking))
            {
                AddIndex(face.Triangle.A);
                AddIndex(face.Triangle.B);
                AddIndex(face.Triangle.C);
                visibility.Add(face.Triangle.Visibility);
            }

            float left = minX + x * bucketSizeX;
            float bottom = minZ + z * bucketSizeZ;
            float right = left + bucketSizeX;
            float top = bottom + bucketSizeZ;
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
                if (!localVertices.TryGetValue(globalIndex, out ushort localIndex))
                {
                    if (localVertices.Count > ushort.MaxValue)
                        throw new InvalidDataException($"Bucket [{x},{z}] exceeds the mapgeo ushort vertex-index limit.");
                    localIndex = checked((ushort)localVertices.Count);
                    localVertices.Add(globalIndex, localIndex);
                    vertices.Add(PositionAt(asset, globalIndex));
                }
                indices.Add(localIndex);
            }

            void AccumulateStickOut(Vector3 point)
            {
                if (point.X < left) stickOutX = MathF.Max(stickOutX, left - point.X);
                if (point.X > right) stickOutX = MathF.Max(stickOutX, point.X - right);
                if (point.Z < bottom) stickOutZ = MathF.Max(stickOutZ, bottom - point.Z);
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

    private static int CellOf(float value, float min, float size) => (int)MathF.Floor((value - min) / size);

    private static bool TriangleOverlapsRectangle(
        Vector3 a, Vector3 b, Vector3 c,
        float left, float bottom, float right, float top)
    {
        if (PointInsideRectangle(a, left, bottom, right, top)
            || PointInsideRectangle(b, left, bottom, right, top)
            || PointInsideRectangle(c, left, bottom, right, top))
            return true;

        Vector2 r0 = new(left, bottom), r1 = new(right, bottom);
        Vector2 r2 = new(right, top), r3 = new(left, top);
        Vector2 aa = new(a.X, a.Z), bb = new(b.X, b.Z), cc = new(c.X, c.Z);
        if (PointInsideTriangle(r0, aa, bb, cc) || PointInsideTriangle(r1, aa, bb, cc)
            || PointInsideTriangle(r2, aa, bb, cc) || PointInsideTriangle(r3, aa, bb, cc))
            return true;

        return SegmentIntersectsRectangle(aa, bb, r0, r1, r2, r3)
            || SegmentIntersectsRectangle(bb, cc, r0, r1, r2, r3)
            || SegmentIntersectsRectangle(cc, aa, r0, r1, r2, r3);
    }

    private static bool PointInsideRectangle(Vector3 point, float left, float bottom, float right, float top)
        => point.X >= left - IntersectionEpsilon && point.X <= right + IntersectionEpsilon
        && point.Z >= bottom - IntersectionEpsilon && point.Z <= top + IntersectionEpsilon;

    private static bool PointInsideTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        if (MathF.Abs(Cross(b - a, c - a)) <= IntersectionEpsilon)
            return false;
        float d1 = Cross(point - a, b - a);
        float d2 = Cross(point - b, c - b);
        float d3 = Cross(point - c, a - c);
        bool hasNegative = d1 < -IntersectionEpsilon || d2 < -IntersectionEpsilon || d3 < -IntersectionEpsilon;
        bool hasPositive = d1 > IntersectionEpsilon || d2 > IntersectionEpsilon || d3 > IntersectionEpsilon;
        return !(hasNegative && hasPositive);
    }

    private static bool SegmentIntersectsRectangle(
        Vector2 a, Vector2 b, Vector2 r0, Vector2 r1, Vector2 r2, Vector2 r3)
        => SegmentsIntersect(a, b, r0, r1) || SegmentsIntersect(a, b, r1, r2)
        || SegmentsIntersect(a, b, r2, r3) || SegmentsIntersect(a, b, r3, r0);

    private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float abC = Cross(b - a, c - a);
        float abD = Cross(b - a, d - a);
        float cdA = Cross(d - c, a - c);
        float cdB = Cross(d - c, b - c);
        if (MathF.Abs(abC) <= IntersectionEpsilon && OnSegment(a, b, c)) return true;
        if (MathF.Abs(abD) <= IntersectionEpsilon && OnSegment(a, b, d)) return true;
        if (MathF.Abs(cdA) <= IntersectionEpsilon && OnSegment(c, d, a)) return true;
        if (MathF.Abs(cdB) <= IntersectionEpsilon && OnSegment(c, d, b)) return true;
        return ((abC > IntersectionEpsilon && abD < -IntersectionEpsilon)
                || (abC < -IntersectionEpsilon && abD > IntersectionEpsilon))
            && ((cdA > IntersectionEpsilon && cdB < -IntersectionEpsilon)
                || (cdA < -IntersectionEpsilon && cdB > IntersectionEpsilon));
    }

    private static bool OnSegment(Vector2 a, Vector2 b, Vector2 point)
        => point.X >= MathF.Min(a.X, b.X) - IntersectionEpsilon
        && point.X <= MathF.Max(a.X, b.X) + IntersectionEpsilon
        && point.Y >= MathF.Min(a.Y, b.Y) - IntersectionEpsilon
        && point.Y <= MathF.Max(a.Y, b.Y) + IntersectionEpsilon;

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private readonly record struct Triangle(uint A, uint B, uint C, byte Visibility);
    private readonly record struct BucketFace(Triangle Triangle, bool Inside);
}
