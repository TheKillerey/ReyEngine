using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M741/M743: the projection sprites of the old jade effects, and the command that switches them off.
///
/// <para>On Map453 every champion plays as its <c>Jade_&lt;Champion&gt;</c> variant - the map's own
/// GameModeChampionList names 68 of them - and those effects are old enough to lay a flat additive quad on
/// the ground under the effect. Riot's own name for it is on the Map453 turret: an emitter called
/// <c>projected</c> drawing <c>blue-proj.tex</c>, which the reporter had already disabled to fix the turret
/// shots. Malzahar's jade Q carries <c>Proj_purple</c> at 800x800 where his modern Q has no such sprite at
/// all. Over a ported map they read as hard-edged washes across the terrain.</para>
///
/// <para>These tests pin the two halves separately: which emitters COUNT as projections, and that
/// disabling one survives a write and a re-read. The narrower question - masking a projection to the
/// navmesh instead of disabling it - was measured and does not apply: a navmesh mask bounds a GROUND
/// layer, and these are not ground layers.</para>
/// </summary>
public sealed class VfxProjectionEmitterTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>A one-system bin, named as the caller asks, holding the given emitters.</summary>
    private static byte[] Bin(string systemName, params (string Name, string? Texture, bool Disabled)[] emitters)
    {
        var structs = emitters.Select(e =>
        {
            var props = new List<BinTreeProperty> { new BinTreeString(H("emitterName"), e.Name) };
            if (e.Texture is not null) props.Add(new BinTreeString(H("texture"), e.Texture));
            if (e.Disabled) props.Add(new BinTreeBool(H("disabled"), true));
            return (BinTreeProperty)new BinTreeStruct(0, H("VfxEmitterDefinitionData"), props.ToArray());
        }).ToArray();

        var system = new BinTreeObject(H(systemName), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), systemName),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, structs),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static IReadOnlyList<VfxEmitterDefinition> Read(byte[] bin) =>
        VfxSystemResolver.ExtractAll(bin).Values.Single().Emitters;

    // ===================================================== which emitters count

    [Fact]
    public void RiotsThreeSpellingsOfAProjectionAllRead()
    {
        Assert.True(VfxProjectionEmitters.IsProjection("Proj_purple", null));       // Malzahar's jade Q
        Assert.True(VfxProjectionEmitters.IsProjection("ProjectedLight", null));
        Assert.True(VfxProjectionEmitters.IsProjection("projected", null));         // the Map453 turret
        Assert.True(VfxProjectionEmitters.IsProjection("dark-proj", null));
        Assert.True(VfxProjectionEmitters.IsProjection("glow", "ASSETS/fx/blue-proj.tex"));
    }

    [Fact]
    public void AnEmitterThatMerelyMENTIONSAProjectionIsLeftAlone()
    {
        // The match is anchored: a name STARTING with Proj, a "-proj" segment, or a "-proj." texture file.
        Assert.False(VfxProjectionEmitters.IsProjection("Sparks", null));
        Assert.False(VfxProjectionEmitters.IsProjection("MissileProjectile", null));
        Assert.False(VfxProjectionEmitters.IsProjection("beam", "ASSETS/fx/proj_beam_glow.tex"));
        Assert.False(VfxProjectionEmitters.IsProjection(null, null));
    }

    // ===================================================== the rewrite

    [Fact]
    public void TheProjectionIsOffAfterTheWriteAndTheRestOfTheSystemIsUntouched()
    {
        byte[] bin = Bin("Jade_Malzahar_Q", ("Proj_purple", "fx/purple-proj.tex", false), ("Sparks", null, false));
        var result = VfxProjectionEmitters.Disable(bin, out var edits, out string? error);

        Assert.Null(error);
        Assert.NotNull(result);
        var edit = Assert.Single(edits);
        Assert.Equal("Proj_purple", edit.Emitter);
        Assert.Equal("Jade_Malzahar_Q", edit.System);
        Assert.Equal("purple-proj.tex", edit.Texture);

        var back = Read(result!);
        Assert.True(back.Single(e => e.Name == "Proj_purple").Disabled);
        Assert.False(back.Single(e => e.Name == "Sparks").Disabled);
    }

    [Fact]
    public void ADisabledFlagIsADDEDWhereRiotOmittedIt()
    {
        // 'disabled' is written only when true (M652), so the emitter to switch off has no such field at
        // all - the writer must create it rather than flip one.
        byte[] bin = Bin("Jade_Turret", ("projected", "fx/blue-proj.tex", false));
        Assert.False(Read(bin).Single().Disabled);

        var result = VfxProjectionEmitters.Disable(bin, out var edits, out _);
        Assert.Single(edits);
        Assert.True(Read(result!).Single().Disabled);
    }

    [Fact]
    public void RunningItTwiceChangesNothingTheSecondTime()
    {
        // The command re-reads the game's own wads every patch, so it is run over its own output routinely.
        byte[] bin = Bin("Jade_Malzahar_Q", ("Proj_purple", null, false));
        var once = VfxProjectionEmitters.Disable(bin, out var first, out _);
        Assert.Single(first);

        var twice = VfxProjectionEmitters.Disable(once!, out var second, out string? error);
        Assert.Null(error);
        Assert.Empty(second);
        Assert.Same(once, twice);        // unchanged bins come back by reference, not rewritten
    }

    [Fact]
    public void ASystemThatIsNotJadeIsNeverTouched()
    {
        // The modern champion effects are shared with every other map; only the jade variants are ported.
        byte[] bin = Bin("Malzahar_Base_Q", ("Proj_purple", null, false));
        var result = VfxProjectionEmitters.Disable(bin, out var edits, out string? error);

        Assert.Null(error);
        Assert.Empty(edits);
        Assert.Same(bin, result);
    }

    [Fact]
    public void AnAlreadyDisabledProjectionIsNotCountedAsAFix()
    {
        byte[] bin = Bin("Jade_Malzahar_Q", ("Proj_purple", null, true));
        VfxProjectionEmitters.Disable(bin, out var edits, out _);
        Assert.Empty(edits);
    }

    [Fact]
    public void ABinThatIsNotABinIsRefusedRatherThanThrown()
    {
        var result = VfxProjectionEmitters.Disable(new byte[] { 1, 2, 3, 4 }, out var edits, out string? error);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Empty(edits);
    }

    // ===================================================== the command around it

    [Fact]
    public void TheFixShipsInItsOwnLayerSoItCanBeSwitchedOffWithoutLosingTheMap()
    {
        var project = new ReyEngine.Core.Projects.ReyProject { RootPath = "/x" };
        project.ProjectFolders.Add("Map453");
        project.ProjectFolders.Add("Malzahar");
        project.Layers.Add(new ReyEngine.Core.Projects.ProjectLayer
        {
            Name = ReyEngine.App.ViewModels.MainWindowViewModel.JadeProjectionLayer,
            Priority = 10,
            Folders = { "Malzahar" },
        });

        // the map stays in base, the champion rides the optional layer
        Assert.Equal(ReyEngine.Core.Projects.ProjectLayer.BaseLayer, project.LayerOf("Map453"));
        Assert.Equal("particle-fix", project.LayerOf("Malzahar"));

        // and the exporter declares both, base first, so the lower-priority map is applied underneath
        var layers = ReyEngine.Core.Build.LtkProjectLayers.Of(project);
        Assert.Equal(new[] { ReyEngine.Core.Projects.ProjectLayer.BaseLayer, "particle-fix" },
            layers.Select(l => l.Name).ToArray());
    }

    [Fact]
    public void TheCommandReadsTheGamesOwnWadsSoRerunningItAfterAPatchIsSafe()
    {
        // Riot rewrites these bins every patch, so the fix is re-applied rather than kept. Reading the
        // project's already-fixed copies instead would make the second run a no-op over stale data.
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ReyEngine.App", "ViewModels",
            "MainWindowViewModel.JadeProjections.cs"));
        Assert.Contains("\"DATA\", \"FINAL\", \"Champions\"", src);
        Assert.Contains("VfxProjectionEmitters.Disable", src);
        Assert.Contains("JadeProjectionLayer", src);
    }

    [Fact]
    public void TheButtonIsBoundToACommandThatExists()
    {
        // A binding naming a member the view model does not have compiles, passes every test, and fails
        // silently at runtime - the same check the Lift Decals button beside it carries.
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ReyEngine.App", "Views",
            "MapInspectorView.axaml"));
        Assert.Contains("{Binding FixJadeChampionProjectionsCommand}", xaml);
        Assert.True(typeof(ReyEngine.App.ViewModels.MainWindowViewModel)
            .GetMember("FixJadeChampionProjectionsCommand",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Length > 0,
            "MapInspectorView binds FixJadeChampionProjectionsCommand, which MainWindowViewModel does not expose");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
