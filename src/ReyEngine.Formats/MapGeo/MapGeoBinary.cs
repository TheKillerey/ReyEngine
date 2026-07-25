using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ReyEngine.Formats.MapGeo;

/// <summary>M159 (Phase 0): a byte-exact, EDITABLE model of a League .mapgeo (OEGM) — the read/write
/// substrate the lightmap baker needs to add UV2 channels and lightmap references to a map.
///
/// Why this exists: LeagueToolkit can READ mapgeo but its WRITER is broken at every shipping version —
/// it silently downgrades v18→v17 and produces files that no longer reopen (verified against Map11 and
/// Map12). So to change a mapgeo at all we need our own writer, and to change it SAFELY we need one that
/// round-trips real files byte-for-byte. This one does: 200/200 shipped mapgeos across v13–v18 re-emit
/// bit-identical.
///
/// Only the sections we edit are modelled in full (vertex declarations, vertex buffers, meshes).
/// Everything after the meshes — scene graphs and planar reflectors — is captured as an opaque verbatim
/// tail. That is SAFE because no persisted field references a byte offset into the earlier sections:
/// meshes reference buffers by index, buffer offsets are recomputed on read. So growing the vertex-buffer
/// section can't invalidate the tail.
///
/// Format facts pinned from LeagueToolkit's reader + real bytes:
///  - strings: int32 length + UTF-8 bytes.
///  - a vertex declaration is a FIXED 128 bytes: usage(u32) + elementCount(u32) + elements + junk padding
///    to 15 slots. The padding is NOT zeroed in shipped files, so it is captured verbatim for round-trip.
///  - a mesh's buffer i is described by declarations[declBaseId + i] — a mesh's buffers use a CONSECUTIVE
///    run of declarations. This is the key to adding a channel: append a cloned run + a new uv7
///    declaration, and repoint the mesh's base id.</summary>
public sealed class MapGeoBinary
{
    public int Version;
    public List<ShaderOverride> ShaderOverrides = new();       // v17+
    public List<string> LegacyBakedStrings = new();            // v9–16 pre-declaration strings
    public List<VertexDeclaration> Declarations = new();
    public List<VertexBuffer> VertexBuffers = new();
    public List<IndexBuffer> IndexBuffers = new();
    public List<Mesh> Meshes = new();
    public byte[] Tail = Array.Empty<byte>();                  // scene graphs + planar reflectors, verbatim

    // ---- element enums (values match LeagueToolkit's ElementName / ElementFormat) ----
    public const uint ElemPosition = 0, ElemNormal = 2, ElemPrimaryColor = 4,
                      ElemTexcoord0 = 7, ElemTexcoord7 = 14;
    /// <summary>The lightmap UV channel. Riot stores baked-lightmap atlas UVs here.</summary>
    public const uint LightmapUvElement = ElemTexcoord7;

    public const uint FmtX_Float32 = 0, FmtXY_Float32 = 1, FmtXYZ_Float32 = 2, FmtXYZW_Float32 = 3,
                      FmtBGRA_Packed8888 = 4;

    public static int FormatSize(uint format) => format switch
    {
        FmtX_Float32 => 4, FmtXY_Float32 => 8, FmtXYZ_Float32 => 12, FmtXYZW_Float32 => 16,
        FmtBGRA_Packed8888 => 4, 5 => 4, 6 => 4, 7 => 4, _ => 0,
    };

    public sealed class ShaderOverride { public int Index; public string Name = ""; }
    public sealed class VertexDeclaration
    {
        public uint Usage;
        public List<(uint Name, uint Format)> Elements = new();
        public byte[] Padding = Array.Empty<byte>();
        public int Stride => Elements.Sum(e => FormatSize(e.Format));
        public bool Has(uint element) => Elements.Any(e => e.Name == element);
    }
    public sealed class VertexBuffer { public byte Visibility; public bool HasVisibility; public byte[] Data = Array.Empty<byte>(); }
    public sealed class IndexBuffer { public byte Visibility; public bool HasVisibility; public byte[] Data = Array.Empty<byte>(); }
    public sealed class Channel { public string Texture = ""; public Vector2 Scale; public Vector2 Bias; }
    public sealed class Submesh { public uint Hash; public string Material = ""; public int StartIndex, IndexCount, MinVertex, MaxVertex; }

