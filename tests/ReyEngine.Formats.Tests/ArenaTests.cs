using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M636: the arena - a shipped map as a floor under the playable character. The heavy loading is covered
/// by a real-data test; the wiring by source assertions, because the trap this feature walks past is a
/// prop set that one renderer reads and the other does not (M628's lesson, in reverse).
/// </summary>
public sealed class ArenaTests
{
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private static bool Installed => Directory.Exists(Game);

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ===================================================== choosing a map

    [Fact]
    public void TheMapListIsTheShippedMapWadsAndNothingElse()
    {
        // Map11.wad.client yes; Map11.en_US.wad.client (a locale WAD) and Common.wad.client no.
        if (!Installed) return;
        var maps = ArenaLoader.AvailableMaps(Game);
        Assert.Contains("Map11", maps);
        Assert.DoesNotContain(maps, m => m.Contains("en_US", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(maps, m => m.StartsWith("Common", StringComparison.OrdinalIgnoreCase));
        Assert.All(maps, m => Assert.DoesNotContain('.', m));
    }

    [Fact]
    public void AnUnknownInstallGivesAnEmptyListNotAThrow()
    {
        Assert.Empty(ArenaLoader.AvailableMaps(@"Q:\nowhere"));
        Assert.Empty(ArenaLoader.AvailableMaps(null));
        Assert.Null(ArenaLoader.WadPathFor(null, "Map11"));
    }

    // ===================================================== loading one

    [Fact]
    public void SummonersRiftLoadsAsAFloorWithItsNavGridAndAWalkableSpawn()
    {
        if (!Installed) return;
        string? wad = ArenaLoader.WadPathFor(Game, "Map11");
        if (wad is null) return;
        HashDatabase database;
        try { database = new HashSyncService().LoadLocal(_ => { }); } catch { return; }
        var resolver = new WadPathResolver(database);

        var lines = new List<string>();
        var scene = ArenaLoader.Load(wad, resolver,
            h => database.TryGetBinName(h, out var n) ? n : null,
            h => database.TryGetPath(h, out var p) ? p : null,
            lines.Add);

        Assert.Equal("Map11", scene.MapKey);
        Assert.True(scene.Geometry.Indices.Length > 1_000_000, "the base mapgeo has hundreds of thousands of triangles");
        Assert.True(scene.GroupsDrawn > 100, $"only {scene.GroupsDrawn} groups drawn");
        Assert.True(scene.Geometry.Submeshes.Count(s => s.Texture is not null) > scene.GroupsDrawn / 2,
            "most groups should have resolved a diffuse");
        Assert.True(scene.HasNavGrid, "Summoner's Rift ships a navgrid");

        // The spawn is on walkable ground, inside the map, at the grid's height for that cell.
        var nav = scene.Nav!;
        Assert.True(ReyEngine.Formats.MapGeo.NavGridPath.IsWalkable(nav, scene.Spawn));
        Assert.InRange(scene.Spawn.X, scene.BoundsMin.X, scene.BoundsMax.X);
        Assert.InRange(scene.Spawn.Z, scene.BoundsMin.Z, scene.BoundsMax.Z);
        Assert.Equal(ReyEngine.Formats.MapGeo.NavGridPath.GroundHeight(nav, scene.Spawn), scene.Spawn.Y, 3);
        Assert.Contains(lines, l => l.Contains("navgrid", StringComparison.OrdinalIgnoreCase));
    }

    // ===================================================== the driven pose

    [Fact]
    public void AMeshWithoutAPoseSourceIdlesOnTheSharedClockAndWithOneIsDriven()
    {
        // A real clip, because AnimationClip wraps a decoded asset and has no test constructor: the
        // practice dummy's idle, off Map11.wad - the same loader the preview window uses.
        if (!Installed) return;
        HashDatabase database;
        try { database = new HashSyncService().LoadLocal(_ => { }); } catch { return; }
        var dummy = TargetDummyLoader.Get(Game, new WadPathResolver(database));
        if (dummy?.IdleClip is not { } idle) return;

        float dur = idle.Duration > 1e-3f ? idle.Duration : 1f;
        float shared = dur * 2.75f;
        var (clip, time) = dummy.PoseAt(shared);
        Assert.Same(idle, clip);
        Assert.Equal(dur * 0.75f, time, 3);        // idle loops on the shared clock

        dummy.PoseSource = () => (idle, 0.125f);
        var driven = dummy.PoseAt(shared);
        Assert.Same(idle, driven.Clip);
        Assert.Equal(0.125f, driven.Time, 4);      // a driven mesh ignores the shared clock
        dummy.PoseSource = null;                   // the loader caches this instance process-wide
    }

    // ===================================================== the wiring both renderers must share

    [Fact]
    public void BothRenderersDrawTheSameSceneSetAndBothPoseThroughTheOneHook()
    {
        var axaml = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        var dx11 = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        var gl = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        var props = Source("src", "ReyEngine.App", "Services", "D3D11MapProps.cs");
        if (axaml is null || dx11 is null || gl is null || props is null) return;

        Assert.Contains("PropMeshes=\"{Binding SceneProps}\"", axaml);
        Assert.Contains("FocusPoint=\"{Binding FocusPoint}\"", axaml);
        Assert.Contains("_dx11.PropMeshes = vm.SceneProps;", dx11);
        Assert.DoesNotContain("_dx11.PropMeshes = vm.DummyProps;", dx11);
        Assert.Contains("pm.PoseAt(t)", gl);
        Assert.Contains("m.PoseAt(seconds)", props);
    }

    [Fact]
    public void ARightClickOnTheArenaGoesThroughTheGridBeforeThePlane()
    {
        var window = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml.cs");
        if (window is null) return;
        Assert.Contains("vm.TryArenaGroundHit(origin, dir, out var arenaPoint)", window);
        Assert.Contains("if (!vm.OrderMoveOnArena(point)) vm.OrderMove(point);", window);
    }

    [Fact]
    public void TheControllerCanBePutOnTheGroundWithoutLosingItsOrder()
    {
        var c = new CharacterController();
        c.Teleport(new Vector3(100f, 0f, 100f));
        c.MoveTo(new Vector3(600f, 0f, 100f));
        c.SetGroundHeight(42f);
        Assert.Equal(42f, c.Position.Y);
        Assert.NotNull(c.Destination);              // Teleport would have cleared it
        c.Tick(0.1f);
        Assert.Equal(42f, c.Position.Y);            // movement is flat; the height it was given stays
        Assert.True(c.Position.X > 100f);
    }
}
