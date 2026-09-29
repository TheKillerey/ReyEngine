using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Vfx;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M793: "aprilfool has this particle that looks like a camera effect" (Map22). It was
/// Level7_AprilFool_Board_Light_VFX_02_1 - an AttachToCamera placement whose one emitter is a 1350x750 screen
/// vignette (unrotated arbitrary quad, depth test off). The editor has no camera attachment, so Play All drew it
/// at its map transform: a dark vertical sheet standing over the middle of the board, not where the flag says the
/// game draws it.
/// Play All now leaves camera-attached placements off; selecting one still previews it.
/// </summary>
public sealed class PlayAllCameraAttachedTests
{
    private static MapParticlePlacement Placement(bool? attachToCamera) =>
        new("P", Vector3.Zero, Matrix4x4.Identity, "Sys", "Group", AttachToCamera: attachToCamera);

    [Fact]
    public void Only_a_camera_attached_placement_is_left_out_of_world_space_play()
    {
        Assert.False(MainWindowViewModel.PlaysInWorldSpace(Placement(true)));
        Assert.True(MainWindowViewModel.PlaysInWorldSpace(Placement(false)));
        Assert.True(MainWindowViewModel.PlaysInWorldSpace(Placement(null)));   // absent = plays, as before
    }

    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    [Fact]
    public void The_aprilfool_level7_vignette_is_off_and_the_rest_of_the_board_still_plays()
    {
        string wadPath = Path.Combine(Final, @"Maps\Shipping\Map22.wad.client");
        if (!File.Exists(wadPath)) return;
        var db = new HashSyncService().LoadLocal(_ => { });
        using var wad = WadArchive.Open(wadPath, new WadPathResolver(db));
        ulong binHash = HashAlgorithms.WadPath("data/maps/mapgeometry/map22/aprilfool.materials.bin");
        if (!wad.TryGetEntry(binHash, out _)) return;
        byte[] bin = wad.Extract(binHash);
        var placements = MapParticleExtractor.Extract(bin, h => db.TryGetBinName(h, out var n) ? n : null);

        var vignette = placements.Single(p => p.Name == "Level7_AprilFool_Board_Light_VFX_02_1");
        Assert.True(vignette.AttachToCamera);
        Assert.False(MainWindowViewModel.PlaysInWorldSpace(vignette));

        // What made it a sheet over everything: one unrotated arbitrary quad of screen proportions, no depth test.
        var system = VfxSystemResolver.ExtractAll(bin)[vignette.SystemHash];
        var emitter = Assert.Single(system.Emitters, e => e.IsVisual);
        Assert.True(emitter.IsArbitraryQuad);
        Assert.Null(emitter.BirthRotation);
        Assert.Equal(new Vector3(1350f, 750f, 0f), emitter.BirthScale.Constant);
        Assert.True(VfxMiscRenderFlags.DisablesDepthTest(emitter));

        // The gate is narrow: every other placement on the board, the same level-7 layer included, still plays.
        Assert.All(placements.Where(p => !ReferenceEquals(p, vignette)),
            p => Assert.True(MainWindowViewModel.PlaysInWorldSpace(p)));
        foreach (string name in new[] { "Level7_AprilFool_Skybox1", "Level7_AprilFool_Rainbow1", "Level1_AprilFool_Board_Light1" })
        {
            var p = placements.Single(x => x.Name == name);
            Assert.True(MainWindowViewModel.PlaysAtMapLoad(p) && MainWindowViewModel.PlaysInWorldSpace(p), name);
        }
    }

    /// <summary>The seam, not just the predicate: Play All on the real aprilfool bin (every layer shown) submits
    /// every playable system except the camera vignette.</summary>
    [Fact]
    public void Play_all_on_aprilfool_submits_everything_but_the_camera_vignette()
    {
        string wadPath = Path.Combine(Final, @"Maps\Shipping\Map22.wad.client");
        if (!File.Exists(wadPath)) return;
        var db = new HashSyncService().LoadLocal(_ => { });
        using var wad = WadArchive.Open(wadPath, new WadPathResolver(db));
        ulong binHash = HashAlgorithms.WadPath("data/maps/mapgeometry/map22/aprilfool.materials.bin");
        if (!wad.TryGetEntry(binHash, out _)) return;
        byte[] bin = wad.Extract(binHash);
        var systems = VfxSystemResolver.ExtractAll(bin);
        var placements = MapParticleExtractor.Extract(bin, h => db.TryGetBinName(h, out var n) ? n : null);
        uint vignette = placements.Single(p => p.Name == "Level7_AprilFool_Board_Light_VFX_02_1").SystemHash;

        var vm = new MainWindowViewModel();
        // What the map loader does (TryLoadMapTextures sets _vfxSystems from the same bin); there is no public seam.
        typeof(MainWindowViewModel).GetField("_vfxSystems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, systems);
        vm.CurrentModelParticles = placements;
        vm.PlayAllParticles = true;

        var submitted = vm.CurrentParticlePlayback?.Items.Select(i => i.System.PathHash).ToList();
        Assert.NotNull(submitted);
        Assert.DoesNotContain(vignette, submitted);
        // Everything the pre-M793 gate played, minus the one camera-attached placement.
        int before = placements.Count(p => p.VisibilityFlags != 0 && MainWindowViewModel.PlaysAtMapLoad(p) && p.Transitional != true
            && systems.TryGetValue(p.SystemHash, out var s) && s.Emitters.Any(e => e.IsVisual));
        Assert.Equal(before - 1, submitted.Count);
    }
}
