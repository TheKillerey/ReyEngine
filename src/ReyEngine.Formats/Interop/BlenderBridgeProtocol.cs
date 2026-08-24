using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ReyEngine.Formats.Interop;

/// <summary>
/// One map mesh on the wire. Geometry is world space RELATIVE TO <see cref="Pivot"/>, in League axes.
///
/// <para>Relative to the pivot rather than to the world origin because that is what makes the transform
/// round trip EXACT. ReyEngine rotates and scales a mesh about its pivot (its local bbox centre); Blender
/// rotates and scales about the object origin. Put the Blender object's origin on the pivot and the two
/// operations are the same operation, so a rotation authored in Blender maps onto a ReyEngine rotation
/// with no re-derivation and no drift.</para>
/// </summary>
public sealed record BridgeMesh(
    int Index,
    string Name,
    Vector3 Pivot,
    float[] Positions,
    float[]? Normals,
    uint[] Indices,
    /// <summary>Where the mesh sits RIGHT NOW: pivot plus any offset already applied in the editor.</summary>
    Vector3 Location,
    Vector3 RotationDegrees,
    Vector3 Scale)
{
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>A mesh placement coming back from Blender, in League axes and ReyEngine's own edit terms.</summary>
public sealed record BridgeTransform(int Index, Vector3 Location, Vector3 RotationDegrees, Vector3 Scale);

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
/// </summary>
public static class BlenderBridgeProtocol
{
    /// <summary>Bumped whenever the framing or the mesh block changes shape. The add-on refuses a mismatch
    /// rather than misreading a payload — a silently wrong decode looks like corrupt geometry.</summary>
    public const int Version = 1;

    /// <summary>Loopback only. This link hands out a map's whole geometry and accepts edits to it; it has no
    /// business being reachable from anywhere but this machine.</summary>
    public const int DefaultPort = 47800;

    /// <summary>Ceiling on one header line.</summary>
    public const int MaxHeaderBytes = 16 * 1024 * 1024;

    // ---- mesh blocks -----------------------------------------------------------------------------

    /// <summary>
    /// Pack meshes into the binary block that follows a <c>meshes</c> header.
    /// Layout per mesh: name, index, pivot, then the three arrays, each length-prefixed.
    /// </summary>
    public static byte[] EncodeMeshes(IReadOnlyList<BridgeMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(meshes.Count);
        foreach (var m in meshes)
        {
            var name = Encoding.UTF8.GetBytes(m.Name ?? "");
            w.Write(name.Length);
            w.Write(name);
            w.Write(m.Index);
            w.Write(m.Pivot.X); w.Write(m.Pivot.Y); w.Write(m.Pivot.Z);
            // The placement travels with the geometry, so a mesh already moved in the editor arrives in
            // Blender where the editor has it rather than back at its original spot.
            w.Write(m.Location.X); w.Write(m.Location.Y); w.Write(m.Location.Z);
            w.Write(m.RotationDegrees.X); w.Write(m.RotationDegrees.Y); w.Write(m.RotationDegrees.Z);
            w.Write(m.Scale.X); w.Write(m.Scale.Y); w.Write(m.Scale.Z);

            w.Write(m.Positions.Length);
            foreach (float f in m.Positions) w.Write(f);

            int normals = m.Normals?.Length ?? 0;
            w.Write(normals);
            if (m.Normals is not null) foreach (float f in m.Normals) w.Write(f);

            w.Write(m.Indices.Length);
            foreach (uint i in m.Indices) w.Write(i);
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
            int nameLength = r.ReadInt32();
            if (nameLength < 0 || nameLength > 4096) throw new InvalidDataException("mesh name length is not plausible");
            string name = Encoding.UTF8.GetString(r.ReadBytes(nameLength));
            int index = r.ReadInt32();
            var pivot = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var location = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var rotation = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var scale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            float[] positions = ReadFloats(r, "positions");
            if (positions.Length % 3 != 0) throw new InvalidDataException($"'{name}' has {positions.Length} position floats, not a multiple of 3");
            float[] normals = ReadFloats(r, "normals");
            if (normals.Length != 0 && normals.Length != positions.Length)
                throw new InvalidDataException($"'{name}' has {normals.Length} normal floats for {positions.Length} position floats");

            int indexCount = r.ReadInt32();
            if (indexCount < 0 || indexCount > 200_000_000) throw new InvalidDataException("index count is not plausible");
            var indices = new uint[indexCount];
            for (int k = 0; k < indexCount; k++) indices[k] = r.ReadUInt32();

            int vertices = positions.Length / 3;
            foreach (uint index2 in indices)
                if (index2 >= vertices)
                    throw new InvalidDataException($"'{name}' indexes vertex {index2} of {vertices}");

            meshes.Add(new BridgeMesh(index, name, pivot, positions,
                normals.Length == 0 ? null : normals, indices, location, rotation, scale));
        }
        return meshes;
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
    public static string? ReadHeader(Stream stream)
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
            if (line.Count > MaxHeaderBytes) throw new InvalidDataException("header line never ended");
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
