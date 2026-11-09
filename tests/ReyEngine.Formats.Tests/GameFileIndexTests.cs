using System.Diagnostics;
using System.Text;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.Scripts;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>M891: the game-wide file index (holder rule, lazy build, no write to the game folder), the .preload and .luabin64 readers, against hand-written archives and, skipped when the install is missing, the real game.</summary>
public sealed class GameFileIndexTests(ITestOutputHelper output) : IDisposable
{
    private const string RealGame = @"C:\Riot Games\League of Legends\Game";
    private readonly TempFolder _temp = new();
    public void Dispose() => _temp.Dispose();

    private string Game => _temp.Combine("game");
    private string Archive(string name) => Path.Combine(Game, "DATA", "FINAL", name.Replace('/', Path.DirectorySeparatorChar));
    private GameFileIndex Index() => new(Game, _temp.Combine("cache.idx"));
    private static ulong H(string p) => HashAlgorithms.WadPath(p);
    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);
    private static string S(byte[]? b) => Encoding.ASCII.GetString(b!);

    private bool SkipReal()
    {
        if (Directory.Exists(Path.Combine(RealGame, "DATA", "FINAL"))) return false;
        output.WriteLine("SKIPPED: League of Legends is not installed at " + RealGame);
        return true;
    }

    // ================================================================ the holder rule

    [Fact]
    public void A_file_is_read_without_naming_its_archive()
    {
        TestWad.Write(Archive("Champions/Kalista.wad.client"), new[] { TestChunk.Compressed(H("data/a.txt"), B("from kalista")) });
        TestWad.Write(Archive("Scripts.wad.client"), new[] { TestChunk.Stored_(H("data/s.txt"), B("from scripts")) });
        var index = Index();
        Assert.True(index.TryRead("data/a.txt", out var a));
        Assert.Equal("from kalista", S(a));
        Assert.True(index.TryRead("DATA/S.TXT", out var s));   // case does not matter
        Assert.Equal("from scripts", S(s));
        Assert.False(index.TryRead("data/none.txt", out _));
        Assert.True(index.Contains("data/a.txt"));
    }

    [Fact]
    public void With_several_holders_the_first_in_archive_order_wins()
    {
        // byte order: Champions/Kalista.wad.client < Scripts.wad.client < Zeta.wad.client
        foreach (var (name, text) in new[] { ("Zeta.wad.client", "zeta"), ("Scripts.wad.client", "scripts"), ("Champions/Kalista.wad.client", "kalista") })
            TestWad.Write(Archive(name), new[] { TestChunk.Stored_(H("data/shared.txt"), B(text)) });
        var index = Index();
        Assert.True(index.TryRead("data/shared.txt", out var bytes));
        Assert.Equal("kalista", S(bytes));
        Assert.Equal("Champions/Kalista.wad.client", index.HolderOf(H("data/shared.txt"))!.Name);
    }

    [Fact]
    public void A_preferred_archive_that_holds_the_file_beats_the_first_holder_and_one_that_does_not_changes_nothing()
    {
        foreach (var (name, text) in new[] { ("Champions/Kalista.wad.client", "kalista"), ("Map11.wad.client", "map11"), ("Scripts.wad.client", "scripts") })
            TestWad.Write(Archive(name), new[] { TestChunk.Stored_(H("data/shared.txt"), B(text)) });
        TestWad.Write(Archive("Other.wad.client"), new[] { TestChunk.Stored_(H("data/other.txt"), B("other")) });
        var index = Index();
        string Read(params string[] preferred) { Assert.True(index.TryRead("data/shared.txt", out var b, preferred)); return S(b); }
        Assert.Equal("map11", Read("Map11.wad.client"));
        Assert.Equal("map11", Read("map11"));                                      // no suffix, any case
        Assert.Equal("scripts", Read("Champions/Nobody.wad.client", "Scripts"));   // the first of the list that holds it
        Assert.Equal("map11", Read(@"C:\x\DATA\FINAL\Map11.wad.client"));          // a full path reduces to its file name
        Assert.Equal("kalista", Read("Other.wad.client"));                         // holds none of it: the LTK rule
        Assert.Equal("kalista", Read());
        Assert.True(index.TryRead("data/other.txt", out var other, new[] { "Map11.wad.client" }));   // a preference never hides a file it cannot serve
        Assert.Equal("other", S(other));
    }

    [Fact]
    public void The_table_is_built_once_lazily_and_the_game_folder_is_not_written()
    {
        TestWad.Write(Archive("A.wad.client"), new[] { TestChunk.Stored_(H("data/a.txt"), B("a")) });
        var before = Snapshot(Game);
        var index = Index();
        Assert.Null(index.Build);
        var one = index.EnsureBuilt();
        Assert.Same(one, index.EnsureBuilt());
        Parallel.For(0, 16, n => Assert.True(index.TryRead("data/a.txt", out _)));
        Assert.Equal(before, Snapshot(Game));
        Assert.True(File.Exists(_temp.Combine("cache.idx")));
    }

    [Fact]
    public void A_patched_archive_rebuilds_the_index_once_and_the_read_succeeds()
    {
        TestWad.Write(Archive("A.wad.client"), new[] { TestChunk.Stored_(H("data/a.txt"), B("old")) });
        var index = Index();
        Assert.True(index.TryRead("data/a.txt", out var old));
        Assert.Equal("old", S(old));
        TestWad.Write(Archive("A.wad.client"), new[] { TestChunk.Stored_(H("data/a.txt"), B("newer bytes")), TestChunk.Stored_(H("data/b.txt"), B("b")) });
        File.SetLastWriteTimeUtc(Archive("A.wad.client"), DateTime.UtcNow.AddMinutes(5));
        Assert.True(index.TryRead("data/a.txt", out var fresh));
        Assert.Equal("newer bytes", S(fresh));
        Assert.True(index.TryRead("data/b.txt", out _));
    }

    private static string Snapshot(string dir) => string.Join("|", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
        .Select(f => $"{f}:{new FileInfo(f).Length}:{File.GetLastWriteTimeUtc(f).Ticks}"));

    // ================================================================ .preload

    [Fact]
    public void Preload_text_gives_typed_entries()
    {
        var p = PreloadFile.Parse(B("PreLoadBuildingBlocks = {\n{Function=BBPreloadParticle, Params={Name=\"doesnotexist.troy\", SkinID=\"-1\"}}, \n{Function=BBPreloadCharacter, Params={Name=\"kalistaspawn\"}}, \n"
            + "{Function=BBPreloadSpell, Params={Name=\"kalistawspawnmarker\"}}, \n{Function=BBPreloadModule, Params={Name=\"Global.ContribScores\"}}, \n{Function=BBPreloadAssetsLua, Params={Name=\"x\"}}, \n{Function=BBFuture, Params={Name=\"y\"}}, \n}\n"));
        Assert.Empty(p.Notes);
        Assert.Equal(new[] { PreloadKind.Particle, PreloadKind.Character, PreloadKind.Spell, PreloadKind.Module, PreloadKind.AssetsLua, PreloadKind.Unknown }, p.Entries.Select(e => e.Kind));
        Assert.Equal("-1", p.Entries[0].SkinId);
        Assert.Null(p.Entries[1].SkinId);
        Assert.Equal(new[] { "kalistaspawn" }, p.Names(PreloadKind.Character));
    }

    [Fact]
    public void A_commented_out_entry_is_not_an_entry()
    {
        var p = PreloadFile.Parse(B("PreLoadBuildingBlocks = {\n{Function=BBPreloadSpell, Params={Name=\"a\"}}, \n--{Function=BBPreloadModule, Params={Name=\"Global.X\"}}, \n}\n"));
        Assert.Empty(p.Notes);
        Assert.Equal("a", Assert.Single(p.Entries).Name);
    }

    [Fact]
    public void A_malformed_preload_keeps_what_was_read_and_says_so()
    {
        var p = PreloadFile.Parse(B("PreLoadBuildingBlocks = {\n{Function=BBPreloadSpell, Params={Name=\"a\"}},\n{Function=BBPreloadSpell, Params={Name=\"b"));
        Assert.Single(p.Entries);
        Assert.NotEmpty(p.Notes);
        Assert.NotEmpty(PreloadFile.Parse(B("nothing")).Notes);
        Assert.Empty(PreloadFile.Parse(B("PreLoadBuildingBlocks = {\n}\n")).Entries);
    }

    // ================================================================ .luabin64

    [Fact]
    public void Luabin64_refuses_other_headers_and_truncation()
    {
        Assert.Throws<LuaBin64Exception>(() => LuaBin64File.Parse(B("not lua at all, not lua")));
        var real = RealLua("data/characters/aatrox/spells/aatroxw.luabin64");
        if (real is null) { output.WriteLine("SKIPPED: no install; header/truncation checks need a real file"); return; }
        var other = (byte[])real.Clone();
        other[7] = 8;   // int size 8
        Assert.Throws<LuaBin64Exception>(() => LuaBin64File.Parse(other));
        for (int cut = 0; cut < real.Length; cut += 7) Assert.Throws<LuaBin64Exception>(() => LuaBin64File.Parse(real.AsSpan(0, cut)));
    }

    // ================================================================ the real game

    private static readonly Lazy<GameFileIndex?> Real = new(() =>
        Directory.Exists(Path.Combine(RealGame, "DATA", "FINAL")) ? GameFileIndex.Shared(RealGame) : null);

    private static byte[]? RealLua(string path) => Real.Value is { } i && i.TryRead(path, out var b) ? b : null;

    [Fact]
    public void Aatrox_W_luabin_keys_come_from_the_instructions()
    {
        if (SkipReal()) return;
        Assert.True(Real.Value!.TryRead("data/characters/aatrox/spells/aatroxw.luabin64", out var bytes));
        var lua = LuaBin64File.Parse(bytes!);
        Assert.Empty(lua.Notes);
        Assert.Equal("AatroxW", lua.GetString("BuffName"));
        Assert.Equal("Aatrox_W1.dds", lua.GetString("BuffTextureName"));
        Assert.Equal("r:Global_Slow", lua.GetString("AutoBuffActivateEffect"));
        Assert.Equal(new[] { "AatroxSkin02" }, lua.GetStrings("SpellFXOverrideSkins"));
        Assert.Equal(true, lua.GetBool("IsDamagingSpell"));
        Assert.Equal(false, lua.GetBool("NotSingleTargetSpell"));
        Assert.Equal(new[] { "NotSingleTargetSpell", "DoesntBreakShields", "BuffTextureName", "BuffName", "AutoBuffActivateEffect", "IsDeathRecapSource", "PopupMessage1", "SpellFXOverrideSkins", "IsDamagingSpell" },
            lua.Globals.Select(g => g.Key));
        Assert.Equal(0, lua.TrailingBytes);
    }

    [Fact]
    public void Champion_spell_luabins_parse_and_what_the_walk_cannot_model_is_noted()
    {
        if (SkipReal()) return;
        int files = 0, notes = 0, chains = 0;
        foreach (var champ in new[] { "aatrox", "kalista", "ahri", "yasuo", "lux", "jinx", "talon", "sion" })
        {
            foreach (var spell in new[] { "q", "w", "e", "r", "passive", "basicattack", "q2", "chains" })
            {
                if (!Real.Value!.TryRead($"data/characters/{champ}/spells/{champ}{spell}.luabin64", out var b)) continue;
                var lua = LuaBin64File.Parse(b!);
                files++; notes += lua.Notes.Count;
                if (lua.TryGet("ChainMissileParameters", out var c) && c is LuaTable) chains++;
            }
        }
        output.WriteLine($"{files} luabin64 parsed, {notes} notes, {chains} with a ChainMissileParameters table");
        Assert.True(files > 5);
    }

    [Fact]
    public void Kalista_W_preload_names_kalistaspawn_and_it_is_read_from_Scripts_without_naming_the_wad()
    {
        if (SkipReal()) return;
        var index = Real.Value!;
        Assert.True(index.TryRead("data/characters/kalista/spells/kalistaw.preload", out var bytes));
        var pre = PreloadFile.Parse(bytes!);
        Assert.Empty(pre.Notes);
        Assert.Contains("kalistaspawn", pre.Names(PreloadKind.Character));
        Assert.Contains("kalistawspawninworld", pre.Names(PreloadKind.Spell));
        Assert.Equal("Scripts.wad.client", index.HolderOf(H("data/characters/kalista/spells/kalistaw.preload"))!.FileName);
    }

    [Fact]
    public void A_Kalista_texture_is_read_from_the_champion_archive_without_naming_it()
    {
        if (SkipReal()) return;
        var index = Real.Value!;
        string tex = "assets/characters/kalista/skins/base/kalista_base_tx_cm.tex";
        Assert.True(index.TryRead(tex, out var bytes), "texture not found");
        Assert.True(bytes!.Length > 100);
        Assert.Equal((byte)'T', bytes[0]);   // "TEX" then a zero byte
        Assert.Equal("Kalista.wad.client", index.HolderOf(H(tex))!.FileName);
        // the summoned unit's own skin lives in the same champion archive: no mount of it was needed
        Assert.True(index.Contains("data/characters/kalistaspawn/skins/skin0.bin"));
    }

    [Fact]
    public void Index_build_time_and_memory_on_the_real_install()
    {
        if (SkipReal()) return;
        var cold = new GameFileIndex(RealGame, _temp.Combine("cold.idx")).EnsureBuilt();   // no cache file: every table of contents is read
        output.WriteLine($"cold build: {cold.Elapsed.TotalMilliseconds:0} ms, {cold.Chunks} chunks, table {cold.TableBytes / 1048576.0:0.0} MiB, managed delta {cold.ManagedDelta / 1048576.0:0.0} MiB");
        var fresh = new GameFileIndex(RealGame);
        var sw = Stopwatch.StartNew();
        var b = fresh.EnsureBuilt();
        output.WriteLine($"index: {b.Archives} archives, {b.Chunks} chunks, skipped {b.SkippedArchives}, {(b.FromCache ? "cache" : "built")}, build {b.Elapsed.TotalMilliseconds:0} ms (wall {sw.ElapsedMilliseconds} ms), table {b.TableBytes / 1048576.0:0.0} MiB, managed delta {b.ManagedDelta / 1048576.0:0.0} MiB");
        Assert.True(b.Chunks > 100_000);
    }
}