    public sealed class Mesh
    {
        public int VertexCount;
        /// <summary>Base declaration id; buffer <c>i</c> uses <c>Declarations[VertexDeclarationBase + i]</c>.</summary>
        public int VertexDeclarationBase;
        public List<int> VertexBufferIds = new();
        public int IndexCount;
        public int IndexBufferId;
        public byte Visibility; public bool HasVisibility;
        public uint RegionHash; public bool HasRegionHash;
        public uint VisibilityControllerPathHash; public bool HasVcHash;
        public List<Submesh> Submeshes = new();
        public bool DisableBackfaceCulling; public bool HasDisableBackface;
        public Vector3 BoundsMin, BoundsMax;
        public Matrix4x4 Transform;
        public byte QualityFilter;
        public byte LayerTransition; public bool HasLayerTransition;
        public ushort RenderFlags; public bool RenderFlagsIsUshort;
        public Channel BakedLight = new();
        public Channel StationaryLight = new();
        public Channel BakedPaint = new(); public bool HasBakedPaintChannel;   // v12–16 only
        public List<ShaderOverride> TextureOverrides = new();                  // v17+
        public Vector2 BakedPaintScale, BakedPaintBias;                        // v17+
    }

    // ---- editing helpers ----

    /// <summary>Does any of the mesh's buffers carry the lightmap UV channel?</summary>
    public bool MeshHasLightmapUv(Mesh mesh)
    {
        for (int i = 0; i < mesh.VertexBufferIds.Count; i++)
            if (Declarations[mesh.VertexDeclarationBase + i].Has(LightmapUvElement)) return true;
        return false;
    }

    /// <summary>Set a mesh's baked-lightmap reference (atlas path + the scale/bias that maps its chart
    /// UVs into that atlas). No buffer surgery — just the channel.</summary>
    public void SetBakedLight(Mesh mesh, string texture, Vector2 scale, Vector2 bias) =>
        mesh.BakedLight = new Channel { Texture = texture, Scale = scale, Bias = bias };

    /// <summary>Add a lightmap UV channel (Texcoord7) to a mesh that lacks one, exactly as Riot lays it
    /// out: a new secondary vertex buffer holding one XY_Float32 per vertex, described by a new
    /// declaration appended right after a clone of the mesh's existing declaration run (buffers must map
    /// to a CONSECUTIVE declaration run). Also sets the BakedLight reference.
    ///
    /// <paramref name="uv"/> length must equal the mesh's vertex count; the UVs are the chart-space [0,1]
    /// coordinates, and <paramref name="scale"/>/<paramref name="bias"/> place that chart in the atlas.
    /// Returns the id of the new uv7 buffer (so callers sharing a buffer across instances can reuse it).</summary>
    public int AddLightmapChannel(Mesh mesh, ReadOnlySpan<Vector2> uv, string texture, Vector2 scale, Vector2 bias)
    {
        if (uv.Length != mesh.VertexCount)
            throw new ArgumentException($"uv length {uv.Length} != vertex count {mesh.VertexCount}", nameof(uv));
        if (MeshHasLightmapUv(mesh))
            throw new InvalidOperationException("mesh already has a lightmap UV channel");

        int newBufId = AddUv7Buffer(uv, mesh.VertexBufferIds.Count > 0 ? VertexBuffers[mesh.VertexBufferIds[0]] : null);
        RewireWithUv7Buffer(mesh, newBufId);
        SetBakedLight(mesh, texture, scale, bias);
        return newBufId;
    }

