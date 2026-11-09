using System.Buffers.Binary;
using System.Text;

namespace ReyEngine.Formats.Scripts;

/// <summary>A <c>.luabin64</c> that is not the Lua 5.1 build League ships (header mismatch) or is cut short.</summary>
public sealed class LuaBin64Exception : Exception
{
    public LuaBin64Exception(string message) : base(message) { }
}

/// <summary>A Lua table built by the main chunk of a property script. Array items are 1-based in Lua; <see cref="Array"/> is in order. Keys are strings, doubles or booleans.</summary>
public sealed class LuaTable
{
    public List<object?> Array { get; } = new();
    public List<KeyValuePair<object, object?>> Hash { get; } = new();

    public object? Get(object key)
    {
        for (int i = Hash.Count - 1; i >= 0; i--) if (Equals(Hash[i].Key, key)) return Hash[i].Value;
        return null;
    }

    public string? GetString(string key) => Get(key) as string;
}

/// <summary>The value of a register whose origin the walker does not model (a call, an upvalue, arithmetic). It is never guessed at.</summary>
public sealed class LuaUnknown
{
    public static readonly LuaUnknown Instance = new();
    private LuaUnknown() { }
    public override string ToString() => "<unknown>";
}

/// <summary>One function prototype of a chunk. Constants are nil (null), bool, double or string (bytes read as Latin-1).</summary>
public sealed class LuaFunctionProto
{
    public string? Source { get; init; }
    public int LineDefined { get; init; }
    public int LastLine { get; init; }
    public int Upvalues { get; init; }
    public int Params { get; init; }
    public int VarArg { get; init; }
    public int MaxStack { get; init; }
    public required uint[] Code { get; init; }
    public required object?[] Constants { get; init; }
    public required IReadOnlyList<LuaFunctionProto> Protos { get; init; }
}

/// <summary>
/// M891: a <c>.luabin64</c> - Lua 5.1 bytecode, format 0, little-endian, int 4, size_t 8, instruction 4, number 8, not integral. The header, every function prototype and its constants are read.
///
/// <para>The flat property scripts (spells, buffs) are a main function of <c>SETGLOBAL</c>s: <c>BuffName = "AatroxW"</c>, <c>SpellFXOverrideSkins = {"AatroxSkin02"}</c>. <see cref="Globals"/> is
/// recovered by walking the main function's instructions in order over a register file (LOADK, LOADBOOL, LOADNIL, MOVE, NEWTABLE, SETTABLE, SETLIST, GETTABLE, GETGLOBAL of an earlier global,
/// SETGLOBAL, UNM) - not by pairing constants that sit next to each other. An instruction outside that set (a call, arithmetic) makes the register it writes
/// <see cref="LuaUnknown"/> and adds a note; a jump or test stops the walk, since what follows depends on a branch. No value is invented, and <see cref="Notes"/> says what was missed.</para>
/// </summary>
public sealed class LuaBin64File
{
    private const int MaxDepth = 64;
    private const int MaxNotes = 24;

    public int Version { get; private init; }
    public LuaFunctionProto Main { get; private init; } = null!;
    public int TrailingBytes { get; private init; }

    /// <summary>The globals the main function sets, in order of assignment; a name set twice appears twice (<see cref="TryGet"/> returns the last).</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Globals { get; private set; } = Array.Empty<KeyValuePair<string, object?>>();

    /// <summary>What the instruction walk could not model.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = Array.Empty<string>();

    public bool TryGet(string name, out object? value)
    {
        for (int i = Globals.Count - 1; i >= 0; i--)
            if (Globals[i].Key == name) { value = Globals[i].Value; return true; }
        value = null;
        return false;
    }

    /// <summary>The global as a string, or null when it is absent, not a string, or not known.</summary>
    public string? GetString(string name) => TryGet(name, out var v) ? v as string : null;

    /// <summary>The global as a list of strings: a table's array items that are strings. Null when absent or not a table.</summary>
    public IReadOnlyList<string>? GetStrings(string name) =>
        TryGet(name, out var v) && v is LuaTable t ? t.Array.OfType<string>().ToList() : null;

    public bool? GetBool(string name) => TryGet(name, out var v) && v is bool b ? b : null;

    public static bool LooksLikeLuaBin(ReadOnlySpan<byte> d) => d.Length >= 12 && d[0] == 0x1B && d[1] == 'L' && d[2] == 'u' && d[3] == 'a' && d[4] == 0x51;

