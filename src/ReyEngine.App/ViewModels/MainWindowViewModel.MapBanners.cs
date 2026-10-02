using LeagueToolkit.Core.Meta;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M805: Summoner's Rift's esports sponsor banners, drawn while their event is on.
///
/// <para><b>The data.</b> A banner is a <c>GdsMapObject</c> carrying a <c>GDSMapObjectBannerInfo</c> (see
/// <see cref="MapBannerProp"/>): 117 in base_srx, every one gated by the MapObjectESportSponsorBanners event. Before M805
/// the editor read no GdsMapObject at all, so ticking that event (M802) changed nothing on screen. The object names no
/// mesh: it is a LevelProp of one of four characters (Srx_Banner_Hero / _Horizontal / _Vertical / _VerticalThin, all in
/// Map11.wad), and that character's Skin0 is drawn at the object's transform through the placed-prop path - the same
/// <see cref="PropRenderSet"/> both viewports already draw for MapAnimatedProps, Riot's shaders on D3D11 included.</para>
///
/// <para><b>The materials.</b> The banner skins link StaticMaterialDefs (Maps/Shipping/Map11/Esports/Materials/...) that
/// live in the map's own shipping bin, map11.bin, which the game has loaded with the map and which no dependency of the
/// skin bin names. The build parses that bin once and hands the materials to both material resolvers
/// (<see cref="LoadedBin"/>); every other prop resolves exactly as before.</para>
///
/// <para><b>Sponsor logos are not shown.</b> At runtime the skins' EsportsBannerMaterialController puts sponsor art on the
/// flag, chosen per league by map11.bin's EsportsRotatingBannerConfiguration - all of it under
/// assets/esports/sponsoredbanners/secret/, encrypted (M353). The flags show the texture their own material names
/// (srx_banner_flags.tex on Summoner's Rift), and the tooltip and the log say so.</para>
///
/// <para><b>Cost.</b> Nothing is decoded on map open: the four skins are decoded once per map, off the UI thread, the
/// first time an event shows a banner. Ticking and unticking afterwards only republishes the prop set. Read-only: the
/// banners are not listed for editing, carry no gizmo and are never written. The other LevelProp GdsMapObjects (snails,
/// gromp props, lizards ...) are drawn with Props on since M806 (MainWindowViewModel.LevelProps.cs).</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The open map's banners as its materials bin places them; empty on every map without one.</summary>
    private IReadOnlyList<MapBannerProp> _mapBanners = Array.Empty<MapBannerProp>();

    /// <summary>The banners decoded into prop instances - one per banner that resolved - parallel to
    /// <see cref="_bannerInstanceOwners"/>. Empty until the first build for this map has finished.</summary>
    private IReadOnlyList<PropInstanceData> _bannerInstances = Array.Empty<PropInstanceData>();
    private IReadOnlyList<MapBannerProp> _bannerInstanceOwners = Array.Empty<MapBannerProp>();

    /// <summary>The banner instances the last published prop set carried, to republish only when that changes.</summary>
    private IReadOnlyList<PropInstanceData> _publishedBanners = Array.Empty<PropInstanceData>();

    /// <summary>The decode for <see cref="_mapBanners"/>, started the first time a banner is shown; null before.</summary>
    private Task? _bannerBuild;

    /// <summary>A map opened (the load extracts its banners), a map tab returned, or the viewport was cleared (null).
    /// Decodes nothing: that waits for an event to show a banner.</summary>
    private void SetMapBanners(IReadOnlyList<MapBannerProp>? banners)
    {
        _mapBanners = banners ?? Array.Empty<MapBannerProp>();
        _bannerInstances = Array.Empty<PropInstanceData>();
        _bannerInstanceOwners = Array.Empty<MapBannerProp>();
        _bannerBuild = null;   // a build still running for the previous map drops its result (see BuildMapBannersAsync)
    }

    /// <summary>Does the open map show this banner now? Only while the event that gates it is on - the same event gate
    /// as a particle's sound (<see cref="EventAllows"/>). Shipped banners all name the MapObjectESportSponsorBanners
    /// event, so in a normal game - every event off - none shows. Without the map's controllers (a load still under
    /// way) a gated banner is not shown: what its event says is not known yet.</summary>
    private bool BannerShown(MapBannerProp banner) =>
        banner.VisibilityControllerHash == 0
        || (_mapControllers is not null && EventAllows(banner.VisibilityControllerHash));

    /// <summary>The decoded banner instances the events show now.</summary>
    private List<PropInstanceData> ShownBannerInstances()
    {
        var shown = new List<PropInstanceData>();
        for (int i = 0; i < _bannerInstances.Count && i < _bannerInstanceOwners.Count; i++)
            if (BannerShown(_bannerInstanceOwners[i])) shown.Add(_bannerInstances[i]);
        return shown;
    }

    /// <summary>Called from <see cref="ApplyMapVisibility"/>, so it runs on every event toggle, map load and tab return:
    /// start the decode the first time a banner is shown, otherwise republish the prop set when the banners it should
    /// carry differ from the ones it does (which also drops a previous map's banners). A no-op on every map without
    /// banners that never had one published.</summary>
    private void RefreshMapBanners()
    {
        if (_bannerBuild is null && _mapBanners.Any(BannerShown))
        {
            _bannerBuild = BuildMapBannersAsync();
            return;
        }
        if (_bannerBuild is { IsCompleted: false }) return;   // its completion publishes what is shown by then
        if (!ShownBannerInstances().SequenceEqual(_publishedBanners, ReferenceEqualityComparer.Instance))
            PublishAddedMeshPreview();
    }

    /// <summary>Decode the open map's banners off the UI thread, then publish them with the props.</summary>
    private async Task BuildMapBannersAsync()
    {
        var banners = _mapBanners;
        string? mapPath = _currentMapEntry?.Path;
        BannerBuild built;
        try { built = await Task.Run(() => BuildBannerInstances(banners, mapPath)); }
        catch (Exception ex) { _log.Warn("Props", "Esports banners could not be decoded: " + ex.Message); return; }
        if (!ReferenceEquals(banners, _mapBanners)) return;   // another map opened (or the viewport cleared) meanwhile
        _bannerInstances = built.Instances;
        _bannerInstanceOwners = built.Owners;
        _log.Info("Props", built.Summary);
        PublishAddedMeshPreview();
    }

    private sealed record BannerBuild(IReadOnlyList<PropInstanceData> Instances, IReadOnlyList<MapBannerProp> Owners, string Summary);

    /// <summary>One prop mesh per banner character (shared by all of its placements), one instance per banner. Runs off
    /// the UI thread.</summary>
    private BannerBuild BuildBannerInstances(IReadOnlyList<MapBannerProp> banners, string? mapPath)
    {
        var host = LoadHostMaterials(banners.Select(b => b.Skin), mapPath, out string hostNote);
        var meshBySkin = new Dictionary<string, PropMesh?>(StringComparer.OrdinalIgnoreCase);
        var texCache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        var instances = new List<PropInstanceData>();
        var owners = new List<MapBannerProp>();
        int unresolved = 0;
        foreach (var banner in banners)
        {
            if (banner.Skin.Length == 0) { unresolved++; continue; }
            if (!meshBySkin.TryGetValue(banner.Skin, out var mesh))
                meshBySkin[banner.Skin] = mesh = TryBuildPropMesh(banner.Skin, texCache, clip: null, hostBin: host);
            if (mesh is null) { unresolved++; continue; }
            instances.Add(PropInstanceData.Place(mesh, banner.Transform));
            owners.Add(banner);
        }

        // what the flags and holders were drawn with, named for the log
        var textures = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in meshBySkin.Values)
        {
            if (mesh?.SkinBinBytes is not { } skinBin) continue;
            var resolved = ChampionMaterialResolver.Resolve(skinBin, ResolveBinName, ResolveWadPath, ReadAssetByPath, host);
            foreach (var path in resolved.SubmeshDiffuse.Values.Append(resolved.DefaultDiffuse ?? ""))
                if (!string.IsNullOrEmpty(path)) textures.Add(path[(path.LastIndexOf('/') + 1)..]);
        }
        var drawn = meshBySkin.Where(kv => kv.Value is not null).Select(kv => kv.Key.Split('/')[1]).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        string summary = $"Esports banners: {instances.Count} of {banners.Count} drawn as their characters' Skin0"
            + (drawn.Count > 0 ? $" ({string.Join(", ", drawn)})" : "")
            + (unresolved > 0 ? $"; {unresolved} could not be resolved and are not drawn" : "") + ". "
            + $"Textures: {(textures.Count > 0 ? string.Join(", ", textures) : "none resolved")}{hostNote}. "
            + "Sponsor logos are not shown: the game fills each banner at runtime with sponsor art chosen per league "
            + "(EsportsRotatingBannerConfiguration), which ships encrypted under assets/esports/sponsoredbanners/secret/ - "
            + "so each flag shows the texture its own material names.";
        return new BannerBuild(instances, owners, summary);
    }

    /// <summary>The StaticMaterialDefs <paramref name="skins"/> link from the map's shipping bin (map11.bin), as a small
    /// <see cref="LoadedBin"/> holding only those - the whole bin is about 4.6 MB and is parsed once, here, and dropped.
    /// Null when the map names no shipping bin or the skins link nothing in it; <paramref name="note"/> then says so.
    /// M806: shared with the level props (MainWindowViewModel.LevelProps.cs), whose shipped skins link nothing there.</summary>
    private LoadedBin? LoadHostMaterials(IEnumerable<string> skins, string? mapPath, out string note)
    {
        note = "";
        string? binPath = mapPath is null ? null : MapBinPathFor(mapPath);
        if (binPath is null || ReadAssetByPath(binPath) is not { } bytes)
        {
            note = $" (the map's shipping bin {binPath ?? "(none for this map path)"} was not found, so materials only it holds could not be resolved)";
            return null;
        }
        LoadedBin whole;
        try { whole = new LoadedBin(binPath, SafeBinTree.Parse(bytes)); }
        catch (Exception ex) { note = $" ({binPath} would not parse: {ex.Message})"; return null; }

        // exactly the materials the skins resolve to there - found by the same rule the resolvers use
        var keep = new Dictionary<uint, BinTreeObject>();
        foreach (string skin in skins.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ReadAssetByPath("data/" + skin.ToLowerInvariant() + ".bin") is not { } skinBin) continue;
            try
            {
                foreach (var m in MaterialDocument.Parse(skinBin, ResolveBinName, ResolveWadPath, ReadAssetByPath, whole).Materials)
                    if (m.LinkedFromBin == binPath && whole.Tree.Objects.TryGetValue(m.ObjectPathHash, out var obj))
                        keep[m.ObjectPathHash] = obj;
            }
            catch { /* a skin that will not parse resolves nothing; its placements are counted as unresolved */ }
        }
        if (keep.Count == 0) { note = $" (no material from {binPath} needed)"; return null; }
        note = $" ({keep.Count} material(s) from {binPath})";
        return new LoadedBin(binPath, new BinTree(keep.Values.ToList(), Array.Empty<string>()));
    }

    /// <summary>The tooltip's sentence about the banners an event gates.</summary>
    private string DescribeMapBanners(int gated)
    {
        var characters = _mapBanners.Where(b => b.CharacterName.Length > 0).Select(b => b.CharacterName)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return $" The {gated} banner prop(s) are drawn as their characters' Skin0 ({string.Join(", ", characters)}), read-only. "
            + "Their sponsor logos are not: the game picks them at runtime from encrypted esports art, so each flag shows the "
            + "texture its own material names.";
    }
}
