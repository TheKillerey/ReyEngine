using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>One <c>MapNavGridOverlay</c> a map declares: the hash name the game switches it by, and its two files.</summary>
public sealed record NavGridOverlayDeclaration(uint NameHash, string NavGridFile, string RegionsFile);

/// <summary>
/// M843: what a map's bins say about WHICH navigation grid it uses.
///
/// <para>Three declarations exist (Map11, 16.20):</para>
/// <list type="bullet">
/// <item><c>mapContainer</c> (in <c>data/maps/mapgeometry/&lt;map&gt;/&lt;name&gt;.materials.bin</c>): a component struct carrying
/// <c>NavGridPath</c> - the grid the geometry set <c>&lt;name&gt;.mapgeo</c> plays on (Base_SRX: AIPath_SRX_3).</item>
/// <item><c>MapSkin</c> (in <c>data/maps/shipping/&lt;map&gt;/&lt;map&gt;.bin</c>): <c>mNavigationMesh</c>. It does NOT always agree with
/// the container (Default: AIPath_SRX_2, its container Base_SRX: AIPath_SRX_3); which one the game wins with is not
/// settled by the data, so the editor follows the container (the one tied to the geometry that is open) and lists
/// the skin's choice in the variant's detail.</item>
/// <item><c>MapNavGridOverlays</c>: the state overlays (<c>.ngrid_overlay</c>) the map can switch on, named by the hash of
/// <c>NavGrid_&lt;State&gt;</c>.</item>
/// </list>
/// </summary>
public sealed class NavGridDeclarations
{
    /// <summary>container object path hash -> grid path as authored.</summary>
    public IReadOnlyDictionary<uint, string> ContainerGrids { get; init; } = new Dictionary<uint, string>();
    /// <summary>(skin object path hash, grid path as authored), in file order.</summary>
    public IReadOnlyList<(uint Skin, string Grid)> SkinGrids { get; init; } = Array.Empty<(uint, string)>();
    public IReadOnlyList<NavGridOverlayDeclaration> Overlays { get; init; } = Array.Empty<NavGridOverlayDeclaration>();

    public static readonly NavGridDeclarations Empty = new();

    public static NavGridDeclarations Read(IEnumerable<byte[]> bins)
    {
        uint cContainer = HashAlgorithms.Fnv1a("mapContainer"), cSkin = HashAlgorithms.Fnv1a("MapSkin"),
             cOverlays = HashAlgorithms.Fnv1a("MapNavGridOverlays");
        uint fNavGridPath = HashAlgorithms.Fnv1a("NavGridPath"), fNavMesh = HashAlgorithms.Fnv1a("mNavigationMesh"),
             fOverlays = HashAlgorithms.Fnv1a("overlays"), fName = HashAlgorithms.Fnv1a("name"),
             fFile = HashAlgorithms.Fnv1a("navGridFileName"), fRegions = HashAlgorithms.Fnv1a("regionsFilename");

        var containers = new Dictionary<uint, string>();
        var skins = new List<(uint, string)>();
        var overlays = new List<NavGridOverlayDeclaration>();
        var seen = new HashSet<uint>();
        foreach (var bin in bins)
        {
            BinTree tree;
            try { tree = SafeBinTree.Parse(bin); } catch { continue; }
            foreach (var o in tree.Objects.Values)
            {
                if (o.ClassHash == cSkin && o.Properties.GetValueOrDefault(fNavMesh) is BinTreeString nm && nm.Value.Length > 0)
                    skins.Add((o.PathHash, nm.Value));
                else if (o.ClassHash == cContainer)
                {
                    // the path sits in a struct of the container's components list
                    foreach (var p in Walk(o.Properties.Values))
                        if (p.NameHash == fNavGridPath && p is BinTreeString ps && ps.Value.Length > 0)
                        { containers[o.PathHash] = ps.Value; break; }
                }
                else if (o.ClassHash == cOverlays && o.Properties.GetValueOrDefault(fOverlays) is BinTreeContainer list)
                {
                    foreach (var item in list.Elements)
                    {
                        if (item is not BinTreeStruct s) continue;
                        if (s.Properties.GetValueOrDefault(fFile) is not BinTreeString file || file.Value.Length == 0) continue;
                        uint name = s.Properties.GetValueOrDefault(fName) is BinTreeHash h ? h.Value : 0;
                        string regions = s.Properties.GetValueOrDefault(fRegions) is BinTreeString r ? r.Value : "";
                        // every materials.bin of a map repeats the same list: keep one entry per overlay file
                        if (seen.Add(HashAlgorithms.Fnv1a(file.Value))) overlays.Add(new NavGridOverlayDeclaration(name, file.Value, regions));
                    }
                }
            }
        }
        return new NavGridDeclarations { ContainerGrids = containers, SkinGrids = skins, Overlays = overlays };
    }

