using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M573: ported materials and textures are named after what they are.
///
/// <para>They used to carry a 12-character content digest — <c>Decal_518d8b774d3e</c>,
/// <c>order_base_circle_tx_dm_d150d919beb1.tex</c> — which guaranteed uniqueness and made every name
/// unreadable. The digest still decides whether two source files are the SAME texture; it just no longer
/// ends up in the name.</para>
/// </summary>
public sealed class LegacyPortNamingTests
{
    private const string Legacy = @"K:\LeagueSandbox\League_Sandbox_Client\LEVELS\Map2";
    private const string Wad =
        @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";
    private const string GeoPath = "data/maps/mapgeometry/map453/jade_container.mapgeo";

    private static readonly Lazy<LegacyMapPortResult?> Port = new(() =>
    {
        if (!Directory.Exists(Legacy) || !File.Exists(Wad)) return null;
        using var wad = ReyEngine.Core.Wad.WadArchive.Open(Wad);
        ulong hash = HashAlgorithms.WadPath(GeoPath);
        if (!wad.TryGetEntry(hash, out _)) return null;
        return LegacyMapPorter.Port(Legacy, wad.Extract(hash), GeoPath);
    });

    /// <summary>A 12-hex-digit run is what the old digest looked like.</summary>
    private static bool LooksLikeADigest(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "[0-9a-f]{12}");

    [Fact]
    public void NoMaterialCarriesADigest()
    {
        if (Port.Value is not { } result) return;
        var offenders = result.Materials.Select(m => m.Name).Where(LooksLikeADigest).ToList();
        Assert.True(offenders.Count == 0, "digest still in: " + string.Join(", ", offenders.Take(5)));
    }

    [Fact]
    public void NoTextureCarriesADigest()
    {
        if (Port.Value is not { } result) return;
        var offenders = result.Textures.Select(t => t.TargetPath).Where(LooksLikeADigest).ToList();
        Assert.True(offenders.Count == 0, "digest still in: " + string.Join(", ", offenders.Take(5)));
    }

    [Fact]
    public void AMaterialIsNamedAfterItsRoleAndItsTexture()
    {
        if (Port.Value is not { } result) return;
        var decals = result.Materials.Where(m => m.Role == LegacyMaterialRole.Decal).ToList();
        if (decals.Count == 0) return;

        Assert.Contains(decals, m => m.Name.EndsWith("/Decal_order_seam", StringComparison.OrdinalIgnoreCase));
        // and the name matches the texture it actually draws
        foreach (var m in decals.Take(20))
        {
            string stem = Path.GetFileNameWithoutExtension(m.Samplers.Values.First());
            Assert.EndsWith(stem, m.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NamesAreStillUniqueAndNothingDangles()
    {
        // What the digest was there for. Uniqueness now comes from role + stem, with a numeric suffix
        // only where two genuinely collide - and a sampler pointing at a texture the port did not write
        // is the failure this would cause.
        if (Port.Value is not { } result) return;

        Assert.Equal(result.Materials.Count,
            result.Materials.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(result.Textures.Count,
            result.Textures.Select(t => t.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var produced = result.Textures.Select(t => t.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in result.Materials)
            foreach (string path in m.Samplers.Values)
                if (path.StartsWith("assets/maps/legacyimport/", StringComparison.OrdinalIgnoreCase))
                    Assert.True(produced.Contains(path), $"{m.Name} points at {path}, which was never written");
    }

    [Fact]
    public void TheSameMapPortsToTheSameNamesEveryTime()
    {
        // Collisions take a numeric suffix, and an unordered walk would hand it to a different one of the
        // pair each run - so a re-port would silently rename materials the user had edited.
        if (Port.Value is not { } first) return;
        using var wad = ReyEngine.Core.Wad.WadArchive.Open(Wad);
        var second = LegacyMapPorter.Port(Legacy, wad.Extract(HashAlgorithms.WadPath(GeoPath)), GeoPath);

        Assert.Equal(first.Materials.Select(m => m.Name).OrderBy(x => x, StringComparer.Ordinal),
                     second.Materials.Select(m => m.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(first.Textures.Select(t => t.TargetPath).OrderBy(x => x, StringComparer.Ordinal),
                     second.Textures.Select(t => t.TargetPath).OrderBy(x => x, StringComparer.Ordinal));
    }
}
