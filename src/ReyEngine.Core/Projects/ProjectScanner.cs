using System.Globalization;

namespace ReyEngine.Core.Projects;

public sealed class ProjectScanResult
{
    /// <summary>Mod .wad.client files (relative to the scanned root).</summary>
    public List<string> Wads { get; } = new();
    /// <summary>Unpacked-WAD folders (relative to the scanned root).</summary>
    public List<string> Folders { get; } = new();
}

/// <summary>
/// Scans a project folder for editable mod content: packed <c>.wad.client</c> files and
/// unpacked-WAD folders (cslol style: a folder holding <c>ASSETS/</c>, <c>DATA/</c>,
/// <c>hashed_bins.json</c>, or loose <c>&lt;hash&gt;.bin</c> chunks).
/// </summary>
public static class ProjectScanner
{
    public static ProjectScanResult Scan(string root)
    {
        var result = new ProjectScanResult();
        if (!Directory.Exists(root)) return result;

        if (IsUnpackedWad(root))
        {
            result.Folders.Add(".");
            return result;
        }
        Recurse(root, root, result, depth: 0);
        return result;
    }

    private static void Recurse(string root, string dir, ProjectScanResult result, int depth)
    {
        if (depth > 8) return;
        var name = Path.GetFileName(dir.TrimEnd('/', '\\'));
        if (name.Equals("Build", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".reyengine", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith('.'))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.wad.client"))
                result.Wads.Add(Rel(root, file));

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (IsUnpackedWad(sub)) result.Folders.Add(Rel(root, sub));
                else Recurse(root, sub, result, depth + 1);
            }
        }
        catch (UnauthorizedAccessException) { /* skip */ }
    }

    private static bool IsUnpackedWad(string dir)
    {
        try
        {
            if (Directory.Exists(Path.Combine(dir, "ASSETS")) || Directory.Exists(Path.Combine(dir, "DATA")))
                return true;
            if (File.Exists(Path.Combine(dir, "hashed_bins.json")))
                return true;
            foreach (var f in Directory.EnumerateFiles(dir, "*.bin"))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (n.Length == 16 && ulong.TryParse(n, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                    return true;
            }
        }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
