using System.Buffers.Binary;
using System.Numerics;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Memory;

namespace ReyEngine.Formats.MapGeo;

public static class MapGeoDecoder
{
    private static readonly ElementName[] AllElementNames = (ElementName[])Enum.GetValues(typeof(ElementName));

    public static MapGeoAsset Decode(byte[] data)
    {
        int version = data.Length >= 8 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) : 0;

        using var ms = new MemoryStream(data, writable: false);
        var env = new EnvironmentAsset(ms);

        var positions = new List<float>();
        var normals = new List<float>();
        var uvs = new List<float>();
        var colors = new List<float>();   // PrimaryColor RGBA 0..1 (white when a mesh has none)
        bool anyColor = false;
        var lightmapUvs = new List<float>();   // atlas-mapped lightmap UV (0,0 when a mesh has none)
        bool anyLightmap = false;
        var indices = new List<uint>();
        var groups = new List<MapGeoGroup>();
        var meshes = new List<MapGeoMesh>();
        var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int meshCount = 0;
        int meshIndex = -1;

        foreach (var mesh in env.Meshes)
        {
            meshIndex++;
            try
            {
                var view = mesh.VerticesView;
                int vc = view.VertexCount;
                int baseVertex = positions.Count / 3;
                var transform = mesh.Transform;

                // Normal matrix = inverse-transpose of the world transform. Correct for mirrored (negative
                // determinant) and non-uniform-scale meshes; identical to the plain 3x3 for the common
                // rotation + uniform-scale case (so no change for most SR geometry). Falls back to the raw
                // transform if it isn't invertible (M34).
                var normalMatrix = Matrix4x4.Invert(transform, out var invT)
                    ? Matrix4x4.Transpose(invT) : transform;

                var meshPos = ReadVector3(view.GetAccessor(ElementName.Position), vc);
                var meshNrm = view.TryGetAccessor(ElementName.Normal, out var nAcc) ? ReadVector3(nAcc, vc) : null;
                var meshUv = view.TryGetAccessor(ElementName.Texcoord0, out var tAcc) ? ReadVector2(tAcc, vc) : null;
                var meshCol = view.TryGetAccessor(ElementName.PrimaryColor, out var cAcc) ? ReadColor(cAcc, vc) : null;
                if (meshCol is not null) anyColor = true;

                // Baked lightmap: a dedicated UV channel (Texcoord7) + the BakedLight channel's atlas
                // texture + scale/bias. Final atlas UV = uv * scale + bias (see Map12/Bilgewater).
                var lm = mesh.BakedLight;
                bool meshHasLm = !string.IsNullOrEmpty(lm.Texture)
                                 && view.TryGetAccessor(ElementName.Texcoord7, out var lmAcc);
                Vector2[]? meshLmUv = meshHasLm ? ReadVector2(view.GetAccessor(ElementName.Texcoord7), vc) : null;
                string lmTex = meshHasLm ? lm.Texture : "";
                if (meshHasLm) anyLightmap = true;

                var meshMin = new Vector3(float.MaxValue);
                var meshMax = new Vector3(float.MinValue);

                for (int i = 0; i < vc; i++)
                {
                    Vector3 p = Vector3.Transform(meshPos[i], transform);
                    positions.Add(p.X); positions.Add(p.Y); positions.Add(p.Z);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                    meshMin = Vector3.Min(meshMin, p);
                    meshMax = Vector3.Max(meshMax, p);

                    if (meshNrm is not null)
                    {
                        Vector3 n = Vector3.TransformNormal(meshNrm[i], normalMatrix);
                        float len = n.Length();
                        if (len > 1e-6f) n /= len;
                        normals.Add(n.X); normals.Add(n.Y); normals.Add(n.Z);
                    }
                    else { normals.Add(0f); normals.Add(1f); normals.Add(0f); }

                    if (meshUv is not null) { uvs.Add(meshUv[i].X); uvs.Add(meshUv[i].Y); }
                    else { uvs.Add(0f); uvs.Add(0f); }

                    if (meshCol is not null) { var c = meshCol[i]; colors.Add(c.X); colors.Add(c.Y); colors.Add(c.Z); colors.Add(c.W); }
                    else { colors.Add(1f); colors.Add(1f); colors.Add(1f); colors.Add(1f); }

                    if (meshLmUv is not null)
                    {
                        lightmapUvs.Add(meshLmUv[i].X * lm.Scale.X + lm.Bias.X);
                        lightmapUvs.Add(meshLmUv[i].Y * lm.Scale.Y + lm.Bias.Y);
                    }
                    else { lightmapUvs.Add(0f); lightmapUvs.Add(0f); }
                }

                var ia = mesh.Indices;
                int vis = (int)mesh.VisibilityFlags;
                uint ctrl = mesh.VisibilityControllerPathHash;
                string meshName = mesh.Name ?? "";

                // M33 per-mesh metadata for the inspector.
                var attrs = new List<string>();
                foreach (var en in AllElementNames)
                    if (view.TryGetAccessor(en, out _)) attrs.Add(en.ToString());
                string? slTex = mesh.StationaryLight.Texture is { Length: > 0 } s ? s : null;

                meshes.Add(new MapGeoMesh
                {
                    Index = meshIndex, Name = meshName, VertexStart = baseVertex, VertexCount = vc,
                    Transform = transform, VisibilityFlags = vis, ControllerHash = ctrl,
                    RegionHash = mesh.UnknownVersion18Int,
                    Pivot = vc > 0 ? (meshMin + meshMax) * 0.5f : transform.Translation,
                    IndexCount = ia.Count,
                    BoundsMin = vc > 0 ? meshMin : transform.Translation,
                    BoundsMax = vc > 0 ? meshMax : transform.Translation,
                    Attributes = attrs.ToArray(),
                    HasLightmapUv = view.TryGetAccessor(ElementName.Texcoord1, out _),
                    HasVertexColor = meshCol is not null,
                    RenderFlags = mesh.RenderFlags.ToString(),
                    DisableBackfaceCulling = mesh.DisableBackfaceCulling,
                    StationaryLightTexture = slTex,
                });
                if (mesh.Submeshes.Count > 0)
                {
                    foreach (var sub in mesh.Submeshes)
                    {
                        string material = sub.Material ?? "";
                        materials.Add(material);
                        int gStart = indices.Count;
                        int end = sub.StartIndex + sub.IndexCount;
                        for (int k = sub.StartIndex; k < end && k < ia.Count; k++)
                            indices.Add((uint)(ia[k] + baseVertex));
                        groups.Add(new MapGeoGroup(material, gStart, indices.Count - gStart, meshName, vis, ctrl, meshIndex, lmTex));
                    }
                }
                else
                {
                    int gStart = indices.Count;
                    for (int k = 0; k < ia.Count; k++)
                        indices.Add((uint)(ia[k] + baseVertex));
                    groups.Add(new MapGeoGroup("", gStart, indices.Count - gStart, meshName, vis, ctrl, meshIndex, lmTex));
                }

                meshCount++;
            }
            catch (Exception ex)
            {
                warnings.Add($"mesh '{mesh.Name}': {ex.Message}");
            }
        }

