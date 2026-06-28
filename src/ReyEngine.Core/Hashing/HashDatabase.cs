using System.Globalization;

namespace ReyEngine.Core.Hashing;

/// <summary>
/// Merged hash dictionary for WAD paths (XxHash64, 64-bit) and .bin names
/// (FNV-1a, 32-bit). Keeps all candidate strings when several map to the same hash
/// (collision/conflict), and persists to a fast binary cache.
/// </summary>
public sealed class HashDatabase : IHashResolver
{
    private const uint CacheMagic = 0x31424852; // "RHB1"

    private readonly Dictionary<ulong, string> _wad = new();
    private readonly Dictionary<uint, string> _bin = new();
    private readonly Dictionary<ulong, List<string>> _wadConflicts = new();
    private readonly Dictionary<uint, List<string>> _binConflicts = new();

    public int WadCount => _wad.Count;
    public int BinCount => _bin.Count;
    public int ConflictCount => _wadConflicts.Count + _binConflicts.Count;

    public bool TryGetPath(ulong hash, out string path) => _wad.TryGetValue(hash, out path!);
    public string ResolvePath(ulong hash) => _wad.TryGetValue(hash, out var p) ? p : $"0x{hash:x16}.unknown";
    public bool TryGetBinName(uint hash, out string name) => _bin.TryGetValue(hash, out name!);

    public IReadOnlyList<string> WadCandidates(ulong hash) =>
        _wadConflicts.TryGetValue(hash, out var l) ? l
        : _wad.TryGetValue(hash, out var s) ? new[] { s }
        : Array.Empty<string>();

    public IReadOnlyList<string> BinCandidates(uint hash) =>
        _binConflicts.TryGetValue(hash, out var l) ? l
        : _bin.TryGetValue(hash, out var s) ? new[] { s }
        : Array.Empty<string>();

    public void AddWad(ulong hash, string value)
    {
        if (_wad.TryGetValue(hash, out var cur))
        {
            if (cur == value) return;
            if (!_wadConflicts.TryGetValue(hash, out var l)) { l = new List<string> { cur }; _wadConflicts[hash] = l; }
            if (!l.Contains(value)) l.Add(value);
        }
        else _wad[hash] = value;
    }

    public void AddBin(uint hash, string value)
    {
        if (_bin.TryGetValue(hash, out var cur))
        {
            if (cur == value) return;
            if (!_binConflicts.TryGetValue(hash, out var l)) { l = new List<string> { cur }; _binConflicts[hash] = l; }
            if (!l.Contains(value)) l.Add(value);
        }
        else _bin[hash] = value;
    }

    public void Clear()
    {
        _wad.Clear(); _bin.Clear(); _wadConflicts.Clear(); _binConflicts.Clear();
    }

    /// <summary>
    /// Parse a CDTB hash list ("&lt;hex&gt; &lt;string&gt;" per line). Bin vs WAD is decided by
    /// filename (anything containing "bin" is 32-bit), falling back to hex length. RST files are skipped.
    /// </summary>
    public (int count, bool isBin) LoadTextFile(string file)
    {
        var name = Path.GetFileName(file).ToLowerInvariant();
        if (name.Contains("rst")) return (0, false); // translation hashes — not WAD/bin paths
        bool isBin = name.Contains("bin");

        int n = 0;
        foreach (var line in File.ReadLines(file))
        {
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var hex = line.AsSpan(0, sp);
            string value = line[(sp + 1)..];

            if (isBin || hex.Length <= 8)
            {
                if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) { AddBin(h, value); n++; }
            }
            else
            {
                if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) { AddWad(h, value); n++; }
            }
        }
        return (n, isBin);
    }

    /// <summary>Merge any loose .txt files placed directly in <c>data/hashes/</c> (manual dictionaries).</summary>
    public void LoadManualDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.txt"))
            LoadTextFile(f);
    }

    // ---- Binary cache ----------------------------------------------------

    public void SaveCache(string file)
    {
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new BinaryWriter(fs);
        w.Write(CacheMagic);

        w.Write(_wad.Count);
        foreach (var (h, s) in _wad) { w.Write(h); w.Write(s); }
        w.Write(_bin.Count);
        foreach (var (h, s) in _bin) { w.Write(h); w.Write(s); }

        WriteConflicts(w, _wadConflicts, static (bw, k) => bw.Write(k));
        WriteConflicts(w, _binConflicts, static (bw, k) => bw.Write(k));
    }

    public bool LoadCache(string file)
    {
        if (!File.Exists(file)) return false;
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var r = new BinaryReader(fs);
        if (r.ReadUInt32() != CacheMagic) return false;

        Clear();
        int wadN = r.ReadInt32();
        for (int i = 0; i < wadN; i++) { ulong h = r.ReadUInt64(); _wad[h] = r.ReadString(); }
        int binN = r.ReadInt32();
        for (int i = 0; i < binN; i++) { uint h = r.ReadUInt32(); _bin[h] = r.ReadString(); }

        ReadConflicts(r, _wadConflicts, static br => br.ReadUInt64());
        ReadConflicts(r, _binConflicts, static br => br.ReadUInt32());
        return true;
    }

    private static void WriteConflicts<TKey>(BinaryWriter w, Dictionary<TKey, List<string>> map, Action<BinaryWriter, TKey> writeKey)
        where TKey : notnull
    {
        w.Write(map.Count);
        foreach (var (k, list) in map)
        {
            writeKey(w, k);
            w.Write(list.Count);
            foreach (var s in list) w.Write(s);
        }
    }

    private static void ReadConflicts<TKey>(BinaryReader r, Dictionary<TKey, List<string>> map, Func<BinaryReader, TKey> readKey)
        where TKey : notnull
    {
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var key = readKey(r);
            int n = r.ReadInt32();
            var list = new List<string>(n);
            for (int j = 0; j < n; j++) list.Add(r.ReadString());
            map[key] = list;
        }
    }
}
