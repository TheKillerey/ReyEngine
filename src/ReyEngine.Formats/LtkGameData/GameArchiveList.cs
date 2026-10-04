using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M818: a game installation that cannot be indexed at all: it has no <c>DATA/FINAL</c>, or the walk over it failed. Per-archive failures never raise this: an
/// archive that does not mount is a <see cref="SkippedGameArchive"/> and the rest index (<c>BuildError</c>).
/// </summary>
public sealed class GameIndexException : Exception
{
    public GameIndexException(string message) : base(message) { }

    public GameIndexException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>One <c>.wad.client</c> of the installation (<c>Archive</c>). Its id is its place in <see cref="GameArchiveList.Enumerate"/>'s order.</summary>
/// <param name="Id">The ordinal of the archive: dense from zero in byte order of <paramref name="Name"/>.</param>
/// <param name="Name">The path below <c>DATA/FINAL</c> with forward slashes: <c>Champions/Aatrox.wad.client</c>.</param>
/// <param name="Path">The absolute path of the file.</param>
/// <param name="Length">The file's length in bytes when the installation was listed.</param>
/// <param name="ModifiedNanos">The file's modification time in nanoseconds since the Unix epoch (0 for a time before it).</param>
public sealed record GameArchive(int Id, string Name, string Path, long Length, ulong ModifiedNanos)
{
    /// <summary>The path <c>ltk_overlay</c> names the archive by: <c>DATA/FINAL/Champions/Aatrox.wad.client</c>.</summary>
    public string WadPath => GameArchiveList.ArchiveRoot + "/" + Name;

    /// <summary>The last segment of <see cref="Name"/>: <c>Aatrox.wad.client</c>.</summary>
    public string FileName => Name[(Name.LastIndexOf('/') + 1)..];
}

/// <summary>
/// M818: the archives of an installation, in the order and with the identity <c>ltk_game_index</c> gives them (<c>archive.rs</c>, <c>build.rs</c>, <c>fingerprint.rs</c>).
///
/// <para><b>Which files.</b> Every file below <c>&lt;game&gt;/DATA/FINAL</c> whose name ends in <c>.wad.client</c>, compared ASCII case-insensitively, at any depth. A link is not
/// followed: a file reached through one is not a file there (<c>WalkDir</c> does not follow them either).</para>
///
/// <para><b>Which order, and so which ids.</b> The ids are the positions in the list sorted by the BYTES of the <c>DATA/FINAL</c>-relative name, written with forward slashes
/// (<c>Champions/Aatrox.wad.client</c>). Byte order puts <c>/</c> (0x2F) before every letter and an upper-case letter before a lower-case one, so
/// <c>Champions/Aatrox.en_US.wad.client</c> comes before <c>Champions/Aatrox.wad.client</c> and <c>Map12</c> before <c>map1</c>. The ids decide which archive is the
/// FIRST holder of a chunk, and so which game bin a reference reads and in what order an entry's chunks are edited. LTK Manager's own object browser sorts its list
/// by <c>compare_names</c> (natural order) instead, which numbers the same archives differently; the overlay, which is what installs a mod, does not.</para>
///
/// <para><b>Identity.</b> The fingerprint is XXH3-64 over each archive's name bytes, its length (u64) and its modification time in nanoseconds since the epoch (u128), in
/// that order, as the crate computes it, so that one installation has one fingerprint on either side. Two installations with one fingerprint index identically.</para>
/// </summary>
public static class GameArchiveList
{
    /// <summary>The folder below the game directory that holds every archive.</summary>
    public const string ArchiveRoot = "DATA/FINAL";

    private const string Suffix = ".wad.client";

