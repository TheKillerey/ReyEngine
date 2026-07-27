using System.Buffers.Binary;
using System.Text;

namespace ReyEngine.Formats.Shaders;

/// <summary>M210: what a compiled Riot shader declares about itself.
///
/// <para>DXBC is self-describing: every blob Riot ships carries an <c>RDEF</c> chunk naming its constant
/// buffers, their variables with byte offsets, and every texture/sampler bind point, plus <c>ISGN</c>/
/// <c>OSGN</c> chunks naming the vertex attributes it expects. That is the whole answer to "what does this
/// shader need bound", and it comes from the shipped bytes rather than from a guess — which is the only
/// reason a DX11 preview can bind Riot's shaders correctly without a source tree.</para>
///
/// <para>This layer is deliberately GPU-free so it can be tested headlessly and so the research half of the
/// work does not depend on a D3D device existing. <see cref="ReyEngine.Formats.Shaders.ShaderCacheReader"/>
/// supplies the bytes; the D3D11 renderer consumes the result.</para>
/// </summary>
public enum DxbcStage { Unknown, Vertex, Pixel, Geometry, Hull, Domain, Compute }

/// <summary>What kind of thing a resource bind point refers to. Values are D3D_SHADER_INPUT_TYPE.</summary>
public enum DxbcResourceKind
{
    ConstantBuffer = 0, TextureBuffer = 1, Texture = 2, Sampler = 3,
    UavRwTyped = 4, Structured = 5, UavRwStructured = 6, ByteAddress = 7,
    UavRwByteAddress = 8, UavAppend = 9, UavConsume = 10, UavRwStructuredCounter = 11,
}

/// <summary>One element of a vertex input (<c>ISGN</c>) or output (<c>OSGN</c>) signature.</summary>
public sealed record DxbcSignatureElement(
    string Semantic, uint Index, uint Register, byte Mask, byte ReadWriteMask,
    uint ComponentType, uint SystemValueType)
{
    /// <summary>Semantic as HLSL writes it — <c>TEXCOORD0</c>, not <c>TEXCOORD</c> + 0.</summary>
    public string FullSemantic => $"{Semantic}{Index}";

    public string ComponentTypeName => ComponentType switch
    {
        1 => "uint", 2 => "int", 3 => "float", _ => $"comp{ComponentType}",
    };

    public string MaskString => MaskOf(Mask);
    public string ReadWriteMaskString => MaskOf(ReadWriteMask);

    /// <summary>Component count the element declares, i.e. how wide the attribute is.</summary>
    public int ComponentCount => System.Numerics.BitOperations.PopCount((uint)(Mask & 0xF));

    /// <summary>For an INPUT signature the read-write mask marks components the shader never reads. An
    /// element that is declared but wholly unread still needs a slot in the input layout — D3D validates
    /// the layout against the signature, not against usage — but it needs no meaningful data.</summary>
    public bool IsRead => (Mask & ReadWriteMask) != 0;

    private static string MaskOf(byte m)
    {
        var s = new StringBuilder(4);
        if ((m & 1) != 0) s.Append('x');
        if ((m & 2) != 0) s.Append('y');
        if ((m & 4) != 0) s.Append('z');
        if ((m & 8) != 0) s.Append('w');
        return s.Length == 0 ? "-" : s.ToString();
    }
}

/// <summary>One variable inside a constant buffer, at a byte offset the shader itself declares.</summary>
public sealed record DxbcConstant(
    string Name, int Offset, int Size, string TypeName, float[]? DefaultValue, bool IsUsed);

/// <summary>A constant buffer and the register slot it binds to.</summary>
public sealed record DxbcConstantBuffer(
    string Name, int Size, int BindPoint, IReadOnlyList<DxbcConstant> Variables)
{
    /// <summary>D3D requires the buffer it is handed to be at least as large as the declared size, rounded
    /// up to a 16-byte register. Allocating exactly <see cref="Size"/> is not always legal.</summary>
    public int AllocationSize => (Size + 15) & ~15;
}

