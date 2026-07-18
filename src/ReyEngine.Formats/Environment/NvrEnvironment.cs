using System.Numerics;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Memory;
using ReyEngine.Formats.Meshes;

namespace ReyEngine.Formats.Environment;

/// <summary>
/// M88: loads an old-format League map room (<c>room.nvr</c>) as static, render-ready geometry for use
/// as a character-preview backdrop. LeagueToolkit exposes NVR through <see cref="SimpleEnvironment"/>,
/// which decodes it into the same <see cref="EnvironmentAsset"/> model as modern .mapgeo — so this reuses
/// the exact vertex-reading logic <c>MapGeoDecoder</c> uses. In NVR the diffuse texture is stored in the
/// mesh's <see cref="EnvironmentAssetMesh.StationaryLight"/> channel (channel 0), not a material library.
/// Never throws on malformed meshes — bad meshes are skipped and recorded in <see cref="Warnings"/>.
/// </summary>
public sealed class NvrEnvironment
{
    /// <summary>Combined world-space geometry: one <see cref="SubMeshInfo"/> per NVR submesh (material).</summary>
    public required MeshAsset Mesh { get; init; }

    /// <summary>Per-submesh diffuse (COLOR_MAP_0) texture file name (aligned to <see cref="MeshAsset.SubMeshes"/>);
    /// null when a submesh has none. Resolve against the map's <c>Scene/Textures/</c> folder.</summary>
    public required IReadOnlyList<string?> SubmeshDiffuseTextures { get; init; }

    /// <summary>M89: four-blend ground layers (CREATE_GROUND_MOSAIC_FOUR_BLEND). Blend is a packed RGB mask
    /// on the mesh's 2nd UV; Color1/2/3 are COLOR_MAP_1/2/3 blended over the diffuse by the mask's r/g/b.
    /// All null on non-ground surfaces. Aligned to submeshes.</summary>
    public required IReadOnlyList<string?> SubmeshBlendTextures { get; init; }
    public required IReadOnlyList<string?> SubmeshColor1Textures { get; init; }
    public required IReadOnlyList<string?> SubmeshColor2Textures { get; init; }
    public required IReadOnlyList<string?> SubmeshColor3Textures { get; init; }

    /// <summary>Per-submesh "disable backface culling" flag (aligned to submeshes).</summary>
    public required IReadOnlyList<bool> SubmeshDoubleSided { get; init; }

