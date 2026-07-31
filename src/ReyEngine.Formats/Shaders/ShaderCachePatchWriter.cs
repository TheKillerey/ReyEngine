using System.Text;

namespace ReyEngine.Formats.Shaders;

/// <summary>Pure byte writers for Riot's TOC3.0 tables and their length-prefixed DXBC containers.</summary>
public static class ShaderCachePatchWriter
{
    public static byte[] WriteToc(ShaderStageToc source,
        IReadOnlyList<ShaderPermutation> permutations, uint declaredBlobCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        WriteString(writer, "TOC3.0");
        writer.Write((uint)permutations.Count);
        writer.Write((uint)source.DefinePool.Count);
        writer.Write(declaredBlobCount);
        writer.Write(source.Flag);
        WriteString(writer, "baseDefines");
        foreach (var (key, value) in source.DefinePool)
        {
            WriteString(writer, key);
            WriteString(writer, value);
        }
        WriteString(writer, "shaders");
        foreach (var permutation in permutations) writer.Write(permutation.Key);
        foreach (var permutation in permutations) writer.Write(permutation.BlobIndex);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Riot's container record includes one trailing zero byte beyond the DXBC size declared at byte 24.
    /// The shipped reader strips it before D3D shader creation; matching it keeps custom and Riot records
    /// byte-for-byte compatible at the container layer.
    /// </summary>
    public static byte[] WriteContainer(IReadOnlyList<byte[]> blobs)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var blob in blobs)
        {
            if (!DxbcReflection.LooksLikeDxbc(blob))
                throw new InvalidDataException("shader blob is not a DXBC container");
            writer.Write(checked(blob.Length + 1));
            writer.Write(blob);
            writer.Write((byte)0);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }
}
