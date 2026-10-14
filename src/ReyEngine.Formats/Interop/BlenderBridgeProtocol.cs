using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ReyEngine.Formats.Interop;

/// <summary>
/// M841: one submesh range of a <see cref="BridgeMesh"/> - the material it is drawn with and the slice of the
/// mesh's OWN index list it covers. <see cref="StartIndex"/> and <see cref="IndexCount"/> count indices, not
/// triangles, and are relative to <see cref="BridgeMesh.Indices"/>.
/// </summary>
public sealed record BridgeSubmesh(string Material, int StartIndex, int IndexCount);

/// <summary>
/// One map mesh on the wire. Geometry is world space RELATIVE TO <see cref="Pivot"/>, in League axes.
///
/// <para>Relative to the pivot rather than to the world origin because that is what makes the transform
/// round trip EXACT. ReyEngine rotates and scales a mesh about its pivot (its local bbox centre); Blender
/// rotates and scales about the object origin. Put the Blender object's origin on the pivot and the two
/// operations are the same operation, so a rotation authored in Blender maps onto a ReyEngine rotation
/// with no re-derivation and no drift.</para>
///
/// <para>M841 (protocol 3) adds the raw lightmap channel (TEXCOORD7 as the file holds it, before the
/// BakedLight scale/bias), the vertex colour and the submesh table with each range's material.</para>
/// </summary>
public sealed record BridgeMesh(
    int Index,
    string Name,
    Vector3 Pivot,
    float[] Positions,
    float[]? Normals,
    float[]? Uvs,
    uint[] Indices,
    /// <summary>Where the mesh sits RIGHT NOW: pivot plus any offset already applied in the editor.</summary>
    Vector3 Location,
    Vector3 RotationDegrees,
    Vector3 Scale)
{
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;

    /// <summary>M841: raw TEXCOORD7, 2 floats per vertex. Null when the mesh has no lightmap channel.</summary>
    public float[]? LightmapUvs { get; init; }

    /// <summary>M841: vertex colour, RGBA 0..1, 4 floats per vertex. Null when the mesh has none.</summary>
    public float[]? Colors { get; init; }

    /// <summary>M842 (protocol 4): TEXCOORD5, the grass clump pivot of a VertexDeform bush - pivot-relative world
    /// space like <see cref="Positions"/>, 3 floats per vertex. Null when the mesh has none.</summary>
    public float[]? GrassPivots { get; init; }

    /// <summary>M841: the submesh ranges, in submesh order. Empty when the sender does not describe them
    /// (a pushed shape: the reshaper keeps one material per mesh).</summary>
    public IReadOnlyList<BridgeSubmesh> Submeshes { get; init; } = Array.Empty<BridgeSubmesh>();
}

/// <summary>A mesh placement coming back from Blender, in League axes and ReyEngine's own edit terms.</summary>
/// <param name="Anchor">
/// The pivot Blender was working from, echoed back.
///
/// <para>Without it a placement silently re-bases itself whenever the geometry changes. A pivot is the
/// bbox centre of the mesh, so editing faces MOVES it - and an untouched Blender object then decodes to
/// a different offset, which is a mesh that jumps on its own and jumps back the other way next time.
/// Anchoring on the pivot the object was given makes the arithmetic independent of what the pivot has
/// since become. Null when an older add-on did not send one.</para>
/// </param>
public sealed record BridgeTransform(
    int Index, Vector3 Location, Vector3 RotationDegrees, Vector3 Scale, Vector3? Anchor = null);

/// <summary>
/// M580: the wire format between ReyEngine and the Blender add-on.
///
/// <para>Framing is a UTF-8 JSON header line terminated by <c>\n</c>, optionally followed by exactly the
/// number of binary bytes the header declares. JSON alone would be simpler, but a map is routinely several
/// hundred thousand vertices and rendering those as decimal text costs tens of megabytes per pull; the
/// header stays readable while the arrays go across as floats.</para>
///
/// <para><b>The wire is League space</b> — Y up, the same numbers the .mapgeo holds. The add-on converts to
/// and from Blender's Z-up on its side. Keeping the conversion in one place, at the edge that actually
/// needs it, means nothing here has to think about two coordinate systems.</para>
///
/// <para>M841: protocol 3 is spoken by MapgeoAddon, which replaced the old single-file "ReyEngine Bridge"
/// add-on. It keeps this framing and adds materials, textures, material assignment and material edits;
/// docs/reyengine-link-protocol.md is the specification, in this repository and in MapgeoAddon's.</para>
/// </summary>
public static class BlenderBridgeProtocol
{
    /// <summary>Bumped whenever the framing or the mesh block changes shape. Both sides refuse a mismatch
    /// rather than misreading a payload — a silently wrong decode looks like corrupt geometry.</summary>
    public const int Version = 4;