    public required int MeshCount { get; init; }
    public required int MaterialCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Parse an NVR file's bytes into render-ready geometry.</summary>
    public static NvrEnvironment Load(byte[] nvrBytes)
    {
        using var ms = new MemoryStream(nvrBytes, writable: false);
        var env = SimpleEnvironment.Load(ms);

        // M89: SimpleEnvironment only surfaces one texture per material, but ground materials are
        // CREATE_GROUND_MOSAIC_FOUR_BLEND (5 textures). Parse the raw material block for the full set.
        var matTex = ParseMaterials(nvrBytes);

        var positions = new List<float>();
        var normals = new List<float>();
        var uvs = new List<float>();
        var blendUvs = new List<float>();   // 2nd UV set (Texcoord7) — the four-blend mask UV
        bool anyBlendUv = false;
        var colors = new List<float>();   // PrimaryColor — NVR bakes ground shading/AO here
        bool anyColor = false;
        var indices = new List<uint>();
        var subMeshes = new List<SubMeshInfo>();
        var subDiffuse = new List<string?>();
        var subBlend = new List<string?>();
        var subColor1 = new List<string?>();
        var subColor2 = new List<string?>();
        var subColor3 = new List<string?>();
        var subDoubleSided = new List<bool>();
        var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int meshCount = 0;

        foreach (var mesh in env.Meshes)
        {
            try
            {
                var view = mesh.VerticesView;
                int vc = view.VertexCount;
                if (vc == 0) continue;
                int baseVertex = positions.Count / 3;
                var transform = mesh.Transform;
                var normalMatrix = Matrix4x4.Invert(transform, out var invT)
                    ? Matrix4x4.Transpose(invT) : transform;

                var meshPos = ReadVector3(view.GetAccessor(ElementName.Position), vc);
                var meshNrm = view.TryGetAccessor(ElementName.Normal, out var nAcc) ? ReadVector3(nAcc, vc) : null;
                var meshUv = view.TryGetAccessor(ElementName.Texcoord0, out var tAcc) ? ReadVector2(tAcc, vc) : null;
                var meshCol = view.TryGetAccessor(ElementName.PrimaryColor, out var cAcc) ? ReadColor(cAcc, vc) : null;
                if (meshCol is not null) anyColor = true;
                var meshBlendUv = view.TryGetAccessor(ElementName.Texcoord7, out var bAcc) ? ReadVector2(bAcc, vc) : null;
                if (meshBlendUv is not null) anyBlendUv = true;

                for (int i = 0; i < vc; i++)
                {
                    Vector3 p = Vector3.Transform(meshPos[i], transform);
                    positions.Add(p.X); positions.Add(p.Y); positions.Add(p.Z);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);

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
                    else { colors.Add(0f); colors.Add(0f); colors.Add(0f); colors.Add(1f); }

                    if (meshBlendUv is not null) { blendUvs.Add(meshBlendUv[i].X); blendUvs.Add(meshBlendUv[i].Y); }
                    else { blendUvs.Add(0f); blendUvs.Add(0f); }
                }

                var ia = mesh.Indices;
                bool doubleSided = mesh.DisableBackfaceCulling;
                string fallbackDiffuse = mesh.StationaryLight.Texture is { Length: > 0 } d ? d : "";

                void AddSub(string material, int gStart)
                {
                    // material name is "NVRMaterial_<rawName>" — the raw block is keyed by <rawName>.
                    var mat = matTex.GetValueOrDefault(StripPrefix(material));
                    subMeshes.Add(new SubMeshInfo(material, gStart, indices.Count - gStart, vc));
                    subDiffuse.Add(Nz(mat?.Base) ?? Nz(fallbackDiffuse));
                    subBlend.Add(Nz(mat?.Blend));
                    subColor1.Add(Nz(mat?.Color1));
                    subColor2.Add(Nz(mat?.Color2));
                    subColor3.Add(Nz(mat?.Color3));
                    subDoubleSided.Add(doubleSided);
                }

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
                        AddSub(material, gStart);
                    }
                }
                else
                {
                    int gStart = indices.Count;
                    for (int k = 0; k < ia.Count; k++)
                        indices.Add((uint)(ia[k] + baseVertex));
                    AddSub("", gStart);
                }

