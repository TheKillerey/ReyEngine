using System.Text.Json;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M475: the shader-catalogue cache must not outlive the client it was read from.
///
/// <para>It previously validated only the game DIRECTORY, and a directory path is identical before and
/// after a Riot patch — so the cache was served forever. Measured on the reporter's install: a Live
/// catalogue written 2026-07-20 held 347 shaders while that client's shaders.bin declares 351, leaving
/// four shaders (including <c>Shaders/StaticMesh/4TextureBlend_UVBased_baseMat</c>) unpickable in every
/// dropdown with no in-app way to refresh. The PBE cache, written the same day, was complete — which is
/// what disguised a stale file as a per-shader problem.</para>
/// </summary>
public class ShaderCatalogCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rey_shcat_" + Guid.NewGuid().ToString("N"));

    public ShaderCatalogCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Write(ShaderCatalog c)
    {
        string p = Path.Combine(_dir, "cat.json");
        ShaderCatalogCache.Save(c, p);
        return p;
    }

    private static ShaderCatalog Catalog(string gameDir, string stamp, params string[] names) => new()
    {
        Environment = "Live",
        GameDirectory = gameDir,
        SourceStamp = stamp,
        Shaders = names.Select(n => new LeagueShaderDef(n, "StaticMesh", new(), new(), new())).ToList(),
    };

    [Fact]
    public void A_cache_matching_directory_and_stamp_is_served()
    {
        string p = Write(Catalog(@"C:\Game", "100:200", "Shaders/StaticMesh/A"));
        var back = ShaderCatalogCache.Load(p, @"C:\Game", "100:200");
        Assert.NotNull(back);
        Assert.Single(back!.Shaders);
    }

    /// <summary>THE BUG. Same install, patched client — the old check passed and served 347 shaders
    /// against a 351-shader game.</summary>
    [Fact]
    public void A_cache_from_a_different_build_of_the_same_install_is_rejected()
    {
        string p = Write(Catalog(@"C:\Game", "100:200", "Shaders/StaticMesh/A"));
        Assert.Null(ShaderCatalogCache.Load(p, @"C:\Game", "100:999"));
    }

    /// <summary>Caches written before the stamp existed carry an empty one; they must rebuild once rather
    /// than be trusted forever, which is what makes this fix self-healing for existing installs.</summary>
    [Fact]
    public void A_pre_M475_cache_without_a_stamp_is_rejected()
    {
        string p = Path.Combine(_dir, "old.json");
        File.WriteAllText(p, JsonSerializer.Serialize(new
        {
            Environment = "Live",
            GameDirectory = @"C:\Game",
            Shaders = new[] { new { Name = "Shaders/StaticMesh/A" } },
        }));

        Assert.Null(ShaderCatalogCache.Load(p, @"C:\Game", "100:200"));
    }

    [Fact]
    public void A_cache_from_another_install_is_still_rejected()
    {
        string p = Write(Catalog(@"C:\Game", "100:200", "Shaders/StaticMesh/A"));
        Assert.Null(ShaderCatalogCache.Load(p, @"D:\OtherGame", "100:200"));
    }

    /// <summary>An empty expected stamp disables the check — the explicit escape hatch for a caller that
    /// cannot identify a source file. Pinned so it stays a deliberate choice rather than something a
    /// future refactor reintroduces by passing "" everywhere.</summary>
    [Fact]
    public void An_empty_expected_stamp_skips_the_check()
    {
        string p = Write(Catalog(@"C:\Game", "100:200", "Shaders/StaticMesh/A"));
        Assert.NotNull(ShaderCatalogCache.Load(p, @"C:\Game", ""));
    }

    [Fact]
    public void An_empty_catalogue_is_never_served()
    {
        string p = Write(Catalog(@"C:\Game", "100:200"));
        Assert.Null(ShaderCatalogCache.Load(p, @"C:\Game", "100:200"));
    }

    /// <summary>The stamp has to CHANGE when the file changes, or the check above is decorative.</summary>
    [Fact]
    public void The_stamp_tracks_the_source_file()
    {
        string wad = Path.Combine(_dir, "Global.wad.client");
        File.WriteAllBytes(wad, new byte[16]);
        string first = ShaderCatalogLoader.StampFor(wad);
        Assert.NotEqual("", first);
        Assert.Equal(first, ShaderCatalogLoader.StampFor(wad));   // stable while untouched

        File.WriteAllBytes(wad, new byte[32]);                    // a patch rewrites it
        Assert.NotEqual(first, ShaderCatalogLoader.StampFor(wad));

        Assert.Equal("", ShaderCatalogLoader.StampFor(Path.Combine(_dir, "missing.wad.client")));
    }

    // ---- M855: hash-table identity ----

    private static ShaderCatalog WithUnresolved(params uint[] hashes) => new()
    {
        Environment = "Live",
        GameDirectory = @"C:\Game",
        SourceStamp = "100:200",
        UnresolvedObjects = hashes.ToList(),
        Shaders = new() { new LeagueShaderDef("Shaders/StaticMesh/A", "StaticMesh", new(), new(), new()) },
    };

    /// <summary>THE M855 BUG. PixelMatrix (0x907d17f5) was unnamed when the cache was written; the same
    /// Global.wad stamp then kept the 351-shader catalogue forever.</summary>
    [Fact]
    public void A_cache_with_unnamed_shaders_rebuilds_once_their_names_resolve()
    {
        string p = Write(WithUnresolved(0x907d17f5u));
        Func<uint, string?> better = h => h == 0x907d17f5u ? "Shaders/SkinnedMesh/PixelMatrix" : null;
        Assert.Null(ShaderCatalogCache.Load(p, @"C:\Game", "100:200", better));
    }

    [Fact]
    public void A_cache_with_unnamed_shaders_is_kept_while_they_stay_unnamed()
    {
        string p = Write(WithUnresolved(0x907d17f5u));
        Assert.NotNull(ShaderCatalogCache.Load(p, @"C:\Game", "100:200", _ => null));
    }

    [Fact]
    public void A_cache_that_never_recorded_its_unnamed_objects_rebuilds_once()
    {
        string p = Write(Catalog(@"C:\Game", "100:200", "Shaders/StaticMesh/A"));   // UnresolvedObjects == null
        Assert.Null(ShaderCatalogCache.Load(p, @"C:\Game", "100:200", _ => null));
    }

    [Fact]
    public void The_unnamed_list_survives_the_json_round_trip()
    {
        string p = Write(WithUnresolved(1u, 2u));
        var back = ShaderCatalogCache.Load(p, @"C:\Game", "100:200");
        Assert.Equal(new uint[] { 1, 2 }, back!.UnresolvedObjects);
    }
}
