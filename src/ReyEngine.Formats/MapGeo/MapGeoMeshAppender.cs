using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ReyEngine.Formats.MapGeo;

/// <summary>A mesh to append to a mapgeo (M79). Positions/normals/uvs are LOCAL-space League coordinates
/// (Y up); the placement transform is stored in the mesh record, so the mesh stays gizmo-movable after a
/// reload. Indices are a triangle list in League winding (as decoded from .scb/.sco — no swap needed).</summary>
public sealed record NewMapMesh(
    string MaterialName,
    float[] Positions,          // xyz triplets
    float[]? Normals,           // xyz triplets (null → flat up)
    float[]? Uvs,               // uv pairs (null → zeros)
    ushort[] Indices,
    Matrix4x4 Transform);

/// <summary>
/// M79: appends new meshes to an existing .mapgeo by SURGICAL splice — the format is sequential with
/// count-prefixed arrays (no offset table), so we walk the sections to find four insertion points
/// (vertex declarations / vertex buffers / index buffers / mesh records), bump the four counts, splice
/// the new blocks in, and copy the bucket-grid + planar-reflector tail byte-for-byte. Field layout per
/// the MapgeoAddon spec (mapgeo_parser.py); supports versions 13–18. Never touches existing bytes.
/// </summary>
public static class MapGeoMeshAppender
{
    private const int MaxU16Vertices = 65535;   // mapgeo index buffers are u16 — hard cap per mesh

