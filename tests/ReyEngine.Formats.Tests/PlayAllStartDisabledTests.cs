using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M784: "Big Penguin has a weird yellow golden flick" (7yanniversary, Map22). The flicker was
/// 7YAnniversary_Progression_Setting_Main - a startDisabled placement the game turns on only for the level-7
/// board transformation (MapBehavior_LevelUp7Planning toggles it on, then off at 6.5 s). Play All played it
/// from load, and the simulator's single-particle re-arm turned its one gold flash into a 0.3 s strobe.
/// </summary>
public sealed class PlayAllStartDisabledTests
{
    private static MapParticlePlacement Placement(bool? startDisabled) =>
        new("P", Vector3.Zero, Matrix4x4.Identity, "Sys", "Group", StartDisabled: startDisabled);

    [Fact]
    public void Only_a_start_disabled_placement_is_left_off_at_load()
    {
        Assert.False(MainWindowViewModel.PlaysAtMapLoad(Placement(true)));
        Assert.True(MainWindowViewModel.PlaysAtMapLoad(Placement(false)));
        Assert.True(MainWindowViewModel.PlaysAtMapLoad(Placement(null)));   // absent = plays, as before
    }

    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    [Fact]
    public void The_7yanniversary_transformation_flash_is_off_and_the_penguin_still_plays()
    {
        string wadPath = Path.Combine(Final, @"Maps\Shipping\Map22.wad.client");
        if (!File.Exists(wadPath)) return;
        var db = new HashSyncService().LoadLocal(_ => { });
        using var wad = WadArchive.Open(wadPath, new WadPathResolver(db));
        ulong bin = HashAlgorithms.WadPath("data/maps/mapgeometry/map22/7yanniversary.materials.bin");
        if (!wad.TryGetEntry(bin, out _)) return;
        var placements = MapParticleExtractor.Extract(wad.Extract(bin), h => db.TryGetBinName(h, out var n) ? n : null);

        static bool Named(MapParticlePlacement p, string part) =>
            p.Name.Contains(part, StringComparison.OrdinalIgnoreCase)
            || p.SystemPath.Contains(part, StringComparison.OrdinalIgnoreCase);

        var flash = placements.Where(p => Named(p, "Progression_Setting_Main")).ToList();
        Assert.NotEmpty(flash);
        Assert.All(flash, p => Assert.False(MainWindowViewModel.PlaysAtMapLoad(p)));

        // the penguin itself is the L1 Center_Shop system - it must keep playing
        var penguin = placements.Where(p => Named(p, "Center_Shop") && !Named(p, "Progression")).ToList();
        Assert.NotEmpty(penguin);
        Assert.All(penguin, p => Assert.True(MainWindowViewModel.PlaysAtMapLoad(p)));
    }
}
