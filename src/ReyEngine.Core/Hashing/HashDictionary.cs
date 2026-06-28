using System.Globalization;

namespace ReyEngine.Core.Hashing;

/// <summary>
/// Resolves obfuscated hashes back to readable paths/names using community hash
/// lists (CDTB format: "&lt;hex&gt; &lt;path&gt;" per line).
/// </summary>
public sealed class HashDictionary
{
    private readonly Dictionary<ulong, string> _wadPaths = new();
    private readonly Dictionary<uint, string> _binNames = new();

    public int WadPathCount => _wadPaths.Count;
    public int BinNameCount => _binNames.Count;

    public bool TryGetPath(ulong hash, out string path) => _wadPaths.TryGetValue(hash, out path!);
    public bool TryGetBinName(uint hash, out string name) => _binNames.TryGetValue(hash, out name!);

    public string ResolvePath(ulong hash) =>
        _wadPaths.TryGetValue(hash, out var p) ? p : $"0x{hash:x16}.unknown";

    /// <summary>Load a 64-bit (WAD path) hash list. Returns number of entries added.</summary>
    public int LoadWadHashes(string file)
    {
        int n = 0;
        foreach (var line in File.ReadLines(file))
        {
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            if (ulong.TryParse(line.AsSpan(0, sp), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
            {
                _wadPaths[h] = line[(sp + 1)..].Trim();
                n++;
            }
        }
        return n;
    }

    /// <summary>Load a 32-bit (bin field/class/entry) hash list. Returns number of entries added.</summary>
    public int LoadBinHashes(string file)
    {
        int n = 0;
        foreach (var line in File.ReadLines(file))
        {
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            if (uint.TryParse(line.AsSpan(0, sp), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
            {
                _binNames[h] = line[(sp + 1)..].Trim();
                n++;
            }
        }
        return n;
    }

    /// <summary>Load every recognised hash file in a directory by filename convention.</summary>
    public (int wad, int bin) LoadDirectory(string dir)
    {
        int wad = 0, bin = 0;
        if (!Directory.Exists(dir)) return (0, 0);
        foreach (var file in Directory.EnumerateFiles(dir, "*.txt"))
        {
            var name = Path.GetFileName(file).ToLowerInvariant();
            if (name.Contains("bin")) bin += LoadBinHashes(file);
            else wad += LoadWadHashes(file);
        }
        return (wad, bin);
    }

    /// <summary>Remember a path you discovered so its hash resolves next time.</summary>
    public void Register(string path) => _wadPaths[HashAlgorithms.WadPath(path)] = path;
}