    private static IEnumerable<BinTreeProperty> Walk(IEnumerable<BinTreeProperty> props)
    {
        foreach (var p in props)
        {
            yield return p;
            switch (p)
            {
                case BinTreeStruct s: foreach (var c in Walk(s.Properties.Values)) yield return c; break;
                case BinTreeContainer c: foreach (var e in Walk(c.Elements)) yield return e; break;
            }
        }
    }
}

/// <summary>One entry of the navgrid variant picker: a whole grid file, or a state overlay shown on top of one.</summary>
public sealed record NavGridVariant(string Id, string Label, string Path, string Kind, string Group)
{
    public const string KindGrid = "grid";
    public const string KindOverlay = "overlay";

    /// <summary>Longer text for a tooltip: who uses the grid, what the overlay is tied to.</summary>
    public string Detail { get; init; } = "";
    /// <summary>For an overlay tied to a Map Visibility state: the axis field hash and the layer bit.</summary>
    public uint AxisHash { get; init; }
    public int AxisBit { get; init; }
    /// <summary>False when the map declares the file but the game's WADs do not hold it.</summary>
    public bool InGame { get; init; } = true;
    /// <summary>The region file that goes with an overlay (not shown by the editor).</summary>
    public string RegionsPath { get; init; } = "";
}

/// <summary>
/// M843: every navigation grid and state overlay that belongs to a map, named readably, plus the rule for which grid the
/// open geometry plays on and which overlays a Map Visibility selection implies.
///
/// <para><b>Overlays are tied to a state by NAME</b>: <c>MapNavGridOverlay.name</c> is the FNV-1a of <c>NavGrid_&lt;layer&gt;</c>
/// (Chemtech, Infernal, Mountain, Ocean on the dragon axis; Cup, Tunnel on the baron-pit axis - all six match a layer name
/// of Map11's definition exactly). Nothing in the data bins says when the game switches an overlay on (no object refers
/// to the names), so "Infernal selected means the Infernal overlay" is the editor's reading, not Riot's wiring.</para>
/// </summary>
public sealed class NavGridVariantCatalog
{
    public string MapFolder { get; init; } = "";
    public IReadOnlyList<NavGridVariant> Grids { get; init; } = Array.Empty<NavGridVariant>();
    public IReadOnlyList<NavGridVariant> Overlays { get; init; } = Array.Empty<NavGridVariant>();
    /// <summary>The grid the open geometry plays on (an id of <see cref="Grids"/>), or "" when the map ships none.</summary>
    public string DefaultGridId { get; init; } = "";
    /// <summary>Why <see cref="DefaultGridId"/> is that grid, for the log and the tooltip.</summary>
    public string DefaultReason { get; init; } = "";

    public IEnumerable<NavGridVariant> All => Grids.Concat(Overlays);

    public NavGridVariant? Find(string id) =>
        All.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The overlays a Map Visibility selection (axis field hash -> selected layer bit, 0 = All) implies.</summary>
    public IReadOnlyList<NavGridVariant> OverlaysFor(IReadOnlyDictionary<uint, int>? selections)
    {
        if (selections is null) return Array.Empty<NavGridVariant>();
        return Overlays.Where(o => o.AxisBit != 0 && selections.TryGetValue(o.AxisHash, out int bit) && bit == o.AxisBit).ToList();
    }

