using System.Globalization;
using ReyEngine.Formats.LtkGameData;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M818 review fixes, the overlay: what it asks of a mod's files and of the game is sized before it is read; whatever a provider throws is that chunk's diagnostic; a result that a failed read produced is
/// not kept; a caller that waits ends its wait with its own token; the layers in play are the ones the overlay was made with; and what a package may make the overlay do is bounded. The providers and the
/// game here fail on purpose, in the ways real ones do.
/// </summary>
public sealed class LtkGameDataOverlayHardeningTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private int _installs;

    public void Dispose() => _temp.Dispose();

    private const string A = "data/t/a.bin", X = "data/t/x.bin";

    private string Write(SyntheticGame game, string name) => game.Write(_temp.Combine(name));

    private InstalledGame Install(SyntheticGame game)
    {
        int n = _installs++;
        return new InstalledGame(Write(game, "game" + n), _temp.Combine("cache" + n, "index.idx"));
    }

    private GameDataOverlay Make(SyntheticGame game, SyntheticMod mod, IGameDataGame? installed = null, IGameDataModFiles? files = null, GameDataOverlayOptions? options = null) =>
        new(mod.ToLayers(), installed ?? Install(game), files ?? mod.ToFiles(), options);

    private static SyntheticGame Everything() => new SyntheticGame()
        .Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
        .Add("X.wad.client", X, Bin(Obj("Test/Obj/X", new[] { "x" }, count: 7)))
        .Add("P.wad.client", "data/t/p.bin", Ptch(new[] { Obj("Test/Obj/P1", new[] { "p1" }) }))
        .Add("Q.wad.client", "data/t/q.bin", Ptch(new[] { Obj("Test/Obj/Q1", new[] { "q1" }) }));

    private static string AddTag(string tag) => Doc(Target(A, $"{{\"Test/Obj/A\":{{\"+tags\":[\"{tag}\"]}}}}"));

    private static GameDataChunkResult Applied(GameDataOverlay overlay, string path)
    {
        var result = overlay.Apply(ChunkOf(path));
        Assert.NotNull(result);
        return result!;
    }

    private static string Digits(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The installed game with hooks: what to do before the table and the index are built, and what each chunk read is asked for and may do.</summary>
    private sealed class HookedGame : IGameDataGame
    {
        private readonly IGameDataGame _inner;
        public Action<CancellationToken>? BeforeObjects;
        public Func<int, ulong, byte[]?>? ReadHook;
        public long LastMaxBytes;
        public int ChunkReads;
        public string? Note;
        public Exception? NoteFailure;

        public HookedGame(IGameDataGame inner) { _inner = inner; }

        public GameChunkTable GetTable(CancellationToken cancellationToken)
        {
            return _inner.GetTable(cancellationToken);
        }

        public GameObjectIndex GetObjects(CancellationToken cancellationToken)
        {
            BeforeObjects?.Invoke(cancellationToken);
            return _inner.GetObjects(cancellationToken);
        }

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ChunkReads);
            LastMaxBytes = maxBytes;
            return ReadHook?.Invoke(archive, chunk) ?? _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);
        }

        public string? IndexNotSaved => NoteFailure is not null ? throw NoteFailure : Note ?? _inner.IndexNotSaved;
    }

    /// <summary>A mod's own files, supplied by functions the test sets, which may throw.</summary>
    private sealed class Files : IGameDataModFiles, IGameDataLayerFiles
    {
        public Func<ulong, byte[]?>? Raw;
        public Func<string, ulong, byte[]?>? Layer;
        public Func<string, byte[]?>? Override;
        public long LastCap;

        public byte[]? ReadRawFile(ulong chunk, long maxBytes)
        {
            LastCap = maxBytes;
            return Raw?.Invoke(chunk);
        }

        public byte[]? ReadLayerFile(string layer, ulong chunk, long maxBytes)
        {
            LastCap = maxBytes;
            return Layer?.Invoke(layer, chunk);
        }

        public byte[]? ReadOverrideFile(string path, long maxBytes) => Override?.Invoke(path);
    }

    private static GameDataOverlay WithFiles(IGameDataGame game, string document, Files files, GameDataOverlayOptions? options = null) =>
        new(new[] { new GameDataLayerInput("base", 0, document, files) }, game, files, options);

    // ================================================================================================ a base is sized before it is decoded

    [Fact]
    public void The_limits_on_a_base_are_64_MiB_whatever_the_output_limit_says_and_a_provider_is_asked_for_no_more()
    {
        var options = new GameDataOverlayOptions { Limits = new GameDataLimits { MaxOutputBytes = 1L << 40 } };

        Assert.Equal(64L << 20, options.MaxModChunkBytes);
        Assert.Equal(64L << 20, options.MaxGameChunkBytes);
        Assert.Equal(64L << 20, GameDataOverlayOptions.DefaultMaxBaseChunkBytes);
        var files = new Files();
        Applied(WithFiles(Install(Everything()), AddTag("b"), files), A);
        Assert.Equal(64L << 20, files.LastCap);
    }

    [Fact]
    public void A_provider_that_ignores_the_cap_is_measured_again_and_its_chunk_is_asked_for_again()
    {
        byte[] big = Bin(Obj("Test/Obj/A", new[] { new string('x', 600) }));
        var files = new Files { Layer = (layer, chunk) => big };
        var overlay = WithFiles(Install(Everything()), AddTag("b"), files, new GameDataOverlayOptions { MaxModChunkBytes = 200 });

        var result = Applied(overlay, A);

        Assert.False(result.Applied);
        Assert.Equal(GameDataBaseKind.None, result.BaseKind);
        Assert.Equal($"the file is {Digits(big.Length)} bytes, more than the 200 the overlay reads", Assert.Single(result.Diagnostics).Message);
        // a refusal of the moment is not kept: a provider that keeps to the cap now is believed
        files.Layer = null;
        var retried = Applied(overlay, A);
        Assert.NotSame(result, retried);
        Assert.Equal(GameDataBaseKind.Game, retried.BaseKind);
        Assert.True(retried.Applied);
    }

    [Fact]
    public void A_game_chunk_is_read_under_the_limit_and_one_larger_is_refused_by_the_archive_and_not_kept()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, TestWad.BigBin("Test/Obj/A", 3));
        var hooked = new HookedGame(Install(game));
        var overlay = new GameDataOverlay(new[] { new GameDataLayerInput("base", 0, AddTag("b")) }, hooked, null, new GameDataOverlayOptions { MaxGameChunkBytes = 1000 });

        var first = Applied(overlay, A);

        Assert.Equal(1000, hooked.LastMaxBytes);
        Assert.False(first.Applied);
        Assert.Contains("more than the 1,000 the read allows", Assert.Single(first.Diagnostics).Message);
        Applied(overlay, A);
        Assert.Equal(2, hooked.ChunkReads);   // what the archive refused is not kept, and is asked again
    }

    [Fact]
    public void A_game_that_ignores_the_limit_is_measured_again()
    {
        var hooked = new HookedGame(Install(Everything()));
        hooked.ReadHook = (archive, chunk) => new byte[5000];
        var overlay = new GameDataOverlay(new[] { new GameDataLayerInput("base", 0, AddTag("b")) }, hooked, null, new GameDataOverlayOptions { MaxGameChunkBytes = 1000 });

        var result = Applied(overlay, A);

        Assert.False(result.Applied);
        Assert.Equal("the chunk is 5,000 bytes, more than the 1,000 the overlay reads", Assert.Single(result.Diagnostics).Message);
    }

    // ================================================================================================ whatever a provider throws is that chunk's

    [Fact]
    public void A_mod_file_provider_that_throws_what_nobody_expected_skips_that_chunk_alone_and_is_asked_again()
    {
        int rawCalls = 0;
        var files = new Files { Raw = chunk => chunk == ChunkOf(A) && ++rawCalls == 1 ? throw new InvalidDataException("the package is damaged") : null };
        var document = Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}"), Target(X, "{\"Test/Obj/X\":{\"+tags\":[\"b\"]}}"));
        var overlay = WithFiles(Install(Everything()), document, files);

        var build = overlay.BuildAll();

        var skipped = build.Chunks.Single(c => c.Chunk == ChunkOf(A));
        Assert.False(skipped.Applied);
        Assert.Equal(GameDataBaseKind.None, skipped.BaseKind);
        Assert.Equal("internal error (InvalidDataException): the package is damaged", Assert.Single(skipped.Diagnostics).Message);
        Assert.True(build.Chunks.Single(c => c.Chunk == ChunkOf(X)).Applied);   // no other chunk is touched
        var retried = Applied(overlay, A);
        Assert.True(retried.Applied);
        Assert.Equal(new[] { "g1", "b" }, TagsOf(retried.Bytes!, "Test/Obj/A"));
    }

    [Fact]
    public void A_layer_file_provider_that_throws_what_nobody_expected_is_the_same()
    {
        int calls = 0;
        var files = new Files { Layer = (layer, chunk) => ++calls == 1 ? throw new InvalidDataException("not a bin") : null };
        var overlay = WithFiles(Install(Everything()), AddTag("b"), files);

        var first = Applied(overlay, A);
        var second = Applied(overlay, A);

        Assert.Equal("internal error (InvalidDataException): not a bin", Assert.Single(first.Diagnostics).Message);
        Assert.True(second.Applied);
    }

    [Fact]
    public void A_game_that_throws_what_nobody_expected_skips_that_chunk_and_is_asked_again()
    {
        var hooked = new HookedGame(Install(Everything()));
        int calls = 0;
        hooked.ReadHook = (archive, chunk) => ++calls == 1 ? throw new InvalidDataException("boom") : null;
        var overlay = new GameDataOverlay(new[] { new GameDataLayerInput("base", 0, AddTag("b")) }, hooked);

        var first = Applied(overlay, A);
        var second = Applied(overlay, A);

        Assert.Equal("internal error (InvalidDataException): boom", Assert.Single(first.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.TargetSkipped).Message);
        Assert.True(second.Applied);
    }

    [Fact]
    public void An_index_that_throws_what_nobody_expected_is_an_unavailable_index_and_not_an_exception()
    {
        var hooked = new HookedGame(Install(Everything())) { BeforeObjects = _ => throw new InvalidDataException("the index is damaged") };
        var overlay = new GameDataOverlay(new[] { new GameDataLayerInput("base", 0, Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]}"))) }, hooked);

        var plan = overlay.Plan();

        Assert.Equal("the index is damaged", plan.ObjectIndexError);
        Assert.Contains(plan.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.IndexUnavailable);
    }

    // ================================================================================================ a result a failed read made is not kept

    [Fact]
    public void An_override_file_the_provider_could_not_read_leaves_a_result_that_is_not_kept()
    {
        var document = Doc(Target(A, "{\"overrides\":[\"fix.ptch\"],\"Test/Obj/A\":{\"+tags\":[\"after\"]}}"));
        int calls = 0;
        var files = new Files { Override = path => ++calls == 1 ? throw new IOException("the file is locked") : Ptch(new[] { Obj("Test/Obj/FromPatch", new[] { "pp" }) }) };
        var overlay = WithFiles(Install(Everything()), document, files);

        var first = Applied(overlay, A);
        var second = Applied(overlay, A);
        var third = Applied(overlay, A);

        var unreadable = Assert.Single(first.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.OverrideUnreadable);
        Assert.Contains("fix.ptch: the file is locked", unreadable.Message);
        Assert.True(first.Applied);   // the module goes on without it
        Assert.NotSame(first, second);
        Assert.DoesNotContain(second.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.OverrideUnreadable);
        Assert.True(PropCodec.ReadProp(second.Bytes!).Objects.ContainsKey(H("Test/Obj/FromPatch")));
        Assert.Same(second, third);   // and once it answered, it is kept
    }

    [Fact]
    public void An_override_file_the_layer_does_not_hold_is_settled_and_is_kept()
    {
        var document = Doc(Target(A, "{\"overrides\":[\"nofile.ptch\"],\"Test/Obj/A\":{\"+tags\":[\"after\"]}}"));
        var overlay = WithFiles(Install(Everything()), document, new Files { Override = _ => null });

        Assert.Same(Applied(overlay, A), Applied(overlay, A));
    }

    [Fact]
    public void A_reference_the_game_could_not_answer_this_time_leaves_a_result_that_is_not_kept()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/X:tags\"}}}")));
        var hooked = new HookedGame(Install(Everything()));
        int reads = 0;
        hooked.ReadHook = (archive, chunk) => chunk == ChunkOf(X) && ++reads == 1 ? throw new GameChunkReadException("the disk is busy") : null;
        var overlay = new GameDataOverlay(mod.ToLayers(), hooked, mod.ToFiles());

        var first = Applied(overlay, A);
        var second = Applied(overlay, A);
        var third = Applied(overlay, A);

        Assert.Contains("the disk is busy", Assert.Single(first.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.ReferenceUnreadable).Message);
        Assert.NotSame(first, second);
        Assert.DoesNotContain(second.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.ReferenceUnreadable);
        Assert.Equal(new[] { "g1", "x" }, TagsOf(second.Bytes!, "Test/Obj/A"));
        Assert.Same(second, third);
    }

    // ================================================================================================ the references of a package are budgeted

    [Fact]
    public void A_reference_the_reader_refuses_for_its_budget_is_a_reference_unreadable_and_the_result_is_not_kept()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/X:tags\"}}}")));
        var overlay = Make(Everything(), mod, options: new GameDataOverlayOptions { References = new GameEntryLimits { MaxEntries = 0 } });

        var first = Applied(overlay, A);
        var second = Applied(overlay, A);

        var unreadable = Assert.Single(first.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.ReferenceUnreadable);
        Assert.Contains("reference budget exceeded: more than the 0 distinct entries a reader keeps", unreadable.Message);
        Assert.NotSame(first, second);
        Assert.Equal(2, overlay.ReferenceStats!.Value.Refused);
    }

    [Fact]
    public void What_the_references_of_one_chunk_cost_counts_against_the_budget_of_work_the_chunk_has_and_refuses_the_reads_past_it()
    {
        var game = Everything().Add("Y.wad.client", "data/t/y.bin", Bin(Obj("Test/Obj/Y", new[] { "y" }, count: 9)));
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/X:tags\"},\"count\":{\"ref\":\"Test/Obj/Y:count\"}}}")));
        var overlay = Make(game, mod, options: new GameDataOverlayOptions { MaxWorkPerChunk = 1 });

        var result = Applied(overlay, A);

        // the first read is within what the chunk has, and spends all of it: the second is refused before it is made
        var unreadable = Assert.Single(result.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.ReferenceUnreadable);
        Assert.Contains("Test/Obj/Y", unreadable.Message);
        Assert.Contains("reference budget exceeded: the applications to this chunk have read as much as the overlay allows one chunk", unreadable.Message);
        Assert.Equal(1, overlay.ReferenceStats!.Value.Entries);
        Assert.Equal(1, overlay.ReferenceStats!.Value.Refused);
    }

    [Fact]
    public void The_overlay_says_what_its_references_cost()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/X:tags\"}}}")));
        var overlay = Make(Everything(), mod);
        Assert.Null(overlay.ReferenceStats);

        Applied(overlay, A);

        var stats = overlay.ReferenceStats!.Value;
        Assert.Equal(1, stats.Entries);
        Assert.Equal(1, stats.ChunkReads);
        Assert.True(stats.ChunkBytes > 0);
        Assert.True(stats.RetainedValues > 0);
        Assert.Equal(0, stats.Refused);
    }

    // ================================================================================================ a caller that waits ends its wait with its own token

    /// <summary>Awaits a waiter that was cancelled: it must end, within ten seconds, with the token's exception; a deadlock is a TimeoutException that fails the test, and not a test that never ends.</summary>
    private static async Task AssertEndsCancelled(Task task, string what)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException) { return; }
        catch (TimeoutException) { Assert.Fail($"{what}: the waiter did not end its wait when its token was cancelled"); }
        Assert.Fail($"{what}: the waiter ended without the token's exception");
    }

    /// <summary>Awaits a task that is expected to finish: a deadlock is a failure that says so.</summary>
    private static async Task<T> Finishes<T>(Task<T> task, string what)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { Assert.Fail($"{what}: did not finish"); throw; }
    }

    private static async Task Finishes(Task task, string what)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { Assert.Fail($"{what}: did not finish"); throw; }
    }

    [Fact]
    public async Task A_plan_waited_for_while_another_caller_builds_it_ends_the_wait_when_the_waiters_token_is_cancelled()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hooked = new HookedGame(Install(Everything())) { BeforeObjects = _ => { entered.Set(); release.Wait(); } };
        var overlay = new GameDataOverlay(new[] { new GameDataLayerInput("base", 0, Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]}"))) }, hooked);
        var first = Task.Run(() => overlay.Plan());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        using var cancel = new CancellationTokenSource();
        var second = Task.Run(() => overlay.Plan(cancel.Token));
        await Task.Delay(100);
        cancel.Cancel();

        await AssertEndsCancelled(second, "Plan");
        Assert.False(first.IsCompleted);   // the first caller is still in the index build
        release.Set();
        var plan = await Finishes(first, "the first caller");
        Assert.Single(plan.Targets);
        Assert.Same(plan, overlay.Plan());
    }

    [Fact]
    public async Task A_chunk_waited_for_while_another_caller_computes_it_ends_the_wait_when_the_waiters_token_is_cancelled()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hooked = new HookedGame(Install(Everything()));
        int reads = 0;
        hooked.ReadHook = (archive, chunk) =>
        {
            if (Interlocked.Increment(ref reads) == 1) { entered.Set(); release.Wait(); }
            return null;
        };
        var overlay = new GameDataOverlay(new[] { new GameDataLayerInput("base", 0, AddTag("b")) }, hooked);
        overlay.Plan();
        var first = Task.Run(() => overlay.Apply(ChunkOf(A)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        using var cancel = new CancellationTokenSource();
        var second = Task.Run(() => overlay.Apply(ChunkOf(A), cancel.Token));
        await Task.Delay(100);
        cancel.Cancel();

        await AssertEndsCancelled(second, "Apply");
        release.Set();
        var result = await Finishes(first, "the first caller");
        Assert.True(result!.Applied);
        Assert.Same(result, overlay.Apply(ChunkOf(A)));   // the one that computed it kept it
    }

    [Fact]
    public async Task A_wait_for_the_index_another_caller_is_loading_for_a_ptch_target_ends_when_the_waiters_token_is_cancelled()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hooked = new HookedGame(Install(Everything())) { BeforeObjects = _ => { entered.Set(); release.Wait(); } };
        var document = Doc(Target("data/t/p.bin", "{\"Test/Obj/P1\":{\"+tags\":[\"owned\"]}}"), Target("data/t/q.bin", "{\"Test/Obj/Q1\":{\"+tags\":[\"owned\"]}}"));
        var overlay = new GameDataOverlay(new[] { new GameDataLayerInput("base", 0, document) }, hooked);
        overlay.Plan();
        var first = Task.Run(() => overlay.Apply(ChunkOf("data/t/p.bin")));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        using var cancel = new CancellationTokenSource();
        var second = Task.Run(() => overlay.Apply(ChunkOf("data/t/q.bin"), cancel.Token));
        await Task.Delay(100);
        cancel.Cancel();

        await AssertEndsCancelled(second, "the wait for the index");
        release.Set();
        Assert.True((await Finishes(first, "the first caller"))!.Applied);
        Assert.True(overlay.Apply(ChunkOf("data/t/q.bin"))!.Applied);
    }

    private sealed class BlockingProgress : IProgress<GameIndexProgress>
    {
        private readonly ManualResetEventSlim _entered, _release;
        private int _calls;

        public BlockingProgress(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public void Report(GameIndexProgress value)
        {
            if (Interlocked.Increment(ref _calls) != 1) return;
            _entered.Set();
            _release.Wait();
        }
    }

    [Fact]
    public async Task An_installed_game_waited_for_while_another_caller_builds_it_ends_the_wait_when_the_waiters_token_is_cancelled()
    {
        string directory = Write(Everything(), "game-built");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var installed = new InstalledGame(directory, _temp.Combine("cache-built", "index.idx"), null, new BlockingProgress(entered, release));
        var first = Task.Run(() => installed.GetTable(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        using var cancel = new CancellationTokenSource();
        var second = Task.Run(() => installed.GetTable(cancel.Token));
        var third = Task.Run(() => installed.GetObjects(cancel.Token));
        await Task.Delay(100);
        cancel.Cancel();

        await AssertEndsCancelled(second, "GetTable");
        await AssertEndsCancelled(third, "GetObjects");
        Assert.False(first.IsCompleted);
        release.Set();
        await Finishes(first, "the first caller");
        Assert.NotNull(installed.GetObjects(CancellationToken.None));   // and the call that was cancelled left nothing behind
    }

    [Fact]
    public async Task Asking_for_the_table_and_reading_a_chunk_at_once_never_finds_a_table_without_its_reader()
    {
        string directory = Write(Everything(), "game-race");
        string cache = _temp.Combine("cache-race", "index.idx");
        new InstalledGame(directory, cache).GetTable(CancellationToken.None);   // written: every round below loads it
        var failures = new List<string>();

        for (int round = 0; round < 300; round++)
        {
            var installed = new InstalledGame(directory, cache);
            using var go = new ManualResetEventSlim();
            var tableAsker = Task.Run(() => { go.Wait(); installed.GetTable(CancellationToken.None); });
            var chunkReader = Task.Run(() =>
            {
                go.Wait();
                try { installed.Read(0, ChunkOf(A)); }
                catch (Exception e) { lock (failures) failures.Add($"{e.GetType().Name}: {e.Message}"); }
            });
            go.Set();
            await Task.WhenAll(tableAsker, chunkReader).WaitAsync(TimeSpan.FromSeconds(30));   // a deadlock is a TimeoutException
        }

        Assert.Empty(failures);
    }

    // ================================================================================================ the layers in play are the ones the overlay was made with

    [Fact]
    public void The_layers_in_play_are_the_set_the_overlay_was_made_with_and_not_the_set_the_caller_still_holds()
    {
        var game = Everything();
        var mod = new SyntheticMod().Layer("base", 0, AddTag("b")).Layer("x", 1, AddTag("x")).Layer("y", 2, AddTag("y"));
        var active = new HashSet<string> { "x" };
        var overlay = Make(game, mod, options: new GameDataOverlayOptions { ActiveLayers = active });
        var other = Make(game, mod, options: new GameDataOverlayOptions { ActiveLayers = new HashSet<string> { "x" } });

        // the caller changes its set after the overlay exists, and before the plan, the base and the fingerprint are asked for
        active.Clear();
        active.Add("y");

        Assert.Equal(new[] { "base", "x" }, overlay.Plan().Layers.Where(l => l.Active).Select(l => l.Name).ToArray());
        Assert.Equal(new[] { "g1", "b", "x" }, TagsOf(Applied(overlay, A).Bytes!, "Test/Obj/A"));
        Assert.Equal(other.DocumentsFingerprint, overlay.DocumentsFingerprint);
    }

    // ================================================================================================ what a package may make the overlay do

    [Fact]
    public void Entries_no_bin_declares_count_against_the_limit_on_applications_like_any_other()
    {
        var names = Enumerable.Range(0, 20).Select(i => $"\"Test/Obj/Nobody{i}\":{{\"+tags\":[\"x\"]}}");
        var mod = new SyntheticMod().Layer("base", 0, Doc(Entries(string.Join(",", names))));

        var plan = Make(Everything(), mod, options: new GameDataOverlayOptions { MaxApplications = 5 }).Plan();

        Assert.Equal(5, plan.Diagnostics.Count(d => d.Kind == GameDataOverlayDiagnosticKind.EntryUnresolved));
        var limit = Assert.Single(plan.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.LimitExceeded);
        Assert.Contains("more than the 5 applications", limit.Message);
        Assert.Empty(plan.Targets);
    }

    [Fact]
    public void Results_are_kept_while_there_is_room_and_one_that_is_not_kept_is_computed_again()
    {
        var document = Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}"), Target(X, "{\"Test/Obj/X\":{\"+tags\":[\"b\"]}}"));
        var mod = new SyntheticMod().Layer("base", 0, document);
        var roomy = Make(Everything(), mod);
        Assert.Same(Applied(roomy, A), Applied(roomy, A));

        // room for the first result and not for the second
        long first = Applied(roomy, A).Bytes!.Length;
        var tight = Make(Everything(), mod, options: new GameDataOverlayOptions { MaxCachedBytes = first + 1 });
        var a1 = Applied(tight, A);
        var x1 = Applied(tight, X);

        Assert.Same(a1, Applied(tight, A));
        Assert.NotSame(x1, Applied(tight, X));
        Assert.Equal(x1.Bytes, Applied(tight, X).Bytes);
        // and a budget of nothing keeps nothing, and still answers
        var none = Make(Everything(), mod, options: new GameDataOverlayOptions { MaxCachedBytes = 0 });
        Assert.NotSame(Applied(none, A), Applied(none, A));
    }

    // ================================================================================================ what the plan tells of the game's index

    [Fact]
    public void The_plan_says_which_archives_the_index_could_not_read_which_of_those_were_a_failure_of_the_moment_and_why_nothing_was_kept()
    {
        var game = new SyntheticGame()
            .Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("B.wad.client", X, Bin(Obj("Test/Obj/X", new[] { "x" })));
        string directory = Write(game, "game-held");
        string cache = _temp.Combine("cache-held", "index.idx");
        var mod = new SyntheticMod().Layer("base", 0, Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]},\"Test/Obj/X\":{\"+tags\":[\"e\"]}")));

        using (new FileStream(Path.Combine(directory, "DATA", "FINAL", "B.wad.client"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var installed = new InstalledGame(directory, cache);
            var plan = new GameDataOverlay(mod.ToLayers(), installed, mod.ToFiles()).Plan();

            var skipped = Assert.Single(plan.SkippedArchives);
            Assert.Equal("B.wad.client", skipped.Name);
            Assert.True(skipped.Transient);
            Assert.StartsWith("DATA/FINAL/B.wad.client: cannot open the archive: ", Assert.Single(plan.IndexUnsettled));
            Assert.Contains("a read failed for a reason of the moment", plan.IndexNotSaved);
            Assert.False(File.Exists(cache));
            // what only B declares reads as absent this time, as the crate would report it
            Assert.Single(plan.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.EntryUnresolved && d.Target == "Test/Obj/X");
        }
    }

    [Fact]
    public void A_games_own_note_on_its_index_is_on_the_plan()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]}")));
        var hooked = new HookedGame(Install(Everything())) { Note = "the disk is full" };

        var plan = new GameDataOverlay(mod.ToLayers(), hooked, mod.ToFiles()).Plan();

        Assert.Equal("the disk is full", plan.IndexNotSaved);
        Assert.Empty(plan.IndexUnsettled);
    }

    [Fact]
    public void A_game_whose_note_on_its_index_throws_is_a_note_on_the_plan_and_neither_ends_it_nor_every_call_after_it()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]}")));
        var hooked = new HookedGame(Install(Everything())) { NoteFailure = new InvalidOperationException("the note is gone") };
        var overlay = new GameDataOverlay(mod.ToLayers(), hooked, mod.ToFiles());

        var plan = overlay.Plan();

        Assert.Null(plan.IndexNotSaved);
        Assert.Equal("the game could not say whether its index was kept: internal error (InvalidOperationException): the note is gone", Assert.Single(plan.IndexUnsettled));
        // the plan is the plan, and every later call goes on as it would
        Assert.Same(plan, overlay.Plan());
        Assert.True(overlay.Apply(ChunkOf(A))!.Applied);
        var build = overlay.BuildAll();
        Assert.Single(build.Chunks);
        Assert.True(build.Chunks[0].Applied);
        // an I/O failure of the getter says its own words
        hooked.NoteFailure = new IOException("the cache folder is unreachable");
        var again = new GameDataOverlay(mod.ToLayers(), hooked, mod.ToFiles()).Plan();
        Assert.Equal("the game could not say whether its index was kept: the cache folder is unreachable", Assert.Single(again.IndexUnsettled));
    }

    [Fact]
    public void The_plan_of_a_settled_index_has_nothing_to_say_of_it()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]}")));

        var plan = Make(Everything(), mod).Plan();

        Assert.Empty(plan.SkippedArchives);
        Assert.Empty(plan.IndexUnsettled);
        Assert.Null(plan.IndexNotSaved);
    }
}
