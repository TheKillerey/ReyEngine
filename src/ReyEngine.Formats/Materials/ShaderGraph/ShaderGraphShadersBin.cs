using System.Buffers.Binary;
using System.Text;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Materials.ShaderGraph;

/// <summary>One parameter a Shader Graph asks the base shader's definition to declare: its name and width (1 float .. 4 float4).</summary>
public sealed record SgDeclaredParam(string Name, int Components)
{
    /// <summary>The logical parameter's <c>fields</c> mask: 1, 3, 7 or 15 for float .. float4 (one bit per component).</summary>
    public uint FieldsMask => (1u << Math.Clamp(Components, 1, 4)) - 1u;
}

/// <summary>M835: one texture a Shader Graph asks the base shader's definition to declare. <see cref="DefaultPath"/> may be empty (the entry then keeps the default
/// Riot's own DiffuseTexture entry has).</summary>
public sealed record SgDeclaredTexture(string Name, string DefaultPath);

/// <summary>
/// M834: the shaders.bin patch that makes a Shader Graph's custom parameter names legal. Proven in game (test4, 2026-10-11): a mod-shipped
/// <c>data/shaders/shaders.bin</c> (Global.wad.client) whose DefaultEnv_Flat definition declares extra parameters lets a material carry them and
/// the client fills them BY NAME. Each new entry copies the shape of Riot's own <c>TintColor</c> element of <c>parameters</c>:
/// <c>ShaderPhysicalParameter { name, logicalParameters = List(0x80) of Embedded(0x83) [ { name, fields } ] }</c> with no <c>data</c> (the zero
/// default Riot also omits). The patch is BYTE LEVEL (the bin is not re-serialised through a tree, so every other object stays byte-identical)
/// and is regenerated from the INSTALLED file at every export - never cached - because the file is GLOBAL: a stale copy would silently revert
/// every shader Riot changed since.
/// </summary>
public static class SgShadersBin
{
    public const string GamePath = "data/shaders/shaders.bin";
    public const string WadName = "Global.wad.client";

    private static readonly uint FName = HashAlgorithms.Fnv1a("name"), FParameters = HashAlgorithms.Fnv1a("parameters"), FData = HashAlgorithms.Fnv1a("data"),
        FLogical = HashAlgorithms.Fnv1a("logicalParameters"), FFields = HashAlgorithms.Fnv1a("fields"), FTextures = HashAlgorithms.Fnv1a("textures"), FDefaultTexturePath = HashAlgorithms.Fnv1a("defaultTexturePath"), ClassCustomShaderDef = HashAlgorithms.Fnv1a("CustomShaderDef");

    // ---------------------------------------------------------------- bin walking (the same rules as the proven build.py)

    private sealed record Entry(int Start, int End, uint Class);

    private static Dictionary<uint, Entry> Entries(byte[] b)
    {
        if (b.Length < 12 || b[0] != 'P' || b[1] != 'R' || b[2] != 'O' || b[3] != 'P') throw new InvalidDataException("shaders.bin is not a PROP file");
        int p = 4;
        uint ver = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
        if (ver >= 2)
        {
            uint linked = Read32(b, p); p += 4;
            for (uint i = 0; i < linked; i++) { int ln = Read16(b, p); p += 2 + ln; Need(b, p); }
        }
        uint n = Read32(b, p); p += 4;
        if (n > 1_000_000 || p + 4L * n > b.Length) throw new InvalidDataException("shaders.bin declares an impossible object count");
        var classes = new uint[n];
        for (int i = 0; i < n; i++) { classes[i] = Read32(b, p); p += 4; }
        var map = new Dictionary<uint, Entry>();
        foreach (uint c in classes)
        {
            uint len = Read32(b, p);
            if ((long)p + 4 + len > b.Length || len < 4) throw new InvalidDataException("an object of shaders.bin runs past the end");
            map[Read32(b, p + 4)] = new Entry(p, p + 4 + (int)len, c);
            p += 4 + (int)len;
        }
        if (p != b.Length) throw new InvalidDataException("shaders.bin has trailing bytes after its objects");
        return map;
    }