    /// <summary>The protocol the retired single-file "ReyEngine Bridge" add-on speaks.</summary>
    public const int LegacyBridgeVersion = 2;

    /// <summary>Loopback only. This link hands out a map's whole geometry and accepts edits to it; it has no
    /// business being reachable from anywhere but this machine.</summary>
    public const int DefaultPort = 47800;

    /// <summary>Ceiling on one header line.</summary>
    public const int MaxHeaderBytes = 16 * 1024 * 1024;

    /// <summary>Ceiling on one submesh table, per mesh. A mapgeo mesh has a handful; more is a desync.</summary>
    private const int MaxSubmeshes = 4096;

    // ---- mesh blocks -----------------------------------------------------------------------------

    /// <summary>
    /// Pack meshes into the binary block that follows a <c>meshes</c> header (and that a
    /// <c>push_geometry</c> request carries).
    /// Layout per mesh: name, index, pivot, location, rotation, scale, then positions, normals, uvs,
    /// lightmap uvs, colours, grass pivots (protocol 4) and indices (each length-prefixed), then the submesh table.
    /// </summary>
    public static byte[] EncodeMeshes(IReadOnlyList<BridgeMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(meshes.Count);
        foreach (var m in meshes)
        {
            WriteString(w, m.Name);
            w.Write(m.Index);
            w.Write(m.Pivot.X); w.Write(m.Pivot.Y); w.Write(m.Pivot.Z);
            // The placement travels with the geometry, so a mesh already moved in the editor arrives in
            // Blender where the editor has it rather than back at its original spot.
            w.Write(m.Location.X); w.Write(m.Location.Y); w.Write(m.Location.Z);
            w.Write(m.RotationDegrees.X); w.Write(m.RotationDegrees.Y); w.Write(m.RotationDegrees.Z);
            w.Write(m.Scale.X); w.Write(m.Scale.Y); w.Write(m.Scale.Z);

            WriteFloats(w, m.Positions);
            WriteFloats(w, m.Normals);
            WriteFloats(w, m.Uvs);
            WriteFloats(w, m.LightmapUvs);
            WriteFloats(w, m.Colors);
            WriteFloats(w, m.GrassPivots);

            w.Write(m.Indices.Length);
            foreach (uint i in m.Indices) w.Write(i);

            w.Write(m.Submeshes.Count);
            foreach (var s in m.Submeshes)
            {
                WriteString(w, s.Material);
                w.Write(s.StartIndex);
                w.Write(s.IndexCount);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Read a mesh block back. Throws on a truncated or self-inconsistent payload rather than
    /// returning half a map — a partial pull would look like the user deleted geometry.</summary>
    public static IReadOnlyList<BridgeMesh> DecodeMeshes(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var ms = new MemoryStream(payload, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        int count = r.ReadInt32();
        if (count < 0 || count > 1_000_000) throw new InvalidDataException($"mesh count {count} is not plausible");

        var meshes = new List<BridgeMesh>(count);
        for (int i = 0; i < count; i++)
        {
            string name = ReadString(r, "mesh name");
            int index = r.ReadInt32();
            var pivot = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var location = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var rotation = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var scale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            float[] positions = ReadFloats(r, "positions");
            if (positions.Length % 3 != 0) throw new InvalidDataException($"'{name}' has {positions.Length} position floats, not a multiple of 3");
            int vertices = positions.Length / 3;
            float[] normals = ReadFloats(r, "normals");
            if (normals.Length != 0 && normals.Length != positions.Length)
                throw new InvalidDataException($"'{name}' has {normals.Length} normal floats for {positions.Length} position floats");
            float[] uvs = ReadFloats(r, "uvs");
            if (uvs.Length != 0 && uvs.Length != vertices * 2)
                throw new InvalidDataException($"'{name}' has {uvs.Length} uv floats for {vertices} vertices");
            float[] lightmap = ReadFloats(r, "lightmap uvs");
            if (lightmap.Length != 0 && lightmap.Length != vertices * 2)
                throw new InvalidDataException($"'{name}' has {lightmap.Length} lightmap uv floats for {vertices} vertices");
            float[] colors = ReadFloats(r, "colors");
            if (colors.Length != 0 && colors.Length != vertices * 4)
                throw new InvalidDataException($"'{name}' has {colors.Length} colour floats for {vertices} vertices");
            float[] grass = ReadFloats(r, "grass pivots");
            if (grass.Length != 0 && grass.Length != vertices * 3)
                throw new InvalidDataException($"'{name}' has {grass.Length} grass pivot floats for {vertices} vertices");

            int indexCount = r.ReadInt32();
            if (indexCount < 0 || indexCount > 200_000_000) throw new InvalidDataException("index count is not plausible");
            var indices = new uint[indexCount];
            for (int k = 0; k < indexCount; k++) indices[k] = r.ReadUInt32();
            foreach (uint index2 in indices)
                if (index2 >= vertices)
                    throw new InvalidDataException($"'{name}' indexes vertex {index2} of {vertices}");

            int submeshCount = r.ReadInt32();
            if (submeshCount < 0 || submeshCount > MaxSubmeshes) throw new InvalidDataException($"'{name}' has {submeshCount} submeshes");
            var submeshes = new BridgeSubmesh[submeshCount];
            for (int s = 0; s < submeshCount; s++)
            {
                string material = ReadString(r, "submesh material");
                int start = r.ReadInt32();
                int length = r.ReadInt32();
                if (start < 0 || length < 0 || (long)start + length > indexCount)
                    throw new InvalidDataException($"'{name}' submesh {s} covers indices {start}+{length} of {indexCount}");
                submeshes[s] = new BridgeSubmesh(material, start, length);
            }

            meshes.Add(new BridgeMesh(index, name, pivot, positions,
                normals.Length == 0 ? null : normals, uvs.Length == 0 ? null : uvs,
                indices, location, rotation, scale)
            {
                LightmapUvs = lightmap.Length == 0 ? null : lightmap,
                Colors = colors.Length == 0 ? null : colors,
                GrassPivots = grass.Length == 0 ? null : grass,
                Submeshes = submeshes,
            });
        }
        if (ms.Position != ms.Length)
            throw new InvalidDataException($"{ms.Length - ms.Position} byte(s) left over after {count} mesh(es) - the block is not protocol {Version}");
        return meshes;
    }

    private static void WriteString(BinaryWriter w, string? text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    private static string ReadString(BinaryReader r, string what)
    {
        int length = r.ReadInt32();
        if (length < 0 || length > 4096) throw new InvalidDataException($"{what} length {length} is not plausible");
        var bytes = r.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException($"{what} is truncated");
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteFloats(BinaryWriter w, float[]? values)
    {
        w.Write(values?.Length ?? 0);
        if (values is not null) foreach (float f in values) w.Write(f);
    }

    private static float[] ReadFloats(BinaryReader r, string what)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 200_000_000) throw new InvalidDataException($"{what} count {n} is not plausible");
        var values = new float[n];
        for (int i = 0; i < n; i++) values[i] = r.ReadSingle();
        return values;
    }

    // ---- framing ---------------------------------------------------------------------------------

    /// <summary>Write a header line and its optional binary body.</summary>
    public static void WriteMessage(Stream stream, string json, byte[]? body = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var line = Encoding.UTF8.GetBytes(json + "\n");
        stream.Write(line, 0, line.Length);
        if (body is { Length: > 0 }) stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    /// <summary>
    /// Read one header line. Returns null at end of stream.
    /// </summary>
    /// <remarks>
    /// Byte at a time on purpose. A buffered reader would swallow part of the binary body that follows the
    /// newline, and the body is not self-describing enough to recover from that.
    /// </remarks>
    public static string? ReadHeader(Stream stream) => ReadHeader(stream, MaxHeaderBytes);

    /// <summary>Read one header line of at most <paramref name="maxBytes"/> bytes (the server reads a
    /// connection's first line with a small ceiling: it has to be a hello).</summary>
    public static string? ReadHeader(Stream stream, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var line = new List<byte>(256);
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) return line.Count == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
            if (b == '\n') return Encoding.UTF8.GetString(line.ToArray());
            if (b != '\r') line.Add((byte)b);
            // A push carries its transforms in the header as JSON, and a big map is thousands of
            // meshes, so this ceiling is about a desynced stream rather than about message size.
            if (line.Count > maxBytes) throw new InvalidDataException("header line never ended");
        }
    }

    /// <summary>M841: read and drop <paramref name="count"/> bytes in small chunks - a body nobody wants is
    /// not worth allocating.</summary>
    public static void Skip(Stream stream, long count)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) throw new EndOfStreamException($"{count} byte(s) of a body never arrived");
            count -= n;
        }
    }

    /// <summary>Read exactly <paramref name="count"/> bytes, or throw. A short read here is a desynced
    /// stream, and continuing from one produces geometry that decodes without complaint and is wrong.</summary>
    public static byte[] ReadExactly(Stream stream, int count)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0) throw new EndOfStreamException($"wanted {count} bytes, got {read}");
            read += n;
        }
        return buffer;
    }
}
