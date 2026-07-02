using System.Numerics;

namespace ReyEngine.Rendering;

/// <summary>
/// Exact click-selection against the baked map buffers: Möller–Trumbore over every visible submesh's
/// triangles, nearest hit wins. Exactness matters on maps — a big terrain mesh's bounding box encloses
/// half the map, so AABB-only picking would grab the floor for almost any click.
/// </summary>
public static class ViewportMeshPicker
{
    /// <summary>
    /// Cast a ray at the combined mesh and return the index of the nearest-hit submesh (or -1).
    /// <paramref name="visible"/> mirrors the renderer's per-submesh visibility (null = all visible),
    /// so hidden dragon/baron layers can't be picked through.
    /// </summary>
    public static int PickSubmesh(float[] positions, uint[] indices,
        IReadOnlyList<(int start, int count)> submeshes, IReadOnlyList<bool>? visible,
        Vector3 rayOrigin, Vector3 rayDir, out float hitDistance)
    {
        int best = -1;
        float bestT = float.MaxValue;

        for (int s = 0; s < submeshes.Count; s++)
        {
            if (visible is not null && s < visible.Count && !visible[s]) continue;
            var (start, count) = submeshes[s];
            int end = Math.Min(start + count, indices.Length);
            for (int i = start; i + 3 <= end; i += 3)
            {
                var v0 = ReadVertex(positions, indices[i]);
                var v1 = ReadVertex(positions, indices[i + 1]);
                var v2 = ReadVertex(positions, indices[i + 2]);
                if (ViewportPicking.RayTriangle(rayOrigin, rayDir, v0, v1, v2) is { } t && t < bestT)
                {
                    bestT = t;
                    best = s;
                }
            }
        }

        hitDistance = bestT;
        return best;
    }

    private static Vector3 ReadVertex(float[] positions, uint index)
        => new(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);
}