    /// <summary>Attach an EXISTING uv7 buffer (from a previous <see cref="AddLightmapChannel"/>) to
    /// another mesh that shares the same geometry/vertex layout — the instancing case, where one unwrapped
    /// buffer serves many meshes and only the per-mesh scale/bias differs.</summary>
    public void AttachSharedLightmapChannel(Mesh mesh, int uv7BufferId, string texture, Vector2 scale, Vector2 bias)
    {
        if (MeshHasLightmapUv(mesh))
            throw new InvalidOperationException("mesh already has a lightmap UV channel");
        RewireWithUv7Buffer(mesh, uv7BufferId);
        SetBakedLight(mesh, texture, scale, bias);
    }

    private int AddUv7Buffer(ReadOnlySpan<Vector2> uv, VertexBuffer? proto)
    {
        var data = new byte[uv.Length * 8];
        for (int i = 0; i < uv.Length; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * 8), uv[i].X);
            BitConverter.TryWriteBytes(data.AsSpan(i * 8 + 4), uv[i].Y);
        }
        int id = VertexBuffers.Count;
        VertexBuffers.Add(new VertexBuffer
        {
            Data = data,
            HasVisibility = proto?.HasVisibility ?? Version >= 13,
            Visibility = proto?.Visibility ?? 0xFF,
        });
        return id;
    }

    private void RewireWithUv7Buffer(Mesh mesh, int uv7BufferId)
    {
        int n = mesh.VertexBufferIds.Count;
        // Append a fresh consecutive declaration run: clones of the mesh's current declarations, then a
        // uv7 declaration. Repoint the mesh's base id at the clones so buffer n resolves to the uv7 decl.
        int newBase = Declarations.Count;
        for (int i = 0; i < n; i++)
        {
            var src = Declarations[mesh.VertexDeclarationBase + i];
            Declarations.Add(new VertexDeclaration { Usage = src.Usage, Elements = new(src.Elements), Padding = (byte[])src.Padding.Clone() });
        }
        Declarations.Add(new VertexDeclaration
        {
            Usage = 0,
            Elements = { (LightmapUvElement, FmtXY_Float32) },
            Padding = new byte[8 * 14],   // 14 unused element slots
        });
        mesh.VertexDeclarationBase = newBase;
        mesh.VertexBufferIds.Add(uv7BufferId);
    }

    /// <summary>Drop vertex/index buffers and declarations no mesh references any more, remapping the
    /// ids that survive. Rebuilding geometry (see MapGeoLightmapBuilder) leaves the originals orphaned —
    /// without this the file roughly DOUBLES, since the old buffers are still serialised.</summary>
    public void Compact()
    {
        var usedVb = new HashSet<int>();
        var usedIb = new HashSet<int>();
        var usedDecl = new HashSet<int>();
        foreach (var m in Meshes)
        {
            foreach (var v in m.VertexBufferIds) usedVb.Add(v);
            usedIb.Add(m.IndexBufferId);
            for (int i = 0; i < m.VertexBufferIds.Count; i++) usedDecl.Add(m.VertexDeclarationBase + i);
        }

        // A mesh resolves buffer i through declarations[base + i], so a declaration run must stay
        // CONTIGUOUS. Keep whole runs, and remap each mesh's base to where its run lands.
        var declKeep = new List<int>();
        var declNewBase = new Dictionary<int, int>();
        foreach (var m in Meshes.OrderBy(m => m.VertexDeclarationBase))
        {
            if (declNewBase.ContainsKey(m.VertexDeclarationBase)) continue;
            declNewBase[m.VertexDeclarationBase] = declKeep.Count;
            for (int i = 0; i < m.VertexBufferIds.Count; i++) declKeep.Add(m.VertexDeclarationBase + i);
        }

        var vbKeep = usedVb.OrderBy(i => i).ToList();
        var ibKeep = usedIb.OrderBy(i => i).ToList();
        var vbMap = vbKeep.Select((old, n) => (old, n)).ToDictionary(t => t.old, t => t.n);
        var ibMap = ibKeep.Select((old, n) => (old, n)).ToDictionary(t => t.old, t => t.n);

        var newVb = vbKeep.Select(i => VertexBuffers[i]).ToList();
        var newIb = ibKeep.Select(i => IndexBuffers[i]).ToList();
        var newDecl = declKeep.Select(i => Declarations[i]).ToList();

        foreach (var m in Meshes)
        {
            for (int i = 0; i < m.VertexBufferIds.Count; i++) m.VertexBufferIds[i] = vbMap[m.VertexBufferIds[i]];
            m.IndexBufferId = ibMap[m.IndexBufferId];
            m.VertexDeclarationBase = declNewBase[m.VertexDeclarationBase];
        }

        VertexBuffers = newVb;
        IndexBuffers = newIb;
        Declarations = newDecl;
    }

    // ---- read ----

    public static MapGeoBinary Read(byte[] data)
    {
        var r = new Reader(data);
        var m = new MapGeoBinary();
        if (Encoding.ASCII.GetString(r.Bytes(4)) != "OEGM") throw new InvalidDataException("not a mapgeo (bad magic)");
        m.Version = r.I32();
        int v = m.Version;
        if (v is < 5 or > 18) throw new InvalidDataException($"unsupported mapgeo version {v}");

        if (v >= 17)
        {
            int cnt = r.I32();
            for (int i = 0; i < cnt; i++) m.ShaderOverrides.Add(new ShaderOverride { Index = r.I32(), Name = r.Str() });
        }
        else
        {
            if (v >= 11) m.LegacyBakedStrings.Add(r.Str());
            if (v >= 9) m.LegacyBakedStrings.Add(r.Str());
        }

        uint declCount = r.U32();
        for (int i = 0; i < declCount; i++)
        {
            var d = new VertexDeclaration { Usage = r.U32() };
            uint elemCount = r.U32();
            for (int e = 0; e < elemCount; e++) d.Elements.Add((r.U32(), r.U32()));
            d.Padding = r.Bytes(8 * (int)(15 - elemCount));
            m.Declarations.Add(d);
        }

        uint vbCount = r.U32();
        for (int i = 0; i < vbCount; i++)
        {
            var b = new VertexBuffer();
            if (v >= 13) { b.Visibility = r.U8(); b.HasVisibility = true; }
            b.Data = r.Bytes((int)r.U32());
            m.VertexBuffers.Add(b);
        }

        uint ibCount = r.U32();
        for (int i = 0; i < ibCount; i++)
        {
            var b = new IndexBuffer();
            if (v >= 13) { b.Visibility = r.U8(); b.HasVisibility = true; }
            b.Data = r.Bytes(r.I32());
            m.IndexBuffers.Add(b);
        }

        uint meshCount = r.U32();
        for (int i = 0; i < meshCount; i++) m.Meshes.Add(ReadMesh(r, v));

        m.Tail = r.Rest();
        return m;
    }

    private static Mesh ReadMesh(Reader r, int v)
    {
        var m = new Mesh { VertexCount = r.I32() };
        uint bufCount = r.U32();
        m.VertexDeclarationBase = r.I32();
        for (int i = 0; i < bufCount; i++) m.VertexBufferIds.Add(r.I32());
        m.IndexCount = r.I32();
        m.IndexBufferId = r.I32();
        if (v >= 13) { m.Visibility = r.U8(); m.HasVisibility = true; }
        if (v >= 18) { m.RegionHash = r.U32(); m.HasRegionHash = true; }
        if (v >= 15) { m.VisibilityControllerPathHash = r.U32(); m.HasVcHash = true; }
        uint subCount = r.U32();
        for (int i = 0; i < subCount; i++)
            m.Submeshes.Add(new Submesh { Hash = r.U32(), Material = r.Str(), StartIndex = r.I32(),
                                          IndexCount = r.I32(), MinVertex = r.I32(), MaxVertex = r.I32() });
        if (v != 5) { m.DisableBackfaceCulling = r.U8() != 0; m.HasDisableBackface = true; }
        m.BoundsMin = r.Vec3(); m.BoundsMax = r.Vec3();
        m.Transform = r.Mat4();
        m.QualityFilter = r.U8();
        if (v is >= 7 and <= 12) { m.Visibility = r.U8(); m.HasVisibility = true; }
        if (v >= 14) { m.LayerTransition = r.U8(); m.HasLayerTransition = true; }
        if (v is >= 11 and <= 13) { m.RenderFlags = r.U8(); m.RenderFlagsIsUshort = false; }
        else if (v >= 14) { if (v >= 16) { m.RenderFlags = r.U16(); m.RenderFlagsIsUshort = true; } else { m.RenderFlags = r.U8(); m.RenderFlagsIsUshort = false; } }
        if (v >= 9) { m.BakedLight = ReadChannel(r); m.StationaryLight = ReadChannel(r); }
        if (v is >= 12 and <= 16) { m.BakedPaint = ReadChannel(r); m.HasBakedPaintChannel = true; }
        else if (v >= 17)
        {
            int oc = r.I32();
            for (int i = 0; i < oc; i++) m.TextureOverrides.Add(new ShaderOverride { Index = r.I32(), Name = r.Str() });
            m.BakedPaintScale = r.Vec2(); m.BakedPaintBias = r.Vec2();
        }
        return m;
    }

    private static Channel ReadChannel(Reader r) => new() { Texture = r.Str(), Scale = r.Vec2(), Bias = r.Vec2() };

    // ---- write ----

    public byte[] Write()
    {
        var w = new Writer();
        int v = Version;
        w.Bytes(Encoding.ASCII.GetBytes("OEGM"));
        w.I32(v);
        if (v >= 17) { w.I32(ShaderOverrides.Count); foreach (var o in ShaderOverrides) { w.I32(o.Index); w.Str(o.Name); } }
        else foreach (var s in LegacyBakedStrings) w.Str(s);

        w.U32((uint)Declarations.Count);
        foreach (var d in Declarations)
        {
            w.U32(d.Usage); w.U32((uint)d.Elements.Count);
            foreach (var (nm, fmt) in d.Elements) { w.U32(nm); w.U32(fmt); }
            w.Bytes(d.Padding);
        }
        w.U32((uint)VertexBuffers.Count);
        foreach (var b in VertexBuffers) { if (b.HasVisibility) w.U8(b.Visibility); w.U32((uint)b.Data.Length); w.Bytes(b.Data); }
        w.U32((uint)IndexBuffers.Count);
        foreach (var b in IndexBuffers) { if (b.HasVisibility) w.U8(b.Visibility); w.I32(b.Data.Length); w.Bytes(b.Data); }
        w.U32((uint)Meshes.Count);
        foreach (var m in Meshes) WriteMesh(w, m, v);
        w.Bytes(Tail);
        return w.ToArray();
    }

    private static void WriteMesh(Writer w, Mesh m, int v)
    {
        w.I32(m.VertexCount);
        w.U32((uint)m.VertexBufferIds.Count);
        w.I32(m.VertexDeclarationBase);
        foreach (var id in m.VertexBufferIds) w.I32(id);
        w.I32(m.IndexCount);
        w.I32(m.IndexBufferId);
        if (m.HasVisibility && v >= 13) w.U8(m.Visibility);
        if (m.HasRegionHash) w.U32(m.RegionHash);
        if (m.HasVcHash) w.U32(m.VisibilityControllerPathHash);
        w.U32((uint)m.Submeshes.Count);
        foreach (var s in m.Submeshes) { w.U32(s.Hash); w.Str(s.Material); w.I32(s.StartIndex); w.I32(s.IndexCount); w.I32(s.MinVertex); w.I32(s.MaxVertex); }
        if (m.HasDisableBackface) w.U8((byte)(m.DisableBackfaceCulling ? 1 : 0));
        w.Vec3(m.BoundsMin); w.Vec3(m.BoundsMax);
        w.Mat4(m.Transform);
        w.U8(m.QualityFilter);
        if (v is >= 7 and <= 12) w.U8(m.Visibility);
        if (m.HasLayerTransition) w.U8(m.LayerTransition);
        if (v is >= 11 and <= 13) w.U8((byte)m.RenderFlags);
        else if (v >= 14) { if (m.RenderFlagsIsUshort) w.U16(m.RenderFlags); else w.U8((byte)m.RenderFlags); }
        if (v >= 9) { WriteChannel(w, m.BakedLight); WriteChannel(w, m.StationaryLight); }
        if (m.HasBakedPaintChannel) WriteChannel(w, m.BakedPaint);
        else if (v >= 17)
        {
            w.I32(m.TextureOverrides.Count);
            foreach (var o in m.TextureOverrides) { w.I32(o.Index); w.Str(o.Name); }
            w.Vec2(m.BakedPaintScale); w.Vec2(m.BakedPaintBias);
        }
    }

    private static void WriteChannel(Writer w, Channel c) { w.Str(c.Texture); w.Vec2(c.Scale); w.Vec2(c.Bias); }

    /// <summary>Parse, and refuse the file unless it re-emits BYTE-IDENTICAL. This is the safety gate for
    /// editing: if we can't reproduce a file exactly, we don't understand it fully, so we must not risk
    /// corrupting it. (The one shipped chunk that fails this also fails LeagueToolkit's own reader.)</summary>
    public static bool TryReadEditable(byte[] data, out MapGeoBinary map)
    {
        map = null!;
        try
        {
            var parsed = Read(data);
            if (!parsed.Write().AsSpan().SequenceEqual(data)) return false;
            map = parsed;
            return true;
        }
        catch { return false; }
    }

    private sealed class Reader
    {
        private readonly byte[] _d; private int _p;
        public Reader(byte[] d) => _d = d;
        public byte[] Bytes(int n) { var s = _d.AsSpan(_p, n).ToArray(); _p += n; return s; }
        public byte U8() => _d[_p++];
        public int I32() { var x = BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(_p)); _p += 4; return x; }
        public uint U32() { var x = BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(_p)); _p += 4; return x; }
        public ushort U16() { var x = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(_p)); _p += 2; return x; }
        public float F32() { var x = BitConverter.ToSingle(_d, _p); _p += 4; return x; }
        public Vector2 Vec2() => new(F32(), F32());
        public Vector3 Vec3() => new(F32(), F32(), F32());
        public Matrix4x4 Mat4() { Span<float> f = stackalloc float[16]; for (int i = 0; i < 16; i++) f[i] = F32();
            return new Matrix4x4(f[0],f[1],f[2],f[3],f[4],f[5],f[6],f[7],f[8],f[9],f[10],f[11],f[12],f[13],f[14],f[15]); }
        public string Str() { int n = I32(); return Encoding.UTF8.GetString(Bytes(n)); }
        public byte[] Rest() { var s = _d.AsSpan(_p).ToArray(); _p = _d.Length; return s; }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _ms = new();
        public void Bytes(byte[] b) => _ms.Write(b);
        public void U8(byte b) => _ms.WriteByte(b);
        public void I32(int x) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(s, x); _ms.Write(s); }
        public void U32(uint x) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, x); _ms.Write(s); }
        public void U16(ushort x) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, x); _ms.Write(s); }
        public void F32(float x) { Span<byte> s = stackalloc byte[4]; BitConverter.TryWriteBytes(s, x); _ms.Write(s); }
        public void Vec2(Vector2 v) { F32(v.X); F32(v.Y); }
        public void Vec3(Vector3 v) { F32(v.X); F32(v.Y); F32(v.Z); }
        public void Mat4(Matrix4x4 m) { F32(m.M11);F32(m.M12);F32(m.M13);F32(m.M14);F32(m.M21);F32(m.M22);F32(m.M23);F32(m.M24);F32(m.M31);F32(m.M32);F32(m.M33);F32(m.M34);F32(m.M41);F32(m.M42);F32(m.M43);F32(m.M44); }
        public void Str(string s) { var b = Encoding.UTF8.GetBytes(s ?? ""); I32(b.Length); Bytes(b); }
        public byte[] ToArray() => _ms.ToArray();
    }
}