    /// <summary>The archives below <c>&lt;game&gt;/DATA/FINAL</c>, sorted by name in byte order and numbered from zero.</summary>
    /// <exception cref="GameIndexException">The installation has no <c>DATA/FINAL</c> or no archive in it, or a walk over it, or the metadata of an archive, failed.</exception>
    public static IReadOnlyList<GameArchive> Enumerate(string gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(gameDirectory);
        string root = System.IO.Path.Combine(gameDirectory, "DATA", "FINAL");
        if (!Directory.Exists(root))
            throw new GameIndexException($"{gameDirectory} is not a League installation: it has no DATA/FINAL directory");

        var found = new List<(string Name, FileInfo File)>();
        try
        {
            Walk(new DirectoryInfo(root), root, found);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new GameIndexException($"cannot enumerate archives under {root}: {e.Message}", e);
        }

        // an installation with no archive is one being installed, repaired or moved, or not one: an index of it is empty, and an empty index that is kept is wrong the moment the archives are there
        if (found.Count == 0)
            throw new GameIndexException($"{root} holds no {Suffix} archive, so there is nothing to index");

        found.Sort(static (a, b) => ByteOrder.Compare(a.Name, b.Name));
        var archives = new List<GameArchive>(found.Count);
        foreach (var (name, file) in found)
        {
            try
            {
                archives.Add(new GameArchive(archives.Count, name, file.FullName, file.Length, ModifiedNanos(file.LastWriteTimeUtc)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new GameIndexException($"cannot read metadata of {file.FullName}: {e.Message}", e);
            }
        }
        return archives;
    }

    /// <summary>Every file below <paramref name="root"/> that is an archive, by a walk of folders that does not follow a link. Only a LINK is left alone (a symbolic link or a junction, which
    /// <see cref="FileSystemInfo.LinkTarget"/> names): a file or a folder that merely carries a reparse point is not one, and the platform puts one on a file it has compressed in place (WOF,
    /// CompactOS) or has not downloaded yet (a cloud placeholder), which are the files of an installation like any other.</summary>
    private static void Walk(DirectoryInfo root, string rootPath, List<(string Name, FileInfo File)> found)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false, RecurseSubdirectories = false };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos("*", options))
            {
                if (IsLink(entry)) continue;
                if (entry is DirectoryInfo directory) pending.Push(directory);
                else if (entry is FileInfo file && IsArchiveFileName(file.Name))
                    found.Add((System.IO.Path.GetRelativePath(rootPath, file.FullName).Replace('\\', '/'), file));
            }
        }
    }

    /// <summary>Whether <paramref name="entry"/> is a symbolic link or a junction. The attribute is looked at first, since asking a plain file for its link target costs a call to the file system.</summary>
    private static bool IsLink(FileSystemInfo entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is not null;

    /// <summary>A modification time as the fingerprint takes it: nanoseconds since the Unix epoch, and zero for a time before it (<c>duration_since(UNIX_EPOCH).ok()</c>).</summary>
    internal static ulong ModifiedNanos(DateTime lastWriteUtc)
    {
        long nanos = (lastWriteUtc.Ticks - DateTime.UnixEpoch.Ticks) * 100;
        return nanos > 0 ? (ulong)nanos : 0;
    }

    /// <summary>Whether <paramref name="fileName"/> ends in <c>.wad.client</c>, ASCII case-insensitively.</summary>
    internal static bool IsArchiveFileName(string fileName) =>
        fileName.Length >= Suffix.Length && fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The identity of an archive set: XXH3-64 over the name, the length and the modification time of each archive, in list order.</summary>
    public static ulong Fingerprint(IReadOnlyList<GameArchive> archives)
    {
        ArgumentNullException.ThrowIfNull(archives);
        var hasher = new XxHash3();
        Span<byte> number = stackalloc byte[16];
        foreach (var archive in archives)
        {
            hasher.Append(Encoding.UTF8.GetBytes(archive.Name));
            BinaryPrimitives.WriteUInt64LittleEndian(number, (ulong)archive.Length);
            hasher.Append(number[..8]);
            number.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(number, archive.ModifiedNanos);
            hasher.Append(number);
        }
        return hasher.GetCurrentHashAsUInt64();
    }

    /// <summary>The order of the UTF-8 bytes of two strings: what Rust's <c>str::as_bytes().cmp</c> compares. A UTF-16 ordinal comparison puts a character above
    /// U+FFFF before one of U+E000 to U+FFFF, and the bytes put it after.</summary>
    internal static class ByteOrder
    {
        public static int Compare(string a, string b)
        {
            // names are ASCII in every installation there is, and for ASCII the two orders are one order
            bool ascii = true;
            foreach (char c in a) if (c >= 0x80) { ascii = false; break; }
            if (ascii) foreach (char c in b) if (c >= 0x80) { ascii = false; break; }
            return ascii
                ? string.CompareOrdinal(a, b)
                : Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
        }
    }
}