    private static uint Read32(byte[] b, int p) { Need(b, p + 4); return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); }
    private static ushort Read16(byte[] b, int p) { Need(b, p + 2); return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); }
    private static void Need(byte[] b, int end) { if (end > b.Length || end < 0) throw new InvalidDataException("shaders.bin is truncated"); }

    private static int SkipValue(byte[] b, int p, int t, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("shaders.bin nests too deeply");
        int fixedSize = t switch
        {
            1 or 2 or 3 or 0x87 => 1, 4 or 5 => 2, 6 or 7 or 10 or 15 or 17 or 0x84 => 4, 8 or 9 or 11 or 18 => 8, 12 => 12, 13 => 16, 14 => 64, _ => -1,
        };
        if (fixedSize >= 0) { Need(b, p + fixedSize); return p + fixedSize; }
        switch (t)
        {
            case 16: return p + 2 + Read16(b, p);
            case 0x80 or 0x81 or 0x86:
            {
                int hdr = t == 0x86 ? 2 : 1;
                long end = (long)p + hdr + 4 + Read32(b, p + hdr);
                if (end > b.Length) throw new InvalidDataException("a container of shaders.bin runs past the end");
                return (int)end;
            }
            case 0x82 or 0x83:
            {
                if (Read32(b, p) == 0) return p + 4;
                long end = (long)p + 8 + Read32(b, p + 4);
                if (end > b.Length) throw new InvalidDataException("an embedded value of shaders.bin runs past the end");
                return (int)end;
            }
            case 0x85:
            {
                Need(b, p + 2);
                int et = b[p], n = b[p + 1], q = p + 2;
                for (int i = 0; i < n; i++) q = SkipValue(b, q, et, depth + 1);
                return q;
            }
        }
        throw new InvalidDataException($"shaders.bin holds a value type 0x{t:x} this reader does not know");
    }

    private readonly record struct Field(uint Hash, int Type, int Start, int End);

    private static List<Field> ObjectFields(byte[] b, Entry e)
    {
        int p = e.Start + 8;
        int n = Read16(b, p); p += 2;
        var list = new List<Field>(n);
        for (int i = 0; i < n; i++)
        {
            Need(b, p + 5);
            uint h = Read32(b, p); int t = b[p + 4];
            int v = p + 5, end = SkipValue(b, v, t);
            list.Add(new Field(h, t, v, end));
            p = end;
        }
        if (p != e.End) throw new InvalidDataException("an object of shaders.bin did not parse to its length");
        return list;
    }

    private static (uint Class, List<Field> Fields, int End) StructFields(byte[] b, int p)
    {
        uint ch = Read32(b, p); uint size = Read32(b, p + 4);
        int q = p + 8;
        int n = Read16(b, q); q += 2;
        var list = new List<Field>(n);
        for (int i = 0; i < n; i++)
        {
            Need(b, q + 5);
            uint h = Read32(b, q); int t = b[q + 4];
            int v = q + 5, end = SkipValue(b, v, t);
            list.Add(new Field(h, t, v, end));
            q = end;
        }
        if (q != p + 8 + (int)size) throw new InvalidDataException("an embedded value of shaders.bin did not parse to its length");
        return (ch, list, q);
    }

    private static byte[] EncStr(string s)
    {
        var raw = Encoding.UTF8.GetBytes(s);
        var o = new byte[2 + raw.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(o, (ushort)raw.Length);
        raw.CopyTo(o, 2);
        return o;
    }

    private static byte[] EncEmbed(uint classHash, IReadOnlyList<(uint Hash, int Type, byte[] Value)> fields)
    {
        using var ms = new MemoryStream();
        Span<byte> tmp = stackalloc byte[5];
        var count = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(count, (ushort)fields.Count);
        ms.Write(count);
        foreach (var (h, t, v) in fields)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, h); tmp[4] = (byte)t;
            ms.Write(tmp); ms.Write(v);
        }
        var body = ms.ToArray();
        var o = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(o, classHash);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), (uint)body.Length);
        body.CopyTo(o, 8);
        return o;
    }

    private static string StringAt(byte[] b, int v) => Encoding.UTF8.GetString(b, v + 2, Read16(b, v));

    // ---------------------------------------------------------------- reading what a definition declares

    /// <summary>The parameters (name, width from the logical parameter's fields mask) a shader definition declares, in file order.</summary>
    public static IReadOnlyList<SgDeclaredParam> Declared(byte[] bin, string shaderDef)
    {
        var ents = Entries(bin);
        if (!ents.TryGetValue(HashAlgorithms.Fnv1a(shaderDef), out var e) || e.Class != ClassCustomShaderDef)
            throw new InvalidDataException($"shaders.bin has no CustomShaderDef '{shaderDef}'");
        var list = new List<SgDeclaredParam>();
        foreach (var f in ObjectFields(bin, e))
        {
            if (f.Hash != FParameters) continue;
            if (f.Type != 0x80 || bin[f.Start] != 0x83) throw new InvalidDataException("the 'parameters' of the definition are not a list of embedded values");
            int count = (int)Read32(bin, f.Start + 5), q = f.Start + 9;
            for (int i = 0; i < count; i++)
            {
                var (_, fl, q2) = StructFields(bin, q);
                string name = "";
                int width = 4;
                foreach (var pf in fl)
                {
                    if (pf.Hash == FName && pf.Type == 16) name = StringAt(bin, pf.Start);
                    else if (pf.Hash == FLogical && pf.Type == 0x80 && bin[pf.Start] == 0x83 && Read32(bin, pf.Start + 5) >= 1)
                    {
                        var (_, lf, _) = StructFields(bin, pf.Start + 9);
                        foreach (var x in lf)
                            if (x.Hash == FFields && x.Type is 6 or 7)
                            {
                                uint mask = Read32(bin, x.Start);
                                width = mask switch { 1 => 1, 3 => 2, 7 => 3, 15 => 4, _ => 4 };
                            }
                    }
                }
                list.Add(new SgDeclaredParam(name, width));
                q = q2;
            }
        }
        return list;
    }

    /// <summary>M835: the names of the textures a shader definition declares, in file order.</summary>
    public static IReadOnlyList<string> DeclaredTextures(byte[] bin, string shaderDef)
    {
        var ents = Entries(bin);
        if (!ents.TryGetValue(HashAlgorithms.Fnv1a(shaderDef), out var e) || e.Class != ClassCustomShaderDef)
            throw new InvalidDataException($"shaders.bin has no CustomShaderDef '{shaderDef}'");
        var list = new List<string>();
        foreach (var f in ObjectFields(bin, e))
        {
            if (f.Hash != FTextures) continue;
            if (f.Type != 0x80 || bin[f.Start] != 0x83) throw new InvalidDataException("the 'textures' of the definition are not a list of embedded values");
            int count = (int)Read32(bin, f.Start + 5), q = f.Start + 9;
            for (int i = 0; i < count; i++)
            {
                var (_, fl, q2) = StructFields(bin, q);
                foreach (var pf in fl)
                    if (pf.Hash == FName && pf.Type == 16) list.Add(StringAt(bin, pf.Start));
                q = q2;
            }
        }
        return list;
    }

    /// <summary>M835: the raw bytes of every element of a definition's <c>textures</c> list (class hash, size, fields), by texture name - for the check that a declared custom
    /// texture has the shape of Riot's DiffuseTexture entry.</summary>
    public static IReadOnlyDictionary<string, byte[]> TextureEntryBytes(byte[] bin, string shaderDef)
    {
        var ents = Entries(bin);
        if (!ents.TryGetValue(HashAlgorithms.Fnv1a(shaderDef), out var e) || e.Class != ClassCustomShaderDef)
            throw new InvalidDataException($"shaders.bin has no CustomShaderDef '{shaderDef}'");
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var f in ObjectFields(bin, e))
        {
            if (f.Hash != FTextures) continue;
            if (f.Type != 0x80 || bin[f.Start] != 0x83) throw new InvalidDataException("the 'textures' of the definition are not a list of embedded values");
            int count = (int)Read32(bin, f.Start + 5), q = f.Start + 9;
            for (int i = 0; i < count; i++)
            {
                var (_, fl, q2) = StructFields(bin, q);
                foreach (var pf in fl)
                    if (pf.Hash == FName && pf.Type == 16) map[StringAt(bin, pf.Start)] = bin.AsSpan(q, q2 - q).ToArray();
                q = q2;
            }
        }
        return map;
    }

    // ---------------------------------------------------------------- the patch

    /// <summary>Riot's <paramref name="riot"/> bytes with <paramref name="add"/> appended to the parameters of <paramref name="shaderDef"/> and NOTHING else changed.
    /// Throws <see cref="InvalidDataException"/> (with the reason) for a file it does not understand, a name already declared, or a definition shaped differently from
    /// what was proven in game (no <c>TintColor</c> element to copy).</summary>
    public static byte[] Patch(byte[] riot, string shaderDef, IReadOnlyList<SgDeclaredParam> add) => Patch(riot, shaderDef, add, Array.Empty<SgDeclaredTexture>());

    /// <summary>M835: the same, also appending <paramref name="addTextures"/> to the definition's <c>textures</c> list. Each new entry is a copy of Riot's own
    /// <c>DiffuseTexture</c> entry (exactly two fields: <c>name</c> and <c>defaultTexturePath</c>, a WadChunkLink); only the name - and the default link, when the graph
    /// names a default path - change. The caller decides WHETHER to ship them (<see cref="SgShip.CustomTexturesProven"/>).</summary>
    public static byte[] Patch(byte[] riot, string shaderDef, IReadOnlyList<SgDeclaredParam> add, IReadOnlyList<SgDeclaredTexture> addTextures)
    {
        if (add.Count == 0 && addTextures.Count == 0) return riot;
        var ents = Entries(riot);
        uint key = HashAlgorithms.Fnv1a(shaderDef);
        if (!ents.TryGetValue(key, out var e) || e.Class != ClassCustomShaderDef) throw new InvalidDataException($"shaders.bin has no CustomShaderDef '{shaderDef}'");
        var taken = Declared(riot, shaderDef).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var a in add)
        {
            if (!SgNames.IsIdentifier(a.Name, SgNames.MaxParamName)) throw new InvalidDataException($"'{a.Name}' is not a valid parameter name");
            if (a.Components is < 1 or > 4) throw new InvalidDataException($"'{a.Name}' has {a.Components} components");
            if (taken.Contains(a.Name)) throw new InvalidDataException($"'{a.Name}' is already declared by {shaderDef}");
            if (!taken.Add(a.Name)) throw new InvalidDataException($"'{a.Name}' is added twice");
        }

        var takenTex = DeclaredTextures(riot, shaderDef).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in addTextures)
        {
            if (!SgNames.IsIdentifier(t.Name, SgNames.MaxParamName)) throw new InvalidDataException($"'{t.Name}' is not a valid texture name");
            if (takenTex.Contains(t.Name)) throw new InvalidDataException($"the texture '{t.Name}' is already declared by {shaderDef}");
            if (!takenTex.Add(t.Name) || taken.Contains(t.Name)) throw new InvalidDataException($"the texture name '{t.Name}' is added twice or is also a parameter");
        }

        var outFields = new List<byte[]>();
        bool done = add.Count == 0, doneTex = addTextures.Count == 0;
        foreach (var f in ObjectFields(riot, e))
        {
            byte[] val = riot.AsSpan(f.Start, f.End - f.Start).ToArray();
            if (f.Hash == FTextures && addTextures.Count > 0)
            {
                if (f.Type != 0x80 || riot[f.Start] != 0x83) throw new InvalidDataException("the 'textures' of the definition are not a list of embedded values");
                int count = (int)Read32(riot, f.Start + 5), q = f.Start + 9;
                var elems = new List<byte[]>();
                (uint Class, List<Field> Fields)? diffuse = null;
                for (int i = 0; i < count; i++)
                {
                    var (ch, fl, q2) = StructFields(riot, q);
                    if (fl.Any(x => x.Hash == FName && x.Type == 16 && StringAt(riot, x.Start) == "DiffuseTexture")) diffuse = (ch, fl);
                    elems.Add(riot.AsSpan(q, q2 - q).ToArray());
                    q = q2;
                }
                if (q != f.End) throw new InvalidDataException("the texture list did not parse to its length");
                if (diffuse is null) throw new InvalidDataException($"{shaderDef} has no DiffuseTexture entry to copy the texture entry shape from");
                if (!diffuse.Value.Fields.Select(x => x.Hash).ToHashSet().SetEquals(new[] { FName, FDefaultTexturePath }))
                    throw new InvalidDataException("DiffuseTexture's entry has fields this patch does not know (expected name and defaultTexturePath)");
                foreach (var a in addTextures)
                {
                    var nf = new List<(uint, int, byte[])>();
                    foreach (var x in diffuse.Value.Fields)
                    {
                        if (x.Hash == FName) nf.Add((x.Hash, x.Type, EncStr(a.Name)));
                        else if (x.Hash == FDefaultTexturePath && a.DefaultPath.Length > 0)
                        {
                            if (x.Type != 18) throw new InvalidDataException("DiffuseTexture's defaultTexturePath is not a WadChunkLink");
                            var link = new byte[8];
                            BinaryPrimitives.WriteUInt64LittleEndian(link, HashAlgorithms.WadPath(a.DefaultPath.Trim().Replace('\\', '/')));
                            nf.Add((x.Hash, x.Type, link));
                        }
                        else nf.Add((x.Hash, x.Type, riot.AsSpan(x.Start, x.End - x.Start).ToArray()));
                    }
                    elems.Add(EncEmbed(diffuse.Value.Class, nf));
                }
                int bodyLen = elems.Sum(x => x.Length);
                var nv = new byte[9 + bodyLen];
                nv[0] = 0x83;
                BinaryPrimitives.WriteUInt32LittleEndian(nv.AsSpan(1), (uint)(4 + bodyLen));
                BinaryPrimitives.WriteUInt32LittleEndian(nv.AsSpan(5), (uint)(count + addTextures.Count));
                int at = 9;
                foreach (var x in elems) { x.CopyTo(nv, at); at += x.Length; }
                val = nv;
                doneTex = true;
            }
            else if (f.Hash == FParameters && add.Count > 0)
            {
                if (f.Type != 0x80 || riot[f.Start] != 0x83) throw new InvalidDataException("the 'parameters' of the definition are not a list of embedded values");
                int count = (int)Read32(riot, f.Start + 5), q = f.Start + 9;
                var elems = new List<byte[]>();
                (uint Class, List<Field> Fields)? tint = null;
                for (int i = 0; i < count; i++)
                {
                    var (ch, fl, q2) = StructFields(riot, q);
                    if (fl.Any(x => x.Hash == FName && x.Type == 16 && StringAt(riot, x.Start) == "TintColor")) tint = (ch, fl);
                    elems.Add(riot.AsSpan(q, q2 - q).ToArray());
                    q = q2;
                }
                if (q != f.End) throw new InvalidDataException("the parameter list did not parse to its length");
                if (tint is null) throw new InvalidDataException($"{shaderDef} has no TintColor parameter to copy the entry shape from");
                foreach (var a in add)
                {
                    var nf = new List<(uint, int, byte[])>();
                    foreach (var x in tint.Value.Fields)
                    {
                        if (x.Hash == FData) continue;   // Riot omits zero defaults
                        if (x.Hash == FName) nf.Add((x.Hash, x.Type, EncStr(a.Name)));
                        else if (x.Hash == FLogical)
                        {
                            if (x.Type != 0x80 || riot[x.Start] != 0x83 || Read32(riot, x.Start + 5) != 1) throw new InvalidDataException("TintColor's logicalParameters are not one embedded value");
                            var (lch, lfl, lend) = StructFields(riot, x.Start + 9);
                            if (lend != x.End) throw new InvalidDataException("TintColor's logical parameter did not end where its list does");
                            var lf = new List<(uint, int, byte[])>();
                            foreach (var y in lfl)
                            {
                                if (y.Hash == FName) lf.Add((y.Hash, y.Type, EncStr(a.Name)));
                                else if (y.Hash == FFields) { var m = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(m, a.FieldsMask); lf.Add((y.Hash, y.Type, m)); }
                                else lf.Add((y.Hash, y.Type, riot.AsSpan(y.Start, y.End - y.Start).ToArray()));
                            }
                            if (lf.Select(z => z.Item1).ToHashSet().SetEquals(new[] { FName, FFields }) is false) throw new InvalidDataException("TintColor's logical parameter has fields this patch does not know");
                            var inner = EncEmbed(lch, lf);
                            var list = new byte[1 + 8 + inner.Length];
                            list[0] = 0x83;
                            BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(1), (uint)(4 + inner.Length));
                            BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(5), 1);
                            inner.CopyTo(list, 9);
                            nf.Add((x.Hash, x.Type, list));
                        }
                        else throw new InvalidDataException($"TintColor's parameter holds a field (0x{x.Hash:x8}) this patch does not know");
                    }
                    elems.Add(EncEmbed(tint.Value.Class, nf));
                }
                int bodyLen = elems.Sum(x => x.Length);
                var nv = new byte[9 + bodyLen];
                nv[0] = 0x83;
                BinaryPrimitives.WriteUInt32LittleEndian(nv.AsSpan(1), (uint)(4 + bodyLen));
                BinaryPrimitives.WriteUInt32LittleEndian(nv.AsSpan(5), (uint)(count + add.Count));
                int at = 9;
                foreach (var x in elems) { x.CopyTo(nv, at); at += x.Length; }
                val = nv;
                done = true;
            }
            var field = new byte[5 + val.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(field, f.Hash); field[4] = (byte)f.Type;
            val.CopyTo(field, 5);
            outFields.Add(field);
        }
        if (!done) throw new InvalidDataException($"{shaderDef} has no 'parameters' list");
        if (!doneTex) throw new InvalidDataException($"{shaderDef} has no 'textures' list");

        int bodySize = 4 + 2 + outFields.Sum(x => x.Length);
        var entry = new byte[4 + bodySize];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)bodySize);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), key);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), (ushort)outFields.Count);
        int w = 10;
        foreach (var x in outFields) { x.CopyTo(entry, w); w += x.Length; }

        var result = new byte[riot.Length - (e.End - e.Start) + entry.Length];
        riot.AsSpan(0, e.Start).CopyTo(result);
        entry.CopyTo(result, e.Start);
        riot.AsSpan(e.End).CopyTo(result.AsSpan(e.Start + entry.Length));
        return result;
    }

    /// <summary>The bindiff-style check, in code: the patched file has the same objects in the same order with the same header, only
    /// <paramref name="shaderDef"/> differs, and it now declares Riot's parameters unchanged followed by exactly <paramref name="added"/>. Empty = fine.</summary>
    public static IReadOnlyList<string> Verify(byte[] riot, byte[] patched, string shaderDef, IReadOnlyList<SgDeclaredParam> added) =>
        Verify(riot, patched, shaderDef, added, Array.Empty<SgDeclaredTexture>());

    public static IReadOnlyList<string> Verify(byte[] riot, byte[] patched, string shaderDef, IReadOnlyList<SgDeclaredParam> added, IReadOnlyList<SgDeclaredTexture> addedTextures)
    {
        var bad = new List<string>();
        try
        {
            var a = Entries(riot); var b = Entries(patched);
            if (!a.Keys.SequenceEqual(b.Keys)) { bad.Add("the patched shaders.bin has a different set or order of objects"); return bad; }
            uint key = HashAlgorithms.Fnv1a(shaderDef);
            if (!riot.AsSpan(0, a.Values.First().Start).SequenceEqual(patched.AsSpan(0, b.Values.First().Start))) bad.Add("the header of shaders.bin changed");
            foreach (var h in a.Keys)
            {
                bool same = riot.AsSpan(a[h].Start, a[h].End - a[h].Start).SequenceEqual(patched.AsSpan(b[h].Start, b[h].End - b[h].Start));
                if (h == key ? same && (added.Count > 0 || addedTextures.Count > 0) : !same) bad.Add(h == key ? "the shader definition was not changed" : $"object 0x{h:x8} differs from Riot's");
            }
            var before = Declared(riot, shaderDef); var after = Declared(patched, shaderDef);
            if (!after.Take(before.Count).SequenceEqual(before)) bad.Add("Riot's own parameters of the definition changed");
            if (!after.Skip(before.Count).SequenceEqual(added)) bad.Add("the declared parameters are not Riot's followed by the added ones");
            var tb = DeclaredTextures(riot, shaderDef); var ta = DeclaredTextures(patched, shaderDef);
            if (!ta.Take(tb.Count).SequenceEqual(tb)) bad.Add("Riot's own textures of the definition changed");
            if (!ta.Skip(tb.Count).SequenceEqual(addedTextures.Select(t => t.Name))) bad.Add("the declared textures are not Riot's followed by the added ones");
        }
        catch (InvalidDataException ex) { bad.Add(ex.Message); }
        return bad;
    }
}
