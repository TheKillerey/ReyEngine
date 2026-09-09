using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Skeletons;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M679: a placed prop plays the clips its skin's OWN animation graph names. Pinned on the two Map453
/// mobs that showed the folder scan was wrong - SmallGolem wears the Golem's rig and graph, YoungLizard's
/// idles are the Lizard's files - and on synthetic tables for the idle ranking.
/// </summary>
public sealed class PropAnimationResolutionTests
{
    private static AnimClipInfo Clip(string name, string path) =>
        new(name, path, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>());

    // ===================================================== the ranking

    [Fact]
    public void AHashNamedIdleIsFoundByItsFileAndBoredBeatsAngry()
    {
        // the Golem graph as shipped: attack/run/death by name, six idles under hash names
        var golem = new[]
        {
            Clip("death", "assets/characters/golem/animations/golem_death.anm"),
            Clip("Attack1", "assets/characters/golem/animations/golem_attack1.anm"),
            Clip("0x234334b6", "assets/characters/golem/animations/golem_idle_angry1.anm"),
            Clip("0xf8cae873", "assets/characters/golem/animations/golem_idle_bored1.anm"),
            Clip("0xf9caea06", "assets/characters/golem/animations/golem_idle_bored2.anm"),
        };
        var idle = PropAnimations.PickIdle(golem);
        Assert.NotNull(idle);
        Assert.EndsWith("golem_idle_bored1.anm", idle!.AnmPath);
        Assert.Equal("golem_idle_bored1", PropAnimations.DisplayName(idle));
    }

    [Fact]
    public void ABaseIdleByNameWinsOverEveryVariant()
    {
        var champion = new[]
        {
            Clip("Idle_Bored1", "assets/x/idle_bored1.anm"),
            Clip("Idle1", "assets/x/x_idle01.anm"),
            Clip("Idle2", "assets/x/x_idle02.anm"),
        };
        Assert.Equal("Idle1", PropAnimations.PickIdle(champion)!.Name);
    }

    [Fact]
    public void ASkinWithOnlyAttacksHasNoIdleAndTheCardStillFindsItsClips()
    {
        var attacks = new[] { Clip("Attack1", "a/attack1.anm"), Clip("0xabc", "a/b_attack2.anm") };
        Assert.Null(PropAnimations.PickIdle(attacks));
        Assert.Equal("b_attack2", PropAnimations.DisplayName(attacks[1]));
        Assert.Same(attacks[1], PropAnimations.Find(attacks, "b_attack2"));
        Assert.Same(attacks[0], PropAnimations.Find(attacks, "attack1"));   // by file name too
        Assert.Null(PropAnimations.Find(attacks, "Run"));
    }

    [Fact]
    public void LooseFilesStandInWhenThereIsNoGraph()
    {
        var table = PropAnimations.FromFiles(new[] { "assets/characters/x/animations/x_idle1.anm", "assets/characters/x/animations/x_run.anm" });
        Assert.Equal(2, table.Count);
        Assert.Equal("x_idle1", PropAnimations.PickIdle(table)!.Name);
    }

    // ===================================================== the real mobs

    private const string Shipping = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping";
    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    [Theory]
    [InlineData("smallgolem", "characters/golem/animations/", "golem_idle")]
    [InlineData("younglizard", "characters/lizard/animations/", "lizard_idle")]
    public void TheMap453MobsPlayTheirGraphsIdleNotTheirFoldersFiles(string character, string idleFolder, string idleFile)
    {
        string wadPath = Path.Combine(Shipping, "Map453.wad.client");
        if (!File.Exists(wadPath) || Database.Value is not { } database) return;
        var resolver = new WadPathResolver(database);
        using var wad = WadArchive.Open(wadPath, resolver);
        byte[]? Read(string path) { ulong h = BinTexturePath.HashOfReference(path); return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null; }
        string? Bin(uint h) => database.TryGetBinName(h, out var n) ? n : null;
        string? Wad(ulong h) => database.TryGetPath(h, out var p) ? p : null;

        var skinBin = Read($"data/characters/{character}/skins/skin0.bin");
        if (skinBin is null) return;
        var clips = PropAnimations.ResolveClips(skinBin, Read, Bin, Wad);
        Assert.NotEmpty(clips);

        var idle = PropAnimations.PickIdle(clips);
        Assert.NotNull(idle);
        Assert.Contains(idleFolder, idle!.AnmPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(idleFile, idle.AnmPath, StringComparison.OrdinalIgnoreCase);
        // and the file the graph names is really in the wad - the idle was never missing, only unlooked-for
        Assert.NotNull(Read(idle.AnmPath));
    }
}