                meshCount++;
            }
            catch (Exception ex) { warnings.Add($"mesh '{mesh.Name}': {ex.Message}"); }
        }

        if (positions.Count == 0) { min = Vector3.Zero; max = Vector3.Zero; }

        var asset = new MeshAsset
        {
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            Uvs = uvs.ToArray(),
            Colors = anyColor ? colors.ToArray() : null,
            LightmapUvs = anyBlendUv ? blendUvs.ToArray() : null,   // 2nd UV set = four-blend mask UV
            Indices = indices.ToArray(),
            SubMeshes = subMeshes,
            VertexCount = positions.Count / 3,
            BoundsMin = min,
            BoundsMax = max,
        };

        return new NvrEnvironment
        {
            Mesh = asset,
            SubmeshDiffuseTextures = subDiffuse,
            SubmeshBlendTextures = subBlend,
            SubmeshColor1Textures = subColor1,
            SubmeshColor2Textures = subColor2,
            SubmeshColor3Textures = subColor3,
            SubmeshDoubleSided = subDoubleSided,
            MeshCount = meshCount,
            MaterialCount = materials.Count,
            Warnings = warnings,
        };
    }

    private sealed record NvrMaterial(string Base, string Blend, string Color1, string Color2, string Color3);

    private static string? Nz(string? s) => string.IsNullOrEmpty(s) ? null : s;
    private static string StripPrefix(string material) =>
        material.StartsWith("NVRMaterial_", StringComparison.OrdinalIgnoreCase) ? material.Substring(12) : material;

    /// <summary>M89: parse the raw NVR material block for the full texture set per material. Ground materials
    /// (CREATE_GROUND_MOSAIC_FOUR_BLEND) use channels 0 (base), 1 (blend map), 2/4/6 (colour maps 1/2/3) —
    /// the same channel→sampler mapping the HLSL uses (s0/s1/s2/s4/s6). NVR v9.x layout; empty on mismatch.</summary>
    private static Dictionary<string, NvrMaterial> ParseMaterials(byte[] b)
    {
        var map = new Dictionary<string, NvrMaterial>(StringComparer.OrdinalIgnoreCase);
        // header: "NVR\0" (4) + version major/minor (2+2) + 5 counts (int each) = 28 bytes.
        if (b.Length < 28 || b[0] != (byte)'N' || b[1] != (byte)'V' || b[2] != (byte)'R') return map;
        ushort major = BitConverter.ToUInt16(b, 4);
        if (major is < 8 or > 9) return map;   // only the record layout we've verified (v8/v9)
        int matCount = BitConverter.ToInt32(b, 8);
        if (matCount is <= 0 or > 100000) return map;
        const int blockStart = 28, stride = 2988, nameLen = 260, chTexOff = 284, chStride = 340;
        for (int m = 0; m < matCount; m++)
        {
            int rec = blockStart + m * stride;
            if (rec + stride > b.Length) break;
            string name = ReadCStr(b, rec, nameLen);
            if (name.Length == 0) continue;
            string Ch(int c) => ReadCStr(b, rec + chTexOff + c * chStride, 256);
            map[name] = new NvrMaterial(Ch(0), Ch(1), Ch(2), Ch(4), Ch(6));
        }
        return map;
    }

    private static string ReadCStr(byte[] b, int off, int max)
    {
        if (off < 0 || off >= b.Length) return "";
        int end = off;
        int limit = Math.Min(b.Length, off + max);
        while (end < limit && b[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(b, off, end - off);
    }

    // ---- vertex accessor readers (float32 or packed-half), mirrors MapGeoDecoder ----
    private static Vector3[] ReadVector3(VertexElementAccessor acc, int count)
    {
        var result = new Vector3[count];
        try { var arr = acc.AsVector3Array(); for (int i = 0; i < count; i++) result[i] = arr[i]; }
        catch
        {
            var arr = acc.AsXyzF16Array();
            for (int i = 0; i < count; i++) { var h = arr[i]; result[i] = new Vector3((float)h.Item1, (float)h.Item2, (float)h.Item3); }
        }
        return result;
    }

    private static Vector2[] ReadVector2(VertexElementAccessor acc, int count)
    {
        var result = new Vector2[count];
        try { var arr = acc.AsVector2Array(); for (int i = 0; i < count; i++) result[i] = arr[i]; }
        catch
        {
            var arr = acc.AsXyF16Array();
            for (int i = 0; i < count; i++) { var h = arr[i]; result[i] = new Vector2((float)h.Item1, (float)h.Item2); }
        }
        return result;
    }

    // PrimaryColor is BGRA_Packed8888 (bytes) — normalize to RGBA 0..1. Falls back to float colors.
    private static Vector4[] ReadColor(VertexElementAccessor acc, int count)
    {
        var result = new Vector4[count];
        try
        {
            var arr = acc.AsBgraU8Array();
            for (int i = 0; i < count; i++) { var c = arr[i]; result[i] = new Vector4(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f); }
        }
        catch
        {
            try { var arr = acc.AsVector4Array(); for (int i = 0; i < count; i++) result[i] = arr[i]; }
            catch { for (int i = 0; i < count; i++) result[i] = Vector4.One; }
        }
        return result;
    }
}
