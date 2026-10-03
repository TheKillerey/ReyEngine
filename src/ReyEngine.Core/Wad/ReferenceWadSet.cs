namespace ReyEngine.Core.Wad;

/// <summary>M814: a read-only Riot reference WAD, as far as looking a chunk up in it is concerned.</summary>
public interface IReferenceWad : IDisposable
{
    /// <summary>The chunk's decompressed bytes, or false when this WAD has no such chunk. Throws when the
    /// chunk is there and cannot be read.</summary>
    bool TryRead(ulong pathHash, out byte[] bytes);
}

/// <summary>
/// M814: a project's Riot reference WADs, opened at most once each for as long as the set lives.
///
/// <para><b>Why.</b> The declaration planner (Send to LTK Manager and Export .fantome) asks for the game's copy
/// of every <c>.bin</c> a project holds. A bin the mounted references do not answer - new content, or a
/// project with no mounted reference - used to be looked up by opening every reference WAD afresh: a table of
/// contents of tens of thousands of entries parsed and resolved against the hash tables, once per bin, per
/// reference. A mod with a few hundred new bins and a few references spent its time re-reading the same
/// headers. A plan run owns one of these instead: each reference is opened the first time a lookup reaches it,
/// kept for the rest of the run, and disposed at the end. One that cannot be opened is remembered as such, so
/// a missing or corrupt reference costs one failed attempt, not one per bin.</para>
///
/// <para><b>Same answers as before.</b> The lookup order is the order the references were given; a reference
/// that is missing, will not open, lacks the chunk or cannot read it is passed over for the next; the first
/// reference that reads the chunk answers. Only the number of opens changed. Opening without the hash tables
/// (the old lookup passed them) cannot change a read - chunks are found by hash - and skips resolving every
/// entry's path, which a lookup never used.</para>
///
/// <para>Not for sharing between threads while reading: a plan run is single-threaded. The lock only keeps a
/// stray second caller from opening a reference twice.</para>
/// </summary>
public sealed class ReferenceWadSet : IDisposable
{
    private sealed class Slot
    {
        public required string Path { get; init; }
        public bool Tried;
        public IReferenceWad? Wad;
    }

    private readonly object _gate = new();
    private readonly List<Slot> _slots = new();
    private readonly Func<string, IReferenceWad?> _open;
    private bool _disposed;

    /// <param name="paths">The reference WADs in lookup order. A path given twice (compared without regard to
    /// case) is one reference.</param>
    /// <param name="open">Opens one reference, or returns null when there is nothing to open (the file is
    /// missing). May throw for one that cannot be opened. Defaults to <see cref="OpenFile"/>.</param>
    public ReferenceWadSet(IEnumerable<string> paths, Func<string, IReferenceWad?>? open = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _open = open ?? OpenFile;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
            if (!string.IsNullOrEmpty(path) && seen.Add(path)) _slots.Add(new Slot { Path = path });
    }

    /// <summary>References that were tried (opened or failed to) so far. Each counts once, however many chunks were asked for.</summary>
    public int OpenAttempts { get { lock (_gate) return _slots.Count(s => s.Tried); } }

    /// <summary>References that are open now.</summary>
    public int OpenCount { get { lock (_gate) return _slots.Count(s => s.Wad is not null); } }

    /// <summary>
    /// The game's bytes for a chunk: the first reference, in order, that holds it and can read it. Null when none does.
    /// </summary>
    public byte[]? Read(ulong pathHash)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var slot in _slots)
            {
                var wad = Resolve(slot);
                if (wad is null) continue;
                try { if (wad.TryRead(pathHash, out var bytes)) return bytes; }
                catch { /* the chunk is there but unreadable: the next reference may hold a good copy */ }
            }
            return null;
        }
    }

    private IReferenceWad? Resolve(Slot slot)
    {
        if (slot.Tried) return slot.Wad;
        slot.Tried = true;                       // set first: a failed open is not retried for every chunk after it
        try { slot.Wad = _open(slot.Path); }
        catch { slot.Wad = null; }               // a reference that will not open is passed over, as it always was
        return slot.Wad;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var slot in _slots)
            {
                try { slot.Wad?.Dispose(); } catch { /* releasing a handle is best-effort */ }
                slot.Wad = null;
            }
        }
    }

    /// <summary>The real opener: a WAD file on disk, or null when it is not there. A file that is there and not a
    /// readable WAD throws, which <see cref="ReferenceWadSet"/> turns into "passed over".</summary>
    public static IReferenceWad? OpenFile(string path)
    {
        if (!File.Exists(path)) return null;
        return new ArchiveReference(WadArchive.Open(path));
    }

    private sealed class ArchiveReference : IReferenceWad
    {
        private readonly WadArchive _archive;
        public ArchiveReference(WadArchive archive) => _archive = archive;

        public bool TryRead(ulong pathHash, out byte[] bytes)
        {
            if (_archive.TryGetEntry(pathHash, out var entry)) { bytes = _archive.Extract(entry); return true; }
            bytes = Array.Empty<byte>();
            return false;
        }

        public void Dispose() => _archive.Dispose();
    }
}