    public static string GridId(string path) => "grid:" + StemOf(path);
    public static string OverlayId(string path) => "overlay:" + StemOf(path);

    public static string StemOf(string path)
    {
        string p = path.Replace('\\', '/');
        string file = p[(p.LastIndexOf('/') + 1)..];
        int dot = file.IndexOf('.');
        return (dot > 0 ? file[..dot] : file).ToLowerInvariant();
    }

    /// <param name="mapFolder">The map's folder key (<c>map11</c>).</param>
    /// <param name="mapgeoStem">The open geometry set's name (<c>base_srx</c>), or null.</param>
    /// <param name="wadPaths">Every resolved asset path the editor can see (the grids are listed from here).</param>
    public static NavGridVariantCatalog Build(string mapFolder, string? mapgeoStem, IEnumerable<string> wadPaths,
        NavGridDeclarations declarations, MapVisibilityDefinition? visibility, Func<uint, string?>? resolveName = null)
    {
        string folder = mapFolder.ToLowerInvariant();
        string gridDir = $"assets/maps/navgrid/{folder}/";
        var paths = wadPaths.Select(p => p.Replace('\\', '/')).ToList();
        var byLower = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

        // ---- whole grids: every .aimesh_ngrid in the map's folder
        var gridPaths = paths.Where(p => p.StartsWith(gridDir, StringComparison.OrdinalIgnoreCase)
                                         && p.EndsWith(".aimesh_ngrid", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.ToLowerInvariant()).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();

        string Skin(uint h) => resolveName?.Invoke(h) is { Length: > 0 } n ? n[(n.LastIndexOf('/') + 1)..] : $"0x{h:x8}";
        string Lower(string p) => p.Replace('\\', '/').ToLowerInvariant();

        // which containers and skins name each grid
        var usedBy = new Dictionary<string, List<string>>();
        void Use(string grid, string who)
        {
            if (!usedBy.TryGetValue(grid, out var l)) usedBy[grid] = l = new List<string>();
            if (!l.Contains(who)) l.Add(who);
        }
        foreach (var (hash, grid) in declarations.ContainerGrids) Use(Lower(grid), "geometry " + Skin(hash));
        foreach (var (hash, grid) in declarations.SkinGrids) Use(Lower(grid), "skin " + Skin(hash));

        // ---- the default: the container of the open geometry, then the classic aipath, then the first grid
        string defaultPath = "", reason = "";
        if (mapgeoStem is { Length: > 0 })
        {
            // the container is named Maps/MapGeometry/<Map11>/<stem>; the hash ignores case
            uint want = HashAlgorithms.Fnv1a($"Maps/MapGeometry/{mapFolder}/{mapgeoStem}");
            if (declarations.ContainerGrids.TryGetValue(want, out var cg) && gridPaths.Contains(Lower(cg)))
            { defaultPath = Lower(cg); reason = $"the map container '{mapgeoStem}' names it (NavGridPath)"; }
        }
        if (defaultPath.Length == 0)
        {
            string classic = Lower(NavGrid.PathFor(mapFolder));
            if (gridPaths.Contains(classic)) { defaultPath = classic; reason = "the classic aipath grid (no container names one)"; }
            else if (gridPaths.Count > 0) { defaultPath = gridPaths[0]; reason = "the first grid of the map (no aipath, no container names one)"; }
        }

        var grids = new List<NavGridVariant>();
        foreach (var gp in gridPaths)
        {
            string stem = StemOf(gp);
            var who = usedBy.TryGetValue(gp, out var l) ? l : new List<string>();
            string shortWho = who.Count == 0 ? ""
                : " - " + string.Join(", ", who.Take(3).Select(w => w.Replace("geometry ", "").Replace("skin ", ""))) + (who.Count > 3 ? $" +{who.Count - 3}" : "");
            bool isDefault = gp == defaultPath;
            grids.Add(new NavGridVariant(GridId(gp), (isDefault ? "Base: " : "Grid: ") + stem + shortWho, gp, NavGridVariant.KindGrid, isDefault ? "Base" : "Grid")
            {
                Detail = (isDefault ? "The grid the open geometry plays on: " + reason + ". " : "")
                         + (who.Count == 0 ? "No container or skin of this map names it." : "Named by " + string.Join("; ", who) + "."),
            });
        }
        // the default first, the rest in name order
        grids = grids.OrderByDescending(g => g.Path == defaultPath).ThenBy(g => g.Path, StringComparer.Ordinal).ToList();

        // ---- overlays: declared first, then any other .ngrid_overlay the WAD holds
        var layerTies = new Dictionary<uint, (uint Axis, int Bit, string Group, string Layer)>();
        if (visibility is not null)
            foreach (var axis in visibility.Axes)
            {
                string group = axis.DefinitionFieldHash == MapVisibility.PrimaryAxisHash ? "Dragon"
                    : axis.DefinitionFieldHash == MapVisibility.BaronPitAxisHash ? "Baron" : axis.Name;
                foreach (var layer in axis.Layers)
                    layerTies.TryAdd(HashAlgorithms.Fnv1a("NavGrid_" + layer.Name), (axis.DefinitionFieldHash, layer.Bit, group, layer.Name));
            }

        var overlays = new List<NavGridVariant>();
        var seenOverlays = new HashSet<string>();
        foreach (var d in declarations.Overlays)
        {
            string path = Lower(d.NavGridFile);
            if (!seenOverlays.Add(path)) continue;
            bool exists = byLower.Contains(path);
            string stem = StemOf(path);
            overlays.Add(layerTies.TryGetValue(d.NameHash, out var tie)
                ? new NavGridVariant(OverlayId(path), $"{tie.Group}: {tie.Layer}", path, NavGridVariant.KindOverlay, tie.Group)
                {
                    AxisHash = tie.Axis, AxisBit = tie.Bit, InGame = exists, RegionsPath = Lower(d.RegionsFile),
                    Detail = $"Overlay {stem}: the cells the {tie.Group.ToLowerInvariant()} state '{tie.Layer}' changes, shown on top of the base grid. "
                             + "Selected automatically with that state in Map Visibility.",
                }
                : new NavGridVariant(OverlayId(path), "Overlay: " + PrettyOverlay(stem), path, NavGridVariant.KindOverlay, "Overlay")
                {
                    InGame = exists, RegionsPath = Lower(d.RegionsFile),
                    Detail = $"Overlay {stem}: no Map Visibility state carries this name, so it is only chosen by hand.",
                });
        }
        foreach (var p in paths.Where(p => p.EndsWith(".ngrid_overlay", StringComparison.OrdinalIgnoreCase)
                                           && p.StartsWith("assets/maps/navgrid/", StringComparison.OrdinalIgnoreCase)
                                           && p.Count(c => c == '/') == 3)
                     .Select(Lower).Distinct().OrderBy(p => p, StringComparer.Ordinal))
            if (seenOverlays.Add(p))
                overlays.Add(new NavGridVariant(OverlayId(p), "Overlay: " + PrettyOverlay(StemOf(p)) + " (undeclared)", p, NavGridVariant.KindOverlay, "Overlay")
                { Detail = "In the game's files but not declared by this map's MapNavGridOverlays." });

        return new NavGridVariantCatalog
        {
            MapFolder = folder, Grids = grids, Overlays = overlays,
            DefaultGridId = defaultPath.Length > 0 ? GridId(defaultPath) : "",
            DefaultReason = reason,
        };
    }

    private static string PrettyOverlay(string stem)
    {
        string s = stem.StartsWith("navgrid_", StringComparison.Ordinal) ? stem["navgrid_".Length..] : stem;
        s = s.Replace("_seasonal", "").Replace("_base", "").Replace('_', ' ');
        return s.Length == 0 ? stem : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