    public static LuaBin64File Parse(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeLuaBin(data)) throw new LuaBin64Exception("not Lua 5.1 bytecode (missing ESC Lua 0x51)");
        var r = new Reader(data);
        r.Take(4);
        int version = r.Byte();
        int format = r.Byte(), endian = r.Byte(), intSize = r.Byte(), sizeT = r.Byte(), instr = r.Byte(), num = r.Byte(), integral = r.Byte();
        if (format != 0 || endian != 1 || intSize != 4 || sizeT != 8 || instr != 4 || num != 8 || integral != 0)
            throw new LuaBin64Exception($"unsupported header: format {format}, endian {endian}, int {intSize}, size_t {sizeT}, instruction {instr}, number {num}, integral {integral}");
        var main = ReadFunction(ref r, 0);
        var file = new LuaBin64File { Version = version, Main = main, TrailingBytes = r.Remaining };
        file.Walk();
        return file;
    }

    private static LuaFunctionProto ReadFunction(ref Reader r, int depth)
    {
        if (depth > MaxDepth) throw new LuaBin64Exception("function nesting is too deep");
        string? source = r.String();
        int line = r.Int(), last = r.Int();
        int ups = r.Byte(), pars = r.Byte(), vararg = r.Byte(), stack = r.Byte();
        int codeCount = r.Count(4);
        var code = new uint[codeCount];
        for (int i = 0; i < codeCount; i++) code[i] = BinaryPrimitives.ReadUInt32LittleEndian(r.Take(4));
        int constCount = r.Count(1);
        var consts = new object?[constCount];
        for (int i = 0; i < constCount; i++)
        {
            int t = r.Byte();
            consts[i] = t switch
            {
                0 => null,
                1 => r.Byte() != 0,
                3 => BitConverter.ToDouble(r.Take(8)),
                4 => r.String() ?? "",
                _ => throw new LuaBin64Exception($"unknown constant type {t}"),
            };
        }
        int protoCount = r.Count(1);
        var protos = new List<LuaFunctionProto>(protoCount);
        for (int i = 0; i < protoCount; i++) protos.Add(ReadFunction(ref r, depth + 1));
        r.Take(r.Count(4) * 4); // line info
        int locals = r.Count(16);
        for (int i = 0; i < locals; i++) { r.String(); r.Int(); r.Int(); }
        int upNames = r.Count(8);
        for (int i = 0; i < upNames; i++) r.String();
        return new LuaFunctionProto { Source = source, LineDefined = line, LastLine = last, Upvalues = ups, Params = pars, VarArg = vararg, MaxStack = stack, Code = code, Constants = consts, Protos = protos };
    }

    // ================================================================== the instruction walk

    private static readonly string[] OpNames =
    {
        "MOVE", "LOADK", "LOADBOOL", "LOADNIL", "GETUPVAL", "GETGLOBAL", "GETTABLE", "SETGLOBAL", "SETUPVAL", "SETTABLE", "NEWTABLE", "SELF", "ADD", "SUB", "MUL", "DIV", "MOD", "POW",
        "UNM", "NOT", "LEN", "CONCAT", "JMP", "EQ", "LT", "LE", "TEST", "TESTSET", "CALL", "TAILCALL", "RETURN", "FORLOOP", "FORPREP", "TFORLOOP", "SETLIST", "CLOSE", "CLOSURE", "VARARG",
    };

    private void Walk()
    {
        var f = Main;
        var notes = new List<string>();
        var globals = new List<KeyValuePair<string, object?>>();
        var reg = new object?[256];
        object? Rk(int x) => x >= 256 ? (x - 256 < f.Constants.Length ? f.Constants[x - 256] : LuaUnknown.Instance) : reg[x];
        void Note(string s) { if (notes.Count < MaxNotes) notes.Add(s); else if (notes.Count == MaxNotes) notes.Add("more notes omitted"); }
        var code = f.Code;
        for (int pc = 0; pc < code.Length; pc++)
        {
            uint c = code[pc];
            int op = (int)(c & 63), a = (int)((c >> 6) & 255), cc = (int)((c >> 14) & 511), b = (int)((c >> 23) & 511), bx = (int)(c >> 14);
            string name = op < OpNames.Length ? OpNames[op] : "OP" + op;
            switch (op)
            {
                case 0: reg[a] = reg[b & 255]; break; // MOVE
                case 1: reg[a] = bx < f.Constants.Length ? f.Constants[bx] : LuaUnknown.Instance; break; // LOADK
                case 2: reg[a] = b != 0; if (cc != 0) pc++; break; // LOADBOOL
                case 3: for (int i = a; i <= b && i < 256; i++) reg[i] = null; break; // LOADNIL
                case 5: // GETGLOBAL: only a global this script set earlier is known
                    {
                        string? g = bx < f.Constants.Length ? f.Constants[bx] as string : null;
                        int at = g is null ? -1 : globals.FindLastIndex(x => x.Key == g);
                        if (at >= 0) reg[a] = globals[at].Value;
                        else { reg[a] = LuaUnknown.Instance; Note($"pc {pc}: GETGLOBAL {g} is not set by this script"); }
                        break;
                    }
                case 6: // GETTABLE
                    {
                        var key = Rk(cc);
                        reg[a] = reg[b & 255] is LuaTable t && key is not LuaUnknown && key is not null ? t.Get(key) : LuaUnknown.Instance;
                        if (reg[a] is LuaUnknown) Note($"pc {pc}: GETTABLE on an unknown value");
                        break;
                    }
                case 7: // SETGLOBAL
                    {
                        string? g = bx < f.Constants.Length ? f.Constants[bx] as string : null;
                        if (g is null) { Note($"pc {pc}: SETGLOBAL with a non-string name"); break; }
                        if (reg[a] is LuaUnknown) Note($"pc {pc}: {g} has no known value");
                        globals.Add(new(g, reg[a]));
                        break;
                    }
                case 9: // SETTABLE
                    {
                        var key = Rk(b);
                        var val = Rk(cc);
                        if (reg[a] is LuaTable t && key is not LuaUnknown && key is not null) t.Hash.Add(new(key, val));
                        else Note($"pc {pc}: SETTABLE on an unknown table or key");
                        break;
                    }
                case 10: reg[a] = new LuaTable(); break; // NEWTABLE
                case 18: // UNM
                    reg[a] = reg[b & 255] is double d ? -d : LuaUnknown.Instance;
                    if (reg[a] is LuaUnknown) Note($"pc {pc}: UNM of a non-number");
                    break;
                case 19: reg[a] = reg[b & 255] is bool nb ? !nb : LuaUnknown.Instance; break; // NOT
                case 34: // SETLIST
                    {
                        int block = cc;
                        if (block == 0) { block = pc + 1 < code.Length ? (int)code[pc + 1] : 1; pc++; }
                        if (b == 0 || reg[a] is not LuaTable t) { Note($"pc {pc}: SETLIST with an open count or an unknown table"); break; }
                        int baseIndex = (block - 1) * 50;
                        while (t.Array.Count < baseIndex + b) t.Array.Add(null);
                        for (int i = 1; i <= b && a + i < 256; i++) t.Array[baseIndex + i - 1] = reg[a + i];
                        break;
                    }
                case 30: // RETURN
                    goto done;
                case 22 or 23 or 24 or 25 or 26 or 27 or 31 or 32 or 33: // jumps and tests: what follows depends on a branch
                    Note($"pc {pc}: {name} - the walk stops here, later assignments are not read");
                    goto done;
                case 35: break; // CLOSE
                default:
                    // CALL, SELF, arithmetic, CONCAT, LEN, CLOSURE, VARARG, upvalues: the register they write is not modelled
                    Note($"pc {pc}: {name} is not modelled");
                    if (op is 4 or 8 or 11 or (>= 12 and <= 17) or 20 or 21 or 28 or 29 or 36 or 37) reg[a] = LuaUnknown.Instance;
                    break;
            }
        }
    done:
        Globals = globals;
        Notes = notes;
    }

    // ================================================================== bounds-checked reader

    private ref struct Reader
    {
        private ReadOnlySpan<byte> _rest;
        public Reader(ReadOnlySpan<byte> data) { _rest = data; }
        public readonly int Remaining => _rest.Length;

        public ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || n > _rest.Length) throw new LuaBin64Exception("unexpected end of data");
            var s = _rest[..n];
            _rest = _rest[n..];
            return s;
        }

        public int Byte() => Take(1)[0];
        public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        /// <summary>A count whose items each need at least <paramref name="minItemBytes"/>, refused before any allocation when the rest of the data cannot hold it.</summary>
        public int Count(int minItemBytes)
        {
            int n = Int();
            if (n < 0 || (long)n * minItemBytes > _rest.Length) throw new LuaBin64Exception($"count {n} does not fit in the remaining {_rest.Length} bytes");
            return n;
        }

        public string? String()
        {
            ulong n = BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
            if (n == 0) return null;
            if (n > (ulong)_rest.Length) throw new LuaBin64Exception("string length exceeds the data");
            var s = Take((int)n);
            return Encoding.Latin1.GetString(s[..^1]);
        }
    }
}
