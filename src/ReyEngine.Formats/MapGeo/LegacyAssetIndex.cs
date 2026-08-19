namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// Resolves a legacy asset reference to a file on disk, tolerating the three ways legacy references
/// lie about themselves (M531).
///
/// <para>Legacy content names its assets as bare filenames with whatever extension the artist typed,
/// and the shipped art routinely disagrees. Across the 16 particle systems Map2 uses, all 49 asset
/// references end in <c>.tga</c> and NONE of them exist as .tga - all 49 are .dds, 30 under
/// <c>DATA/Particles</c> and 19 under <c>DATA/Shared/Particles</c>. So a resolver has to be tolerant
/// three ways at once: case, extension, and which root folder the file is under.</para>
///
/// <para>This is the map porter's own texture index generalised - it was private, single-root, and
/// images-only, which is exactly the shape that could not find particle art.</para>
/// </summary>
public sealed class LegacyAssetIndex
{
    /// <summary>Images. The set the map porter has always indexed.</summary>
    public static readonly string[] ImageExtensions = { ".dds", ".tga", ".tex" };

    /// <summary>Images plus the legacy mesh formats mesh-emitters point at (e.g. insphere.sco).</summary>
    public static readonly string[] ParticleExtensions = { ".dds", ".tga", ".tex", ".sco", ".scb" };

    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _extensions;

    public LegacyAssetIndex(string root, IEnumerable<string>? extensions = null)
        : this(new[] { root }, extensions) { }

    /// <param name="roots">Searched in order; the FIRST root to supply a given name wins, so callers
    /// pass the more specific folder first.</param>
    public LegacyAssetIndex(IEnumerable<string> roots, IEnumerable<string>? extensions = null)
    {
        _extensions = (extensions ?? ImageExtensions).ToArray();

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (string file in files)
            {
                if (!_extensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                _files.TryAdd(Path.GetFileName(file), file);
                _files.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            }
        }
    }

    public int Count => _files.Count;

    /// <summary>
    /// Resolve one reference. Tries the name as written, then the bare stem, then the stem under every
    /// indexed extension - which is the step that turns a reference to <c>flame.tga</c> into the
    /// <c>flame.dds</c> that actually shipped.
    /// </summary>
    public bool TryResolve(string reference, out string file)
    {
        file = "";
        if (string.IsNullOrWhiteSpace(reference)) return false;

        string name = Path.GetFileName(reference.Replace('\\', '/'));
        if (_files.TryGetValue(name, out file!)) return true;

        string stem = Path.GetFileNameWithoutExtension(name);
        if (_files.TryGetValue(stem, out file!)) return true;
        foreach (string ext in _extensions)
            if (_files.TryGetValue(stem + ext, out file!)) return true;

        file = "";
        return false;
    }
}
