using System.Text.Json;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M730: a forced map bin is the Map Skin Switcher's RESULT, and the patch updater carried that result across
/// patches as a diff - faithful to the slots that existed, blind to the rule. Measured on the user's Map Forcer:
/// 36 slots forced on 16.15, Hall_Of_Legends added by 16.17 and left as Riot shipped it, the game loading Hall of
/// Legends. The recipe (target, source, carry flag, a snapshot of the source) is now recorded and REPLAYED on each
/// new original; only edits the recipe does not explain are merged.
/// </summary>
public sealed class MapSkinForceRecipeTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const uint CharacterListField = 0x2d3285eb;
    private static readonly uint MapHash = H("Maps/Shipping/Map11");
    private static uint SlotHash(string name) => H("Maps/Shipping/Map11/MapSkins/" + name);
    private static uint AudioHash(string name) => H("Audio/" + name);

    private static string? Names(uint hash) => hash switch
    {
        _ when hash == H("mMapContainerLink") => "mMapContainerLink",
        _ when hash == H("mMapObjectsCFG") => "mMapObjectsCFG",
        _ when hash == H("mWorldParticlesINI") => "mWorldParticlesINI",
        _ when hash == H("mGrassTintTexture") => "mGrassTintTexture",
        _ => null,
    };

    // ===================================================== synthetic bins

    private sealed record Slot(string Name, string? Container, string Cfg, string? Particles, string Grass,
        int Chars = 0, bool Audio = false);

    private static readonly Slot Default = new("Default", "Maps/MapGeometry/Map11/Base_SRX",
        "ASSETS/Maps/Deprecated/Map11/CFG/objectcfg_SRX.cfg", "ASSETS/Maps/Particles/Deprecated/Map11/Particles_SRX.ini",
        "ASSETS/Maps/Info/Map11/GrassTint_SRX.tex", Audio: true);
    private static readonly Slot Odyssey = new("Odyssey", "Maps/MapGeometry/Map11/Base_SRX",
        "ASSETS/Maps/Deprecated/Map11/CFG/ObjectCFG.cfg", "ASSETS/Maps/Particles/Deprecated/Map11/Particles.ini",
        "ASSETS/Maps/Info/Map11/GrassTint.tex", Chars: 3);
    private static readonly Slot Milkshake = new("Milkshake_SRS", "Maps/MapGeometry/Map11/Milkshake_SRS",
        "ASSETS/Maps/Deprecated/Map11/CFG/objectcfg_SRX.cfg", "ASSETS/Maps/Particles/Deprecated/Map11/Particles_SRX.ini",
        "ASSETS/Maps/Info/Map11/GrassTint_SRX_Milkshake_Env.tex", Chars: 24, Audio: true);
    // No particles field: the switcher must never ADD a route field a slot shipped without (StartSpawn crash).
    private static readonly Slot Arcade = new("Arcade", "Maps/MapGeometry/Map11/Arcade",
        "ASSETS/Maps/Deprecated/Map11/CFG/ObjectCFG_Arcade.cfg", null, "ASSETS/Maps/Info/Map11/GrassTint.tex", Audio: true);
    private static readonly Slot HallOfLegends = new("Hall_Of_Legends", "Maps/MapGeometry/Map11/Hall_Of_Legends",
        "ASSETS/Maps/Deprecated/Map11/CFG/objectcfg_SRX.cfg", "ASSETS/Maps/Particles/Deprecated/Map11/Particles_SRX.ini",
        "ASSETS/Maps/Info/Map11/GrassTint_SRX.tex");

    private static byte[] Build(params Slot[] slots)
    {
        var objects = new List<BinTreeObject>
        {
            new(MapHash, H("Map"), new BinTreeProperty[]
            {
                new BinTreeString(H("mapStringId"), "SR"),
                new BinTreeUnorderedContainer(H("mapSkins"), BinPropertyType.ObjectLink,
                    slots.Select(s => new BinTreeObjectLink(0, SlotHash(s.Name)))),
            }),
        };
        foreach (var s in slots)
        {
            var props = new List<BinTreeProperty> { new BinTreeString(H("name"), s.Name) };
            if (s.Container is not null) props.Add(new BinTreeString(H("mMapContainerLink"), s.Container));
            props.Add(new BinTreeString(H("mMapObjectsCFG"), s.Cfg));
            if (s.Particles is not null) props.Add(new BinTreeString(H("mWorldParticlesINI"), s.Particles));
            props.Add(new BinTreeString(H("mGrassTintTexture"), s.Grass));
            props.Add(new BinTreeString(H("mNavigationMesh"), $"ASSETS/Maps/NavGrid/Map11/{s.Name}.aimesh_ngrid"));
            if (s.Chars > 0)
                props.Add(new BinTreeUnorderedContainer(CharacterListField, BinPropertyType.Embedded,
                    Enumerable.Range(0, s.Chars).Select(i => new BinTreeEmbedded(0, H("MapCharacterSkin"), new BinTreeProperty[]
                    {
                        new BinTreeHash(H("Character"), H($"Characters/Unit{i}")),
                        new BinTreeU32(H("SkinID"), (uint)(i + 1)),
                    }))));
            objects.Add(new BinTreeObject(SlotHash(s.Name), H("MapSkin"), props));
            if (s.Audio)
                objects.Add(new BinTreeObject(AudioHash(s.Name), H("FeatureAudioDataProperties"), new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(H("bankUnits"), BinPropertyType.String, new BinTreeProperty[]
                    {
                        new BinTreeString(0, $"ASSETS/Sounds/Wwise2016/SFX/Shared/{s.Name}_events.bnk"),
                    }),
                    new BinTreeHash(H("feature"), H(s.Name)),
                }));
        }
        using var ms = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static byte[] Base() => Build(Default, Odyssey, Milkshake, Arcade);

    private static BinTree Parse(byte[] bytes) => SafeBinTree.Parse(bytes);

    private static string Str(BinTree tree, uint objectHash, string field) =>
        Assert.IsType<BinTreeString>(tree.Objects[objectHash].Properties[H(field)]).Value;

    private static void AssertSameBin(byte[] expected, byte[] actual, string because)
    {
        var a = Parse(expected);
        var b = Parse(actual);
        Assert.True(a.Objects.Count == b.Objects.Count, $"{because}: {a.Objects.Count} vs {b.Objects.Count} objects");
        foreach (var (key, objectA) in a.Objects)
            Assert.True(b.Objects.TryGetValue(key, out var objectB) && BinPropEquality.ObjectsEqual(objectA, objectB),
                $"{because}: object 0x{key:x8} differs");
    }

    private static MapSkinForceRecipe Recorded(bool carry = true) =>
        MapSkinForceRecipe.Record(Base(), 11, SlotHash("Default"), SlotHash("Milkshake_SRS"), carry, Names);

    // ===================================================== record and replay

    [Fact]
    public void RecordThenReplayOnTheSameOriginalIsTheSwitchItself()
    {
        var swap = MapSkinSwitcher.Switch(Base(), 11, SlotHash("Default"), SlotHash("Milkshake_SRS"), Names, carryCharacterSkins: true);
        var recipe = Recorded();

        var replay = recipe.Replay(Base(), Names);

        AssertSameBin(swap.Bytes, replay.Bytes, "the replay of a freshly recorded recipe");
        Assert.False(replay.UsedSnapshot);
        Assert.Equal("Default <- Milkshake_SRS (Maps/MapGeometry/Map11/Milkshake_SRS), character skins carried", recipe.Describe());
        Assert.NotNull(recipe.Snapshot);
        Assert.NotNull(recipe.Snapshot!.Audio);   // Milkshake has a profile of its own, so the snapshot keeps it
    }

    [Fact]
    public void ReplayOnAPatchWithANewSlotForcesTheNewSlotToo()
    {
        // 16.17 added Hall_Of_Legends. The diff carried across the 36 slots it knew and the game loaded the 37th.
        var recipe = Recorded();
        var newBase = Build(Default, Odyssey, Milkshake, Arcade, HallOfLegends);

        var replay = recipe.Replay(newBase, Names);
        var tree = Parse(replay.Bytes);

        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS", Str(tree, SlotHash("Hall_Of_Legends"), "mMapContainerLink"));
        Assert.Equal("ASSETS/Maps/Info/Map11/GrassTint_SRX_Milkshake_Env.tex", Str(tree, SlotHash("Hall_Of_Legends"), "mGrassTintTexture"));
        var carried = Assert.IsType<BinTreeUnorderedContainer>(tree.Objects[SlotHash("Hall_Of_Legends")].Properties[CharacterListField]);
        Assert.Equal(24, carried.Elements.Count);
        // the slot that shipped without a particles field still has none - the switcher adds no route field
        Assert.False(tree.Objects[SlotHash("Arcade")].Properties.ContainsKey(H("mWorldParticlesINI")));
        Assert.Equal("ASSETS/Maps/NavGrid/Map11/Hall_Of_Legends.aimesh_ngrid", Str(tree, SlotHash("Hall_Of_Legends"), "mNavigationMesh"));
        Assert.Contains(SlotHash("Hall_Of_Legends"), replay.Swap.RoutedSkinHashes);
    }

    [Fact]
    public void RiotsChangesToTheSourceSlotFlowThroughAReplayAndAreNamed()
    {
        var recipe = Recorded();
        var moved = Milkshake with { Grass = "ASSETS/Maps/Info/Map11/GrassTint_SRX_Milkshake_v2.tex" };
        var newBase = Build(Default, Odyssey, moved, Arcade);

        var replay = recipe.Replay(newBase, Names);

        Assert.Equal("ASSETS/Maps/Info/Map11/GrassTint_SRX_Milkshake_v2.tex", Str(Parse(replay.Bytes), SlotHash("Odyssey"), "mGrassTintTexture"));
        Assert.Contains(replay.Notes, n => n.Contains("Riot changed Milkshake_SRS's mGrassTintTexture"));
    }

    [Fact]
    public void AVaultedSourceSlotIsReplayedFromTheSnapshotAndFlagged()
    {
        // Riot vaults seasonal slots. The mod ships the assets; the recipe must still force them.
        var recipe = Recorded();
        var vaulted = Build(Default, Odyssey, Arcade, HallOfLegends);

        var replay = recipe.Replay(vaulted, Names);
        var tree = Parse(replay.Bytes);

        Assert.True(replay.UsedSnapshot);
        Assert.Contains(replay.Notes, n => n.Contains("not in this patch") && n.Contains("recorded copy"));
        foreach (string name in new[] { "Default", "Odyssey", "Arcade", "Hall_Of_Legends" })
            Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS", Str(tree, SlotHash(name), "mMapContainerLink"));
        Assert.Equal(Parse(vaulted).Objects.Count, tree.Objects.Count);   // the vaulted slot is not smuggled back in
        // and the audio profile of the base slot still takes the recorded source profile
        var defaultAudio = Assert.IsType<BinTreeUnorderedContainer>(tree.Objects[AudioHash("Default")].Properties[H("bankUnits")]);
        Assert.Contains("Milkshake_SRS_events.bnk", Assert.IsType<BinTreeString>(defaultAudio.Elements[0]).Value);
    }

    [Fact]
    public void AMissingBaseSlotFallsBackToDefaultAndSaysSo()
    {
        var withHall = Build(Default, Odyssey, Milkshake, Arcade, HallOfLegends);
        var recipe = MapSkinForceRecipe.Record(withHall, 11, SlotHash("Hall_Of_Legends"), SlotHash("Milkshake_SRS"), true, Names);

        var replay = recipe.Replay(Base(), Names);   // 16.18 dropped Hall_Of_Legends again

        Assert.Equal("Default", replay.TargetName);
        Assert.Contains(replay.Notes, n => n.Contains("base slot Hall_Of_Legends is not in this patch"));
        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS", Str(Parse(replay.Bytes), SlotHash("Odyssey"), "mMapContainerLink"));
    }

    [Fact]
    public void ARecipeWithoutABaseSlotRoutesEverySlotAndTouchesNoAudioProfile()
    {
        var recipe = new MapSkinForceRecipe(11, null, "Milkshake_SRS", "Maps/MapGeometry/Map11/Milkshake_SRS", false, null);

        var replay = recipe.Replay(Base(), Names);
        var tree = Parse(replay.Bytes);

        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS", Str(tree, SlotHash("Odyssey"), "mMapContainerLink"));
        Assert.Equal(0, replay.Swap.ChangedAudioProperties);
        Assert.True(BinPropEquality.ObjectsEqual(Parse(Base()).Objects[AudioHash("Default")], tree.Objects[AudioHash("Default")]));
        Assert.False(tree.Objects[SlotHash("Odyssey")].Properties.ContainsKey(CharacterListField) && ((BinTreeContainer)tree.Objects[SlotHash("Odyssey")].Properties[CharacterListField]).Elements.Count == 24,
            "no carry was asked for");
    }

    // ===================================================== infer

    [Fact]
    public void InferReadsTheSwitchBackOutOfAForcedBin()
    {
        var mod = MapSkinSwitcher.Switch(Base(), 11, SlotHash("Default"), SlotHash("Milkshake_SRS"), Names, carryCharacterSkins: true).Bytes;

        var recipe = MapSkinForceRecipe.Infer(mod, Base(), Names);

        Assert.NotNull(recipe);
        Assert.Equal("Milkshake_SRS", recipe!.SourceName);
        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS", recipe.SourceContainerLink);
        Assert.True(recipe.CarryCharacterSkins);
        Assert.Equal("Default", recipe.TargetName);   // its audio profile was the one routed
        Assert.Equal(11, recipe.MapId);
        AssertSameBin(mod, recipe.Replay(Base(), Names).Bytes, "the inferred recipe replayed on its own original");
    }

    [Fact]
    public void InferRefusesABinThatIsNotASwitch()
    {
        Assert.Null(MapSkinForceRecipe.Infer(Base(), Base(), Names));   // nothing routed

        // one slot pointed at another's container by hand: no untouched slot explains every slot
        var tree = Parse(Base());
        tree.Objects[SlotHash("Odyssey")].Properties[H("mMapContainerLink")] = new BinTreeString(H("mMapContainerLink"), "Maps/MapGeometry/Map11/Arcade");
        using var ms = new MemoryStream();
        tree.Write(ms);
        Assert.Null(MapSkinForceRecipe.Infer(ms.ToArray(), Base(), Names));

        Assert.Null(MapSkinForceRecipe.Infer(new byte[] { 1, 2, 3 }, Base(), Names));   // not a bin at all
    }

    // ===================================================== rebase

    [Fact]
    public void RebaseReAppliesWithoutMergingWhenTheBinIsExactlyTheRecipe()
    {
        var recipe = Recorded();
        var mod = MapSkinSwitcher.Switch(Base(), 11, SlotHash("Default"), SlotHash("Milkshake_SRS"), Names, carryCharacterSkins: true).Bytes;
        var newBase = Build(Default, Odyssey, Milkshake, Arcade, HallOfLegends);

        var result = BinRecipeRebase.Rebase(recipe, Base(), mod, newBase, Names);

        Assert.False(result.MergedRemainder);
        Assert.Equal(0, result.Conflicts);
        Assert.Contains(result.Lines, l => l.Contains("1 slot(s) new in this patch forced as well: Hall_Of_Legends"));
        AssertSameBin(recipe.Replay(newBase, Names).Bytes, result.Bytes, "a recipe-only bin rebases to the recipe replayed on the new original");
    }

    [Fact]
    public void RebaseMergesOnlyWhatTheRecipeDoesNotExplain()
    {
        var recipe = Recorded();
        var forced = Parse(MapSkinSwitcher.Switch(Base(), 11, SlotHash("Default"), SlotHash("Milkshake_SRS"), Names, carryCharacterSkins: true).Bytes);
        // a hand edit on top of the switch: a runtime field the recipe never touches
        forced.Objects[SlotHash("Odyssey")].Properties[H("mNavigationMesh")] = new BinTreeString(H("mNavigationMesh"), "ASSETS/Maps/NavGrid/Map11/Custom.aimesh_ngrid");
        using var ms = new MemoryStream();
        forced.Write(ms);
        var newBase = Build(Default, Odyssey, Milkshake, Arcade, HallOfLegends);

        var result = BinRecipeRebase.Rebase(recipe, Base(), ms.ToArray(), newBase, Names);
        var tree = Parse(result.Bytes);

        Assert.True(result.MergedRemainder);
        Assert.Equal(0, result.Conflicts);   // the patch did not touch the nav mesh
        Assert.Equal("ASSETS/Maps/NavGrid/Map11/Custom.aimesh_ngrid", Str(tree, SlotHash("Odyssey"), "mNavigationMesh"));
        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS", Str(tree, SlotHash("Hall_Of_Legends"), "mMapContainerLink"));
        Assert.Contains(result.Lines, l => l.Contains("0 added / 0 removed / 1 modified object(s)"));
    }

    // ===================================================== the project record

    [Fact]
    public void TheRecordRoundTripsThroughProjectJson()
    {
        var recipe = Recorded();
        var project = new ReyProject { Name = "Forcer" };
        BinRecipeRecord.Upsert(project.BinRecipes, recipe.ToRecord("data/maps/shipping/map11/map11.bin", "16.17", "switcher"));
        BinRecipeRecord.Upsert(project.BinRecipes, recipe.ToRecord("DATA/Maps/Shipping/Map11/Map11.bin", "16.18", "switcher"));   // same path, replaces

        var json = JsonSerializer.Serialize(project);
        var back = JsonSerializer.Deserialize<ReyProject>(json)!;

        var record = Assert.Single(back.BinRecipes);
        Assert.Equal("16.18", record.RecordedOnPatch);
        Assert.NotNull(BinRecipeRecord.Find(back.BinRecipes, "data/maps/shipping/map11/map11.bin"));
        var restored = MapSkinForceRecipe.FromRecord(record);
        Assert.Equal(recipe with { Snapshot = null }, restored with { Snapshot = null });
        AssertSameBin(MapSkinForceRecipe.SnapshotBytes(recipe.Snapshot!), MapSkinForceRecipe.SnapshotBytes(restored.Snapshot!), "the snapshot");
        AssertSameBin(recipe.Replay(Build(Default, Odyssey, Arcade), Names).Bytes,
            restored.Replay(Build(Default, Odyssey, Arcade), Names).Bytes, "a replay from the restored snapshot");

        Assert.Empty(JsonSerializer.Deserialize<ReyProject>("{\"Name\":\"Legacy\",\"ProjectVersion\":1}")!.BinRecipes);
    }

    // ===================================================== the updater

    [Fact]
    public async Task TheUpdaterReDoesARecipeBinAndRemembersTheOneItInferred()
    {
        const ulong hash = 1;
        const string rel = "data/maps/shipping/map11/map11.bin";
        var old = Base();
        var installed = Build(Default, Odyssey, Milkshake, Arcade, HallOfLegends);
        var project = new Dictionary<ulong, byte[]>
        {
            [hash] = MapSkinSwitcher.Switch(old, 11, SlotHash("Default"), SlotHash("Milkshake_SRS"), Names, carryCharacterSkins: true).Bytes,
        };
        var kept = new List<BinRecipeRecord>();
        var vm = new PatchUpdateWindowViewModel
        {
            SelectedPatch = "16.15",
            TargetPatch = "16.17",
            ListPatches = () => Task.FromResult<IReadOnlyList<string>>(["16.17", "16.15"]),
            DownloadOld = (_, _) => Task.FromResult<byte[]?>(old),
            ReadCurrentOriginal = _ => installed,
            ReadProjectBytes = h => project[h],
            SaveBytes = (entry, bytes) => { project[entry.PathHash] = bytes; return Task.FromResult(true); },
            Backup = (row, _) => row.ProjectRel,
            ValidateAfter = false,
            Resolve = Names,
            RecipeFor = r => BinRecipeRecord.Find(kept, r),
            RecipeInferred = (r, record) => BinRecipeRecord.Upsert(kept, record),
        };
        vm.Bins.Add(new PatchUpdateBinRowViewModel { Rel = rel, ProjectRel = "Map11/" + rel,
            Entry = new WadAssetEntry { PathHash = hash, Path = rel, IsResolved = true } });

        await vm.InitAsync();
        var result = await vm.RunUpdateAsync();

        Assert.NotNull(result);
        Assert.True(result!.Success, result.Summary);
        Assert.Equal(0, result.Conflicts);
        Assert.StartsWith("re-applied", vm.Bins[0].Status);
        Assert.Contains("Recipe read out of the file and remembered", vm.Bins[0].Detail);
        Assert.Contains("Hall_Of_Legends", vm.Bins[0].Detail);
        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS", Str(Parse(project[hash]), SlotHash("Hall_Of_Legends"), "mMapContainerLink"));
        var record = Assert.Single(kept);
        Assert.Equal("inferred", record.Origin);
        Assert.Equal("Milkshake_SRS", record.SourceSkin);
        Assert.True(record.CarryCharacterSkins);
        Assert.Equal("16.15", record.RecordedOnPatch);
    }

    // ===================================================== the real projects, where they exist

    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    // System.Environment, qualified: inside this namespace a bare Environment is ReyEngine.Formats.Environment.
    private static readonly string CdragonCache = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "ReyEngine", "cdragon");
    private const string MapForcerBackup =
        @"D:\ReyEngine\Map Forcer\.reyengine\backups\patch-update-16.15-to-16.17-20260828-225614\Map11\data\maps\shipping\map11\map11.bin";
    private const string WinterRift = @"D:\ReyEngine\Winter Rift 2025\Map11\data\maps\shipping\map11\map11.bin";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static string? Resolve(uint hash) => Database.Value is { } db && db.TryGetBinName(hash, out var n) ? n : null;

    [Fact]
    public void TheMapForcerIsExactlyDefaultForcedEverywhereAndTheNextPatchWouldHaveForcedHallOfLegends()
    {
        // The bin the user shipped on 16.15 (kept by the 16.17 update's backup) against the 16.15 original the
        // updater cached, then replayed on the 16.17 original that added Hall_Of_Legends.
        string old = Path.Combine(CdragonCache, "16.15", "4081e5c462798b73.bin");
        string next = Path.Combine(CdragonCache, "16.17", "4081e5c462798b73.bin");
        if (!File.Exists(MapForcerBackup) || !File.Exists(old) || !File.Exists(next)) return;

        byte[] mod = File.ReadAllBytes(MapForcerBackup), oldBase = File.ReadAllBytes(old), newBase = File.ReadAllBytes(next);
        var recipe = MapSkinForceRecipe.Infer(mod, oldBase, Resolve);

        Assert.NotNull(recipe);
        Assert.Equal("Default", recipe!.SourceName);
        Assert.Equal("Maps/MapGeometry/Map11/Base_SRX", recipe.SourceContainerLink);
        Assert.False(recipe.CarryCharacterSkins);
        Assert.Null(recipe.TargetName);   // no audio profile was routed
        AssertSameBin(mod, recipe.Replay(oldBase, Resolve).Bytes, "Map Forcer 16.15 is the recipe replayed on its original");

        var result = BinRecipeRebase.Rebase(recipe, oldBase, mod, newBase, Resolve);
        Assert.False(result.MergedRemainder);
        Assert.Equal(0, result.Conflicts);
        Assert.Contains(result.Lines, l => l.Contains("new in this patch forced as well") && l.Contains("Hall_Of_Legends"));
        Assert.Equal("Maps/MapGeometry/Map11/Base_SRX", Str(Parse(result.Bytes), SlotHash("Hall_Of_Legends"), "mMapContainerLink"));
    }

    [Fact]
    public void TheWinterRiftBinIsMilkshakeWithItsCharacterSkinsCarried()
    {
        if (!File.Exists(WinterRift) || !File.Exists(Champions)) return;
        byte[] installedBin;
        using (var wad = WadArchive.Open(Champions))
        {
            ulong hash = HashAlgorithms.WadPath("data/maps/shipping/map11/map11.bin");
            if (!wad.TryGetEntry(hash, out _)) return;
            installedBin = wad.Extract(hash);
        }
        byte[] mod = File.ReadAllBytes(WinterRift);

        var recipe = MapSkinForceRecipe.Infer(mod, installedBin, Resolve);

        Assert.NotNull(recipe);
        Assert.Equal("Milkshake_SRS", recipe!.SourceName);
        Assert.True(recipe.CarryCharacterSkins);
        var replay = recipe.Replay(installedBin, Resolve);
        Assert.Equal(24, replay.Swap.CarriedCharacterSkins.Count);
        Assert.True(replay.Swap.RoutedSkinHashes.Count >= 35, $"routed {replay.Swap.RoutedSkinHashes.Count}");

        // As first met, the bin carried one stale unregistered alias: 16.18 removed Hall_Of_Legends and the
        // 16.17->16.18 merge restored the mod's copy ("edited by mod but removed by the patch - mod version
        // restored"), which a rebase names as the remainder. The switcher has since been run on the bin again
        // (2026-09-14), which made it exactly the recipe replayed, so there is nothing left to merge. Either
        // state of the file is the same rebase read honestly, and the test holds for both.
        var result = BinRecipeRebase.Rebase(recipe, installedBin, mod, installedBin, Resolve);
        Assert.Contains(result.Lines, l => l.StartsWith("Re-applied on the installed patch: ")
                                           && l.Contains("Milkshake_SRS") && l.Contains("24 character skin(s) carried"));
        if (result.MergedRemainder)
            Assert.Contains(result.Lines, l => l.Contains("1 added / 0 removed / 0 modified"));
        else
            AssertSameBin(replay.Bytes, result.Bytes, "a bin that is exactly the recipe rebases to the recipe replayed on the installed original");
    }

    // ===================================================== wiring

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) continue;
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        return null;
    }

    [Fact]
    public void TheSwitcherRecordsAndTheUpdaterReplays()
    {
        string? main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        string? updater = Source("src", "ReyEngine.App", "ViewModels", "PatchUpdateWindowViewModel.cs");
        string? switcher = Source("src", "ReyEngine.Formats", "Meta", "MapSkinSwitcher.cs");
        if (main is null || updater is null || switcher is null) return;

        // the switcher records from the ORIGINAL bytes, before the project is saved
        int record = main.IndexOf("BinRecipeRecord.Upsert(Project.BinRecipes, recipe.ToRecord(shippingEntry.Path", StringComparison.Ordinal);
        int save = main.IndexOf("ReyProjectService.Save(Project, Project.ProjectFilePath!);", record, StringComparison.Ordinal);
        Assert.True(record > 0 && save > record, "the map skin switch must record its recipe before the project is saved");
        Assert.Contains("MapSkinForceRecipe.Record(original,", main);
        // the updater is handed the project's recipes and keeps the ones it infers
        Assert.Contains("RecipeFor = rel => BinRecipeRecord.Find(patchProject.BinRecipes, rel)", main);
        Assert.Contains("BinRecipeRecord.Upsert(patchProject.BinRecipes, record);", main);
        Assert.Contains("BinRecipeRebase.Rebase(recipe.Recipe, old, mod, newBase, Resolve)", updater);
        // and the live switch is byte-for-byte the switch it always was: one core, the public entry delegates
        Assert.Contains("=> SwitchCore(shippingBin, mapId, targetSkinHash, sourceSkinHash, null, resolve, carryCharacterSkins, routeAudio);", switcher);
    }
}
