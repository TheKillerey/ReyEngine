using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M665: the arena floor stopped being a diffuse-only prop and became the viewport's backdrop — the same
/// second <c>ViewportMeshRenderer</c> the map backdrop has always used, fed by the same resolver the map
/// viewport feeds. Reported as "the mapgeo we load on there is not working the same how we handle it in
/// our dx11 viewport".
/// </summary>
public sealed class ArenaBackdropTests
{
    private const string GameDir = @"C:\Riot Games\League of Legends\Game";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static ArenaScene? Arena(string key)
    {
        if (Database.Value is not { } db) return null;
        string? wad = ArenaLoader.WadPathFor(GameDir, key);
        if (wad is null || !File.Exists(wad)) return null;
        try
        {
            return ArenaLoader.Load(wad, new WadPathResolver(db),
                h => db.TryGetBinName(h, out var n) ? n : null,
                h => db.TryGetPath(h, out var p) ? p : null, _ => { });
        }
        catch { return null; }
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.sln"))) dir = dir.Parent;
        return dir is null ? "" : File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    /// <summary>
    /// Every per-submesh array has to be exactly as long as the mesh's submesh list, because the renderer
    /// indexes all of them by submesh number. The groups are FILTERED by the layer rule on the way in, so
    /// this is the one thing that can silently go wrong: drop a group from the mesh and not from the
    /// lightmaps and every layer after it lands on the wrong surface.
    /// </summary>
    [Theory]
    [InlineData("Map30")]
    [InlineData("Map11")]
    public void EveryPerSubmeshLayerIsAsLongAsTheMesh(string key)
    {
        if (Arena(key) is not { } scene) return;   // no install here
        var bg = scene.Background;
        int n = bg.Mesh.SubMeshes.Count;

        Assert.Equal(n, bg.SubmeshTextures.Count);
        Assert.Equal(n, bg.SubmeshMaterials!.Count);
        Assert.Equal(n, bg.SubmeshBlend.Count);
        Assert.Equal(n, bg.SubmeshColor1.Count);
        Assert.Equal(n, bg.SubmeshColor2.Count);
        Assert.Equal(n, bg.SubmeshColor3.Count);
        Assert.Equal(n, bg.SubmeshDoubleSided.Count);
        Assert.Equal(n, bg.SubmeshMask!.Count);
        Assert.Equal(n, bg.SubmeshLightmap!.Count);
        Assert.Equal(n, scene.GroupsDrawn);
    }

    /// <summary>
    /// The point of the change. The prop pipeline gave every group one alpha mode (cutout at 0.35) and no
    /// way to say anything else; a real map's groups disagree with each other. Map30 alone has 13 blended
    /// and 4 two-sided of 24.
    /// </summary>
    [Fact]
    public void TheFloorCarriesPerMaterialRenderStateRatherThanOneGlobalCutout()
    {
        if (Arena("Map30") is not { } scene) return;
        var mats = scene.Background.SubmeshMaterials!;
        Assert.NotEmpty(mats);
        Assert.True(mats.Any(m => m.AlphaMode >= 2), "no blended group survived the resolve");
        Assert.True(mats.Any(m => m.DoubleSided), "no two-sided group survived the resolve");
    }

    /// <summary>
    /// Baked light is where a League map gets most of its look, and the prop path had no slot to put it
    /// in. Map30 binds 23 of its 24 groups' atlases. Map11's own base_srx carries none at all - that is
    /// Riot's data, not a regression (see the M33 finding that lightmaps are map-dependent), so this only
    /// asserts the UV set and atlas travel together.
    /// </summary>
    [Fact]
    public void ABakedLitMapArrivesWithBothItsAtlasAndItsUvSet()
    {
        if (Arena("Map30") is not { } scene) return;
        Assert.True(scene.LightmappedGroups > 0, "Map30 should arrive baked-lit");
        Assert.NotNull(scene.Background.Mesh.LightmapUvs);
        Assert.NotEmpty(scene.Background.Mesh.LightmapUvs!);
    }

    /// <summary>
    /// One floor per renderer. GL draws it as the backdrop; the D3D11 host has no backdrop channel and
    /// still draws the prop. Both at once in GL would double-draw the whole map.
    /// </summary>
    [Fact]
    public void TheFloorIsAPropOnlyForTheD3D11Host()
    {
        var src = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.Arena.cs");
        if (src.Length == 0) return;
        Assert.Contains("if (UseDx11Preview && _arena is { } arena)", src);
        Assert.Contains("SetArenaBackdrop(scene.Background)", src);
    }

    /// <summary>An arena owns the backdrop; the champion load path must not stream its NVR room over it.</summary>
    [Fact]
    public void TheChampionBackdropPathStandsAsideForAnArena()
    {
        var src = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (src.Length == 0) return;
        Assert.Contains("if (MeshPreview.ArenaOwnsBackdrop) return;", src);
    }

    /// <summary>
    /// The map viewport and the arena must build render state with the same code. MainWindowViewModel
    /// keeps a same-named forwarder, so a future edit to "its" copy cannot quietly diverge.
    /// </summary>
    [Fact]
    public void BothViewportsBuildRenderStateThroughTheOneConverter()
    {
        var vm = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (vm.Length == 0) return;
        Assert.Contains("Services.MapSubmeshResources.ToSubmeshMaterial(p, terrainWorldMaskTransform);", vm);
        // and the real thing lives in the service, exactly once
        var svc = Source("src", "ReyEngine.App", "Services", "MapSubmeshResources.cs");
        Assert.Contains("IsPbrLighting: p.IsPbrShader)", svc);
    }
}