/// <summary>A texture, sampler or cbuffer bind point.</summary>
public sealed record DxbcResource(
    string Name, DxbcResourceKind Kind, uint BindPoint, uint BindCount, uint Dimension, uint ReturnType)
{
    public string DimensionName => Dimension switch
    {
        1 => "buffer", 2 => "tex1d", 3 => "tex1darray", 4 => "tex2d", 5 => "tex2darray",
        6 => "tex2dms", 7 => "tex2dmsarray", 8 => "tex3d", 9 => "texcube", 10 => "texcubearray",
        _ => Kind == DxbcResourceKind.Sampler ? "sampler" : $"dim{Dimension}",
    };

    public bool IsCube => Dimension is 9 or 10;
}

/// <summary>Everything a compiled shader declares. Produced by <see cref="DxbcReflection.Parse"/>.</summary>
public sealed class DxbcShader
{
    public required byte[] Bytecode { get; init; }
    public DxbcStage Stage { get; init; }
    public int ShaderModelMajor { get; init; }
    public int ShaderModelMinor { get; init; }
    public string Creator { get; init; } = "";
    public IReadOnlyList<DxbcConstantBuffer> ConstantBuffers { get; init; } = Array.Empty<DxbcConstantBuffer>();
    public IReadOnlyList<DxbcResource> Resources { get; init; } = Array.Empty<DxbcResource>();
    public IReadOnlyList<DxbcSignatureElement> Inputs { get; init; } = Array.Empty<DxbcSignatureElement>();
    public IReadOnlyList<DxbcSignatureElement> Outputs { get; init; } = Array.Empty<DxbcSignatureElement>();
    public IReadOnlyList<string> ChunkTags { get; init; } = Array.Empty<string>();
    /// <summary>Set when the container declared a size that disagreed with the buffer it arrived in — the
    /// condition that makes D3D reject the blob with a bare E_INVALIDARG. See <see cref="ShaderCacheReader"/>.</summary>
    public bool WasTrimmed { get; init; }

    public IEnumerable<DxbcResource> Textures => Resources.Where(r => r.Kind == DxbcResourceKind.Texture);
    public IEnumerable<DxbcResource> Samplers => Resources.Where(r => r.Kind == DxbcResourceKind.Sampler);

    public string ShaderModel => $"sm{ShaderModelMajor}.{ShaderModelMinor}";
    public int ByteSize => Bytecode.Length;
}

public static class DxbcReflection
{
    /// <summary>The four-byte container magic. Anything else is not a shader blob.</summary>
    public static bool LooksLikeDxbc(ReadOnlySpan<byte> b) =>
        b.Length >= 32 && b[0] == (byte)'D' && b[1] == (byte)'X' && b[2] == (byte)'B' && b[3] == (byte)'C';

