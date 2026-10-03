using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M814 review: a declaration plan asks the project's Riot reference WADs about every bin it holds, and a bin the
/// mounts do not answer used to reopen every reference WAD for itself - a table of contents of tens of thousands of
/// entries parsed and resolved once per bin, per reference. <see cref="ReferenceWadSet"/> opens each reference at most
/// once for a whole run. These pin that with an open-counting fake, and that the answers are the ones the per-bin loop
/// gave: same order, a reference that is missing, will not open, lacks the chunk or cannot read it is passed over.
/// </summary>
public sealed class ReferenceWadSetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rey-refset-" + Guid.NewGuid().ToString("N"));

    public ReferenceWadSetTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private sealed class FakeWad : IReferenceWad
    {
        public readonly Dictionary<ulong, byte[]> Chunks = new();
        public readonly HashSet<ulong> Unreadable = new();
        public int Disposals;
        public bool ThrowOnDispose;

        public bool TryRead(ulong pathHash, out byte[] bytes)
        {
            if (Unreadable.Contains(pathHash)) throw new InvalidDataException("the chunk cannot be decoded");
            return Chunks.TryGetValue(pathHash, out bytes!);
        }

        public void Dispose()
        {
            Disposals++;
            if (ThrowOnDispose) throw new IOException("the handle is gone");
        }
    }

    /// <summary>Counts every attempt to open a reference, in order, and answers from a table.</summary>
    private sealed class Opener
    {
        public readonly Dictionary<string, FakeWad?> Wads = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Throwing = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Attempts = new();

        public IReferenceWad? Open(string path)
        {
            lock (Attempts) Attempts.Add(path);
            if (Throwing.Contains(path)) throw new InvalidDataException("not a WAD");
            return Wads.GetValueOrDefault(path);
        }

        public FakeWad Add(string path, params (ulong Hash, string Text)[] chunks)
        {
            var wad = new FakeWad();
            foreach (var (hash, text) in chunks) wad.Chunks[hash] = System.Text.Encoding.UTF8.GetBytes(text);
            Wads[path] = wad;
            return wad;
        }
    }

    private static string Text(byte[]? bytes) => bytes is null ? "<null>" : System.Text.Encoding.UTF8.GetString(bytes);

    // ===================================================== how often a reference is opened

    [Fact]
    public void ManyLookupsOpenEachReferenceOnce()
    {
        var o = new Opener();
        o.Add("A.wad.client", (1, "a1"));
        o.Add("B.wad.client", (2, "b2"));
        using var set = new ReferenceWadSet(new[] { "A.wad.client", "B.wad.client" }, o.Open);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal("b2", Text(set.Read(2)));       // only the second holds it: both are reached every time
            Assert.Equal("a1", Text(set.Read(1)));
            Assert.Null(set.Read(3));                    // neither holds it
        }

        Assert.Equal(new[] { "A.wad.client", "B.wad.client" }, o.Attempts);
        Assert.Equal(2, set.OpenAttempts);
        Assert.Equal(2, set.OpenCount);
    }

    [Fact]
    public void AReferenceIsOpenedWhenALookupReachesItAndNotBefore()
    {
        var o = new Opener();
        o.Add("A.wad.client", (1, "a1"));
        o.Add("B.wad.client", (2, "b2"));
        using var set = new ReferenceWadSet(new[] { "A.wad.client", "B.wad.client" }, o.Open);
        Assert.Empty(o.Attempts);                        // building the set opens nothing

        Assert.Equal("a1", Text(set.Read(1)));
        Assert.Equal(new[] { "A.wad.client" }, o.Attempts);   // the first reference answered: the second is not needed

        Assert.Equal("b2", Text(set.Read(2)));
        Assert.Equal(new[] { "A.wad.client", "B.wad.client" }, o.Attempts);
    }

    [Fact]
    public void AReferenceThatWillNotOpenIsRememberedAndNeverRetried()
    {
        var o = new Opener();
        o.Throwing.Add("Broken.wad.client");
        o.Add("Good.wad.client", (7, "seven"));
        using var set = new ReferenceWadSet(new[] { "Broken.wad.client", "Good.wad.client" }, o.Open);

        for (int i = 0; i < 50; i++) Assert.Equal("seven", Text(set.Read(7)));

        Assert.Equal(new[] { "Broken.wad.client", "Good.wad.client" }, o.Attempts);   // the broken one: one attempt, not fifty
        Assert.Equal(2, set.OpenAttempts);
        Assert.Equal(1, set.OpenCount);
    }

    [Fact]
    public void AReferenceThatIsNotThereIsRememberedToo()
    {
        var o = new Opener();                            // "Gone" has no entry: the opener answers null, as for a missing file
        o.Add("Good.wad.client", (7, "seven"));
        using var set = new ReferenceWadSet(new[] { "Gone.wad.client", "Good.wad.client" }, o.Open);

        for (int i = 0; i < 20; i++) Assert.Null(set.Read(99));

        Assert.Equal(new[] { "Gone.wad.client", "Good.wad.client" }, o.Attempts);
    }

    [Fact]
    public void ABinNoReferenceHoldsCostsNoReopeningHoweverOftenItIsAsked()
    {
        var o = new Opener();
        foreach (var name in new[] { "A", "B", "C" }) o.Add(name, (1, name));
        using var set = new ReferenceWadSet(new[] { "A", "B", "C" }, o.Open);

        for (int i = 0; i < 40; i++) Assert.Null(set.Read(1000 + (ulong)i));

        Assert.Equal(new[] { "A", "B", "C" }, o.Attempts);
    }

    [Fact]
    public void APathGivenTwiceIsOneReferenceAndABlankOneIsNone()
    {
        var o = new Opener();
        o.Add("A.wad.client", (1, "a1"));
        using var set = new ReferenceWadSet(new[] { "A.wad.client", "a.WAD.client", "", null!, "A.wad.client" }, o.Open);

        Assert.Equal("a1", Text(set.Read(1)));
        Assert.Null(set.Read(2));

        Assert.Equal(new[] { "A.wad.client" }, o.Attempts);
    }

    [Fact]
    public void ConcurrentLookupsStillOpenEachReferenceOnce()
    {
        var o = new Opener();
        o.Add("A", (1, "a1"));
        o.Add("B", (2, "b2"));
        using var set = new ReferenceWadSet(new[] { "A", "B" }, o.Open);

        var failures = new List<Exception>();
        var threads = Enumerable.Range(0, 6).Select(t => new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    Assert.Equal("b2", Text(set.Read(2)));
                    Assert.Null(set.Read(9));
                }
            }
            catch (Exception ex) { lock (failures) failures.Add(ex); }
        })).ToList();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Empty(failures);
        Assert.Equal(new[] { "A", "B" }, o.Attempts.Order(StringComparer.Ordinal).ToArray());
    }

    // ===================================================== the same answers as the per-bin loop

    [Fact]
    public void TheFirstReferenceThatHoldsTheChunkAnswers()
    {
        var o = new Opener();
        o.Add("A", (1, "from A"));
        o.Add("B", (1, "from B"));
        using var set = new ReferenceWadSet(new[] { "A", "B" }, o.Open);

        Assert.Equal("from A", Text(set.Read(1)));
        Assert.Equal(new[] { "A" }, o.Attempts);
    }

    [Fact]
    public void AChunkThatCannotBeReadFallsThroughToTheNextReferenceAsItAlwaysDid()
    {
        var o = new Opener();
        var a = o.Add("A", (1, "damaged in A"));
        a.Unreadable.Add(1);
        o.Add("B", (1, "good in B"));
        using var set = new ReferenceWadSet(new[] { "A", "B" }, o.Open);

        Assert.Equal("good in B", Text(set.Read(1)));
        Assert.Equal("good in B", Text(set.Read(1)));
        Assert.Equal(new[] { "A", "B" }, o.Attempts);    // A stays open and is asked; it is not reopened to be asked again
    }

    [Fact]
    public void AChunkNoReferenceCanReadIsNullAndDoesNotThrow()
    {
        var o = new Opener();
        var a = o.Add("A", (1, "damaged"));
        a.Unreadable.Add(1);
        using var set = new ReferenceWadSet(new[] { "A" }, o.Open);

        Assert.Null(set.Read(1));
    }

    [Fact]
    public void NoReferencesAtAllAnswerNothing()
    {
        using var set = new ReferenceWadSet(Array.Empty<string>(), new Opener().Open);
        Assert.Null(set.Read(1));
        Assert.Equal(0, set.OpenAttempts);
    }

    // ===================================================== releasing them

    [Fact]
    public void DisposeReleasesEveryOpenReferenceOnceAndRefusesFurtherReads()
    {
        var o = new Opener();
        var a = o.Add("A", (1, "a1"));
        var b = o.Add("B", (2, "b2"));
        var never = o.Add("C", (3, "c3"));
        var set = new ReferenceWadSet(new[] { "A", "B", "C" }, o.Open);
        Assert.Equal("b2", Text(set.Read(2)));           // A and B opened; C never reached

        set.Dispose();
        set.Dispose();                                   // twice is fine

        Assert.Equal(1, a.Disposals);
        Assert.Equal(1, b.Disposals);
        Assert.Equal(0, never.Disposals);
        Assert.Equal(0, set.OpenCount);
        Assert.Throws<ObjectDisposedException>(() => set.Read(1));
    }

    [Fact]
    public void AReferenceThatFailsToReleaseDoesNotKeepTheOthersOpen()
    {
        var o = new Opener();
        var a = o.Add("A", (1, "a1"));
        a.ThrowOnDispose = true;
        var b = o.Add("B", (2, "b2"));
        var set = new ReferenceWadSet(new[] { "A", "B" }, o.Open);
        set.Read(2);

        set.Dispose();                                   // does not throw

        Assert.Equal(1, a.Disposals);
        Assert.Equal(1, b.Disposals);
    }

    // ===================================================== real WAD files

    private string PackedWad(string name, params (string Rel, string Text)[] files)
    {
        string folder = Path.Combine(_dir, "src-" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in files)
        {
            string path = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        string wad = Path.Combine(_dir, name);
        Assert.True(WadPackService.Pack(folder, wad).Success);
        return wad;
    }

    [Fact]
    public void TheRealOpenerReadsAChunkOfAWadFileAndFindsNoOtherChunk()
    {
        string wad = PackedWad("A.wad.client", ("data/maps/a.bin", "the game's a.bin"));

        using var reference = ReferenceWadSet.OpenFile(wad)!;

        Assert.True(reference.TryRead(HashAlgorithms.WadPath("data/maps/a.bin"), out var bytes));
        Assert.Equal("the game's a.bin", Text(bytes));
        Assert.False(reference.TryRead(HashAlgorithms.WadPath("data/maps/other.bin"), out _));
    }

    [Fact]
    public void TheRealOpenerAnswersNullForAFileThatIsNotThere()
    {
        Assert.Null(ReferenceWadSet.OpenFile(Path.Combine(_dir, "Missing.wad.client")));
    }

    [Fact]
    public void TheRealOpenerThrowsForAFileThatIsNotAWadAndTheSetPassesOverIt()
    {
        string junk = Path.Combine(_dir, "Junk.wad.client");
        File.WriteAllBytes(junk, Enumerable.Range(0, 2048).Select(i => (byte)(i * 13 + 5)).ToArray());
        string good = PackedWad("Good.wad.client", ("data/maps/a.bin", "the game's a.bin"));

        Assert.ThrowsAny<Exception>(() => ReferenceWadSet.OpenFile(junk));

        using var set = new ReferenceWadSet(new[] { Path.Combine(_dir, "Missing.wad.client"), junk, good });
        Assert.Equal("the game's a.bin", Text(set.Read(HashAlgorithms.WadPath("data/maps/a.bin"))));
        Assert.Equal("the game's a.bin", Text(set.Read(HashAlgorithms.WadPath("data/maps/a.bin"))));
        Assert.Equal(3, set.OpenAttempts);
        Assert.Equal(1, set.OpenCount);
    }

    [Fact]
    public void TheDefaultSetFindsAChunkOnlyALaterRealReferenceHolds()
    {
        string a = PackedWad("A.wad.client", ("data/maps/a.bin", "in A"));
        string b = PackedWad("B.wad.client", ("data/maps/b.bin", "in B"));
        using var set = new ReferenceWadSet(new[] { a, b });

        Assert.Equal("in B", Text(set.Read(HashAlgorithms.WadPath("data/maps/b.bin"))));
        Assert.Equal("in A", Text(set.Read(HashAlgorithms.WadPath("data/maps/a.bin"))));
        Assert.Null(set.Read(HashAlgorithms.WadPath("data/maps/c.bin")));
    }

    [Fact]
    public void ADisposedSetHasReleasedItsFiles()
    {
        string a = PackedWad("A.wad.client", ("data/maps/a.bin", "in A"));
        var set = new ReferenceWadSet(new[] { a });
        Assert.Equal("in A", Text(set.Read(HashAlgorithms.WadPath("data/maps/a.bin"))));

        set.Dispose();

        File.Delete(a);                                  // a handle still open would make this fail on Windows
        Assert.False(File.Exists(a));
    }
}
