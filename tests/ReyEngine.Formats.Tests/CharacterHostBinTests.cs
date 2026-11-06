using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M857: the Character preview hands the open map's bin to the D3D11 scene. srx_banner_hero's
/// <c>skinMeshProperties.material</c> points into map11.bin, no dependency of the skin carries it, and
/// without a host the preview drew it as a 100% white stand-in.
/// </summary>
public sealed class CharacterHostBinTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string MapBin = "data/maps/shipping/map11/map11.bin";

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir is null ? "" : File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    /// <summary>Source guard: both places that prepare a character scene pass the host. This is the exact
    /// regression - the call used to end at <c>driverState: driverState)</c>.</summary>
    [Fact]
    public void The_character_preview_passes_a_host_bin_to_the_scene()
    {
        string src = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Characters.cs");
        if (src.Length == 0) return;
        Assert.Contains("hostBin: CharacterHostBin(binPath)", src);
        Assert.Contains("LoadHostMaterials(new[] { skin }, mapPath", src);
    }

    [Fact]
    public void Srx_banner_hero_draws_its_own_shader_only_with_the_map_bin_as_host()
    {
        string wadPath = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(wadPath)) { output.WriteLine("SKIPPED: Map11.wad.client is not installed"); return; }
        var db = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(db);
        using var wad = WadArchive.Open(wadPath, resolver);
        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) { output.WriteLine("SKIPPED: no shader cache"); return; }
        string? Name(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? WadP(ulong h) => db.TryGetPath(h, out var p) ? p : null;
        byte[]? Read(ulong h) => wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;

        var skinBin = Read(HashAlgorithms.WadPath("data/characters/srx_banner_hero/skins/skin0.bin"))!;
        var meshRef = SkinMeshExtractor.Extract(skinBin, WadP)!;
        var skn = Read(BinTexturePath.HashOfReference(meshRef.SimpleSkin!))!;
        var host = new LoadedBin(MapBin, SafeBinTree.Parse(Read(HashAlgorithms.WadPath(MapBin))!));

        PreparedCharacterScene? Prepare(LoadedBin? h) => Dx11CharacterScene.Prepare(
            skn, skinBin, cache, perms: null, readAsset: Read, resolveBinName: Name, resolveWadPath: WadP,
            fallbackShader: Dx11CharacterScene.DefaultCharacterShader, hostBin: h);

        var without = Prepare(null)!;
        var with = Prepare(host)!;
        output.WriteLine("without host: " + string.Join("; ", without.Slices.Select(s => $"{s.Submesh}->{s.Material} fallback={s.UsedFallbackShader}")));
        output.WriteLine("with host:    " + string.Join("; ", with.Slices.Select(s => $"{s.Submesh}->{s.Material} fallback={s.UsedFallbackShader} vs={s.VsDesc.ShaderName}")));

        Assert.Contains(without.Slices, s => s.UsedFallbackShader);                 // the bug: a stand-in
        Assert.NotEmpty(with.Slices);
        Assert.All(with.Slices, s => Assert.False(s.UsedFallbackShader));            // the fix: its own shader
        Assert.Contains(with.Slices, s => s.VsDesc.ShaderName.Contains("VertexWave", StringComparison.OrdinalIgnoreCase));
    }
}
