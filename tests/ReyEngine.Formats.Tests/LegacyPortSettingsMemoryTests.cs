using System.Numerics;
using System.Text.Json;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M600: the legacy map port remembers what it was last run with.
///
/// <para>Nothing recorded these before — not <c>project.json</c>, not <c>EditorSettings</c>. The wizard
/// seeded itself from <c>LegacyPortCleanupOptions</c>/<c>LegacyPortShaderOptions</c> defaults every time,
/// so seven cleanup flags, the decal-plane choice, the alignment correction and four role shaders lived
/// only in the dialog and were gone the moment it closed.</para>
///
/// <para>That is not a convenience gap. Re-running a port to change ONE option meant re-deriving the
/// other twelve from memory, and a wrong one produces a DIFFERENT map with nothing saying so — a ported
/// Map453 had to be re-run to turn decal planes off, and the only record of which cleanup flags the first
/// run used was what could be reverse-engineered out of the output (which meshes were gone, how many
/// placements had been dropped).</para>
/// </summary>
public sealed class LegacyPortSettingsMemoryTests
{
    private static readonly string[] Choices =
    {
        LegacyMapPorter.NormalShader, LegacyMapPorter.DecalShader,
        LegacyMapPorter.GrassShader, LegacyMapPorter.TerrainShader,
        "Shaders/StaticMesh/SomethingElse",
    };

    /// <summary>A selection with every field set to something distinguishable from the defaults, so a
    /// field that fails to round-trip cannot pass by coincidence.</summary>
    private static LegacyMapPortShaderSelection Selection(bool quads = true) => new(
        new LegacyPortShaderOptions(
            "Shaders/StaticMesh/SomethingElse", LegacyMapPorter.DecalShader,
            LegacyMapPorter.GrassShader, LegacyMapPorter.TerrainShader),
        new Dictionary<string, string> { ["LegacyPort/map1/Normal_x"] = "Shaders/StaticMesh/SomethingElse" },
        new LegacyPortCleanupOptions(
            RemoveOriginalMeshes: true, RemoveOriginalBushes: false,
            RemoveUnusedOriginalMaterials: true, RemoveOriginalParticles: true,
            RemoveOriginalProps: false, RemoveOriginalSounds: false, RemoveOriginalProbes: false),
        FixImportedMapPosition: true,
        PositionCorrection: new Vector3(1000.5f, -2.25f, 7.75f),
        ImportLegacyParticles: false,
        Decals: new LegacyPortDecalOptions(GenerateQuads: quads, Lift: 6.5f, SingleImage: true),
        ImportLegacySounds: false);

    [Fact]
    public void EverySettingSurvivesRememberAndReplay()
    {
        var replayed = LegacyMapPortWindowViewModel.Replay(
            LegacyMapPortWindowViewModel.Remember(Selection()), Choices);

        Assert.Equal(Selection().Cleanup, replayed.Cleanup);
        Assert.Equal(Selection().RoleShaders, replayed.RoleShaders);
        Assert.Equal(Selection().PositionCorrection, replayed.PositionCorrection);
        Assert.True(replayed.FixImportedMapPosition);
        Assert.False(replayed.ImportLegacyParticles);
        Assert.False(replayed.ImportLegacySounds);
        Assert.Equal(Selection().Decals, replayed.Decals);
    }

    /// <summary>The one that started this: a re-port has to keep the seven cleanup flags it ran with while
    /// the decal-plane choice changes on its own.</summary>
    [Fact]
    public void TurningDecalPlanesOffDoesNotDisturbTheCleanupFlags()
    {
        var settings = LegacyMapPortWindowViewModel.Remember(Selection(quads: true));
        settings.GenerateDecalQuads = false;

        var replayed = LegacyMapPortWindowViewModel.Replay(settings, Choices);

        Assert.False(replayed.Decals!.GenerateQuads);
        Assert.Equal(Selection().Cleanup, replayed.Cleanup);
        Assert.Equal(6.5f, replayed.Decals.Lift);
    }

