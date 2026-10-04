using System.IO.Hashing;
using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M819: the project-agnostic half of the GameData preview - what <see cref="AssetMountService"/> does with an overlay, the order a project of layers shadows its folders in, the mod's own copies read as an
/// overlay base, the map thumbnail's key, and the container a map skin names. None of it needs the game.
/// </summary>
public sealed class LtkPreviewCoreTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>An overlay with what a test says it serves and when it is ready.</summary>
    private sealed class FakeOverlay : IAssetOverlay
    {
        public readonly Dictionary<ulong, byte[]> Served = new();
        public readonly Dictionary<ulong, string> Names = new();
        public readonly HashSet<ulong> Targets = new();
        public bool Ready;
        public bool Planned = true;
        public string Name => "LTK GameData";
        public bool IsPending => !Ready;
        public bool TargetsKnown => Planned;

        public IReadOnlyList<AssetOverlayEntry> Entries =>
            Ready ? Served.Select(p => new AssetOverlayEntry(p.Key, Names.GetValueOrDefault(p.Key), p.Value.Length, "id:" + p.Value.Length)).ToList() : Array.Empty<AssetOverlayEntry>();

        public bool TryRead(ulong pathHash, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            return Ready && Served.TryGetValue(pathHash, out bytes!);
        }

        public bool IsTarget(ulong pathHash) => Targets.Contains(pathHash) || Served.ContainsKey(pathHash);
    }

    private string Folder(string name, params (string Rel, string Text)[] files)
    {
        string root = _temp.Combine(name);
        foreach (var (rel, text) in files)
        {
            string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Directory.CreateDirectory(root);
        return root;
    }

    private static ulong H(string path) => HashAlgorithms.WadPath(path);

    private static string Text(byte[]? bytes) => Encoding.UTF8.GetString(bytes ?? throw new InvalidOperationException("no bytes"));

    // ================================================================================================ the service

    [Fact]
    public void A_service_with_no_overlay_is_what_it_always_was()
    {
        var service = new AssetMountService();
        service.Add(new FolderMount(Folder("one", ("data/a.bin", "from one"), ("data/only-one.bin", "o")), null, "one"));
        service.Add(new FolderMount(Folder("two", ("data/a.bin", "from two")), null, "two"));
        service.Rebuild();

        Assert.Null(service.Overlay);
        Assert.Equal(2, service.Count);
        Assert.Equal("from one", Text(service.Read(H("data/a.bin"))));          // first added wins
        Assert.Equal(2, service.SourcesOf(H("data/a.bin")).Count);
        Assert.Equal("from one", Text(service.ReadRaw(H("data/a.bin"))));       // with no overlay, raw is the read
        Assert.False(service.IsOverlaid(H("data/a.bin")));
        Assert.False(service.IsOverlayTarget(H("data/a.bin")));
        Assert.Null(service.OverlayIdentityOf(H("data/a.bin")));
        Assert.Equal(new[] { "one", "two" }, service.Mounts.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void An_overlay_that_is_not_ready_serves_nothing_and_a_refresh_once_it_is_serves_what_it_has()
    {
        var service = new AssetMountService();
        service.Add(new FolderMount(Folder("p", ("data/a.bin", "project copy")), null, "p"));
        var overlay = new FakeOverlay();
        overlay.Served[H("data/a.bin")] = Encoding.UTF8.GetBytes("overlaid");
        service.SetOverlay(overlay);
        service.Rebuild();

        // pending: what the mounts hold answers, and nothing is listed as served
        Assert.Equal("project copy", Text(service.Read(H("data/a.bin"))));
        Assert.False(service.IsOverlaid(H("data/a.bin")));

        overlay.Ready = true;
        service.RefreshOverlay();

        Assert.Equal("overlaid", Text(service.Read(H("data/a.bin"))));
        Assert.True(service.IsOverlaid(H("data/a.bin")));
    }

    [Fact]
    public void A_served_chunk_a_mount_holds_has_the_overlay_as_its_source_and_the_mount_shadowed_and_raw_reads_the_mount()
    {
        var service = new AssetMountService();
        var mount = new FolderMount(Folder("p", ("data/a.bin", "project copy")), null, "p");
        service.Add(mount);
        var overlay = new FakeOverlay { Ready = true };
        overlay.Served[H("data/a.bin")] = Encoding.UTF8.GetBytes("overlaid");
        service.SetOverlay(overlay);
        service.Rebuild();

        Assert.True(service.TryGet(H("data/a.bin"), out var asset));
        Assert.Equal(AssetSourceKind.LtkGameData, asset.SourceKind);
        Assert.False(asset.IsEditable);
        Assert.False(asset.HasConflict);                                         // the overlay shadows the copy under it by design: that is not a conflict between mounts
        Assert.Equal(new[] { AssetSourceKind.LtkGameData, AssetSourceKind.ProjectFolder }, asset.AllSources.Select(s => s.Kind).ToArray());
        Assert.Equal("data/a.bin", asset.VirtualPath);                           // the name the mounts resolved
        Assert.Equal("overlaid".Length, asset.Size);
        Assert.Equal("overlaid", Text(service.Read(H("data/a.bin"))));
        Assert.Equal("project copy", Text(service.ReadRaw(H("data/a.bin"))));    // never the overlay's
        Assert.Equal("id:8", service.OverlayIdentityOf(H("data/a.bin")));
        Assert.True(asset.ToEntry().ReadOnly);
        // the mount's own asset object, which a reader may hold, is not changed
        Assert.Equal(AssetSourceKind.ProjectFolder, mount.Get(H("data/a.bin"))!.SourceKind);
        Assert.DoesNotContain(service.Mounts, m => m.Kind == AssetSourceKind.LtkGameData);   // never in Mounts: what walks the project's mounts never meets it
    }

    [Fact]
    public void A_served_chunk_no_mount_holds_is_listed_and_readable_and_read_only()
    {
        var service = new AssetMountService();
        service.Add(new FolderMount(Folder("p", ("data/other.bin", "x")), null, "p"));
        var overlay = new FakeOverlay { Ready = true };
        overlay.Served[H("data/characters/x/skins/skin0.bin")] = Encoding.UTF8.GetBytes("only the game has it");
        service.SetOverlay(overlay);
        service.Rebuild();

        Assert.Equal(2, service.Count);
        Assert.True(service.Has(H("data/characters/x/skins/skin0.bin")));
        Assert.Contains(service.Assets, a => a.PathHash == H("data/characters/x/skins/skin0.bin") && a.SourceKind == AssetSourceKind.LtkGameData);
        Assert.Equal("only the game has it", Text(service.Read(H("data/characters/x/skins/skin0.bin"))));
        // a chunk only the game holds has no raw copy in the mounts, and the overlay's result is not it
        Assert.Null(service.ReadRaw(H("data/characters/x/skins/skin0.bin")));
        Assert.False(service.TryGetFilePath(H("data/characters/x/skins/skin0.bin"), out _, out _));
    }

    [Fact]
    public void A_chunk_the_overlay_names_but_does_not_serve_is_a_target_and_is_read_as_ever()
    {
        var service = new AssetMountService();
        service.Add(new FolderMount(Folder("p", ("data/a.bin", "project copy")), null, "p"));
        var overlay = new FakeOverlay { Ready = true };
        overlay.Targets.Add(H("data/a.bin"));
        service.SetOverlay(overlay);
        service.Rebuild();

        Assert.True(service.IsOverlayTarget(H("data/a.bin")));
        Assert.False(service.IsOverlaid(H("data/a.bin")));
        Assert.Equal("project copy", Text(service.Read(H("data/a.bin"))));
    }

    [Fact]
    public void A_rebuild_keeps_the_overlay_and_merges_it_again_and_Clear_lets_it_go()
    {
        var service = new AssetMountService();
        service.Add(new FolderMount(Folder("p", ("data/a.bin", "project copy")), null, "p"));
        var overlay = new FakeOverlay { Ready = true };
        overlay.Served[H("data/a.bin")] = Encoding.UTF8.GetBytes("overlaid");
        service.SetOverlay(overlay);
        service.Rebuild();

        service.Rebuild();                                     // Sync Hashes rebuilds the live service in place

        Assert.Equal("overlaid", Text(service.Read(H("data/a.bin"))));
        service.Clear();
        Assert.Null(service.Overlay);
        Assert.Equal(0, service.Count);
    }

    [Fact]
    public void A_name_the_dictionary_knows_is_the_listed_name_and_the_overlays_own_names_a_chunk_nothing_else_can()
    {
        var db = new HashDatabase();
        db.AddWad(H("data/known.bin"), "data/known.bin");
        var service = new AssetMountService();
        var overlay = new FakeOverlay { Ready = true };
        overlay.Served[H("data/known.bin")] = new byte[] { 1 };
        overlay.Served[H("data/unknown.bin")] = new byte[] { 2 };
        service.SetOverlay(overlay, new WadPathResolver(db));
        service.Rebuild();

        Assert.True(service.TryGet(H("data/known.bin"), out var known));
        Assert.True(known.IsResolved);
        Assert.Equal("data/known.bin", known.VirtualPath);
        Assert.True(service.TryGet(H("data/unknown.bin"), out var unknown));
        Assert.False(unknown.IsResolved);
        Assert.Equal($"0x{H("data/unknown.bin"):x16}.bin", unknown.VirtualPath);
    }

    [Fact]
    public void Of_two_mounts_of_one_kind_the_lower_rank_wins_whatever_order_they_were_added_in_and_Mounts_keeps_that_order()
    {
        var service = new AssetMountService();
        service.Add(new FolderMount(Folder("base", ("data/a.bin", "base"), ("data/b.bin", "base")), null, "base"), 0);
        service.Add(new FolderMount(Folder("layer", ("data/a.bin", "layer")), null, "layer"), -1);
        service.Rebuild();

        Assert.Equal("layer", Text(service.Read(H("data/a.bin"))));
        Assert.Equal("base", Text(service.Read(H("data/b.bin"))));
        Assert.Equal(new[] { "layer", "base" }, service.SourcesOf(H("data/a.bin")).Select(m => m.Name).ToArray());
        Assert.Equal(new[] { "base", "layer" }, service.Mounts.Select(m => m.Name).ToArray());   // the browser lists them as they were added
    }

    [Fact]
    public void A_rank_orders_only_the_mounts_of_one_kind()
    {
        // overrides still outrank folders, and a folder still outranks a project WAD, whatever rank they are given
        var service = new AssetMountService();
        service.Add(new FolderMount(Folder("folder", ("data/a.bin", "folder")), null, "folder"), int.MinValue);
        service.Add(new OverrideMount(Folder("ov"), null), 5);
        service.Rebuild();
        Assert.Equal("folder", Text(service.Read(H("data/a.bin"))));
        Assert.Equal(AssetSourceKind.ProjectOverride, service.Mounts[1].Kind);
    }

    // ================================================================================================ the order a project of layers shadows its folders in

    private static ReyProject Project(params string[] folders)
    {
        var project = new ReyProject { Name = "p", RootPath = Path.Combine(Path.GetTempPath(), "never-read") };
        project.ProjectFolders.AddRange(folders);
        return project;
    }

    [Fact]
    public void A_project_that_has_no_layers_ranks_every_folder_alike_in_the_order_it_lists_them_even_one_called_RAW()
    {
        var project = Project("Map11", "RAW", "Champions/Aatrox");

        var ranks = ProjectMountOrder.Rank(project);

        Assert.Equal(new[] { "Map11", "RAW", "Champions/Aatrox" }, ranks.Select(r => r.Entry).ToArray());
        Assert.All(ranks, r => { Assert.Equal(0, r.Precedence); Assert.Equal("base", r.Layer); Assert.False(r.IsRaw); });
    }

    [Fact]
    public void A_project_of_layers_ranks_the_layer_applied_last_first_and_RAW_above_every_layer()
    {
        var project = Project("Map11", "layers/b/Map11", "layers/a/Map11", "layers/z10/Map11", "layers/z9/Map11", "RAW");
        project.Layers.Add(new ProjectLayer { Name = "a", Priority = 2, Folders = { "layers/a/Map11" } });
        project.Layers.Add(new ProjectLayer { Name = "b", Priority = 1, Folders = { "layers/b/Map11" } });
        project.Layers.Add(new ProjectLayer { Name = "z10", Priority = 3, Folders = { "layers/z10/Map11" } });
        project.Layers.Add(new ProjectLayer { Name = "z9", Priority = 3, Folders = { "layers/z9/Map11" } });

        var ranks = ProjectMountOrder.Rank(project).ToDictionary(r => r.Entry);

        // apply order: base, b (1), a (2), z9 (3, natural order before z10), z10 (3)
        Assert.Equal(0, ranks["Map11"].Precedence);
        Assert.Equal(-1, ranks["layers/b/Map11"].Precedence);
        Assert.Equal(-2, ranks["layers/a/Map11"].Precedence);
        Assert.Equal(-3, ranks["layers/z9/Map11"].Precedence);
        Assert.Equal(-4, ranks["layers/z10/Map11"].Precedence);
        Assert.Equal(int.MinValue, ranks["RAW"].Precedence);
        Assert.True(ranks["RAW"].IsRaw);
        Assert.Equal("a", ranks["layers/a/Map11"].Layer);
    }

    [Fact]
    public void A_layer_that_claims_a_folder_by_its_leaf_ranks_it_as_it_ranks_the_layer()
    {
        // M744's way: a plain name is the leaf of every folder that ends in it
        var project = Project("Map11", "Champions/Jinx");
        project.Layers.Add(new ProjectLayer { Name = "fix", Priority = 1, Folders = { "Jinx" } });

        var ranks = ProjectMountOrder.Rank(project).ToDictionary(r => r.Entry);

        Assert.Equal(0, ranks["Map11"].Precedence);
        Assert.Equal(-1, ranks["Champions/Jinx"].Precedence);
        Assert.Equal("fix", ranks["Champions/Jinx"].Layer);
    }

    [Fact]
    public void A_layer_the_project_does_not_list_is_the_base_layer_and_a_base_named_layer_is_applied_first()
    {
        var project = Project("Map11", "Other");
        project.Layers.Add(new ProjectLayer { Name = "base", Priority = 9, Folders = { "Other" } });   // base is first whatever it is given

        var ranks = ProjectMountOrder.Rank(project);

        Assert.All(ranks, r => Assert.Equal(0, r.Precedence));
    }

    [Fact]
    public void HasGameData_asks_the_disk_only_of_a_project_that_has_layers_and_says_so_only_for_a_stored_document()
    {
        var plain = new ReyProject { RootPath = _temp.Path };
        Assert.False(LtkProjectStore.HasGameData(plain));

        var layered = new ReyProject { RootPath = _temp.Path };
        layered.Layers.Add(new ProjectLayer { Name = "fix", Priority = 1 });                       // no key
        Assert.False(LtkProjectStore.HasGameData(layered));
        layered.Layers[0].DeclarationsKey = "fix";                                                 // a key and no file
        Assert.False(LtkProjectStore.HasGameData(layered));
        LtkProjectStore.WriteDeclarations(_temp.Path, "fix", "{\"version\":1,\"modules\":[]}");
        Assert.True(LtkProjectStore.HasGameData(layered));
        layered.Layers[0].DeclarationsKey = "..\\..\\evil";                                         // a key that is no folder name of ours names nothing
        Assert.False(LtkProjectStore.HasGameData(layered));
    }

    // ================================================================================================ the mod's own copies, as a base

    [Fact]
    public void The_mods_own_copy_of_a_chunk_is_read_from_the_mounts_of_its_layer_in_the_order_they_win_in_and_RAW_apart()
    {
        var first = new FolderMount(Folder("f1", ("data/a.bin", "first of base"), ("data/only-first.bin", "1")), null, "f1");
        var second = new FolderMount(Folder("f2", ("data/a.bin", "second of base")), null, "f2");
        var layer = new FolderMount(Folder("l", ("data/a.bin", "layer copy")), null, "l");
        var raw = new FolderMount(Folder("raw", ("data/a.bin", "raw copy")), null, "RAW");
        var files = new MountModFiles(
            new Dictionary<string, IReadOnlyList<IAssetMount>> { ["base"] = new IAssetMount[] { first, second }, ["fix"] = new IAssetMount[] { layer } },
            new IAssetMount[] { raw });

        Assert.Equal("first of base", Text(files.ReadLayerFile("base", H("data/a.bin"), 1000)));
        Assert.Equal("layer copy", Text(files.ReadLayerFile("fix", H("data/a.bin"), 1000)));
        Assert.Equal("raw copy", Text(files.ReadRawFile(H("data/a.bin"), 1000)));
        Assert.Equal("1", Text(files.ReadLayerFile("base", H("data/only-first.bin"), 1000)));
        Assert.Null(files.ReadLayerFile("fix", H("data/only-first.bin"), 1000));            // a layer that does not hold it answers none
        Assert.Null(files.ReadLayerFile("nobody", H("data/a.bin"), 1000));                   // and a layer nothing was mounted for
        Assert.Null(files.ReadRawFile(H("data/only-first.bin"), 1000));
    }

    [Fact]
    public void A_copy_larger_than_the_overlay_reads_is_refused_before_a_byte_of_it_is_read()
    {
        var mount = new FolderMount(Folder("f", ("data/a.bin", new string('x', 500))), null, "f");
        var files = new MountModFiles(new Dictionary<string, IReadOnlyList<IAssetMount>> { ["base"] = new IAssetMount[] { mount } });

        var ex = Assert.Throws<IOException>(() => files.ReadLayerFile("base", H("data/a.bin"), 100));

        Assert.Contains("more than the 100 the overlay reads", ex.Message);
    }

    // ================================================================================================ the map thumbnail's key

    private static string KeyBeforeM819(string appVersion, string state, string mapPath, string mapIdentity, string materialsPath, string materialsIdentity, string shippingIdentity, string shaderCacheIdentity)
    {
        // the text M807 hashed, written out again: a project without GameData must keep every key - and so every cached picture - it had
        var text = new StringBuilder()
            .Append("renderer:").Append(MapThumbnailKey.RendererVersion).Append('\n')
            .Append("size:").Append(MapThumbnailKey.RenderWidth).Append('x').Append(MapThumbnailKey.RenderHeight).Append('>')
            .Append(MapThumbnailKey.ThumbWidth).Append('x').Append(MapThumbnailKey.ThumbHeight).Append('\n')
            .Append("app:").Append(appVersion).Append('\n')
            .Append("state:").Append(state).Append('\n')
            .Append("map:").Append(mapPath.ToLowerInvariant()).Append('|').Append(mapIdentity).Append('\n')
            .Append("materials:").Append(materialsPath.ToLowerInvariant()).Append('|').Append(materialsIdentity).Append('\n')
            .Append("shipping:").Append(shippingIdentity).Append('\n')
            .Append("shadercache:").Append(shaderCacheIdentity)
            .ToString();
        return Convert.ToHexString(XxHash128.Hash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    [Fact]
    public void The_map_thumbnail_key_of_a_project_without_GameData_is_the_key_it_always_was_and_the_documents_fingerprint_changes_it()
    {
        string old = KeyBeforeM819("0.5.1", "start", "Data/Maps/MapGeometry/Map11/base_srx.mapgeo", "file:1:2", "data/maps/mapgeometry/map11/base_srx.materials.bin", "file:3:4", "file:5:6", "file:7:8");

        string none = MapThumbnailKey.Compute("0.5.1", "start", "Data/Maps/MapGeometry/Map11/base_srx.mapgeo", "file:1:2", "data/maps/mapgeometry/map11/base_srx.materials.bin", "file:3:4", "file:5:6", "file:7:8");
        string withDocuments = MapThumbnailKey.Compute("0.5.1", "start", "Data/Maps/MapGeometry/Map11/base_srx.mapgeo", "file:1:2", "data/maps/mapgeometry/map11/base_srx.materials.bin", "file:3:4", "file:5:6", "file:7:8", "00000000000000aa");
        string otherDocuments = MapThumbnailKey.Compute("0.5.1", "start", "Data/Maps/MapGeometry/Map11/base_srx.mapgeo", "file:1:2", "data/maps/mapgeometry/map11/base_srx.materials.bin", "file:3:4", "file:5:6", "file:7:8", "00000000000000ab");

        Assert.Equal(old, none);
        Assert.NotEqual(old, withDocuments);
        Assert.NotEqual(withDocuments, otherDocuments);
        Assert.Equal(withDocuments, MapThumbnailKey.Compute("0.5.1", "start", "Data/Maps/MapGeometry/Map11/base_srx.mapgeo", "file:1:2", "data/maps/mapgeometry/map11/base_srx.materials.bin", "file:3:4", "file:5:6", "file:7:8", "00000000000000aa"));
    }

    // ================================================================================================ the container a map skin names

    private static uint Hash(string s) => HashAlgorithms.Fnv1a(s);

    private static byte[] ShippingBin(params (string Skin, string? Link)[] skins)
    {
        var objects = skins.Select(s =>
        {
            var props = new List<BinTreeProperty> { new BinTreeString(Hash("name"), s.Skin) };
            if (s.Link is not null) props.Add(new BinTreeString(Hash("mMapContainerLink"), s.Link));
            return new BinTreeObject(Hash("Maps/Shipping/Map11/MapSkins/" + s.Skin), Hash("MapSkin"), props);
        }).ToArray();
        using var ms = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    [Fact]
    public void The_container_the_game_loads_is_the_one_the_Default_skin_names_and_its_geometry_and_bin_are_beside_each_other()
    {
        var bytes = ShippingBin(("Milkshake_SRS", "Maps/MapGeometry/Map11/Milkshake_SRS"), ("Default", "Maps/MapGeometry/Map11/Milkshake_SRS"), ("Other", "Maps/MapGeometry/Map11/Base_SRX"));

        var container = MapGameContainer.Resolve(bytes);

        Assert.NotNull(container);
        Assert.Equal("Default", container!.SkinName);
        Assert.Equal("Maps/MapGeometry/Map11/Milkshake_SRS", container.ContainerLink);
        Assert.Equal("data/Maps/MapGeometry/Map11/Milkshake_SRS.mapgeo", container.GeometryPath);
        Assert.Equal("data/Maps/MapGeometry/Map11/Milkshake_SRS.materials.bin", container.MaterialsBinPath);
        Assert.Equal("Milkshake_SRS", container.Stem);
        Assert.True(container.IsGeometry("data/maps/mapgeometry/map11/milkshake_srs.mapgeo"));    // the casing of the path is not the file
        Assert.False(container.IsGeometry("data/maps/mapgeometry/map11/base_srx.mapgeo"));
    }

    [Fact]
    public void A_bin_with_no_Default_skin_or_one_with_no_container_names_no_container()
    {
        Assert.Null(MapGameContainer.Resolve(ShippingBin(("Milkshake_SRS", "Maps/MapGeometry/Map11/Milkshake_SRS"))));
        Assert.Null(MapGameContainer.Resolve(ShippingBin(("Default", null))));
        Assert.Null(MapGameContainer.Resolve(Array.Empty<byte>()));                                // a bin that does not parse is no container, not an exception
    }

    [Fact]
    public void A_shipping_map_bin_is_told_by_its_path()
    {
        Assert.True(GameDataPreview.IsShippingMapBin("data/maps/shipping/map11/map11.bin"));
        Assert.True(GameDataPreview.IsShippingMapBin("DATA\\Maps\\Shipping\\Map12\\Map12.bin"));
        Assert.False(GameDataPreview.IsShippingMapBin("data/maps/shipping/map11/map12.bin"));
        Assert.False(GameDataPreview.IsShippingMapBin("data/maps/mapgeometry/map11/milkshake_srs.materials.bin"));
        Assert.False(GameDataPreview.IsShippingMapBin("data/maps/shipping/map/map.bin"));
    }

    // ================================================================================================ what the overlay is asked while it is working

    [Fact]
    public void A_read_made_while_the_overlay_is_pending_is_told_to_the_owner_and_one_made_when_it_is_ready_or_quietly_or_raw_is_not()
    {
        var service = new AssetMountService();
        service.Add(new FolderMount(Folder("p", ("data/a.bin", "project copy")), null, "p"));
        var overlay = new FakeOverlay();                                          // pending
        service.SetOverlay(overlay);
        service.Rebuild();
        var told = new List<ulong>();
        service.ReadWhileOverlayPending = told.Add;
        ulong a = H("data/a.bin");

        service.Read(a);
        Assert.Equal(new[] { a }, told.ToArray());                                // answered from the mounts, which is not final: the owner is told

        service.ReadRaw(a);
        service.ReadFallback(a);
        Assert.Single(told);                                                      // raw readers never meant the overlay

        using (AssetMountService.QuietReads()) service.Read(a);
        Assert.Single(told);                                                      // a reader whose product is made again when the overlay is ready says so

        Assert.True(Task.Run(() => service.Read(a)).Wait(TimeSpan.FromSeconds(60)));   // the scope is the thread's own
        Assert.Equal(2, told.Count);

        overlay.Ready = true;
        service.RefreshOverlay();
        service.Read(a);
        Assert.Equal(2, told.Count);                                              // ready: the read is final

        // and a service that has no hook, or no overlay, asks nothing of anybody
        var plain = new AssetMountService();
        plain.Add(new FolderMount(Folder("q", ("data/a.bin", "q")), null, "q"));
        plain.Rebuild();
        plain.ReadWhileOverlayPending = told.Add;
        plain.Read(a);
        Assert.Equal(2, told.Count);
    }

    [Fact]
    public void Whether_the_targets_are_known_is_the_overlays_to_say_and_a_service_with_none_has_nothing_unknown()
    {
        var service = new AssetMountService();
        Assert.True(service.OverlayTargetsKnown);

        var overlay = new FakeOverlay { Planned = false };
        service.SetOverlay(overlay);
        Assert.False(service.OverlayTargetsKnown);

        overlay.Planned = true;
        Assert.True(service.OverlayTargetsKnown);
    }

    [Fact]
    public void A_read_in_the_window_between_the_overlay_being_ready_and_being_published_is_told_too()
    {
        var service = new AssetMountService();
        service.Add(new FolderMount(Folder("w", ("data/a.bin", "project copy")), null, "w"));
        var overlay = new FakeOverlay();                                          // pending
        overlay.Served[H("data/a.bin")] = Encoding.UTF8.GetBytes("overlaid");
        service.SetOverlay(overlay);
        service.Rebuild();
        var told = new List<ulong>();
        service.ReadWhileOverlayPending = told.Add;
        ulong a = H("data/a.bin");

        overlay.Ready = true;                                                     // the worker is done: the overlay no longer says it is pending...
        Assert.False(overlay.IsPending);
        Assert.False(service.IsOverlaid(a));                                      // ...but its owner has not merged it into the index yet
        Assert.Equal("project copy", Text(service.Read(a)));
        Assert.Equal(new[] { a }, told.ToArray());                                // so that read was not final, and its owner is told

        service.RefreshOverlay();                                                 // merged: published
        Assert.Equal("overlaid", Text(service.Read(a)));
        Assert.Single(told);

        // another overlay is unpublished until it is merged, however ready it says it is; a rebuild merges what is ready
        var next = new FakeOverlay { Ready = true };
        next.Served[a] = Encoding.UTF8.GetBytes("next");
        service.SetOverlay(next);
        service.Read(a);
        Assert.Equal(2, told.Count);
        service.Rebuild();
        Assert.Equal("next", Text(service.Read(a)));
        Assert.Equal(2, told.Count);

        // and with no overlay nothing is checked
        service.SetOverlay(null);
        service.Read(a);
        Assert.Equal(2, told.Count);
    }

    [Fact]
    public void An_overlay_that_was_pending_when_the_index_was_published_stays_unpublished_until_a_refresh_that_finds_it_ready()
    {
        var service = new AssetMountService();
        service.Add(new FolderMount(Folder("p2", ("data/a.bin", "project copy")), null, "p2"));
        var overlay = new FakeOverlay();
        overlay.Served[H("data/a.bin")] = Encoding.UTF8.GetBytes("overlaid");
        service.SetOverlay(overlay);
        service.Rebuild();                                                        // Sync Hashes rebuilds the live service in place, whatever the overlay is doing
        var told = new List<ulong>();
        service.ReadWhileOverlayPending = told.Add;

        service.Rebuild();                                                        // still pending: published as it is, and not final
        service.Read(H("data/a.bin"));
        Assert.Single(told);

        overlay.Ready = true;
        service.Rebuild();                                                        // ready: this publish merges it, and is final
        service.Read(H("data/a.bin"));
        Assert.Single(told);
    }

    // ================================================================================================ names that come from a package

    [Theory]
    [InlineData("data/maps/shipping/map11/map11.bin")]
    [InlineData("data/characters/sru_baron/skins/skin0.bin")]
    [InlineData("assets/ux/some folder (copy)/a-b_c.2.dds")]
    [InlineData("0x0123456789abcdef.bin")]
    public void A_plain_relative_path_is_a_safe_spelling(string path) => Assert.True(AssetPathSafety.IsSafeRelativePath(path));

    [Theory]
    [InlineData("data/x/..\\..\\y.cmd")]
    [InlineData("data/x/../../y.cmd")]
    [InlineData("../y.cmd")]
    [InlineData("./y.cmd")]
    [InlineData("/etc/y")]
    [InlineData("\\\\server\\share\\y")]
    [InlineData("C:\\Windows\\y.dll")]
    [InlineData("C:/Windows/y.dll")]
    [InlineData("data//y.bin")]
    [InlineData("data/y.bin/")]
    [InlineData("data/y.bin.")]
    [InlineData("data/y.bin ")]
    [InlineData("data/ y.bin")]
    [InlineData("data/con.txt")]
    [InlineData("data/NUL")]
    [InlineData("data/COM1.bin")]
    [InlineData("data/COM0.bin")]
    [InlineData("data/lpt0")]
    [InlineData("data/LPT0.txt")]
    [InlineData("data/COM\u00b9.bin")]
    [InlineData("data/com\u00b2")]
    [InlineData("data/COM\u00b3.txt")]
    [InlineData("data/LPT\u00b9")]
    [InlineData("data/lpt\u00b2.bin")]
    [InlineData("data/LPT\u00b3.dds")]
    [InlineData("data/a:b.bin")]
    [InlineData("data/a:stream")]
    [InlineData("data/a?.bin")]
    [InlineData("data/a|b.bin")]
    [InlineData("data/a\"b.bin")]
    [InlineData("")]
    public void A_path_that_can_leave_a_folder_or_name_a_device_is_not_a_safe_spelling(string path) => Assert.False(AssetPathSafety.IsSafeRelativePath(path));

    [Fact]
    public void A_control_character_or_an_overlong_name_is_not_a_safe_spelling_and_neither_is_nothing()
    {
        Assert.False(AssetPathSafety.IsSafeRelativePath("data/a\u0000b.bin"));
        Assert.False(AssetPathSafety.IsSafeRelativePath("data/a\nb.bin"));
        Assert.False(AssetPathSafety.IsSafeRelativePath("data/a\u007fb.bin"));
        Assert.False(AssetPathSafety.IsSafeRelativePath("data/" + new string('a', 256) + ".bin"));
        Assert.False(AssetPathSafety.IsSafeRelativePath(string.Join("/", Enumerable.Repeat("abcdefgh", 200))));
        Assert.False(AssetPathSafety.IsSafeRelativePath(null));
        Assert.True(AssetPathSafety.IsSafeFileName("y.cmd"));
        Assert.False(AssetPathSafety.IsSafeFileName("data/y.cmd"));
        Assert.False(AssetPathSafety.IsSafeFileName(".."));
        Assert.False(AssetPathSafety.IsSafeFileName(null));
    }

    [Fact]
    public void A_name_is_proven_to_lie_below_the_folder_before_it_is_written_there()
    {
        string folder = _temp.Combine("below");
        Assert.True(AssetPathSafety.TryCombineUnder(folder, "a/b.bin", out var ok));
        Assert.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, ok);

        foreach (string hostile in new[] { "..\\..\\y.cmd", "../../y.cmd", "a/../../y.cmd", "a\\..\\..\\y.cmd", "/y", "\\y", "C:\\y.cmd", "C:y.cmd", "../below-sibling/x.bin", "" })
        {
            Assert.False(AssetPathSafety.TryCombineUnder(folder, hostile, out var full), hostile);
            Assert.Equal("", full);
        }
    }

    [Fact]
    public void A_chunk_the_overlay_names_by_a_path_that_can_leave_a_folder_is_listed_by_its_hash_and_a_plain_one_by_its_path()
    {
        var overlay = new FakeOverlay { Ready = true };
        ulong plain = H("data/t/plain.bin"), traversal = H("data/x/..\\..\\y.cmd"), rooted = H("C:/Windows/z.bin"), viaDictionary = H("data/t/dict.bin");
        foreach (ulong hash in new[] { plain, traversal, rooted, viaDictionary }) overlay.Served[hash] = new byte[] { 1 };
        overlay.Names[plain] = "data/t/plain.bin";
        overlay.Names[traversal] = "data/x/..\\..\\y.cmd";
        overlay.Names[rooted] = "C:/Windows/z.bin";
        var db = new HashDatabase();
        db.AddWad(viaDictionary, "data/t/../../dict.bin");                        // the dictionary can carry a package's names too
        var service = new AssetMountService();
        service.SetOverlay(overlay, new WadPathResolver(db));
        service.Rebuild();

        Assert.True(service.TryGet(plain, out var a));
        Assert.Equal("data/t/plain.bin", a.VirtualPath);
        Assert.True(a.IsResolved);
        foreach (ulong hostile in new[] { traversal, rooted, viaDictionary })
        {
            Assert.True(service.TryGet(hostile, out var b));
            Assert.Equal($"0x{hostile:x16}.bin", b.VirtualPath);                  // the hash form: a name for the browser, not a way out of a folder
            Assert.False(b.IsResolved);
            Assert.Equal($"0x{hostile:x16}.bin", b.ToEntry().DisplayName);
        }
    }

    [Theory]
    [InlineData("data/COM10.bin")]
    [InlineData("data/COMMON/x.bin")]
    [InlineData("data/lpt/x.bin")]
    [InlineData("data/lpt00.bin")]
    [InlineData("data/COM.bin")]
    [InlineData("data/aux2.bin")]
    public void A_name_that_only_looks_like_a_device_is_a_safe_spelling(string path) => Assert.True(AssetPathSafety.IsSafeRelativePath(path));

    [Fact]
    public void A_colon_names_an_NTFS_stream_or_a_drive_and_is_refused_wherever_it_stands()
    {
        string folder = _temp.Combine("colon");
        foreach (string hostile in new[] { "x.bin:s", "a/b.bin::$DATA", "a:b/c.bin", "data/x.bin:Zone.Identifier", "a/b:c", ":x" })
        {
            Assert.False(AssetPathSafety.TryCombineUnder(folder, hostile, out var full), hostile);
            Assert.Equal("", full);
        }
        Assert.True(AssetPathSafety.TryCombineUnder(folder, "a/b.bin", out _));
        Assert.True(AssetPathSafety.TryCombineUnder(folder, "a/b-c_d.2.bin", out _));
    }

    [Fact]
    public void A_project_made_from_a_WAD_never_writes_an_entry_outside_its_folder_whatever_the_dictionary_calls_it()
    {
        const string hostile = "data/x/../../../escaped-create.bin", plain = "data/t/plain-create.bin", stream = "data/t/stream-create.bin:hidden";
        string wadPath = _temp.Combine("src-create", "creator.wad.client");
        // the entries are packed under their hashes, and the dictionary - which a package's tables can teach - is what names them
        LayeredFantomeSupport.PackWad(_temp.Combine("pack-create"), wadPath,
            ($"{H(hostile):x16}.bin", new byte[] { 1 }), ($"{H(plain):x16}.bin", new byte[] { 2 }), ($"{H(stream):x16}.bin", new byte[] { 3 }));
        var db = new HashDatabase();
        db.AddWad(H(hostile), hostile);
        db.AddWad(H(plain), plain);
        db.AddWad(H(stream), stream);
        string location = _temp.Combine("projects-create");

        var result = ProjectCreator.Create(
            new ProjectCreationSpec("p", location, null, _temp.Combine("no-game"), new[] { new WadSelection(wadPath, new[] { AssetCategories.Materials, AssetCategories.Other, AssetCategories.Unresolved }) }),
            new WadPathResolver(db));

        string outDir = Path.Combine(result.RootPath, "creator");
        Assert.Equal(0, result.FailedChunks);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(outDir, "data", "t", "plain-create.bin")));   // a plain name is written at its path
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(outDir, $"{H(hostile):x16}.bin")));          // one that leaves the folder is written under its hash, like a chunk with no name
        Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(Path.Combine(outDir, $"{H(stream):x16}.bin")));           // and so is one that names a stream
        Assert.False(File.Exists(Path.Combine(result.RootPath, "escaped-create.bin")));
        Assert.False(File.Exists(Path.Combine(location, "escaped-create.bin")));
        Assert.Equal(3, Directory.EnumerateFiles(result.RootPath, "*", SearchOption.AllDirectories).Count(f => !f.Contains(Path.DirectorySeparatorChar + ReyProjectService.FolderMetaDir + Path.DirectorySeparatorChar)));   // the three entries, and nothing else but the project file
    }
}