        if (positions.Count == 0) { min = Vector3.Zero; max = Vector3.Zero; }

        // M55: capture the scene bucket grid(s) — the culling grid League uses; one per visibility
        // controller. Kept as summary info + bounds for the outliner showcase + viewport overlay.
        var bucketGrids = new List<MapBucketGridInfo>();
        MapGeoSceneGraphSection.TryLocate(data, env, version, out var rawSceneGraphs);
        for (int sceneGraphIndex = 0; sceneGraphIndex < env.SceneGraphs.Count; sceneGraphIndex++)
        {
            var sg = env.SceneGraphs[sceneGraphIndex];
            try
            {
                // M77: capture the baked scene mesh so the viewport can draw the grid as REAL 3D wireframe
                // (a bucket grid is a simplified bake of the map — flat cell lines misrepresent it).
                // CRITICAL: the grid's index buffer is PER-BUCKET LOCAL — every index is relative to its
                // bucket's BaseVertex. Resolve through the bucket table to global vertex indices; reading
                // the flat list globally draws garbage fan lines across the whole map.
                var meshPositions = new float[sg.Vertices.Count * 3];
                for (int vi = 0; vi < sg.Vertices.Count; vi++)
                {
                    meshPositions[vi * 3 + 0] = sg.Vertices[vi].X;
                    meshPositions[vi * 3 + 1] = sg.Vertices[vi].Y;
                    meshPositions[vi * 3 + 2] = sg.Vertices[vi].Z;
                }
                var resolved = new List<int>(sg.Indices.Count);
                var bucketSpan = sg.Buckets.Span;
                for (int by = 0; by < bucketSpan.Height; by++)
                for (int bx = 0; bx < bucketSpan.Width; bx++)
                {
                    var bucket = bucketSpan[by, bx];
                    int faceCount = bucket.InsideFaceCount + bucket.StickingOutFaceCount;
                    for (int f = 0; f < faceCount; f++)
                    {
                        int i0 = (int)bucket.StartIndex + f * 3;
                        if (i0 + 2 >= sg.Indices.Count) break;
                        int a = (int)bucket.BaseVertex + sg.Indices[i0];
                        int b = (int)bucket.BaseVertex + sg.Indices[i0 + 1];
                        int c2 = (int)bucket.BaseVertex + sg.Indices[i0 + 2];
                        if (a >= sg.Vertices.Count || b >= sg.Vertices.Count || c2 >= sg.Vertices.Count) continue;
                        resolved.Add(a); resolved.Add(b); resolved.Add(c2);
                    }
                }
                var meshIndices = resolved.ToArray();

                bucketGrids.Add(new MapBucketGridInfo(
                    rawSceneGraphs?.Grids[sceneGraphIndex].ControllerHash ?? sg.VisibilityControllerPathHash,
                    sg.MinX, sg.MinZ, sg.MaxX, sg.MaxZ,
                    sg.BucketSizeX, sg.BucketSizeZ,
                    sg.Buckets.Width, sg.Buckets.Height,
                    sg.IsDisabled, sg.Vertices.Count, sg.Indices.Count,
                    rawSceneGraphs?.Grids[sceneGraphIndex].RegionHash ?? 0,
                    meshPositions, meshIndices));
            }
            catch { /* malformed grid: skip */ }
        }

