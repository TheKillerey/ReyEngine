using System.IO.Hashing;
using System.Text;
using System.Text.Json.Nodes;
using ReyEngine.Formats.LtkGameData;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M818: the game-data stage of <c>ltk_overlay</c> over a synthetic installation - which layers play and in what order, which bytes a target's base is, how entries bind to chunks, what the
/// object index is asked for and when, and every diagnostic. <c>Fixtures/LtkGameData/overlay-scenarios.json</c> is what <c>ltk_overlay</c> itself produced for the scenarios of
/// <see cref="Scenarios"/>; the first test replays all of them. The rest state each rule on its own, where the answer is a few strings a reader can check.
/// </summary>
public sealed class LtkGameDataOverlayTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private int _installs;

    public void Dispose() => _temp.Dispose();

    private InstalledGame Install(SyntheticGame game)
    {
        int n = _installs++;
        string directory = game.Write(_temp.Combine("game" + n));
        return new InstalledGame(directory, _temp.Combine("cache" + n, "index.idx"));
    }

    private GameDataOverlay Overlay(SyntheticGame game, SyntheticMod mod, string[]? active = null, GameDataOverlayOptions? options = null, IGameDataGame? installed = null)
    {
        options ??= new GameDataOverlayOptions();
        if (active is not null) options = Copy(options, active);
        return new GameDataOverlay(mod.ToLayers(), installed ?? Install(game), mod.ToFiles(), options);
    }

    private static GameDataOverlayOptions Copy(GameDataOverlayOptions o, string[] active) => new()
    {
        ModId = o.ModId, ActiveLayers = active.ToHashSet(), Schema = o.Schema, Limits = o.Limits, MaxDocumentBytes = o.MaxDocumentBytes,
        MaxOverrideFileBytes = o.MaxOverrideFileBytes, MaxApplications = o.MaxApplications, MaxWorkPerChunk = o.MaxWorkPerChunk,
        MaxModChunkBytes = o.MaxModChunkBytes, MaxGameChunkBytes = o.MaxGameChunkBytes, References = o.References, MaxCachedBytes = o.MaxCachedBytes,
    };

    private const string A = "data/t/a.bin", B = "data/t/b.bin";

    private static string[] Tags(byte[] bytes, string path = "Test/Obj/A") => TagsOf(bytes, path);

    private static GameDataChunkResult Applied(GameDataOverlay overlay, string path)
    {
        var result = overlay.Apply(ChunkOf(path));
        Assert.NotNull(result);
        return result!;
    }

    // ================================================================================================ the replay

    private static string Digest(byte[] bytes) =>
        $"{XxHash64.HashToUInt64(bytes, 0):x16}{XxHash64.HashToUInt64(bytes, unchecked((long)0x9E3779B97F4A7C15UL)):x16}";

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static JsonNode Describe(GameDataOverlayDiagnostic d) => new JsonObject
    {
        ["kind"] = d.KindName,
        ["modId"] = d.ModId,
        ["layer"] = d.Layer,
        ["target"] = d.Target,
        ["chunk"] = d.Chunk is { } c ? $"{c:x16}" : null,
        ["origin"] = d.Origin is { } o ? new JsonObject { ["manifest"] = o.Manifest, ["source"] = o.Source, ["module"] = (long)(ulong)o.ModuleIndex } : null,
        ["edit"] = d.Edit,
        ["record"] = d.Record is { } r ? new JsonObject { ["index"] = r.Index, ["object"] = $"{r.Object:x8}", ["property"] = r.Property, ["reason"] = Camel(r.Reason.ToString()) } : null,
        ["property"] = d.Property is { } p ? new JsonObject { ["entry"] = p.Entry.Text, ["reason"] = Camel(p.Reason.ToString()) } : null,
        ["object"] = d.Object is { } ob ? new JsonObject { ["name"] = ob.Name.Text, ["reason"] = Camel(ob.Reason.ToString()) } : null,
        ["message"] = d.Message,
    };

    private static readonly Lazy<JsonObject> Recorded = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LtkGameData", "overlay-scenarios.json");
        Assert.True(File.Exists(path), $"the overlay fixture was not copied to the output folder: {path}");
        return (JsonObject)JsonNode.Parse(File.ReadAllBytes(path))!;
    });

    public static IEnumerable<object[]> ScenarioNames() => Scenarios.All().Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Every_scenario_is_what_ltk_overlay_made_of_it_chunk_for_chunk_and_diagnostic_for_diagnostic(string name)
    {
        var scenario = Scenarios.All().Single(s => s.Name == name);
        var expected = (JsonObject)Recorded.Value[name]!;
        var overlay = Overlay(scenario.Game, scenario.Mod, scenario.ActiveLayers);

        var build = overlay.BuildAll();

        var chunks = build.Chunks.Where(c => c.Applied).ToList();
        var recordedChunks = (JsonArray)expected["chunks"]!;
        Assert.Equal(recordedChunks.Count, chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            var recorded = (JsonObject)recordedChunks[i]!;
            Assert.Equal(recorded["chunk"]!.GetValue<string>(), $"{chunks[i].Chunk:x16}");
            Assert.Equal(recorded["len"]!.GetValue<int>(), chunks[i].Bytes!.Length);
            Assert.Equal(recorded["h"]!.GetValue<string>(), Digest(chunks[i].Bytes!));
            Assert.Equal(recorded["dependencies"]!.ToJsonString(), new JsonArray(chunks[i].Dependencies.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()).ToJsonString());
        }

        var recordedDiagnostics = (JsonArray)expected["diagnostics"]!;
        Assert.Equal(recordedDiagnostics.Count, build.Diagnostics.Count);
        for (int i = 0; i < build.Diagnostics.Count; i++)
            Assert.True(JsonNode.DeepEquals(recordedDiagnostics[i], Describe(build.Diagnostics[i])),
                $"diagnostic {i}: recorded {recordedDiagnostics[i]!.ToJsonString()}, got {Describe(build.Diagnostics[i]).ToJsonString()}");
    }

    // ================================================================================================ layers and bases

    private static (SyntheticGame Game, SyntheticMod Mod) LayeredCopies(bool raw = false)
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })));
        var mod = new SyntheticMod()
            .Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")))
            .Layer("x", 1, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"x\"]}}")));
        mod.WadFiles.Add(("base", "A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "m_base" }))));
        mod.WadFiles.Add(("x", "A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "m_x" }))));
        if (raw) mod.Raw.Add((A, Bin(Obj("Test/Obj/A", new[] { "m_raw" }))));
        return (game, mod);
    }

    [Fact]
    public void The_higher_priority_layers_copy_of_a_chunk_is_the_base_and_each_layers_module_runs_over_it_in_order()
    {
        var (game, mod) = LayeredCopies();

        var result = Applied(Overlay(game, mod), A);

        Assert.Equal(GameDataBaseKind.ModLayer, result.BaseKind);
        Assert.Equal("x", result.BaseLayer);
        Assert.Equal(new[] { "m_x", "b", "x" }, Tags(result.Bytes!));
    }

    [Fact]
    public void An_inactive_layer_contributes_neither_its_files_nor_its_modules()
    {
        var (game, mod) = LayeredCopies();

        var result = Applied(Overlay(game, mod, new string[0]), A);

        Assert.Equal("base", result.BaseLayer);
        Assert.Equal(new[] { "m_base", "b" }, Tags(result.Bytes!));
    }

    [Fact]
    public void The_base_layer_is_active_whatever_the_set_of_layers_in_play_says()
    {
        var (game, mod) = LayeredCopies();

        var result = Applied(Overlay(game, mod, new[] { "x" }), A);

        Assert.Equal(new[] { "m_x", "b", "x" }, Tags(result.Bytes!));
        Assert.Equal(new[] { "base", "x" }, Overlay(game, mod, new[] { "x" }).Plan().Layers.Where(l => l.Active).Select(l => l.Name).ToArray());
    }

    [Fact]
    public void A_raw_file_is_the_base_over_every_layers_copy()
    {
        var (game, mod) = LayeredCopies(raw: true);

        var result = Applied(Overlay(game, mod), A);

        Assert.Equal(GameDataBaseKind.ModRaw, result.BaseKind);
        Assert.Equal(new[] { "m_raw", "b", "x" }, Tags(result.Bytes!));
    }

    [Fact]
    public void Without_a_copy_in_the_mod_the_base_is_the_games_from_the_first_holder()
    {
        var game = new SyntheticGame()
            .Add("B.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "from_B" })))
            .Add("a.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "from_a" })));
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")));

        var result = Applied(Overlay(game, mod), A);

        Assert.Equal(GameDataBaseKind.Game, result.BaseKind);
        Assert.Equal(0, result.BaseArchive);   // 'B' sorts before 'a' in bytes
        Assert.Equal(new[] { "from_B", "b" }, Tags(result.Bytes!));
    }

    [Fact]
    public void A_copy_the_mod_ships_is_the_base_even_where_it_is_byte_identical_to_the_games()
    {
        byte[] bytes = Bin(Obj("Test/Obj/A", new[] { "same" }));
        var game = new SyntheticGame().Add("A.wad.client", A, bytes);
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")));
        mod.WadFiles.Add(("base", "A.wad.client", A, bytes));

        var result = Applied(Overlay(game, mod), A);

        Assert.Equal(GameDataBaseKind.ModLayer, result.BaseKind);
    }

    [Fact]
    public void Layers_apply_base_first_then_by_priority_then_by_name_as_a_person_reads_it()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g" })));
        string Add(string tag) => Doc(Target(A, $"{{\"Test/Obj/A\":{{\"+tags\":[\"{tag}\"]}}}}"));
        var mod = new SyntheticMod()
            .Layer("layer10", 0, Add("layer10")).Layer("layer9", 0, Add("layer9")).Layer("Layer2", 0, Add("Layer2"))
            .Layer("late", 5, Add("late")).Layer("layer09", 0, Add("layer09")).Layer("base", 3, Add("base"));
        var overlay = Overlay(game, mod);

        // upper case before lower in bytes, 9 before 10 by number, 09 before 9 where the numbers tie, and the base layer first whatever its priority
        Assert.Equal(new[] { "base", "Layer2", "layer09", "layer9", "layer10", "late" }, overlay.Plan().Layers.Select(l => l.Name).ToArray());
        Assert.Equal(new[] { "g", "base", "Layer2", "layer09", "layer9", "layer10", "late" }, Tags(Applied(overlay, A).Bytes!));
    }

    [Fact]
    public void A_mod_with_no_base_layer_has_one_and_it_comes_first()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g" })));
        var mod = new SyntheticMod().Layer("only", 9, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"o\"]}}")));

        var plan = Overlay(game, mod).Plan();

        Assert.Equal(new[] { "base", "only" }, plan.Layers.Select(l => l.Name).ToArray());
    }

    [Fact]
    public void Several_modules_on_one_target_each_run_over_the_bytes_the_previous_one_left()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })));
        var mod = new SyntheticMod().Layer("base", 0, Doc(
            Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"1\"]}}"),
            Target(A, "{\"Test/Obj/A\":{\"nope\":5}}"),
            Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"3\"]},\"links\":[\"data/other.bin\"]}")));

        var result = Applied(Overlay(game, mod), A);

        Assert.Equal(new[] { "g1", "1", "3" }, Tags(result.Bytes!));
        Assert.Equal(new[] { "data/other.bin" }, result.Dependencies);
        Assert.Equal(3, result.Applications);
        var kinds = result.Diagnostics.Select(d => d.Kind).ToArray();
        Assert.Equal(
            new[]
            {
                GameDataOverlayDiagnosticKind.SchemaFallback, GameDataOverlayDiagnosticKind.PropertyEditSkipped, GameDataOverlayDiagnosticKind.NoEffect,
                GameDataOverlayDiagnosticKind.SchemaFallback,
            },
            kinds);
        // the application that did nothing says which module it was
        var noEffect = result.Diagnostics.Single(d => d.Kind == GameDataOverlayDiagnosticKind.NoEffect);
        Assert.Equal(1UL, noEffect.Origin!.ModuleIndex);
        Assert.Equal(A, noEffect.Target);
        Assert.Equal("every edit was skipped, so the target is unchanged", noEffect.Message);
    }

    // ================================================================================================ entries

    private static (SyntheticGame Game, SyntheticMod Mod) FanOut()
    {
        var game = new SyntheticGame()
            .Add("B.wad.client", "data/t/b1.bin", Bin(Obj("Test/Obj/E", new[] { "e_in_b1" }), Obj("Test/Obj/Other", new[] { "o" })))
            .Add("C.wad.client", "data/t/c1.bin", Bin(Obj("Test/Obj/E", new[] { "e_in_c1" })))
            .Add("C.wad.client", "data/t/c2.bin", Bin(Obj("Test/Obj/G", new[] { "g_in_c2" })));
        var mod = new SyntheticMod().Layer("base", 0, Doc(Entries(
            "\"Test/Obj/E\":{\"+tags\":[\"added\"]},\"Test/Obj/F\":{\"+tags\":[\"nowhere\"]},\"Test/Obj/G\":{\"+tags\":[\"g\"],\"links\":[\"data/dep.bin\"]}")));
        return (game, mod);
    }

    [Fact]
    public void An_entries_module_edits_every_chunk_that_declares_its_entry_and_reports_the_ones_that_matter()
    {
        var (game, mod) = FanOut();
        var overlay = Overlay(game, mod);

        var build = overlay.BuildAll();

        Assert.Equal(3, build.Chunks.Count(c => c.Applied));
        Assert.Equal(new[] { "e_in_b1", "added" }, Tags(Applied(overlay, "data/t/b1.bin").Bytes!, "Test/Obj/E"));
        Assert.Equal(new[] { "e_in_c1", "added" }, Tags(Applied(overlay, "data/t/c1.bin").Bytes!, "Test/Obj/E"));
        var c2 = Applied(overlay, "data/t/c2.bin");
        Assert.Equal(new[] { "g_in_c2", "g" }, Tags(c2.Bytes!, "Test/Obj/G"));
        Assert.Equal(new[] { "data/dep.bin" }, c2.Dependencies);   // the links of an entry ride along on the chunk that declares it

        var plan = build.Plan.Diagnostics;
        var fanOut = Assert.Single(plan, d => d.Kind == GameDataOverlayDiagnosticKind.EntryFanOut);
        Assert.Equal("Test/Obj/E", fanOut.Target);
        Assert.Null(fanOut.Chunk);
        Assert.StartsWith("Entry is declared in 2 chunks, each edited: ", fanOut.Message);
        Assert.Contains("(B.wad.client)", fanOut.Message);
        Assert.Contains("(C.wad.client)", fanOut.Message);
        var unresolved = Assert.Single(plan, d => d.Kind == GameDataOverlayDiagnosticKind.EntryUnresolved);
        Assert.Equal("Test/Obj/F", unresolved.Target);
        Assert.Equal("No game bin declares the entry; edits are skipped", unresolved.Message);
        // an entry that one chunk declares is not worth a word
        Assert.DoesNotContain(plan, d => d.Target == "Test/Obj/G");
    }

    [Fact]
    public void The_plan_lists_the_chunks_each_layer_touches_and_the_game_archives_that_hold_each()
    {
        var (game, mod) = FanOut();
        var mod2 = new SyntheticMod()
            .Layer("base", 0, Doc(Entries("\"Test/Obj/E\":{\"+tags\":[\"added\"]}")))
            .Layer("extra", 1, Doc(Target("data/t/c2.bin", "{\"Test/Obj/G\":{\"+tags\":[\"x\"]}}")));

        var plan = Overlay(game, mod2).Plan();

        Assert.Equal(new[] { "base", "extra" }, plan.Layers.Select(l => l.Name).ToArray());
        Assert.Equal(2, plan.Layers[0].Targets.Count);
        Assert.Equal(new[] { ChunkOf("data/t/c2.bin") }, plan.Layers[1].Targets);
        Assert.Equal(3, plan.Targets.Count);
        Assert.Equal(plan.Targets.Select(t => t.Chunk).Order().ToArray(), plan.Targets.Select(t => t.Chunk).ToArray());   // ascending path hash
        var c2 = plan.Target(ChunkOf("data/t/c2.bin"))!;
        Assert.Equal(new[] { 1 }, c2.Holders);
        Assert.Equal(new[] { "DATA/FINAL/C.wad.client" }, c2.HolderPaths);
        Assert.Equal(new[] { "extra" }, c2.Layers);
        Assert.Null(plan.Target(0x1234));
        Assert.True(plan.UsedObjectIndex);
    }

    [Fact]
    public void An_entry_spelled_by_name_and_by_hash_in_two_modules_of_one_chunk_applies_twice()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })));
        string hash = "0x" + H("Test/Obj/A").ToString("x8");
        var mod = new SyntheticMod()
            .Layer("base", 0, Doc(
                Entries("\"Test/Obj/A\":{\"+tags\":[\"e1\"]}"),
                Target(A, $"{{\"{hash}\":{{\"+tags\":[\"t1\"]}}}}"),
                Entries($"\"{hash}\":{{\"+tags\":[\"e2\"]}}")))
            .Layer("z", 1, Doc(Entries("\"Test/Obj/A\":{\"-tags\":[\"e1\"]}")));

        var result = Applied(Overlay(game, mod), A);

        Assert.Equal(new[] { "g1", "t1", "e2" }, Tags(result.Bytes!));
        Assert.Equal(4, result.Applications);
    }

    // ================================================================================================ references

    [Fact]
    public void A_reference_reads_the_first_chunk_that_declares_the_entry_in_byte_order_of_the_archive_name_and_then_by_chunk_hash()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target("data/t/d.bin", "{\"Test/Obj/D\":{\"+tags\":{\"ref\":\"Test/Obj/X:tags\"}}}")));

        // 'Z' sorts before 'a': the copy in Z.wad.client is the first declaration, though a.wad.client comes first in a case-insensitive listing
        var byArchive = new SyntheticGame()
            .Add("a.wad.client", "data/t/xa.bin", Bin(Obj("Test/Obj/X", new[] { "from_a" })))
            .Add("Z.wad.client", "data/t/xz.bin", Bin(Obj("Test/Obj/X", new[] { "from_z" })))
            .Add("D.wad.client", "data/t/d.bin", Bin(Obj("Test/Obj/D", new[] { "d" })));
        Assert.Equal(new[] { "d", "from_z" }, Tags(Applied(Overlay(byArchive, mod), "data/t/d.bin").Bytes!, "Test/Obj/D"));

        // within one archive the lowest chunk hash is first
        var byHash = new SyntheticGame()
            .AddHash("R.wad.client", 0x2000, Bin(Obj("Test/Obj/X", new[] { "high_hash" })))
            .AddHash("R.wad.client", 0x1000, Bin(Obj("Test/Obj/X", new[] { "low_hash" })))
            .Add("D.wad.client", "data/t/d.bin", Bin(Obj("Test/Obj/D", new[] { "d" })));
        Assert.Equal(new[] { "d", "low_hash" }, Tags(Applied(Overlay(byHash, mod), "data/t/d.bin").Bytes!, "Test/Obj/D"));
    }

    [Fact]
    public void A_reference_reads_the_unmodified_game_never_the_mods_own_copy_or_another_modules_edit()
    {
        var game = new SyntheticGame()
            .Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("X.wad.client", "data/t/x.bin", Bin(Obj("Test/Obj/X", new[] { "game_x" })));
        var mod = new SyntheticMod().Layer("base", 0, Doc(
            Target("data/t/x.bin", "{\"Test/Obj/X\":{\"+tags\":[\"edited\"]}}"),
            Target(A, "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/X:tags\"}}}")));
        mod.WadFiles.Add(("base", "X.wad.client", "data/t/x.bin", Bin(Obj("Test/Obj/X", new[] { "mod_x" }))));

        var overlay = Overlay(game, mod);

        Assert.Equal(new[] { "g1", "game_x" }, Tags(Applied(overlay, A).Bytes!));
        Assert.Equal(new[] { "mod_x", "edited" }, Tags(Applied(overlay, "data/t/x.bin").Bytes!, "Test/Obj/X"));
    }

    [Fact]
    public void A_reference_to_an_entry_the_game_lacks_or_a_path_it_does_not_resolve_is_a_skipped_key_and_a_ptch_first_holder_is_unreadable()
    {
        var game = new SyntheticGame()
            .Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("P.wad.client", "data/t/p.bin", Ptch(new[] { Obj("Test/Obj/P1", new[] { "p1" }) }))
            .Add("X.wad.client", "data/t/x.bin", Bin(Obj("Test/Obj/Src", new[] { "from_src" }, count: 7)));
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A,
            "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/Nope:tags\"},\"count\":{\"ref\":\"Test/Obj/Src:nofield\"}}}," +
            "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/P1:tags\"}}}," +
            "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/Src:tags\"},\"count\":{\"ref\":\"Test/Obj/Src:count\"}}}")));

        var result = Applied(Overlay(game, mod), A);

        var reasons = result.Diagnostics.Where(d => d.Kind == GameDataOverlayDiagnosticKind.PropertyEditSkipped).Select(d => d.Property!.Reason).ToArray();
        Assert.Equal(new[] { PropertySkipReason.ReferenceMissingEntry, PropertySkipReason.ReferenceUnresolved, PropertySkipReason.ReferenceMissingEntry }, reasons);
        var unreadable = Assert.Single(result.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.ReferenceUnreadable);
        Assert.Contains("Expected a PROP bin, found a PTCH bin", unreadable.Message);
        Assert.Contains("DATA/FINAL/P.wad.client", unreadable.Message);
        Assert.Equal(new[] { "g1", "from_src" }, Tags(result.Bytes!));
    }

    [Fact]
    public void An_entry_is_read_once_for_the_whole_overlay_however_many_targets_name_it()
    {
        var game = new SyntheticGame().Add("X.wad.client", "data/t/x.bin", Bin(Obj("Test/Obj/X", new[] { "x" })));
        var modules = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            game.Add($"T{i}.wad.client", $"data/t/t{i}.bin", Bin(Obj($"Test/Obj/T{i}", new[] { "t" })));
            modules.Add(Target($"data/t/t{i}.bin", $"{{\"Test/Obj/T{i}\":{{\"+tags\":{{\"ref\":\"Test/Obj/X:tags\"}}}}}}"));
        }
        var mod = new SyntheticMod().Layer("base", 0, Doc(modules.ToArray()));
        var spy = new Spy(Install(game));
        var overlay = new GameDataOverlay(mod.ToLayers(), spy, mod.ToFiles());

        var build = overlay.BuildAll();

        Assert.Equal(6, build.Chunks.Count(c => c.Applied));
        // six targets read once each, and the entry's bin once for all of them
        Assert.Equal(7, spy.ChunkReads);
    }

    // ================================================================================================ layers refused, targets refused

    [Fact]
    public void A_layer_whose_document_is_refused_is_dropped_whole_and_the_others_apply()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })));
        var mod = new SyntheticMod()
            .Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"ok\"]}}")))
            .Layer("bad", 1, Doc(Target(A, "{\"unsupported\":1}")))
            .Layer("worse", 2, "{\"version\":2,\"modules\":[]}");
        var overlay = Overlay(game, mod);

        var plan = overlay.Plan();

        Assert.Equal(new[] { "ok" }, Tags(Applied(overlay, A).Bytes!).Skip(1));
        var rejected = plan.Diagnostics.Where(d => d.Kind == GameDataOverlayDiagnosticKind.DeclarationsRejected).ToList();
        Assert.Equal(new[] { "bad", "worse" }, rejected.Select(d => d.Layer).ToArray());
        Assert.Equal("Layer declarations refused: unsupported: unsupported binding `unsupported`; update the consumer for unsupported bindings", rejected[0].Message);
        Assert.Equal("Layer declarations refused: unsupported declaration version; update the consumer for unsupported bindings", rejected[1].Message);
        Assert.All(rejected, d => { Assert.Null(d.Target); Assert.Null(d.Chunk); Assert.Null(d.Origin); });
        Assert.True(plan.Layers.Single(l => l.Name == "bad").Rejected);
    }

    [Fact]
    public void A_target_the_game_lacks_or_blocks_is_skipped_with_a_statement_for_each_application()
    {
        string toc = "data/final/champions/aatrox.wad.subchunktoc";
        var game = new SyntheticGame()
            .Add("Champions/Aatrox.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("Champions/Aatrox.wad.client", toc, new byte[] { 1, 2, 3, 4 });
        var mod = new SyntheticMod().Layer("base", 0, Doc(
            Target("data/t/missing.bin", "{\"Test/Obj/A\":{\"+tags\":[\"x\"]}}"),
            Target("data/t/missing.bin", "{\"Test/Obj/A\":{\"+tags\":[\"y\"]}}"),
            Target(toc, "{\"Test/Obj/A\":{\"+tags\":[\"x\"]}}")));
        var overlay = Overlay(game, mod);

        var absent = Applied(overlay, "data/t/missing.bin");
        var blocked = Applied(overlay, toc);

        Assert.False(absent.Applied);
        Assert.Null(absent.Bytes);
        Assert.Equal(GameDataBaseKind.None, absent.BaseKind);
        Assert.Equal(2, absent.Diagnostics.Count);
        Assert.All(absent.Diagnostics, d =>
        {
            Assert.Equal(GameDataOverlayDiagnosticKind.TargetSkipped, d.Kind);
            Assert.Equal("target is absent from enabled content and the game index", d.Message);
        });
        Assert.Equal(new ulong[] { 0, 1 }, absent.Diagnostics.Select(d => d.Origin!.ModuleIndex).ToArray());
        Assert.Equal("target is a blocked game chunk", Assert.Single(blocked.Diagnostics).Message);
    }

    [Fact]
    public void A_mod_copy_that_is_not_a_bin_and_a_copy_that_cannot_be_read_skip_the_target()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })));
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")));
        mod.WadFiles.Add(("base", "A.wad.client", A, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

        var broken = Applied(Overlay(game, mod), A);

        Assert.False(broken.Applied);
        Assert.Equal("Invalid file signature", Assert.Single(broken.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.TargetSkipped).Message);

        var unreadable = new GameDataOverlay(mod.ToLayers(), Install(game), new ThrowingFiles());
        var skipped = Applied(unreadable, A);
        Assert.Equal(GameDataBaseKind.None, skipped.BaseKind);
        Assert.Equal("the file is locked", Assert.Single(skipped.Diagnostics).Message);
    }

    private sealed class ThrowingFiles : IGameDataModFiles
    {
        public byte[]? ReadLayerFile(string layer, ulong chunk, long maxBytes) => throw new IOException("the file is locked");

        public byte[]? ReadRawFile(ulong chunk, long maxBytes) => null;
    }

    // ================================================================================================ objects created

    [Fact]
    public void A_created_object_that_another_chunk_declares_is_reported_and_created()
    {
        var game = new SyntheticGame()
            .Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("S.wad.client", "data/t/s.bin", Bin(Obj("Test/Obj/Shadowed", new[] { "s" })));
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A,
            "{\"objects\":{\"Test/Obj/Shadowed\":{\"clone\":\"Test/Obj/A\",\"set\":{\"+tags\":[\"c\"]}},\"Test/Obj/Fresh\":{\"clone\":\"Test/Obj/A\"}}}")));
        var overlay = Overlay(game, mod);

        var plan = overlay.Plan();
        var result = Applied(overlay, A);

        var shadow = Assert.Single(plan.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.ObjectShadowsGame);
        Assert.Equal("Test/Obj/Shadowed", shadow.Target);
        Assert.StartsWith("Game bins declare the created object: ", shadow.Message);
        Assert.EndsWith(" (S.wad.client); the object is created", shadow.Message);
        Assert.Equal(new[] { "g1", "c" }, Tags(result.Bytes!, "Test/Obj/Shadowed"));
        Assert.Equal(new[] { "g1" }, Tags(result.Bytes!, "Test/Obj/Fresh"));
        Assert.Single(plan.Diagnostics);   // an object nobody else declares is not worth a word
    }

    // ================================================================================================ override files

    [Fact]
    public void Override_files_come_from_the_layer_and_are_read_once_and_a_missing_one_says_so()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" }), Obj("Test/Obj/Gone", new[] { "gone" })));
        var mod = new SyntheticMod().Layer("base", 0, Doc(
            Target(A, "{\"overrides\":[\"fix.ptch\",\"nofile.ptch\"],\"Test/Obj/A\":{\"+tags\":[\"after\"]}}"),
            Target(A, "{\"overrides\":[\"fix.ptch\"]}")));
        mod.LayerFiles.Add(("base", "fix.ptch", Ptch(new[] { Obj("Test/Obj/FromPatch", new[] { "pp" }) }, H("Test/Obj/Gone"))));
        var counting = new CountingLayers(mod);
        var overlay = new GameDataOverlay(counting.Layers(), Install(game), mod.ToFiles());

        var result = Applied(overlay, A);

        var bin = PropCodec.ReadProp(result.Bytes!);
        Assert.False(bin.Objects.ContainsKey(H("Test/Obj/Gone")));
        Assert.True(bin.Objects.ContainsKey(H("Test/Obj/FromPatch")));
        var unreadable = Assert.Single(result.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.OverrideUnreadable);
        Assert.Equal("Override file cannot be read: nofile.ptch (nofile.ptch: input is missing)", unreadable.Message);
        // fix.ptch is named by two modules and was read once; the one that is missing is read again by each module that names it
        Assert.Equal(1, counting.Reads["fix.ptch"]);
        Assert.Equal(1, counting.Reads["nofile.ptch"]);
    }

    private sealed class CountingLayers : IGameDataLayerFiles
    {
        private readonly SyntheticMod _mod;
        public readonly Dictionary<string, int> Reads = new();

        public CountingLayers(SyntheticMod mod) { _mod = mod; }

        public IReadOnlyList<GameDataLayerInput> Layers() => _mod.Layers.Select(l => new GameDataLayerInput(l.Name, l.Priority, l.GameData, this)).ToList();

        public byte[]? ReadOverrideFile(string path, long maxBytes)
        {
            lock (Reads) Reads[path] = Reads.GetValueOrDefault(path) + 1;
            return _mod.LayerFiles.FirstOrDefault(f => f.Path == path).Bytes;
        }
    }

    // ================================================================================================ the object index: when, and what if not

    /// <summary>The installed game with a count of what it was asked for, and a way to make each part fail.</summary>
    private sealed class Spy : IGameDataGame
    {
        private readonly IGameDataGame _inner;
        public int TableCalls, ObjectCalls, ChunkReads;
        public Exception? TableFailure, ObjectFailure;
        public Func<int, ulong, byte[]?>? ReadHook;

        public Spy(IGameDataGame inner) { _inner = inner; }

        public GameChunkTable GetTable(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref TableCalls);
            if (TableFailure is not null) throw TableFailure;
            return _inner.GetTable(cancellationToken);
        }

        public GameObjectIndex GetObjects(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ObjectCalls);
            if (ObjectFailure is not null) throw ObjectFailure;
            return _inner.GetObjects(cancellationToken);
        }

        public long LastMaxBytes;

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ChunkReads);
            LastMaxBytes = maxBytes;
            return ReadHook?.Invoke(archive, chunk) ?? _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);
        }
    }

    private static SyntheticGame Everything() => new SyntheticGame()
        .Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
        .Add("X.wad.client", "data/t/x.bin", Bin(Obj("Test/Obj/X", new[] { "x" })))
        .Add("P.wad.client", "data/t/p.bin", Ptch(new[] { Obj("Test/Obj/P1", new[] { "p1" }) }));

    [Theory]
    [InlineData("target only", 0)]
    [InlineData("reference", 1)]
    [InlineData("creation", 1)]
    [InlineData("entries", 1)]
    [InlineData("empty entries", 0)]
    public void The_object_index_is_loaded_when_a_module_needs_it_and_not_otherwise(string kind, int expectedLoads)
    {
        string document = kind switch
        {
            "target only" => Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"t\"]}}")),
            "reference" => Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/X:tags\"}}}")),
            "creation" => Doc(Target(A, "{\"objects\":{\"Test/Obj/New\":{\"clone\":\"Test/Obj/A\"}}}")),
            "entries" => Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]}")),
            _ => Doc(Entries("")),
        };
        var mod = new SyntheticMod().Layer("base", 0, document);
        var spy = new Spy(Install(Everything()));
        var overlay = new GameDataOverlay(mod.ToLayers(), spy, mod.ToFiles());

        overlay.Plan();
        overlay.BuildAll();

        Assert.Equal(expectedLoads, spy.ObjectCalls);
        Assert.Equal(expectedLoads == 1, overlay.Plan().UsedObjectIndex);
    }

    [Fact]
    public void A_ptch_target_loads_the_object_index_when_it_is_applied_and_not_when_the_plan_is_made()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target("data/t/p.bin", "{\"Test/Obj/P1\":{\"+tags\":[\"owned\"]}}")));
        var spy = new Spy(Install(Everything()));
        var overlay = new GameDataOverlay(mod.ToLayers(), spy, mod.ToFiles());

        overlay.Plan();
        Assert.Equal(0, spy.ObjectCalls);

        var result = Applied(overlay, "data/t/p.bin");

        Assert.Equal(1, spy.ObjectCalls);
        Assert.True(result.Applied);
        Assert.StartsWith("PTCH", Encoding.ASCII.GetString(result.Bytes!, 0, 4));
        Assert.Equal(new[] { "p1", "owned" }, TagsOfPatch(result.Bytes!, "Test/Obj/P1"));
    }

    private static string[] TagsOfPatch(byte[] bytes, string path)
    {
        var patch = PropCodec.ReadPtch(bytes);
        var obj = patch.Objects.ValueAt(patch.Objects.IndexOf(H(path)));
        return ((PropList)obj.Properties.ValueAt(obj.Properties.IndexOf(H("tags")))).Items.Select(i => ((PropString)i).Value).ToArray();
    }

    [Fact]
    public void An_unavailable_index_skips_the_entries_modules_and_the_references_but_keeps_the_rest_of_a_target()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(
            Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]}"),
            Target(A, "{\"Test/Obj/A\":{\"+tags\":{\"ref\":\"Test/Obj/X:tags\"},\"count\":9}}"),
            Target(A, "{\"objects\":{\"Test/Obj/New\":{\"clone\":\"Test/Obj/A\"}}}")));
        var spy = new Spy(Install(Everything())) { ObjectFailure = new IOException("the index is broken") };
        var overlay = new GameDataOverlay(mod.ToLayers(), spy, mod.ToFiles());

        var build = overlay.BuildAll();

        var unavailable = build.Plan.Diagnostics.Where(d => d.Kind == GameDataOverlayDiagnosticKind.IndexUnavailable).ToList();
        Assert.Equal(
            new[]
            {
                "Object index is unavailable: the index is broken; entries are skipped",
                "Object index is unavailable: the index is broken; references cannot resolve, so their keys are skipped",
                "Object index is unavailable: the index is broken; created objects are not checked against the game's entries",
            },
            unavailable.Select(d => d.Message).ToArray());
        Assert.Equal(new ulong[] { 0, 1, 2 }, unavailable.Select(d => d.Origin!.ModuleIndex).ToArray());
        Assert.Equal(new string?[] { null, null, null }, unavailable.Select(d => d.Target).ToArray());
        Assert.Equal(1, spy.ObjectCalls);
        Assert.Equal("the index is broken", build.Plan.ObjectIndexError);

        // the entries module made no application, and the other two applied: the reference skipped its key and the created object was created
        var result = Applied(overlay, A);
        Assert.Equal(2, result.Applications);
        var skipped = Assert.Single(result.Diagnostics, d => d.Property is not null && d.Property.Reason == PropertySkipReason.ReferenceMissingEntry);
        Assert.Equal("Property edit is skipped: +tags", skipped.Message);
        var bin = PropCodec.ReadProp(result.Bytes!);
        Assert.True(bin.Objects.ContainsKey(H("Test/Obj/New")));
    }

    [Fact]
    public void A_ptch_target_whose_index_is_unavailable_applies_without_a_word_about_it()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target("data/t/p.bin", "{\"Test/Obj/P1\":{\"+tags\":[\"owned\"]}}")));
        var spy = new Spy(Install(Everything())) { ObjectFailure = new IOException("the index is broken") };
        var overlay = new GameDataOverlay(mod.ToLayers(), spy, mod.ToFiles());

        var build = overlay.BuildAll();

        Assert.DoesNotContain(build.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.IndexUnavailable);
        Assert.True(Assert.Single(build.Chunks).Applied);
    }

    [Fact]
    public void A_chunk_table_that_is_unavailable_skips_every_target_that_reads_the_game_and_none_that_the_mod_ships()
    {
        var game = Everything();
        var mod = new SyntheticMod().Layer("base", 0, Doc(
            Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}"),
            Target("data/t/x.bin", "{\"Test/Obj/X\":{\"+tags\":[\"b\"]}}")));
        mod.WadFiles.Add(("base", "X.wad.client", "data/t/x.bin", Bin(Obj("Test/Obj/X", new[] { "mod_x" }))));
        var spy = new Spy(Install(game)) { TableFailure = new IOException("no table") };
        var overlay = new GameDataOverlay(mod.ToLayers(), spy, mod.ToFiles());

        var build = overlay.BuildAll();

        var skipped = build.Chunks.Single(c => c.Chunk == ChunkOf(A));
        Assert.Equal("the game index is unavailable: no table", Assert.Single(skipped.Diagnostics).Message);
        Assert.True(build.Chunks.Single(c => c.Chunk == ChunkOf("data/t/x.bin")).Applied);
        Assert.Equal("no table", build.Plan.TableError);
        Assert.Empty(build.Plan.Targets.SelectMany(t => t.Holders));
    }

    // ================================================================================================ lazy, cached, cancellable, shared

    [Fact]
    public void A_chunk_is_computed_once_and_served_from_the_cache_while_its_base_and_the_documents_are_what_they_were()
    {
        var game = Everything();
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")));
        var spy = new Spy(Install(game));
        var overlay = new GameDataOverlay(mod.ToLayers(), spy, mod.ToFiles());

        var first = Applied(overlay, A);
        var second = Applied(overlay, A);

        Assert.Same(first, second);
        Assert.Equal(1, spy.ChunkReads);
        Assert.Equal(overlay.DocumentsFingerprint, first.DocumentsFingerprint);
        Assert.Null(overlay.Apply(0x1234));
    }

    private sealed class MutableFiles : IGameDataModFiles
    {
        public byte[]? Copy;
        public int Reads;

        public byte[]? ReadLayerFile(string layer, ulong chunk, long maxBytes)
        {
            Interlocked.Increment(ref Reads);
            return layer == "base" && chunk == ChunkOf(A) ? Copy : null;
        }

        public byte[]? ReadRawFile(ulong chunk, long maxBytes) => null;
    }

    [Fact]
    public void A_mods_own_copy_that_changes_is_a_new_base_and_one_that_does_not_is_the_same_result()
    {
        var game = Everything();
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")));
        var files = new MutableFiles { Copy = Bin(Obj("Test/Obj/A", new[] { "one" })) };
        var overlay = new GameDataOverlay(mod.ToLayers(), Install(game), files);

        var first = Applied(overlay, A);
        files.Copy = Bin(Obj("Test/Obj/A", new[] { "one" }));   // another array, the same bytes
        var same = Applied(overlay, A);
        files.Copy = Bin(Obj("Test/Obj/A", new[] { "two" }));
        var changed = Applied(overlay, A);

        Assert.Same(first, same);
        Assert.Equal(new[] { "one", "b" }, Tags(first.Bytes!));
        Assert.NotSame(first, changed);
        Assert.NotEqual(first.BaseFingerprint, changed.BaseFingerprint);
        Assert.Equal(new[] { "two", "b" }, Tags(changed.Bytes!));
        Assert.Equal(3, files.Reads);   // a mod's copy is read each time, to know what it is
    }

    [Fact]
    public void A_result_a_failed_read_produced_is_not_kept()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")));
        var spy = new Spy(Install(Everything()));
        int calls = 0;
        spy.ReadHook = (archive, chunk) => ++calls == 1 ? throw new GameChunkReadException("the disk is busy") : null;
        var overlay = new GameDataOverlay(mod.ToLayers(), spy, mod.ToFiles());

        var failed = Applied(overlay, A);
        var retried = Applied(overlay, A);

        Assert.False(failed.Applied);
        Assert.Equal("the disk is busy", Assert.Single(failed.Diagnostics).Message);
        Assert.True(retried.Applied);
        Assert.Equal(new[] { "g1", "b" }, Tags(retried.Bytes!));
    }

    [Fact]
    public void A_cancelled_call_leaves_nothing_cached_and_the_next_call_computes()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")));
        using var cancel = new CancellationTokenSource();
        var spy = new Spy(Install(Everything()));
        bool first = true;
        spy.ReadHook = (archive, chunk) =>
        {
            if (first) { first = false; cancel.Cancel(); }
            return null;
        };
        var overlay = new GameDataOverlay(mod.ToLayers(), spy, mod.ToFiles());

        Assert.Throws<OperationCanceledException>(() => overlay.Apply(ChunkOf(A), cancel.Token));

        var result = overlay.Apply(ChunkOf(A))!;
        Assert.True(result.Applied);
        Assert.Equal(new[] { "g1", "b" }, Tags(result.Bytes!));
    }

    [Fact]
    public void A_plan_cancelled_while_the_index_loads_is_made_again_by_the_next_call()
    {
        var mod = new SyntheticMod().Layer("base", 0, Doc(Entries("\"Test/Obj/A\":{\"+tags\":[\"e\"]}")));
        var game = new CancellingGame(Install(Everything()));
        var overlay = new GameDataOverlay(mod.ToLayers(), game, mod.ToFiles());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => overlay.Plan(cancel.Token));

        var plan = overlay.Plan();
        Assert.Single(plan.Targets);
        Assert.Null(plan.ObjectIndexError);
    }

    private sealed class CancellingGame : IGameDataGame
    {
        private readonly IGameDataGame _inner;

        public CancellingGame(IGameDataGame inner) { _inner = inner; }

        public GameChunkTable GetTable(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return _inner.GetTable(cancellationToken); }

        public GameObjectIndex GetObjects(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return _inner.GetObjects(cancellationToken); }

        public byte[] ReadChunk(int archive, ulong chunk, long maxBytes, CancellationToken cancellationToken) => _inner.ReadChunk(archive, chunk, maxBytes, cancellationToken);
    }

    [Fact]
    public void BuildAll_reports_every_diagnostic_in_the_order_the_crate_does_the_plans_and_then_each_chunks_by_ascending_hash()
    {
        var game = new SyntheticGame()
            .Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })))
            .Add("A.wad.client", B, Bin(Obj("Test/Obj/B", new[] { "g2" })));
        var mod = new SyntheticMod()
            .Layer("base", 0, Doc(
                Target(B, "{\"Test/Obj/B\":{\"nope\":1}}"),
                Target(A, "{\"Test/Obj/A\":{\"nope\":1}}")))
            .Layer("bad", 1, "{\"version\":9,\"modules\":[]}");
        var overlay = Overlay(game, mod);
        var progress = new List<GameDataOverlayProgress>();

        var build = overlay.BuildAll(new SyncProgress<GameDataOverlayProgress>(progress.Add));

        Assert.Equal(GameDataOverlayDiagnosticKind.DeclarationsRejected, build.Diagnostics[0].Kind);
        var chunks = build.Chunks.Select(c => c.Chunk).ToArray();
        Assert.Equal(chunks.Order().ToArray(), chunks);
        var rest = build.Diagnostics.Skip(1).Where(d => d.Chunk is not null).Select(d => d.Chunk!.Value).ToArray();
        Assert.Equal(rest.Order().ToArray(), rest);
        Assert.Equal(2, progress.Count);
        Assert.Equal(new[] { 1, 2 }, progress.Select(p => p.Done).ToArray());
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public SyncProgress(Action<T> report) { _report = report; }

        public void Report(T value) => _report(value);
    }

    [Fact]
    public void Many_threads_applying_many_chunks_over_entries_they_share_compute_each_chunk_and_each_entry_once()
    {
        const int chunks = 40;
        var game = new SyntheticGame().Add("X.wad.client", "data/t/x.bin", Bin(WideSource("Test/Obj/X")));
        var modules = new List<string>();
        for (int i = 0; i < chunks; i++)
        {
            game.Add($"T{i:D2}.wad.client", $"data/t/t{i}.bin", Bin(Obj($"Test/Obj/T{i}", new[] { "t" })));
            modules.Add(Target($"data/t/t{i}.bin", $"{{\"Test/Obj/T{i}\":{{\"+tags\":{{\"ref\":\"Test/Obj/X:tags\"}},\"count\":{{\"ref\":\"Test/Obj/X:p30\"}}}}}}"));
        }
        var mod = new SyntheticMod().Layer("base", 0, Doc(modules.ToArray()));
        var installed = Install(game);

        var expected = new GameDataOverlay(mod.ToLayers(), installed, mod.ToFiles()).BuildAll().Chunks.ToDictionary(c => c.Chunk, c => Digest(c.Bytes!));
        Assert.Equal(chunks, expected.Count);

        for (int round = 0; round < 25; round++)
        {
            // a fresh overlay each round: its entry reader decodes the shared source object anew, and that object's ordered map builds its index while the threads read it
            var spy = new Spy(installed);
            var overlay = new GameDataOverlay(mod.ToLayers(), spy, mod.ToFiles());
            overlay.Plan();
            var results = new Dictionary<ulong, string>();
            var failures = new List<string>();
            using var barrier = new Barrier(8);
            var workers = Enumerable.Range(0, 8).Select(w => new Thread(() =>
            {
                try
                {
                    // a deadlock is a failure that says so, and not a test that never ends
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)), "the threads did not all arrive");
                    foreach (var target in overlay.Plan().Targets.Skip(w * 3).Concat(overlay.Plan().Targets.Take(w * 3)))
                    {
                        var r = overlay.Apply(target.Chunk)!;
                        lock (results) results[r.Chunk] = Digest(r.Bytes!);
                    }
                }
                catch (Exception e) { lock (failures) failures.Add(e.ToString()); }
            }) { IsBackground = true }).ToList();
            foreach (var worker in workers) worker.Start();
            foreach (var worker in workers) Assert.True(worker.Join(TimeSpan.FromSeconds(120)), "a worker did not finish: a deadlock");

            Assert.Empty(failures);
            Assert.Equal(expected, results);
            // each target's base once, and the source entry's bin once
            Assert.Equal(chunks + 1, spy.ChunkReads);
        }
    }

    /// <summary>An object of forty properties besides its tags: enough that its ordered map builds an index, lazily, on the first reader to ask.</summary>
    private static PropObject WideSource(string path)
    {
        var obj = Obj(path, new[] { "x" });
        for (int i = 0; i < 40; i++) obj.Properties.Set(H("p" + i), new PropInt(PropKind.I32, (ulong)(uint)(1000 + i)));
        return obj;
    }

    // ================================================================================================ what a package may ask

    [Fact]
    public void More_applications_than_the_limit_are_not_made_and_the_overlay_says_so_once()
    {
        var game = new SyntheticGame();
        var entries = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            game.Add("A.wad.client", $"data/t/c{i}.bin", Bin(Obj($"Test/Obj/E{i}", new[] { "e" })));
            entries.Add($"\"Test/Obj/E{i}\":{{\"+tags\":[\"x\"]}}");
        }
        var mod = new SyntheticMod().Layer("base", 0, Doc(Entries(string.Join(",", entries))));

        var plan = Overlay(game, mod, options: new GameDataOverlayOptions { MaxApplications = 5 }).Plan();

        Assert.Equal(5, plan.Targets.Sum(t => t.Applications));
        var limit = Assert.Single(plan.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.LimitExceeded);
        Assert.Equal("base", limit.Layer);
        Assert.Contains("more than the 5 applications", limit.Message);
    }

    [Fact]
    public void A_document_larger_than_the_limit_rejects_its_layer_and_so_does_a_problem_the_caller_found_reading_it()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })));
        string big = Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"" + new string('x', 2000) + "\"]}}"));
        var layers = new List<GameDataLayerInput>
        {
            new("base", 0, null),
            new("big", 1, big),
            new("unread", 2, null, null, "the document cannot be read: access denied"),
        };
        var overlay = new GameDataOverlay(layers, Install(game), null, new GameDataOverlayOptions { MaxDocumentBytes = 1000 });

        var rejected = overlay.Plan().Diagnostics.Where(d => d.Kind == GameDataOverlayDiagnosticKind.DeclarationsRejected).ToList();

        Assert.Equal(new[] { "big", "unread" }, rejected.Select(d => d.Layer).ToArray());
        Assert.Equal("Layer declarations refused: the document is more than the 1,000 bytes the overlay reads; update the consumer for unsupported bindings", rejected[0].Message);
        Assert.Equal("Layer declarations refused: the document cannot be read: access denied; update the consumer for unsupported bindings", rejected[1].Message);
        Assert.Empty(overlay.Plan().Targets);
    }

    [Fact]
    public void An_override_file_larger_than_the_limit_is_an_unreadable_override_and_the_module_goes_on()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })));
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"overrides\":[\"big.ptch\"],\"Test/Obj/A\":{\"+tags\":[\"after\"]}}")));
        mod.LayerFiles.Add(("base", "big.ptch", new byte[5000]));

        var result = Applied(Overlay(game, mod, options: new GameDataOverlayOptions { MaxOverrideFileBytes = 1000 }), A);

        var unreadable = Assert.Single(result.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.OverrideUnreadable);
        Assert.Contains("more than the 1,000", unreadable.Message);
        Assert.Equal(new[] { "g1", "after" }, Tags(result.Bytes!));
    }

    [Fact]
    public void The_engines_limits_apply_to_every_application_and_an_application_over_them_is_a_skipped_target_not_an_exception()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })));
        var mod = new SyntheticMod().Layer("base", 0, Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}")));

        var starved = Applied(Overlay(game, mod, options: new GameDataOverlayOptions { Limits = new GameDataLimits { MaxWork = 3 } }), A);
        var small = Applied(Overlay(game, mod, options: new GameDataOverlayOptions { Limits = new GameDataLimits { MaxOutputBytes = 40 } }), A);

        Assert.False(starved.Applied);
        Assert.Contains("work limit of 3 units exceeded", Assert.Single(starved.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.TargetSkipped).Message);
        Assert.False(small.Applied);
        Assert.Contains("output limit of 40 bytes exceeded", Assert.Single(small.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.TargetSkipped).Message);
    }

    [Fact]
    public void The_applications_to_one_chunk_share_a_budget_of_work_and_the_ones_past_it_are_skipped_with_a_word()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })));
        var modules = Enumerable.Range(0, 60).Select(i => Target(A, $"{{\"Test/Obj/A\":{{\"+tags\":[\"m{i}\"]}}}}")).ToArray();
        var mod = new SyntheticMod().Layer("base", 0, Doc(modules));

        var plenty = Applied(Overlay(game, mod), A);
        var starved = Applied(Overlay(game, mod, options: new GameDataOverlayOptions { MaxWorkPerChunk = 150 }), A);

        Assert.Equal(60, plenty.Applications);
        Assert.Equal(61, Tags(plenty.Bytes!).Length);
        Assert.InRange(starved.Applications, 1, 59);
        var limit = Assert.Single(starved.Diagnostics, d => d.Kind == GameDataOverlayDiagnosticKind.LimitExceeded);
        Assert.Contains("more than the 150 units of work", limit.Message);
        Assert.Equal(starved.Applications + 1, Tags(starved.Bytes!).Length);   // the ones that ran landed, in order
    }

    [Fact]
    public void A_documents_fingerprint_follows_its_layers_priorities_activity_and_text()
    {
        var game = new SyntheticGame().Add("A.wad.client", A, Bin(Obj("Test/Obj/A", new[] { "g1" })));
        string doc = Doc(Target(A, "{\"Test/Obj/A\":{\"+tags\":[\"b\"]}}"));
        ulong Of(SyntheticMod mod, string[]? active = null) => Overlay(game, mod, active).DocumentsFingerprint;

        var one = new SyntheticMod().Layer("base", 0, doc).Layer("x", 1, doc);
        ulong baseline = Of(one);

        Assert.Equal(baseline, Of(new SyntheticMod().Layer("x", 1, doc).Layer("base", 0, doc)));   // the order given is not the identity
        Assert.NotEqual(baseline, Of(new SyntheticMod().Layer("base", 0, doc).Layer("x", 2, doc)));
        Assert.NotEqual(baseline, Of(new SyntheticMod().Layer("base", 0, doc).Layer("x", 1, doc.Replace("\"b\"", "\"c\""))));
        Assert.NotEqual(baseline, Of(one, new string[0]));
    }
}
