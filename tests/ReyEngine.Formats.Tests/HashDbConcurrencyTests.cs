using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M508: the hash tables are read from several threads at once, and were not built for it.
///
/// <para>Mounting a reference wad resolves tens of thousands of path hashes while the viewport, a material
/// save and the texture loader resolve their own. The seekable arena's frame cache is a plain Dictionary
/// plus a Queue plus a byte counter, all unguarded — two threads writing it corrupt the bucket array, and
/// the symptom is an IndexOutOfRangeException thrown from inside the dictionary.</para>
///
/// <para>Which is exactly what a user saw: <c>reference Map453.wad.client: Index was outside the bounds of
/// the array</c>, after which the Riot reference assets were missing from the project for the rest of the
/// session — the mount had thrown and there was nothing to fall back to. The file was fine; opening it on
/// its own took 8 ms and read all 17,365 entries.</para>
///
/// <para>These tests are inherently probabilistic — a race that is fixed cannot be proven absent by
/// running it. They are sized to fail reliably against the unlocked version (which they did) rather than
/// to prove correctness.</para>
/// </summary>
public sealed class HashDbConcurrencyTests
{
    private static string WriteTable(string path, int count)
    {
        var entries = new List<(ulong, string)>(count);
        for (int i = 0; i < count; i++)
            entries.Add(((ulong)i, $"assets/maps/mapgeometry/map453/textures/texture_{i:D6}_padding_to_make_frames.tex"));
        // A genuinely seekable multi-frame arena: the frame cache only exists on this path.
        return HashDbTestWriter.Write(path, entries, compressArena: true, framesWanted: 12);
    }

    [Fact]
    public void ManyThreadsReadingTheSameTableAgreeAndDoNotThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"reyengine-race-{Guid.NewGuid():N}.hashdb");
        try
        {
            WriteTable(path, 4000);
            using var db = HashDbFile.Open(path);

            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            // Scattered rather than sequential: clustered reads would sit in one frame and never exercise
            // the eviction path, which is the half that mutates three structures at once.
            Parallel.For(0, 32, worker =>
            {
                var rng = new Random(worker * 7919);
                try
                {
                    for (int i = 0; i < 400; i++)
                    {
                        long index = rng.Next(0, 4000);
                        string got = db.StringAt(index);
                        string want = $"assets/maps/mapgeometry/map453/textures/texture_{index:D6}_padding_to_make_frames.tex";
                        if (got != want) failures.Add($"index {index}: got '{got}'");
                    }
                }
                catch (Exception ex) { failures.Add($"{ex.GetType().Name}: {ex.Message}"); }
            });

            Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(5)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void EvictionUnderPressureStaysConsistent()
    {
        // A cache small enough that nearly every read evicts — the window where the dictionary, the queue
        // and the byte counter must move together.
        string path = Path.Combine(Path.GetTempPath(), $"reyengine-race-{Guid.NewGuid():N}.hashdb");
        try
        {
            WriteTable(path, 3000);
            using var db = HashDbFile.Open(path);
            db.SetFrameCacheLimit(1);          // one frame at a time

            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, 16, worker =>
            {
                try
                {
                    for (int i = 0; i < 300; i++)
                    {
                        long index = (worker * 337 + i * 91) % 3000;
                        if (!db.StringAt(index).EndsWith($"texture_{index:D6}_padding_to_make_frames.tex"))
                            failures.Add($"index {index} came back wrong");
                    }
                }
                catch (Exception ex) { failures.Add($"{ex.GetType().Name}: {ex.Message}"); }
            });

            Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(5)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TheWholeTableStillReadsCorrectlySingleThreaded()
    {
        // The locking must not change what the reader returns.
        string path = Path.Combine(Path.GetTempPath(), $"reyengine-race-{Guid.NewGuid():N}.hashdb");
        try
        {
            WriteTable(path, 500);
            using var db = HashDbFile.Open(path);
            Assert.Equal(500, db.EntryCount);
            for (long i = 0; i < db.EntryCount; i++)
                Assert.Equal($"assets/maps/mapgeometry/map453/textures/texture_{i:D6}_padding_to_make_frames.tex",
                    db.StringAt(i));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
