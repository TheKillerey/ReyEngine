using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Cleanup;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M302: "can anything in this project reach this file?", answered over the project's own .bin files -
/// which is where League keeps every asset reference there is.
///
/// <para>The existing Asset Usage report answered this with an exact string match on the relative path.
/// That is fine for a report you read, and NOT fine for a tool that deletes: a bin routinely names the
/// same asset with a different case, a leading slash, a backslash, no folder at all, or as a bare hash,
/// and every one of those spellings would have read as "nothing references it".</para>
///
/// <para>So a reference is recorded under several keys at once - full path, path without extension, bare
/// file name, the WAD path hash, and the FNV-1a name hash - and a lookup wins on ANY of them. The
/// asymmetry is deliberate: a false match keeps a file that could have gone, a missed match deletes a
/// file the mod needs. Only one of those is recoverable by scanning again.</para>
/// </summary>
public sealed class ProjectReferenceIndex : IReferenceIndex
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ulong> _wadHashes = new();
    private readonly HashSet<uint> _nameHashes = new();

    public int PathCount => _paths.Count;
    public int HashCount => _wadHashes.Count + _nameHashes.Count;
    public int BinsRead { get; private set; }

    /// <summary>Files that carry a bin magic and still would not parse. This is the number that matters:
    /// each one is a set of references the scan cannot see, so a non-zero count must make the caller
    /// more cautious, not merely be logged.</summary>
    public int BinsFailed { get; private set; }

    /// <summary>Files handed in that are not bins at all. Harmless - an unknown chunk written ".bin"
    /// because its type could not be sniffed was never going to contain references.</summary>
    public int NotBins { get; private set; }

    /// <summary>True when every real bin was read, so "nothing references it" is a claim the index can
    /// actually support.</summary>
    public bool IsComplete => BinsFailed == 0;

    /// <summary>Feed one .bin. Broken bins contribute nothing rather than aborting the scan - a mod with
    /// one unreadable bin must still be cleanable, just more cautiously.</summary>
    public void AddBin(byte[] bytes)
    {
        // PROP/PTCH is the bin magic. Checked on content, because M300 names any unidentifiable chunk
        // ".bin", and counting those as parse failures would make every project look incomplete.
        if (bytes.Length < 4 || bytes[0] != 'P'
            || !((bytes[1] == 'R' && bytes[2] == 'O' && bytes[3] == 'P')
              || (bytes[1] == 'T' && bytes[2] == 'C' && bytes[3] == 'H')))
        { NotBins++; return; }

        BinTree tree;
        try { tree = SafeBinTree.Parse(bytes); }
        catch { BinsFailed++; return; }

        var strings = new List<string>();
        try { BinStringHarvester.Collect(tree, strings); }
        catch { BinsFailed++; return; }
        foreach (var s in strings) AddReference(s);

        // Link-shaped references carry no string at all. An ObjectLink names another bin object by its
        // FNV hash, and a Hash field names an asset the same way - both are references that a purely
        // string-based harvest cannot see.
        try
        {
            foreach (var o in tree.Objects.Values)
            {
                _nameHashes.Add(o.PathHash);
                foreach (var p in o.Properties.Values) WalkLinks(p, 0);
            }
        }
        catch { /* partial harvest still beats none */ }

        BinsRead++;
    }

    private void WalkLinks(BinTreeProperty p, int depth)
    {
        if (depth > 32) return;
        switch (p)
        {
            case BinTreeObjectLink l: _nameHashes.Add(l.Value); break;
            case BinTreeHash h: _nameHashes.Add(h.Value); break;
            case BinTreeContainer c: foreach (var el in c.Elements) WalkLinks(el, depth + 1); break;
            case BinTreeStruct st: foreach (var v in st.Properties.Values) WalkLinks(v, depth + 1); break;
            case BinTreeOptional { Value: { } inner }: WalkLinks(inner, depth + 1); break;
            case BinTreeMap m: foreach (var (k, v) in m) { WalkLinks(k, depth + 1); WalkLinks(v, depth + 1); } break;
        }
    }

    /// <summary>Harvest names embedded in a NON-bin asset - .mapgeo, .skn, .scb, .sco all carry material
    /// and texture names inline, and those are references no bin scan can see. This is the "loaded through
    /// another asset definition" channel: a mapgeo names a material, the material names a texture, and only
    /// the middle link lives in a bin.
    ///
    /// <para>Printable-run scanning rather than parsing each format, for the same reason M301 chose it:
    /// the goal is to find MORE reasons to keep a file, so recall matters and precision does not - a
    /// spurious name can only ever spare a file, never delete one.</para></summary>
    public void AddAssetNames(byte[] bytes, int maxBytes = 64 * 1024 * 1024)
    {
        int limit = Math.Min(bytes.Length, maxBytes);
        int start = -1;
        for (int i = 0; i < limit; i++)
        {
            byte b = bytes[i];
            if (b >= 0x20 && b < 0x7F) { if (start < 0) start = i; continue; }
            if (start >= 0 && i - start >= 4)
            {
                string run = Encoding.ASCII.GetString(bytes, start, Math.Min(i - start, 512));
                // Only name-shaped runs: a path, or a bare identifier. Skips the numeric/binary noise
                // that happens to land in printable range.
                if (run.IndexOf('/') > 0 || run.IndexOf('.') > 0 || run.IndexOf('_') > 0)
                    AddReference(run);
            }
            start = -1;
        }
        if (start >= 0 && limit - start >= 4)
        {
            string run = Encoding.ASCII.GetString(bytes, start, Math.Min(limit - start, 512));
            if (run.IndexOf('/') > 0 || run.IndexOf('.') > 0 || run.IndexOf('_') > 0)
                AddReference(run);
        }
    }

    /// <summary>Record one referencing string under every spelling it could match a file by.</summary>
    public void AddReference(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 512) return;
        string s = raw.Replace('\\', '/').Trim().TrimStart('/');
        if (s.Length == 0) return;

        // Path-shaped strings index as paths. Everything else is still worth keeping as a NAME, because
        // map bins name characters and particle systems by bare identifier and those become file stems.
        bool pathy = s.Contains('/') || s.Contains('.');
        if (pathy)
        {
            _paths.Add(s);
            _wadHashes.Add(HashAlgorithms.WadPath(s));
            string noExt = StripExtension(s);
            _stems.Add(noExt);
            _nameHashes.Add(HashAlgorithms.Fnv1a(s));
            _nameHashes.Add(HashAlgorithms.Fnv1a(noExt));

            int slash = s.LastIndexOf('/');
            string file = slash < 0 ? s : s[(slash + 1)..];
            if (file.Length > 0) { _names.Add(file); _names.Add(StripExtension(file)); }
        }
        else
        {
            _names.Add(s);
            _nameHashes.Add(HashAlgorithms.Fnv1a(s));
        }
    }

    /// <summary>Record a reference that is already a WAD path hash (e.g. one held in project metadata).</summary>
    public void AddHash(ulong wadPathHash) => _wadHashes.Add(wadPathHash);

    public bool IsReferenced(string relPath, ulong pathHash, out string how)
    {
        // The hash is spelling-proof: WadPath lowercases and normalises separators, so this one check
        // already absorbs case and slash differences for every path-shaped reference harvested.
        if (_wadHashes.Contains(pathHash)) { how = "path hash referenced by a project bin"; return true; }

        string s = relPath.Replace('\\', '/').TrimStart('/');
        if (_paths.Contains(s)) { how = "path referenced by a project bin"; return true; }

        string noExt = StripExtension(s);
        if (_stems.Contains(noExt)) { how = "referenced with a different extension"; return true; }

        // Extension pairs the pipeline converts between: a bin naming the source art still means the
        // shipped conversion is in use.
        foreach (var alt in AltExtensions(s))
            if (_paths.Contains(alt) || _wadHashes.Contains(HashAlgorithms.WadPath(alt)))
            { how = "referenced as " + Path.GetExtension(alt); return true; }

        int slash = s.LastIndexOf('/');
        string file = slash < 0 ? s : s[(slash + 1)..];
        if (_names.Contains(file)) { how = "file name referenced by a project bin"; return true; }
        string fileNoExt = StripExtension(file);
        if (fileNoExt.Length >= 4 && _names.Contains(fileNoExt))
        { how = "name referenced by a project bin"; return true; }

        if (_nameHashes.Contains(HashAlgorithms.Fnv1a(s)) || _nameHashes.Contains(HashAlgorithms.Fnv1a(noExt)))
        { how = "name hash referenced by a project bin"; return true; }

        how = "";
        return false;
    }

    /// <summary>Only the literal-path rule, with no normalisation at all - what the pre-M302 Asset Usage
    /// check did. Kept so the difference the index makes can be measured rather than asserted.</summary>
    public bool IsReferencedExactPathOnly(string relPath) => _paths.Contains(relPath);

    private static IEnumerable<string> AltExtensions(string s)
    {
        string ext = Path.GetExtension(s).ToLowerInvariant();
        string stem = StripExtension(s);
        string[] alts = ext switch
        {
            ".dds" => new[] { ".tex", ".tga", ".png" },
            ".tex" => new[] { ".dds", ".tga", ".png" },
            ".tga" => new[] { ".dds", ".tex" },
            ".png" => new[] { ".dds", ".tex" },
            ".scb" => new[] { ".sco" },
            ".sco" => new[] { ".scb" },
            _ => Array.Empty<string>(),
        };
        foreach (var a in alts) yield return stem + a;
    }

    private static string StripExtension(string s)
    {
        int dot = s.LastIndexOf('.');
        int slash = s.LastIndexOf('/');
        return dot > slash && dot >= 0 ? s[..dot] : s;
    }
}
