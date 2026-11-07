using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M868: the Character window's one environment is Summoner's Rift (Map11). It loads by itself when a
/// character is shown, once, and the champion and the dummy stand on a fixed, open, walkable stage.
/// Manual play, the arena picker and the NVR backdrop are gone.
///
/// The load itself is driven through <see cref="MeshPreviewViewModel.ArenaHost.LoadScene"/> with a
/// synthetic scene (the host stub); the stage point is checked against the REAL Map11 navgrid and
/// skipped when the install is missing.
/// </summary>
public sealed class Map11EnvironmentTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private readonly string _install = Path.Combine(Path.GetTempPath(), "reyengine-m868-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_install)) Directory.Delete(_install, true); } catch { /* best effort */ }
    }

    private sealed class NoHashes : IHashResolver
    {
        public bool TryGetPath(ulong hash, out string path) { path = ""; return false; }
        public string ResolvePath(ulong hash) => "";
    }

    private static AnimClipInfo Clip(string name) => new(
        name, "animations/" + name.ToLowerInvariant() + ".anm",
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>());

    /// <summary>A fake game install: the DATA.wad.client marker the finder looks for, and (optionally) an empty
    /// Map11.wad.client. Nothing reads the map WAD itself - the stub host produces the scene.</summary>
    private string MakeInstall(bool withMap11)
    {
        string final = Path.Combine(_install, "Game", "DATA", "FINAL");
        Directory.CreateDirectory(Path.Combine(final, "Maps", "Shipping"));
        File.WriteAllBytes(Path.Combine(final, "DATA.wad.client"), Array.Empty<byte>());
        if (withMap11) File.WriteAllBytes(Path.Combine(final, "Maps", "Shipping", "Map11.wad.client"), Array.Empty<byte>());
        return Path.Combine(_install, "Game");
    }

    private static MeshAsset Floor() => new()
    {
        Positions = new float[] { -500f, 0f, -500f, 500f, 0f, -500f, 0f, 0f, 500f },
        Normals = new float[] { 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f },
        Uvs = new float[6],
        Indices = new uint[] { 0, 1, 2 },
        SubMeshes = new[] { new SubMeshInfo("ground", 0, 3, 3) },
        VertexCount = 3,
    };

    private static ArenaScene Scene()
    {
        var none = new TextureImage?[1];
        var bg = new MapPreviewBackground("Map11", Floor(), none, none, none, none, none, new[] { false },
            new List<ReyEngine.Formats.Lighting.PointLight>(), MeshCount: 1, MissingTextures: 0);
        var prop = new PropMesh("Map11", bg.Mesh.Positions, bg.Mesh.Normals, bg.Mesh.Uvs, bg.Mesh.Indices, new List<PropSubmesh>());
        return new ArenaScene("Map11", bg, prop, Nav: null, Spawn: new Vector3(10f, 0f, 20f),
            BoundsMin: new Vector3(-500f, 0f, -500f), BoundsMax: new Vector3(500f, 0f, 500f), 1, 0, 0, 0);
    }

    private sealed class Host
    {
        public int SceneLoads;
        public string? LastWad;
        public MeshPreviewViewModel.ArenaHost Make(string gameDirectory) => new(
            gameDirectory, new NoHashes(), _ => null, _ => null,
            LoadViewport: null,
            LoadScene: wad => { SceneLoads++; LastWad = wad; return Task.FromResult(Scene()); });
    }

    private static MeshPreviewViewModel Preview()
    {
        var preview = new MeshPreviewViewModel();
        preview.SetActions(CharacterActions.Build(
            new[] { "XQAbility/x", "XWAbility/x", "XEAbility/x", "XRAbility/x" },
            new[] { Clip("Spell1"), Clip("Spell2"), Clip("Spell3"), Clip("Spell4"),
                    Clip("Attack1"), Clip("Run"), Clip("Idle1") }));
        return preview;
    }

    // ===================================================== Map11 is the default environment

    [Fact]
    public async Task Map11LoadsByItselfWhenACharacterIsShownAndOnlyOnce()
    {
        var host = new Host();
        var preview = Preview();
        var arenaHost = host.Make(MakeInstall(withMap11: true));

        preview.ConfigureArena(arenaHost);                    // the first champion
        await preview.EnvironmentTask;

        Assert.True(preview.EnvironmentAvailable);
        Assert.True(preview.HasArena);
        Assert.Equal("Map11", preview.ArenaName);
        Assert.Equal(1, host.SceneLoads);
        Assert.EndsWith("Map11.wad.client", host.LastWad);
        Assert.StartsWith(MeshPreviewViewModel.EnvironmentLine, preview.ArenaStatus);   // the stub scene has no navgrid, so a suffix says so
        Assert.True(preview.TargetDummyEnabled);
        Assert.True(preview.ControlMode);                      // the stage owns the character; no user toggle did this

        // another champion, then another skin: the same call, and the map stays where it is
        preview.ConfigureArena(arenaHost);
        await preview.EnvironmentTask;
        preview.ConfigureArena(arenaHost);
        await preview.EnvironmentTask;
        Assert.Equal(1, host.SceneLoads);
        Assert.Equal(1, preview.EnvironmentLoadCount);
        preview.StopControl();
    }

    [Fact]
    public async Task ASubjectThatIsNotACharacterDoesNotLoadTheMap()
    {
        var host = new Host();
        var preview = Preview();
        preview.ConfigureArena(host.Make(MakeInstall(withMap11: true)), loadEnvironment: false);
        await preview.EnvironmentTask;

        Assert.False(preview.HasArena);
        Assert.Equal(0, host.SceneLoads);
        Assert.False(preview.ControlMode);
    }

    [Fact]
    public async Task WithoutMap11TheEmptyPreviewStaysAndOneStatusLineSaysWhy()
    {
        var host = new Host();
        var preview = Preview();
        preview.ConfigureArena(host.Make(MakeInstall(withMap11: false)));
        await preview.EnvironmentTask;

        Assert.False(preview.EnvironmentAvailable);
        Assert.False(preview.HasArena);
        Assert.Equal(0, host.SceneLoads);
        Assert.False(preview.ControlMode);
        Assert.Contains("Map11", preview.ArenaStatus);
        Assert.DoesNotContain('\n', preview.ArenaStatus);
    }

    [Fact]
    public async Task ALegacyMapThatTookTheBackdropMakesTheNextCharacterReloadTheEnvironment()
    {
        // The legacy-map viewer clears the backdrop. The arena is then set but its floor is gone, so showing
        // a champion again must bring Map11 back rather than trusting the stale state.
        var host = new Host();
        var preview = Preview();
        var arenaHost = host.Make(MakeInstall(withMap11: true));
        preview.ConfigureArena(arenaHost);
        await preview.EnvironmentTask;

        preview.SetBackground(null);
        preview.ConfigureArena(arenaHost);
        await preview.EnvironmentTask;

        Assert.Equal(2, host.SceneLoads);
        preview.StopControl();
    }

    // ===================================================== the stage does not leak into other subjects

    [Fact]
    public async Task ALegacyMapOrAPropLeavesTheStageAndTheNextChampionBringsItBackWithoutAReload()
    {
        var host = new Host();
        var preview = Preview();
        var arenaHost = host.Make(MakeInstall(withMap11: true));
        preview.ConfigureArena(arenaHost);
        await preview.EnvironmentTask;
        var stagePosition = preview.CharacterPosition;
        Assert.True(preview.ControlMode);

        // a champion to another champion keeps the stage (keepStage), and costs nothing
        preview.Show("another champion", Floor(), null, null, keepStage: true);
        Assert.True(preview.HasArena);
        Assert.True(preview.ControlMode);

        // File > Open Legacy Map (and a prop, and a bare mesh) call Show without keepStage
        preview.Show("legacy map", Floor(), null, null);
        preview.SetBackground(null);

        Assert.False(preview.ControlMode, "no control tick, no follow camera");
        Assert.False(preview.TargetDummyEnabled, "no dummy gizmo capturing drags");
        Assert.False(preview.HasArena);
        Assert.Null(preview.ArenaViewport);
        Assert.Null(preview.SceneProps);
        Assert.Equal(Vector3.Zero, preview.CharacterPosition);   // the subject draws at the origin
        Assert.Equal(0d, preview.CharacterYaw);

        // then a champion again: the stage is back, the map was not read twice
        preview.ConfigureArena(arenaHost);
        await preview.EnvironmentTask;
        Assert.Equal(1, host.SceneLoads);
        Assert.Equal(1, preview.EnvironmentLoadCount);
        Assert.True(preview.HasArena);
        Assert.True(preview.ControlMode);
        Assert.True(preview.TargetDummyEnabled);
        Assert.Equal(stagePosition, preview.CharacterPosition);
        Assert.True(preview.ArenaOwnsBackdrop);
        preview.StopControl();
    }

    [Fact]
    public async Task ASubjectThatIsNotACharacterAlsoLeavesThroughConfigureArena()
    {
        var host = new Host();
        var preview = Preview();
        var arenaHost = host.Make(MakeInstall(withMap11: true));
        preview.ConfigureArena(arenaHost);
        await preview.EnvironmentTask;

        preview.ConfigureArena(arenaHost, loadEnvironment: false);

        Assert.False(preview.ControlMode);
        Assert.False(preview.HasArena);
        Assert.Equal(1, host.SceneLoads);
    }

    [Fact]
    public async Task AFailedLoadIsNotRetriedOnEveryCharacterAndKeepsItsStatusLine()
    {
        int attempts = 0;
        var preview = Preview();
        string game = MakeInstall(withMap11: true);
        var failing = new MeshPreviewViewModel.ArenaHost(game, new NoHashes(), _ => null, _ => null, null,
            _ => { attempts++; throw new InvalidOperationException("boom"); });

        preview.ConfigureArena(failing);
        await preview.EnvironmentTask;
        Assert.Equal(1, attempts);
        Assert.Contains("boom", preview.ArenaStatus);
        Assert.False(preview.ControlMode);

        preview.ConfigureArena(failing);                       // the next champion click
        await preview.EnvironmentTask;
        Assert.Equal(1, attempts);
        Assert.Contains("boom", preview.ArenaStatus);

        // a different install is a new chance
        string other = Path.Combine(_install, "Other");
        Directory.CreateDirectory(Path.Combine(other, "DATA", "FINAL", "Maps", "Shipping"));
        File.WriteAllBytes(Path.Combine(other, "DATA", "FINAL", "DATA.wad.client"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(other, "DATA", "FINAL", "Maps", "Shipping", "Map11.wad.client"), Array.Empty<byte>());
        preview.ConfigureArena(new MeshPreviewViewModel.ArenaHost(other, new NoHashes(), _ => null, _ => null, null,
            _ => Task.FromResult(Scene())));
        await preview.EnvironmentTask;
        Assert.True(preview.HasArena);
        preview.StopControl();
    }

    [Fact]
    public async Task ALoadThatFinishesAfterTheWindowClosedStartsNoTimerAndIsKeptForNextTime()
    {
        var gate = new TaskCompletionSource<ArenaScene>();
        int loads = 0;
        var preview = Preview();
        var arenaHost = new MeshPreviewViewModel.ArenaHost(MakeInstall(withMap11: true), new NoHashes(), _ => null, _ => null,
            null, _ => { loads++; return gate.Task; });

        preview.ConfigureArena(arenaHost);
        Assert.True(preview.ArenaLoading);
        preview.StopControl();                                  // the window closes mid-load
        gate.SetResult(Scene());
        await preview.EnvironmentTask;

        Assert.False(preview.ControlMode);
        Assert.False(preview.HasArena);

        preview.ConfigureArena(arenaHost);                      // reopened: restored, not read again
        await preview.EnvironmentTask;
        Assert.Equal(1, loads);
        Assert.True(preview.ControlMode);
        Assert.True(preview.HasArena);
        preview.StopControl();
    }

    [Fact]
    public async Task TheDummyFacesWhereTheChampionActuallyStands()
    {
        var host = new Host();
        var preview = Preview();
        var tri = Floor();
        preview.LoadDummyMesh = () => new PropMesh("dummy", tri.Positions, tri.Normals, tri.Uvs, tri.Indices, new List<PropSubmesh>());
        preview.ConfigureArena(host.Make(MakeInstall(withMap11: true)));
        await preview.EnvironmentTask;

        // stood well away from the origin, as the Map11 stage is
        preview.MoveDummy(new Vector3(2000, 0, 3000));
        var champion = preview.CharacterPosition;
        var dummy = preview.TargetDummyPosition!.Value;
        var transform = preview.DummyProps!.Instances[0].Transform;
        var facing = Vector3.TransformNormal(Vector3.UnitZ, transform);
        var toChampion = Vector3.Normalize(new Vector3(champion.X - dummy.X, 0, champion.Z - dummy.Z));
        Assert.True(Vector3.Dot(Vector3.Normalize(new Vector3(facing.X, 0, facing.Z)), toChampion) > 0.999f);
        preview.StopControl();
    }

    [Fact]
    public void TheStageCameraOrbitsAndZoomsWhileFollowingAndReframesRegardlessOfFollow()
    {
        string? gameplay = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.Gameplay.cs");
        string? window = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml.cs");
        if (gameplay is null || window is null) return;
        Assert.DoesNotContain("ControlMode: true, ArenaFollowCamera: true } vm)", gameplay);   // reframing is not gated on Follow
        Assert.Contains("_framedScene != vm.SceneFrameCount", gameplay);
        Assert.Contains("else if (vm.ArenaFollowCamera)", gameplay);
        Assert.Contains("StageMinDistance = 250f", gameplay);
        Assert.Contains("if (_lmb)\n        {\n            PreviewViewport.OrbitBy(dx, dy);", window.Replace("\r\n", "\n"));   // orbit is never gated
        Assert.Contains("Math.Clamp(PreviewViewport.Camera.Pitch, StageMinPitch, StageMaxPitch)", window);  // ...but stays above ground
        Assert.DoesNotContain("_gameplayDistance", window + gameplay);
    }

    // ===================================================== code-driven control, no user toggle

    [Fact]
    public async Task CastingWorksOnceTheStageOwnsTheCharacterAndNothingUserFacingTurnedControlOn()
    {
        var host = new Host();
        var preview = Preview();
        Assert.False(preview.ControlMode);
        preview.CastAbility(0, new Vector3(500, 0, 0));
        Assert.Null(preview.SelectedAction);                   // before the stage: ignored, as it always was

        preview.ConfigureArena(host.Make(MakeInstall(withMap11: true)));
        await preview.EnvironmentTask;
        preview.CastAbility(0, preview.CharacterPosition + new Vector3(300, 0, 0));

        Assert.NotNull(preview.SelectedAction);
        Assert.StartsWith("Q", preview.SelectedAction!.Label);
        preview.StopControl();
    }

    [Fact]
    public void ThePreviewHasNoMapPickerAndNoUnloadAndNoResetPosition()
    {
        var t = typeof(MeshPreviewViewModel);
        foreach (var gone in new[] { "ArenaMaps", "SelectedArenaMap", "HasArenaMaps", "LoadArenaCommand",
                                     "UnloadArenaCommand", "ResetCharacterCommand", "ReapplyBackdrop" })
            Assert.Null(t.GetMember(gone, BindingFlags.Public | BindingFlags.Instance).FirstOrDefault());
        Assert.NotNull(t.GetProperty("ResetSceneCommand"));
    }

    // ===================================================== Reset scene

    [Fact]
    public async Task ResetSceneRestoresTheStageAndStopsWhatWasPlaying()
    {
        var host = new Host();
        var preview = Preview();
        preview.ConfigureArena(host.Make(MakeInstall(withMap11: true)));
        await preview.EnvironmentTask;

        var stagePosition = preview.CharacterPosition;
        var stageYaw = preview.CharacterYaw;
        var stageDummy = preview.TargetDummyPosition!.Value;
        int frames = preview.SceneFrameCount;

        // wander off, move the dummy, cast, leave an effect set
        preview.OrderMove(stagePosition + new Vector3(900, 0, 0));
        preview.MoveDummy(new Vector3(-250, 0, 300));
        preview.CastAbility(2, stagePosition + new Vector3(200, 0, 0));
        Assert.NotEqual(stageDummy, preview.TargetDummyPosition!.Value);

        preview.ResetSceneCommand.Execute(null);

        Assert.Equal(stagePosition, preview.CharacterPosition);
        Assert.Equal(stageYaw, preview.CharacterYaw, 4);
        Assert.Equal(stageDummy.X, preview.TargetDummyPosition!.Value.X, 3);
        Assert.Equal(stageDummy.Z, preview.TargetDummyPosition!.Value.Z, 3);
        Assert.Null(preview.Playback);
        Assert.Null(preview.SelectedVfx);
        Assert.True(preview.SceneFrameCount > frames, "a reset reframes the camera");
        Assert.Equal((preview.CharacterPosition + preview.TargetDummyPosition!.Value) * 0.5f, preview.CameraFocus);
        Assert.Equal(preview.CameraFocus, preview.FocusPoint);
        Assert.True(preview.ControlMode, "the stage still owns the character after a reset");
        preview.StopControl();
    }

    [Fact]
    public async Task ResetDummyGoesBackToTheStageDummy()
    {
        var host = new Host();
        var preview = Preview();
        preview.ConfigureArena(host.Make(MakeInstall(withMap11: true)));
        await preview.EnvironmentTask;
        var stageDummy = preview.TargetDummyPosition!.Value;

        preview.MoveDummy(new Vector3(700, 0, 700));
        preview.ResetDummyCommand.Execute(null);

        Assert.Equal(stageDummy.X, preview.TargetDummyPosition!.Value.X, 3);
        Assert.Equal(stageDummy.Z, preview.TargetDummyPosition!.Value.Z, 3);
        preview.StopControl();
    }

    // ===================================================== the stage, on the real Map11 navgrid

    private static NavGrid? RealMap11Nav()
    {
        string wad = Path.Combine(Game, "DATA", "FINAL", "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(wad)) return null;
        using var archive = WadArchive.Open(wad, new NoHashes());
        ulong hash = HashAlgorithms.WadPath(NavGrid.PathFor("map11"));
        if (!archive.TryGetEntry(hash, out _)) return null;
        return NavGrid.TryParse(archive.Extract(hash), out var grid, out _) ? grid : null;
    }

    [Fact]
    public void TheMap11StagePointIsWalkableOpenFlatAndNearATerrainWall()
    {
        var nav = RealMap11Nav();
        if (nav is null) { output.WriteLine("SKIPPED: Map11 navgrid not available"); return; }   // no install on this machine
        output.WriteLine($"navgrid {nav.CountX}x{nav.CountZ}, cell {nav.CellSize}");

        var stage = ArenaStage.For("Map11", nav, new Vector3(-1000), new Vector3(16000));

        // standing and facing, exactly as documented in ArenaStage
        Assert.Equal(ArenaStage.Map11X, stage.Champion.X, 1);
        Assert.Equal(ArenaStage.Map11Z, stage.Champion.Z, 1);
        Assert.Equal(MathF.PI / 4f, stage.Yaw, 5);
        Assert.True(NavGridPath.IsWalkable(nav, stage.Champion), "champion cell is walkable");
        Assert.True(NavGridPath.IsWalkable(nav, stage.Dummy), "dummy cell is walkable");
        Assert.Equal(NavGridPath.GroundHeight(nav, stage.Champion), stage.Champion.Y, 3);

        // the dummy is 400..600 units straight ahead
        var delta = stage.Dummy - stage.Champion;
        float flat = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        Assert.InRange(flat, 400f, 600f);
        Assert.Equal(MathF.Atan2(delta.X, delta.Z), stage.Yaw, 3);

        // open: a 17 x 17 cell disc (radius 8 cells = 400 units) around BOTH points carries no wall, bush,
        // team-restricted or outline bit, and is flat to 6 units
        const ushort Unwanted = NavGrid.BushFlag | 0x0004 | 0x0008 | 0x0400 | 0x0800 | 0x1000 | 0x0040;
        foreach (var point in new[] { stage.Champion, stage.Dummy })
        {
            var (cx, cz) = NavGridPath.CellOf(nav, point);
            float lo = float.MaxValue, hi = float.MinValue;
            for (int dz = -8; dz <= 8; dz++)
                for (int dx = -8; dx <= 8; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    Assert.True(NavGridPath.IsWalkable(nav, x, z), $"cell ({x},{z}) beside {point} is not walkable");
                    Assert.Equal(0, nav.Flags[z * nav.CountX + x] & Unwanted);
                    float h = nav.Heights[z * nav.CountX + x];
                    lo = MathF.Min(lo, h); hi = MathF.Max(hi, h);
                }
            Assert.True(hi - lo <= 6f, $"ground around {point} spans {hi - lo:0.0} units");
        }

        // and a terrain wall is within 700 units of the champion, for a later wall-jump scenario
        var (px, pz) = NavGridPath.CellOf(nav, stage.Champion);
        float nearest = float.MaxValue;
        for (int dz = -20; dz <= 20; dz++)
            for (int dx = -20; dx <= 20; dx++)
                if (!NavGridPath.IsWalkable(nav, px + dx, pz + dz))
                    nearest = MathF.Min(nearest, MathF.Sqrt(dx * dx + dz * dz) * nav.CellSize);
        Assert.InRange(nearest, 400f, 700f);
    }

    [Fact]
    public void TheStageIsDeterministic()
    {
        var nav = RealMap11Nav();
        if (nav is null) { output.WriteLine("SKIPPED: Map11 navgrid not available"); return; }
        var a = ArenaStage.For("Map11", nav, Vector3.Zero, Vector3.One);
        var b = ArenaStage.For("map11", nav, new Vector3(5), new Vector3(9));   // bounds and case do not matter
        Assert.Equal(a, b);
    }

    [Fact]
    public void AnotherMapKeepsTheOldFountainRuleAndFacesItsDummy()
    {
        var stage = ArenaStage.For("Map12", nav: null, new Vector3(0, 0, 0), new Vector3(1000, 0, 1000));
        var delta = stage.Dummy - stage.Champion;
        Assert.Equal(400f, delta.X, 2);
        Assert.Equal(0f, delta.Z, 2);
        Assert.Equal(MathF.Atan2(delta.X, delta.Z), stage.Yaw, 4);
    }

    // ===================================================== source wiring and bindings

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx")))
            {
                string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
        return null;
    }

    [Fact]
    public void TheArenaViewportStartsWithPropsAnimationsParticlesAndFogOff()
    {
        string? src = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Arena.cs");
        if (src is null) return;
        Assert.Contains("arena.ShowFog = false;", src);
        Assert.DoesNotContain("_showPropMeshes = true", src);
        Assert.DoesNotContain("PlayAllParticles = true", src);
        Assert.DoesNotContain("PlayPropAnimations = true", src);

        // and a freshly built viewport defaults the three to off (no initialiser), so "does not turn them on" means off
        string? main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (main is null) return;
        Assert.Contains("[ObservableProperty] private bool _showPropMeshes;", main);
        Assert.Contains("[ObservableProperty] private bool _playAllParticles;", main);
        Assert.Contains("[ObservableProperty] private bool _playPropAnimations;", main);
    }

    [Fact]
    public void EveryBindingLeftInTheEditedRegionsOfTheWindowResolves()
    {
        string? xaml = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        if (xaml is null) return;

        // header bar, Play tab, Scene tab: the regions this milestone edited. A binding inside a DataTemplate
        // belongs to its item type, so only top-level paths are checked here.
        string header = xaml[..xaml.IndexOf("<Grid ColumnDefinitions=\"320,4,*,4,340\">", StringComparison.Ordinal)];
        int play = xaml.IndexOf("<TextBlock Text=\"Play\"", StringComparison.Ordinal);
        int chroma = xaml.IndexOf("<TextBlock Text=\"Chroma\"", StringComparison.Ordinal);
        Assert.True(play > 0 && chroma > play);
        string tabs = xaml[play..chroma];

        var members = typeof(MeshPreviewViewModel)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name).ToHashSet();
        var missing = new List<string>();
        // M869: the SIMULATION card's ability rows are a DataTemplate over SimAbilityRowViewModel; their bindings belong to the row.
        // The probe in .codex_tmp/M869UiProbe resolves those at runtime.
        foreach (string region in new[] { header, Regex.Replace(tabs, @"<DataTemplate.*?</DataTemplate>", "", RegexOptions.Singleline) })
            foreach (Match m in Regex.Matches(region, @"\{Binding\s+!?([A-Za-z_][A-Za-z0-9_]*)"))
                if (!members.Contains(m.Groups[1].Value)) missing.Add(m.Groups[1].Value);

        Assert.True(missing.Count == 0, "bound but not on MeshPreviewViewModel: " + string.Join(", ", missing.Distinct()));
        Assert.Contains("ResetSceneCommand", tabs);
        Assert.DoesNotContain("ControlMode", xaml);
        Assert.DoesNotContain("HasArenaMaps", xaml);
        Assert.DoesNotContain("LoadArenaCommand", xaml);
    }
}