        return new MapGeoAsset
        {
            BucketGrids = bucketGrids,
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            Uvs = uvs.ToArray(),
            Colors = anyColor ? colors.ToArray() : null,
            HasVertexColor = anyColor,
            LightmapUvs = anyLightmap ? lightmapUvs.ToArray() : null,
            HasLightmap = anyLightmap,
            Indices = indices.ToArray(),
            Groups = groups,
            Meshes = meshes,
            Version = version,
            VertexCount = positions.Count / 3,
            MeshCount = meshCount,
            MaterialCount = materials.Count,
            BoundsMin = min,
            BoundsMax = max,
            Warnings = warnings,
        };
    }

    // mapgeo attributes can be float32 OR packed 16-bit (half). Read whichever this accessor is.
    private static Vector3[] ReadVector3(VertexElementAccessor acc, int count)
    {
        var result = new Vector3[count];
        try
        {
            var arr = acc.AsVector3Array();
            for (int i = 0; i < count; i++) result[i] = arr[i];
        }
        catch
        {
            var arr = acc.AsXyzF16Array();
            for (int i = 0; i < count; i++)
            {
                var h = arr[i];
                result[i] = new Vector3((float)h.Item1, (float)h.Item2, (float)h.Item3);
            }
        }
        return result;
    }

    // PrimaryColor is BGRA_Packed8888 (bytes) — normalize to RGBA 0..1. Falls back to Vector4 float colors.
    private static Vector4[] ReadColor(VertexElementAccessor acc, int count)
    {
        var result = new Vector4[count];
        try
        {
            var arr = acc.AsBgraU8Array();
            for (int i = 0; i < count; i++)
            {
                var c = arr[i]; // (byte b, byte g, byte r, byte a)
                result[i] = new Vector4(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
            }
        }
        catch
        {
            try { var arr = acc.AsVector4Array(); for (int i = 0; i < count; i++) result[i] = arr[i]; }
            catch { for (int i = 0; i < count; i++) result[i] = Vector4.One; }
        }
        return result;
    }

    private static Vector2[] ReadVector2(VertexElementAccessor acc, int count)
    {
        var result = new Vector2[count];
        try
        {
            var arr = acc.AsVector2Array();
            for (int i = 0; i < count; i++) result[i] = arr[i];
        }
        catch
        {
            var arr = acc.AsXyF16Array();
            for (int i = 0; i < count; i++)
            {
                var h = arr[i];
                result[i] = new Vector2((float)h.Item1, (float)h.Item2);
            }
        }
        return result;
    }
}