    [Fact]
    public void AllSevenCleanupFlagsAreCarriedIndividually()
    {
        // Seven booleans is exactly the shape where "I copied the record" silently drops one, so each is
        // flipped on its own and checked on its own.
        for (int i = 0; i < 7; i++)
        {
            bool[] flags = Enumerable.Repeat(false, 7).ToArray();
            flags[i] = true;
            var cleanup = new LegacyPortCleanupOptions(flags[0], flags[1], flags[2], flags[3], flags[4], flags[5], flags[6]);
            var selection = Selection() with { Cleanup = cleanup };

            var replayed = LegacyMapPortWindowViewModel.Replay(
                LegacyMapPortWindowViewModel.Remember(selection), Choices);

            Assert.Equal(cleanup, replayed.Cleanup);
        }
    }

    [Fact]
    public void AShaderTheInstalledClientNoLongerOffersFallsBackToTheRoleDefault()
    {
        // The catalogue is read from the game, so a name stored against an older patch can simply be gone.
        // Seeding a row with a shader the port would then refuse is worse than falling back.
        var settings = LegacyMapPortWindowViewModel.Remember(Selection());
        settings.NormalShader = "Shaders/StaticMesh/DeletedInSomeFuturePatch";

        var replayed = LegacyMapPortWindowViewModel.Replay(settings, Choices);

        Assert.Equal(LegacyMapPorter.NormalShader, replayed.RoleShaders.NormalShader);
        // and the ones that DO still exist are untouched
        Assert.Equal(LegacyMapPorter.DecalShader, replayed.RoleShaders.DecalShader);
    }

    [Fact]
    public void AReplayCarriesNoPerMaterialOverrides()
    {
        // Row-level choices belong to the dialog. Replaying a guess at them would change materials the
        // user never touched, which is the opposite of reproducing their port.
        var replayed = LegacyMapPortWindowViewModel.Replay(
            LegacyMapPortWindowViewModel.Remember(Selection()), Choices);

        Assert.Empty(replayed.MaterialShaders);
    }

    [Fact]
    public void SettingsRoundTripThroughProjectJson()
    {
        var project = new ReyProject { Name = "p", LegacyPort = LegacyMapPortWindowViewModel.Remember(Selection()) };

        string json = JsonSerializer.Serialize(project);
        var reloaded = JsonSerializer.Deserialize<ReyProject>(json);

        Assert.NotNull(reloaded?.LegacyPort);
        var replayed = LegacyMapPortWindowViewModel.Replay(reloaded!.LegacyPort!, Choices);
        Assert.Equal(Selection().Cleanup, replayed.Cleanup);
        Assert.Equal(Selection().Decals, replayed.Decals);
        Assert.Equal(Selection().PositionCorrection, replayed.PositionCorrection);
    }

    [Fact]
    public void AProjectThatHasNeverBeenPortedRemembersNothing()
    {
        // Null is the signal the wizard uses to open on defaults, so it must not become an all-false
        // settings object that would silently port with every cleanup flag off.
        Assert.Null(new ReyProject { Name = "p" }.LegacyPort);
    }

    [Fact]
    public void TheDefaultSettingsMatchThePortersOwnDefaults()
    {
        // A fresh object is what a hand-written project.json entry falls back to, so it has to agree with
        // the porter rather than with whatever C# initialises first.
        var fresh = new LegacyPortSettings();

        Assert.False(fresh.GenerateDecalQuads);                       // M550: opt-in, not a correction
        Assert.Equal(LegacyPortDecalOptions.Defaults.Lift, fresh.DecalLift);
        Assert.False(fresh.DecalSingleImage);                          // M554
        Assert.True(fresh.ImportLegacyParticles);
        Assert.True(fresh.ImportLegacySounds);
    }

    [Fact]
    public void RememberStampsWhenItWasSaved()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var settings = LegacyMapPortWindowViewModel.Remember(Selection());

        Assert.NotNull(settings.SavedUtc);
        Assert.InRange(settings.SavedUtc!.Value, before, DateTime.UtcNow.AddSeconds(1));
    }
}
