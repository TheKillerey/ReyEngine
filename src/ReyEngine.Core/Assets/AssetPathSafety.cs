namespace ReyEngine.Core.Assets;

/// <summary>
/// M819: what may become a file name, or a path below a folder, when the name came from a package or a hash table rather than from the game.
///
/// <para>A chunk's path hash says nothing about its spelling: <c>data/x/..\..\y.cmd</c> hashes as happily as a real path, and a package can declare it as a GameData target or name it in its hashtables. A name like
/// that must not reach <c>Path.Combine</c> as it stands - it would write outside the folder the user chose. Two checks, both used at the places that write: a spelling is only listed when it is a plain relative path
/// (<see cref="IsSafeRelativePath"/>), and a file made from a name is only written where the final path is proven to lie below the folder (<see cref="TryCombineUnder"/>).</para>
/// </summary>
public static class AssetPathSafety
{
    private const int MaxPathLength = 1024;
    private const int MaxSegmentLength = 255;

    // the names Windows treats as devices whatever the extension: "CON.txt" opens the console. COM0 and LPT0 are reserved like the others, and so are COM and LPT followed by a superscript 1, 2 or 3 (U+00B9, U+00B2, U+00B3)
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00b9", "COM\u00b2", "COM\u00b3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
    };

    /// <summary>Whether <paramref name="name"/> is one plain file name: no directory part, nothing a file name cannot hold, nothing Windows would read as something else.</summary>
    public static bool IsSafeFileName(string? name) => name is { Length: > 0 } && IsSafeSegment(name);

    /// <summary>
    /// Whether <paramref name="path"/> is a relative path of the shape a chunk's spelling has: segments separated by <c>/</c>, none empty, <c>.</c> or <c>..</c>, no backslash, drive, stream marker or rooted start, no
    /// control character or character a Windows file name cannot hold, no device name, no segment that ends in a dot or a space. Case and non-ASCII letters are the caller's own.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength) return false;
        foreach (string segment in path.Split('/'))
            if (!IsSafeSegment(segment)) return false;
        return true;
    }

    /// <summary>
    /// <paramref name="relative"/> below <paramref name="folder"/>, proven: false (and an empty <paramref name="full"/>) when the name is rooted, holds a colon, or when the combined path - once <c>..</c> and the separators
    /// of either kind are resolved - does not lie strictly below the folder. The proof is made on the full path, so it holds whatever the spelling. A colon is refused wherever it stands: <c>x.bin:s</c> would write a hidden
    /// NTFS stream of <c>x.bin</c>, which lies below the folder and is no file the project lists.
    /// </summary>
    public static bool TryCombineUnder(string folder, string relative, out string full)
    {
        full = "";
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) return false;
        string root = Path.GetFullPath(folder);
        string target;
        try { target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(prefix, comparison)) return false;
        full = target;
        return true;
    }

    private static bool IsSafeSegment(string segment)
    {
        if (segment.Length == 0 || segment.Length > MaxSegmentLength) return false;
        if (segment is "." or "..") return false;
        if (segment[0] == ' ' || segment[^1] is ' ' or '.') return false;
        foreach (char c in segment)
        {
            if (c < 0x20 || c == 0x7f) return false;
            if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
        }
        int dot = segment.IndexOf('.');
        string stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        return !Devices.Contains(stem);
    }
}