    public static byte[]? Append(byte[] mapgeo, IReadOnlyList<NewMapMesh> meshes, out string? error)
    {
        error = null;
        if (meshes.Count == 0) return mapgeo;
        try
        {
            var s = WalkSections(mapgeo);

            foreach (var m in meshes)
            {
                if (m.Positions.Length / 3 > MaxU16Vertices)
                { error = $"mesh has {m.Positions.Length / 3:n0} vertices — mapgeo index buffers are u16 (max {MaxU16Vertices:n0}). Split the mesh."; return null; }
                if (m.Indices.Length % 3 != 0)
                { error = "index count is not a multiple of 3."; return null; }
            }

            using var ms = new MemoryStream(mapgeo.Length + meshes.Sum(EstimateSize));
            var w = new BinaryWriter(ms);

            // ---- prefix up to the vertex-declaration COUNT, then patched count ----
            w.Write(mapgeo, 0, s.VertexDeclCountOffset);
            w.Write((uint)(s.VertexDeclCount + meshes.Count));
            // existing declarations
            w.Write(mapgeo, s.VertexDeclCountOffset + 4, s.VertexBufferCountOffset - (s.VertexDeclCountOffset + 4));
            // new declarations (one per mesh): POSITION f32x3, NORMAL f32x3, TEXCOORD0 f32x2; pad to 15
            foreach (var _ in meshes) WriteDeclaration(w);

            // ---- vertex buffers ----
            w.Write((uint)(s.VertexBufferCount + meshes.Count));
            w.Write(mapgeo, s.VertexBufferCountOffset + 4, s.IndexBufferCountOffset - (s.VertexBufferCountOffset + 4));
            foreach (var m in meshes) WriteVertexBuffer(w, m, s.Version);

            // ---- index buffers ----
            w.Write((uint)(s.IndexBufferCount + meshes.Count));
            w.Write(mapgeo, s.IndexBufferCountOffset + 4, s.MeshCountOffset - (s.IndexBufferCountOffset + 4));
            foreach (var m in meshes) WriteIndexBuffer(w, m, s.Version);

            // ---- mesh records ----
            w.Write((uint)(s.MeshCount + meshes.Count));
            w.Write(mapgeo, s.MeshCountOffset + 4, s.TailOffset - (s.MeshCountOffset + 4));
            for (int i = 0; i < meshes.Count; i++)
                WriteMeshRecord(w, meshes[i], s,
                    declId: s.VertexDeclCount + i,
                    vbId: s.VertexBufferCount + i,
                    ibId: s.IndexBufferCount + i);

            // ---- tail: bucket grids + planar reflectors, byte-exact ----
            w.Write(mapgeo, s.TailOffset, mapgeo.Length - s.TailOffset);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static int EstimateSize(NewMapMesh m) =>
        128 + 16 + m.Positions.Length / 3 * 32 + m.Indices.Length * 2 + 256 + m.MaterialName.Length;

    // ---- new-block writers (defaults per the MapgeoAddon export path) ----

    private static void WriteDeclaration(BinaryWriter w)
    {
        w.Write(0u);        // usage = Static
        w.Write(3u);        // elementCount
        w.Write(0u); w.Write(2u);    // POSITION,  XYZ_FLOAT32
        w.Write(2u); w.Write(2u);    // NORMAL,    XYZ_FLOAT32
        w.Write(7u); w.Write(1u);    // TEXCOORD0, XY_FLOAT32
        for (int i = 3; i < 15; i++) { w.Write(0u); w.Write(3u); }   // Riot pads name=POSITION, fmt=XYZW_FLOAT32
    }

    private static void WriteVertexBuffer(BinaryWriter w, NewMapMesh m, int version)
    {
        int n = m.Positions.Length / 3;
        if (version >= 13) w.Write((byte)0xFF);   // visibility: all layers
        w.Write((uint)(n * 32));                  // stride 32 = pos12 + normal12 + uv8
        for (int i = 0; i < n; i++)
        {
            w.Write(m.Positions[i * 3]); w.Write(m.Positions[i * 3 + 1]); w.Write(m.Positions[i * 3 + 2]);
            if (m.Normals is { } nr && nr.Length >= (i + 1) * 3)
            { w.Write(nr[i * 3]); w.Write(nr[i * 3 + 1]); w.Write(nr[i * 3 + 2]); }
            else { w.Write(0f); w.Write(1f); w.Write(0f); }
            if (m.Uvs is { } uv && uv.Length >= (i + 1) * 2)
            { w.Write(uv[i * 2]); w.Write(uv[i * 2 + 1]); }
            else { w.Write(0f); w.Write(0f); }
        }
    }

    private static void WriteIndexBuffer(BinaryWriter w, NewMapMesh m, int version)
    {
        if (version >= 13) w.Write((byte)0xFF);
        w.Write((uint)(m.Indices.Length * 2));
        foreach (var i in m.Indices) w.Write(i);
    }

    private static void WriteMeshRecord(BinaryWriter w, NewMapMesh m, Sections s, int declId, int vbId, int ibId)
    {
        int n = m.Positions.Length / 3;
        w.Write((uint)n);                 // vertexCount
        w.Write(1u);                      // vertexDeclarationCount (single stream)
        w.Write((uint)declId);            // vertexDeclarationID
        w.Write((uint)vbId);              // vertexBufferIDs[1]
        w.Write((uint)m.Indices.Length);  // indexCount
        w.Write((uint)ibId);              // indexBufferID
        if (s.Version >= 13) w.Write((byte)0xFF);   // visibility: all dragon layers
        if (s.Version >= 18) w.Write(0u);           // renderRegionHash: none
        if (s.Version >= 15) w.Write(0u);           // visibilityControllerPathHash: none

        // one primitive covering everything; materialHash stays 0 (game hashes the name at load)
        w.Write(1u);
        w.Write(0u);
        var nameBytes = Encoding.ASCII.GetBytes(m.MaterialName);
        w.Write((uint)nameBytes.Length); w.Write(nameBytes);
        w.Write(0u);                      // startIndex
        w.Write((uint)m.Indices.Length);  // indexCount
        uint minV = uint.MaxValue, maxV = 0;
        foreach (var i in m.Indices) { if (i < minV) minV = i; if (i > maxV) maxV = i; }
        w.Write(minV == uint.MaxValue ? 0u : minV);
        w.Write(maxV);

        w.Write((byte)0);                 // disableBackfaceCulling = false

        // local AABB
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i + 2 < m.Positions.Length; i += 3)
        {
            minX = MathF.Min(minX, m.Positions[i]); maxX = MathF.Max(maxX, m.Positions[i]);
            minY = MathF.Min(minY, m.Positions[i + 1]); maxY = MathF.Max(maxY, m.Positions[i + 1]);
            minZ = MathF.Min(minZ, m.Positions[i + 2]); maxZ = MathF.Max(maxZ, m.Positions[i + 2]);
        }
        w.Write(minX); w.Write(minY); w.Write(minZ);
        w.Write(maxX); w.Write(maxY); w.Write(maxZ);

        // 4x4 transform, COLUMN-major (translation at floats 12..14)
        var t = m.Transform;
        w.Write(t.M11); w.Write(t.M12); w.Write(t.M13); w.Write(t.M14);
        w.Write(t.M21); w.Write(t.M22); w.Write(t.M23); w.Write(t.M24);
        w.Write(t.M31); w.Write(t.M32); w.Write(t.M33); w.Write(t.M34);
        w.Write(t.M41); w.Write(t.M42); w.Write(t.M43); w.Write(t.M44);

        w.Write((byte)31);                // quality: all 5 levels
        if (s.Version >= 14)
        {
            w.Write((byte)0);             // layerTransitionBehavior
            if (s.Version >= 16) w.Write((ushort)0); else w.Write((byte)0);   // renderFlags
        }
        else if (s.Version >= 11) w.Write((byte)0);

        WriteEmptyLightChannel(w);        // BAKED_LIGHT   (empty texture, scale 1,1 bias 0,0)
        WriteEmptyLightChannel(w);        // STATIONARY_LIGHT
        if (s.Version is >= 12 and < 17) WriteEmptyLightChannel(w);   // BAKED_PAINT (older layout)
        if (s.Version >= 17)
        {
            w.Write(0u);                  // textureOverrideCount
            w.Write(1f); w.Write(1f);     // bakedPaintScale
            w.Write(0f); w.Write(0f);     // bakedPaintBias
        }
    }

    private static void WriteEmptyLightChannel(BinaryWriter w)
    {
        w.Write(0u);                      // no texture
        w.Write(1f); w.Write(1f);         // scale — Riot's empty channel is (1,1), NOT zeros
        w.Write(0f); w.Write(0f);         // bias
    }

    // ---- section walker: locate the four count fields + the tail (bucket grids onward) ----

    private sealed record Sections(int Version,
        int VertexDeclCountOffset, int VertexDeclCount,
        int VertexBufferCountOffset, int VertexBufferCount,
        int IndexBufferCountOffset, int IndexBufferCount,
        int MeshCountOffset, int MeshCount,
        int TailOffset);

    private static Sections WalkSections(byte[] d)
    {
        int p = 0;
        if (d.Length < 8 || d[0] != 'O' || d[1] != 'E' || d[2] != 'G' || d[3] != 'M')
            throw new InvalidDataException("not a mapgeo (OEGM magic missing).");
        p = 4;
        int version = ReadI32(d, ref p);
        if (version is < 13 or > 18)
            throw new InvalidDataException($"mapgeo version {version} not supported for mesh append (13–18 only).");

        // sampler defs
        if (version >= 17)
        {
            int c = ReadI32(d, ref p);
            for (int i = 0; i < c; i++) { p += 4; SkipString(d, ref p); }
        }
        else { SkipString(d, ref p); SkipString(d, ref p); }   // v13–16: two bare strings

        // vertex declarations (fixed 128 bytes each)
        int declCountOff = p;
        int declCount = ReadI32(d, ref p);
        p += declCount * 128;

        // vertex buffers
        int vbCountOff = p;
        int vbCount = ReadI32(d, ref p);
        for (int i = 0; i < vbCount; i++) { p += 1; int size = ReadI32(d, ref p); p += size; }   // u8 visibility (v>=13)

        // index buffers
        int ibCountOff = p;
        int ibCount = ReadI32(d, ref p);
        for (int i = 0; i < ibCount; i++) { p += 1; int size = ReadI32(d, ref p); p += size; }

        // mesh records
        int meshCountOff = p;
        int meshCount = ReadI32(d, ref p);
        for (int i = 0; i < meshCount; i++) SkipMeshRecord(d, ref p, version);

        return new Sections(version, declCountOff, declCount, vbCountOff, vbCount,
            ibCountOff, ibCount, meshCountOff, meshCount, TailOffset: p);
    }

    private static void SkipMeshRecord(byte[] d, ref int p, int version)
    {
        p += 4;                                   // vertexCount
        int vdCount = ReadI32(d, ref p);          // vertexDeclarationCount
        p += 4;                                   // vertexDeclarationID
        p += vdCount * 4;                         // vertexBufferIDs
        p += 4;                                   // indexCount
        p += 4;                                   // indexBufferID
        p += 1;                                   // visibility (v>=13)
        if (version >= 18) p += 4;                // renderRegionHash
        if (version >= 15) p += 4;                // visibilityControllerPathHash

        int prims = ReadI32(d, ref p);
        for (int i = 0; i < prims; i++)
        {
            p += 4;                               // materialHash
            SkipString(d, ref p);                 // materialName
            p += 16;                              // startIndex, indexCount, minVertex, maxVertex
        }

        p += 1;                                   // disableBackfaceCulling
        p += 24;                                  // AABB min/max
        p += 64;                                  // 4x4 transform
        p += 1;                                   // quality
        if (version >= 14)
        {
            p += 1;                               // layerTransitionBehavior
            p += version >= 16 ? 2 : 1;           // renderFlags u16 / u8
        }
        else p += 1;                              // v13: renderFlags u8

        SkipLightChannel(d, ref p);               // baked light
        SkipLightChannel(d, ref p);               // stationary light
        if (version is >= 12 and < 17) SkipLightChannel(d, ref p);   // baked paint (older layout)
        if (version >= 17)
        {
            int overrides = ReadI32(d, ref p);
            for (int i = 0; i < overrides; i++) { p += 4; SkipString(d, ref p); }
            p += 16;                              // bakedPaintScale + bias
        }
    }

    private static void SkipLightChannel(byte[] d, ref int p)
    {
        int len = ReadI32(d, ref p);
        p += len + 16;                            // texture bytes + scale/bias floats
    }

    private static void SkipString(byte[] d, ref int p)
    {
        int len = ReadI32(d, ref p);
        if (len < 0 || p + len > d.Length) throw new InvalidDataException("string overruns file (walker desynced).");
        p += len;
    }

    private static int ReadI32(byte[] d, ref int p)
    {
        int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p, 4));
        p += 4;
        return v;
    }
}