    /// <summary>Chunk directory. Offsets in the header are ABSOLUTE from the start of the container, which
    /// is why chunk parsing never noticed the over-long length prefix that broke shader creation.</summary>
    public static IReadOnlyList<(string Tag, int Offset, int Size)> Chunks(byte[] b)
    {
        var list = new List<(string, int, int)>();
        if (!LooksLikeDxbc(b)) return list;
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(28));
        for (int i = 0; i < n; i++)
        {
            int dirAt = 32 + 4 * i;
            if (dirAt + 4 > b.Length) break;
            int off = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(dirAt));
            if (off < 0 || off + 8 > b.Length) break;
            string tag = Encoding.ASCII.GetString(b, off, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(off + 4));
            if (size < 0 || off + 8 + size > b.Length) break;
            list.Add((tag, off + 8, size));
        }
        return list;
    }

    public static DxbcShader Parse(byte[] bytecode, bool wasTrimmed = false)
    {
        var chunks = Chunks(bytecode);
        var tags = chunks.Select(c => c.Tag).ToList();

        var cbs = Array.Empty<DxbcConstantBuffer>() as IReadOnlyList<DxbcConstantBuffer>;
        var res = Array.Empty<DxbcResource>() as IReadOnlyList<DxbcResource>;
        var inputs = Array.Empty<DxbcSignatureElement>() as IReadOnlyList<DxbcSignatureElement>;
        var outputs = Array.Empty<DxbcSignatureElement>() as IReadOnlyList<DxbcSignatureElement>;
        DxbcStage stage = DxbcStage.Unknown;
        int major = 0, minor = 0;
        string creator = "";

        foreach (var (tag, off, size) in chunks)
        {
            var data = new byte[size];
            Array.Copy(bytecode, off, data, 0, size);
            switch (tag)
            {
                case "RDEF":
                    (cbs, res, stage, major, minor, creator) = ParseRdef(data);
                    break;
                // ISGN/OSGN are the SM4 names; SM5 shaders with system values use the 1/5 variants.
                case "ISGN" or "ISG1":
                    inputs = ParseSignature(data);
                    break;
                case "OSGN" or "OSG1" or "OSG5":
                    outputs = ParseSignature(data);
                    break;
            }
        }

        return new DxbcShader
        {
            Bytecode = bytecode, Stage = stage,
            ShaderModelMajor = major, ShaderModelMinor = minor, Creator = creator,
            ConstantBuffers = cbs, Resources = res, Inputs = inputs, Outputs = outputs,
            ChunkTags = tags, WasTrimmed = wasTrimmed,
        };
    }

    private static string Str(byte[] d, int off)
    {
        if (off <= 0 || off >= d.Length) return "";
        int e = off;
        while (e < d.Length && d[e] != 0) e++;
        return Encoding.ASCII.GetString(d, off, e - off);
    }

    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));

    /// <summary>D3D_SHADER_VARIABLE_TYPE, indexed by the type id RDEF stores.</summary>
    private static readonly string[] VarTypeNames =
    {
        "void","bool","int","float","string","texture","texture1D","texture2D","texture3D","textureCube",
        "sampler","sampler1D","sampler2D","sampler3D","samplerCUBE","pixelshader","vertexshader","pixelfragment",
        "vertexfragment","uint","uint8","geometryshader","rasterizer","depthstencil","blend","buffer","cbuffer",
        "tbuffer","texture1DArray","texture2DArray","rendertargetview","depthstencilview","texture2DMS",
        "texture2DMSArray","texturecubearray","hullshader","domainshader","interfacepointer","computeshader",
        "double","rwtexture1d","rwtexture1darray","rwtexture2d","rwtexture2darray","rwtexture3d","rwbuffer",
        "byteaddressbuffer","rwbyteaddressbuffer","structuredbuffer","rwstructuredbuffer","appendstructuredbuffer",
        "consumestructuredbuffer","min8float","min10float","min16float","min12int","min16int","min16uint",
    };

    private static (IReadOnlyList<DxbcConstantBuffer>, IReadOnlyList<DxbcResource>, DxbcStage, int, int, string)
        ParseRdef(byte[] d)
    {
        var cbs = new List<DxbcConstantBuffer>();
        var rbs = new List<DxbcResource>();
        if (d.Length < 28) return (cbs, rbs, DxbcStage.Unknown, 0, 0, "");

        uint cbCount = U32(d, 0), cbOff = U32(d, 4);
        uint rbCount = U32(d, 8), rbOff = U32(d, 12);
        byte minor = d[16], major = d[17];
        ushort progType = U16(d, 18);
        string creator = Str(d, (int)U32(d, 24));

        // The program-type word doubles as two ASCII chars for the tessellation/compute stages.
        DxbcStage stage = progType switch
        {
            0xFFFF => DxbcStage.Pixel,
            0xFFFE => DxbcStage.Vertex,
            0x4753 => DxbcStage.Geometry,
            0x4853 => DxbcStage.Hull,
            0x4453 => DxbcStage.Domain,
            0x4353 => DxbcStage.Compute,
            _ => DxbcStage.Unknown,
        };

        for (int i = 0; i < rbCount; i++)
        {
            int o = (int)rbOff + i * 32;
            if (o + 32 > d.Length) break;
            rbs.Add(new DxbcResource(
                Str(d, (int)U32(d, o)),
                (DxbcResourceKind)U32(d, o + 4),
                BindPoint: U32(d, o + 20),
                BindCount: U32(d, o + 24),
                Dimension: U32(d, o + 12),
                ReturnType: U32(d, o + 8)));
        }

        // SM5 grew the variable record from 24 to 40 bytes; reading the wrong stride yields garbage names.
        int varStride = major >= 5 ? 40 : 24;
        for (int i = 0; i < cbCount; i++)
        {
            int o = (int)cbOff + i * 24;
            if (o + 24 > d.Length) break;
            string name = Str(d, (int)U32(d, o));
            uint varCount = U32(d, o + 4), varOff = U32(d, o + 8);
            int size = (int)U32(d, o + 12);

            var vars = new List<DxbcConstant>();
            for (int v = 0; v < varCount; v++)
            {
                int vo = (int)varOff + v * varStride;
                if (vo + 24 > d.Length) break;
                string vname = Str(d, (int)U32(d, vo));
                int voff = (int)U32(d, vo + 4);
                int vsize = (int)U32(d, vo + 8);
                uint vflags = U32(d, vo + 12);
                int typeOff = (int)U32(d, vo + 16);
                int defOff = (int)U32(d, vo + 20);

                string typeName = "?";
                if (typeOff > 0 && typeOff + 12 <= d.Length)
                {
                    ushort cls = U16(d, typeOff), typ = U16(d, typeOff + 2);
                    ushort rows = U16(d, typeOff + 4), cols = U16(d, typeOff + 6);
                    ushort elems = U16(d, typeOff + 8), members = U16(d, typeOff + 10);
                    string bn = typ < VarTypeNames.Length ? VarTypeNames[typ] : $"t{typ}";
                    typeName = cls switch
                    {
                        0 => bn,
                        1 => $"{bn}{cols}",
                        2 or 3 => $"{bn}{rows}x{cols}",
                        5 => $"struct({members})",
                        _ => $"cls{cls}:{bn}{rows}x{cols}",
                    };
                    if (elems > 0) typeName += $"[{elems}]";
                }

                float[]? def = null;
                if (defOff > 0 && vsize > 0 && vsize % 4 == 0 && vsize <= 256 && defOff + vsize <= d.Length)
                {
                    def = new float[vsize / 4];
                    for (int k = 0; k < def.Length; k++) def[k] = BitConverter.ToSingle(d, defOff + k * 4);
                }

                // D3D_SVF_USED. A declared-but-unused constant is real and common: the compiler keeps the
                // reflection entry even when the permutation's #ifs removed every reference.
                bool used = (vflags & 2) != 0;
                vars.Add(new DxbcConstant(vname, voff, vsize, typeName, def, used));
            }

            int bind = -1;
            foreach (var rb in rbs)
                if (rb.Kind == DxbcResourceKind.ConstantBuffer && rb.Name == name) bind = (int)rb.BindPoint;

            cbs.Add(new DxbcConstantBuffer(name, size, bind, vars));
        }

        return (cbs, rbs, stage, major, minor, creator);
    }

    private static IReadOnlyList<DxbcSignatureElement> ParseSignature(byte[] d)
    {
        var list = new List<DxbcSignatureElement>();
        if (d.Length < 8) return list;
        uint count = U32(d, 0);
        for (int i = 0; i < count; i++)
        {
            int o = 8 + i * 24;
            if (o + 24 > d.Length) break;
            list.Add(new DxbcSignatureElement(
                Semantic: Str(d, (int)U32(d, o)),
                Index: U32(d, o + 4),
                SystemValueType: U32(d, o + 8),
                ComponentType: U32(d, o + 12),
                Register: U32(d, o + 16),
                Mask: d[o + 20],
                ReadWriteMask: d[o + 21]));
        }
        return list;
    }
}
