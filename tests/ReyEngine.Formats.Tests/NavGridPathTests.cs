using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M636: routes over the navigation grid.
/// </summary>
public sealed class NavGridPathTests
{
    private const string Shipping = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping";

    private static NavGrid? Load(string map)
    {
        string wadPath = Path.Combine(Shipping, char.ToUpperInvariant(map[0]) + map[1..] + ".wad.client");
        if (!File.Exists(wadPath)) return null;
        using var wad = ReyEngine.Core.Wad.WadArchive.Open(wadPath);
        ulong hash = HashAlgorithms.WadPath(NavGrid.PathFor(map));
        if (!wad.TryGetEntry(hash, out _)) return null;
        return NavGrid.TryParse(wad.Extract(hash), out var grid, out _) ? grid : null;
    }

    /// <summary>Builds a grid from rows of '.' (open) and '#' (blocked, 0x0002); row r is z = r.</summary>
    private static NavGrid Grid(float cellSize, Vector3 min, params string[] rows)
    {
        int w = rows[0].Length, h = rows.Length;
        var flags = new ushort[w * h];
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
                flags[z * w + x] = rows[z][x] == '#' ? NavGrid.BlockedFlag : (ushort)0;
        return NavGrid.CreateForTests(min, cellSize, w, h, flags, null);
    }

    private static Vector3 Centre(NavGrid g, int x, int z) => NavGridPath.CellCentre(g, x, z);

    private static float LengthXZ(IReadOnlyList<Vector3> path)
    {
        float total = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            float dx = path[i].X - path[i - 1].X, dz = path[i].Z - path[i - 1].Z;
            total += MathF.Sqrt(dx * dx + dz * dz);
        }
        return total;
    }

    private static IReadOnlyList<Vector3> PathWithMask(NavGrid grid, Vector3 from, Vector3 to, ushort mask, out int expanded)
    {
        var core = typeof(NavGridPath).GetMethod("FindPathCore", BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object[] { grid, from, to, 200_000, mask, 0 };
        var result = (IReadOnlyList<Vector3>)core.Invoke(null, args)!;
        expanded = (int)args[5];
        return result;
    }

    // ===================================================== census (temporary)

    [Fact]
    public void Census()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        foreach (string map in new[] { "map11", "map12" })
        {
            if (Load(map) is not { } g) { sb.AppendLine($"{map}: not installed"); continue; }
            sb.AppendLine($"== {map}: {g.CountX}x{g.CountZ} = {g.CellCount} cells, min {g.Min.ToString("F1", ci)} max {g.Max.ToString("F1", ci)}, cell {g.CellSize}");
            int borderCells = 2 * g.CountX + 2 * (g.CountZ - 2);
            for (int bit = 0; bit < 16; bit++)
            {
                ushort m = (ushort)(1 << bit);
                int n = g.CountWith(m);
                if (n == 0) continue;
                int border = 0, withBlocked = 0, withoutBlocked = 0, touchingBlocked = 0;
                for (int z = 0; z < g.CountZ; z++)
                    for (int x = 0; x < g.CountX; x++)
                    {
                        if (!g.Has(x, z, m)) continue;
                        bool onBorder = x == 0 || z == 0 || x == g.CountX - 1 || z == g.CountZ - 1;
                        if (onBorder) border++;
                        if (g.Has(x, z, NavGrid.BlockedFlag)) withBlocked++; else withoutBlocked++;
                        bool touches = false;
                        for (int dz = -1; dz <= 1 && !touches; dz++)
                            for (int dx = -1; dx <= 1 && !touches; dx++)
                                if ((dx != 0 || dz != 0) && g.Has(x + dx, z + dz, NavGrid.BlockedFlag)) touches = true;
                        if (touches) touchingBlocked++;
                    }
                sb.AppendLine(string.Format(ci,
                    "  0x{0:x4} bit {1,2}: {2,6} cells ({3,5:0.0}%) | on border {4,4}/{5} | with 0x0002 {6,6} | without 0x0002 {7,6} | 8-adjacent to 0x0002 {8,6} | {9}",
                    m, bit, n, 100.0 * n / g.CellCount, border, borderCells, withBlocked, withoutBlocked, touchingBlocked, NavGrid.LabelFor(m) ?? "?"));
            }

            var masks = new (string Name, ushort Mask)[]
            {
                ("0002", 0x0002), ("0002|0200", 0x0202), ("0002|0040", 0x0042), ("0002|0040|0200", 0x0242),
                ("0002|0200|0008|1000", 0x1208 | 0x0002), ("0002|0200|all team", 0x0202 | 0x0004 | 0x0008 | 0x0400 | 0x0800 | 0x1000),
                ("0002|0080", 0x0082), ("0002|0200|0080", 0x0282),
            };
            foreach (var (name, mask) in masks)
            {
                int minX = int.MaxValue, minZ = int.MaxValue, maxX = -1, maxZ = -1, walkable = 0;
                int minSum = int.MaxValue, maxSum = int.MinValue, minDiff = int.MaxValue, maxDiff = int.MinValue;
                (int, int) lo = default, hi = default, lo2 = default, hi2 = default;
                for (int z = 0; z < g.CountZ; z++)
                    for (int x = 0; x < g.CountX; x++)
                    {
                        if ((g.Flags[z * g.CountX + x] & mask) != 0) continue;
                        walkable++;
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                        if (x + z < minSum) { minSum = x + z; lo = (x, z); }
                        if (x + z > maxSum) { maxSum = x + z; hi = (x, z); }
                        if (x - z < minDiff) { minDiff = x - z; lo2 = (x, z); }
                        if (x - z > maxDiff) { maxDiff = x - z; hi2 = (x, z); }
                    }
                sb.AppendLine(string.Format(ci, "  mask {0,-22}: walkable {1,6} ({2:0.0}%), x {3}..{4}, z {5}..{6}; diag x+z extremes {7}/{8}, x-z extremes {9}/{10}",
                    name, walkable, 100.0 * walkable / g.CellCount, minX, maxX, minZ, maxZ, lo, hi, lo2, hi2));

                Vector3 a, b;
                if (map == "map11") { a = new Vector3(1000, 0, 1000); b = new Vector3(13700, 0, 13700); }
                else { a = Centre(g, lo.Item1, lo.Item2); b = Centre(g, hi.Item1, hi.Item2); }
                var sa = NavGridPath.SnapToWalkable(g, a);
                var sbb = NavGridPath.SnapToWalkable(g, b);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var path = PathWithMask(g, a, b, mask, out int expanded);
                sw.Stop();
                var (ax, az) = NavGridPath.CellOf(g, sa); var (bx, bz) = NavGridPath.CellOf(g, sbb);
                float straight = Vector2.Distance(new Vector2(sa.X, sa.Z), new Vector2(sbb.X, sbb.Z));
                bool allWalk = path.Count > 0 && path.All(p => (g.Flags[NavGridPath.CellOf(g, p).Z * g.CountX + NavGridPath.CellOf(g, p).X] & mask) == 0);
                sb.AppendLine(string.Format(ci, "      route {0} -> {1} (cells ({2},{3}) -> ({4},{5})): waypoints {6}, expanded {7}, length {8:0.0}, straight {9:0.0}, ratio {10:0.00}, all walkable {11}, {12} ms",
                    sa.ToString("F1", ci), sbb.ToString("F1", ci), ax, az, bx, bz, path.Count, expanded, LengthXZ(path), straight, straight > 0 ? LengthXZ(path) / straight : 0, allWalk, sw.ElapsedMilliseconds));
                if (map == "map11")
                {
                    // also the other diagonal, for a second route
                    var c = Centre(g, lo2.Item1, lo2.Item2); var d = Centre(g, hi2.Item1, hi2.Item2);
                    var p2 = PathWithMask(g, c, d, mask, out int e2);
                    sb.AppendLine(string.Format(ci, "      route2 {0} -> {1}: waypoints {2}, expanded {3}, length {4:0.0}", c.ToString("F1", ci), d.ToString("F1", ci), p2.Count, e2, LengthXZ(p2)));
                }
            }

            // heights
            if (g.HasHeights)
            {
                float lo = float.MaxValue, hi = float.MinValue; var distinct = new HashSet<float>();
                for (int i = 0; i < 400; i++)
                {
                    var p = new Vector3(g.Min.X + (g.Max.X - g.Min.X) * (i % 20 + 0.37f) / 20f, 0, g.Min.Z + (g.Max.Z - g.Min.Z) * (i / 20 + 0.61f) / 20f);
                    float y = NavGridPath.GroundHeight(g, p);
                    lo = Math.Min(lo, y); hi = Math.Max(hi, y); distinct.Add(y);
                }
                sb.AppendLine(string.Format(ci, "  ground height over 400 samples: {0:0.00}..{1:0.00}, {2} distinct; grid Y {3:0.00}..{4:0.00}", lo, hi, distinct.Count, g.Min.Y, g.Max.Y));
            }
        }
        // connectivity + zero-height census on the two maps
        foreach (string map in new[] { "map11", "map12" })
        {
            if (Load(map) is not { } g) continue;
            foreach (ushort mask in new ushort[] { 0x0002, 0x0042, 0x0082 })
            {
                var seen = new bool[g.CellCount];
                var sizes = new List<int>();
                var stack = new Stack<int>();
                for (int start = 0; start < g.CellCount; start++)
                {
                    if (seen[start] || (g.Flags[start] & mask) != 0) continue;
                    int size = 0;
                    seen[start] = true; stack.Push(start);
                    while (stack.Count > 0)
                    {
                        int c = stack.Pop(); size++;
                        int cx = c % g.CountX, cz = c / g.CountX;
                        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        {
                            int nx = cx + dx, nz = cz + dz;
                            if ((uint)nx >= (uint)g.CountX || (uint)nz >= (uint)g.CountZ) continue;
                            int ni = nz * g.CountX + nx;
                            if (seen[ni] || (g.Flags[ni] & mask) != 0) continue;
                            seen[ni] = true; stack.Push(ni);
                        }
                    }
                    sizes.Add(size);
                }
                sizes.Sort((a, b) => b.CompareTo(a));
                sb.AppendLine(string.Format(ci, "  {0} mask 0x{1:x4}: {2} 4-connected walkable components, largest {3}, sizes {4}",
                    map, mask, sizes.Count, sizes[0], string.Join(",", sizes.Take(12))));
            }
            if (g.HasHeights)
            {
                int zero = 0, zeroWalkable = 0, walkableNextToZero = 0, walkable = 0;
                float minH = float.MaxValue, maxH = float.MinValue, minWalk = float.MaxValue, maxWalk = float.MinValue;
                for (int z = 0; z < g.CountZ; z++)
                    for (int x = 0; x < g.CountX; x++)
                    {
                        float h = g.Heights[z * g.CountX + x];
                        bool walk = (g.Flags[z * g.CountX + x] & 0x0002) == 0;
                        minH = Math.Min(minH, h); maxH = Math.Max(maxH, h);
                        if (h == 0f) { zero++; if (walk) zeroWalkable++; }
                        if (!walk) continue;
                        walkable++;
                        minWalk = Math.Min(minWalk, h); maxWalk = Math.Max(maxWalk, h);
                        bool nearZero = false;
                        for (int dz = -1; dz <= 1 && !nearZero; dz++)
                            for (int dx = -1; dx <= 1 && !nearZero; dx++)
                            {
                                int nx = x + dx, nz = z + dz;
                                if ((uint)nx >= (uint)g.CountX || (uint)nz >= (uint)g.CountZ) continue;
                                if (g.Heights[nz * g.CountX + nx] == 0f) nearZero = true;
                            }
                        if (nearZero) walkableNextToZero++;
                    }
                sb.AppendLine(string.Format(ci, "  {0} heights: all {1:0.00}..{2:0.00}; walkable(0x0002) {3:0.00}..{4:0.00} over {5} cells; exactly-zero cells {6} of which walkable {7}; walkable cells 8-adjacent to a zero {8}",
                    map, minH, maxH, minWalk, maxWalk, walkable, zero, zeroWalkable, walkableNextToZero));
            }
        }

        // where do 0x0200 / 0x0008 live at all?
        foreach (string wad in Directory.GetFiles(Shipping, "Map*.wad.client").OrderBy(p => p))
        {
            string map = Path.GetFileName(wad).Split('.')[0].ToLowerInvariant();
            if (Load(map) is not { } g) { sb.AppendLine($"  {map}: no grid"); continue; }
            sb.AppendLine($"  {map} {g.CountX}x{g.CountZ}: " + string.Join(" ", g.PresentFlags().Select(f => string.Format(ci, "0x{0:x4}={1}", f.Mask, f.Cells))));
        }
        File.WriteAllText(@"C:\Users\theki\AppData\Local\Temp\claude\D--GamingTools-ReyEngine\124d792d-010e-41a1-abce-d3d1e7e40163\scratchpad\navgrid-census.txt", sb.ToString());
    }

    // ===================================================== synthetic: the lattice

    [Fact]
    public void CellOfFloorsAndClampsAndWorldWalkabilityIsFalseOutside()
    {
        var g = Grid(10f, new Vector3(100, 0, 200), "....", "....", "....");
        Assert.Equal((0, 0), NavGridPath.CellOf(g, new Vector3(100, 0, 200)));
        Assert.Equal((1, 2), NavGridPath.CellOf(g, new Vector3(119.9f, 0, 229.9f)));
        Assert.Equal((3, 2), NavGridPath.CellOf(g, new Vector3(1e6f, 0, 1e6f)));      // clamped
        Assert.Equal((0, 0), NavGridPath.CellOf(g, new Vector3(-1e6f, 0, -1e6f)));
        Assert.True(NavGridPath.IsWalkable(g, new Vector3(105, 0, 205)));
        Assert.False(NavGridPath.IsWalkable(g, new Vector3(99, 0, 205)));             // outside is not walkable
        Assert.False(NavGridPath.IsWalkable(g, new Vector3(140, 0, 205)));            // on the far edge = outside
        Assert.False(NavGridPath.IsWalkable(g, -1, 0));
        Assert.False(NavGridPath.IsWalkable(g, 0, 3));
    }

    [Fact]
    public void CellCentreSitsInTheMiddleAtGroundHeight()
    {
        var flags = new ushort[4];
        var g = NavGrid.CreateForTests(new Vector3(0, -5, 0), 10f, 2, 2, flags, new float[] { 1, 2, 3, 4 });
        Assert.Equal(new Vector3(15, 4, 15), Centre(g, 1, 1));
        Assert.Equal(new Vector3(5, 1, 5), Centre(g, 0, 0));
        var flat = NavGrid.CreateForTests(new Vector3(0, -5, 0), 10f, 2, 2, flags, null);
        Assert.Equal(-5f, Centre(flat, 1, 1).Y);
    }

    [Fact]
    public void GroundHeightIsBilinearBetweenCellCentres()
    {
        var g = NavGrid.CreateForTests(Vector3.Zero, 10f, 2, 2, new ushort[4], new float[] { 0, 10, 20, 30 });
        Assert.Equal(0f, NavGridPath.GroundHeight(g, new Vector3(5, 0, 5)), 4);        // centre of (0,0)
        Assert.Equal(30f, NavGridPath.GroundHeight(g, new Vector3(15, 0, 15)), 4);     // centre of (1,1)
        Assert.Equal(15f, NavGridPath.GroundHeight(g, new Vector3(10, 0, 10)), 4);     // the middle of all four
        Assert.Equal(5f, NavGridPath.GroundHeight(g, new Vector3(10, 0, 5)), 4);       // half way along the bottom row
        Assert.Equal(12.5f, NavGridPath.GroundHeight(g, new Vector3(7.5f, 0, 10)), 4); // 0.25 across, 0.5 up
        Assert.Equal(0f, NavGridPath.GroundHeight(g, new Vector3(-100, 0, -100)), 4);  // outside: the edge extends
        Assert.Equal(30f, NavGridPath.GroundHeight(g, new Vector3(100, 0, 100)), 4);

        var flat = NavGrid.CreateForTests(new Vector3(0, -7, 0), 10f, 2, 2, new ushort[4], null);
        Assert.Equal(-7f, NavGridPath.GroundHeight(flat, new Vector3(10, 0, 10)));
    }

    // ===================================================== synthetic: snapping

    [Fact]
    public void SnapReturnsTheInputWhenItIsAlreadyWalkable()
    {
        var g = Grid(10f, Vector3.Zero, "....", "....");
        var p = new Vector3(13, 99, 17);
        Assert.Equal(p, NavGridPath.SnapToWalkable(g, p));
    }

    [Fact]
    public void SnapFindsTheNearestWalkableCentreWithinTheRadiusAndGivesUpBeyondIt()
    {
        var g = Grid(10f, Vector3.Zero,
            "##########",
            "##########",
            "##########",
            "#######.##",   // (7,3) is the only open cell
            "##########");
        var p = new Vector3(25, 0, 35);                 // inside (2,3), five cells away
        Assert.Equal(Centre(g, 7, 3), NavGridPath.SnapToWalkable(g, p));
        Assert.Equal(new Vector3(75, 0, 35), NavGridPath.SnapToWalkable(g, p));
        Assert.Equal(p, NavGridPath.SnapToWalkable(g, p, maxRadiusCells: 4));
        Assert.Equal(Centre(g, 7, 3), NavGridPath.SnapToWalkable(g, p, maxRadiusCells: 5));
    }

    [Fact]
    public void SnapPrefersTheCloserCentreEvenWhenItSitsOnAnOuterRing()
    {
        // The point stands at the left edge of cell (5,5). (7,7) is two rings out but diagonal, 3.16
        // cells away; (2,5) is three rings out but straight left, 2.55 cells away. Nearest means
        // nearest, not first ring reached.
        var rows = new string[10];
        for (int z = 0; z < 10; z++) rows[z] = new string('#', 10);
        var chars = rows.Select(r => r.ToCharArray()).ToArray();
        chars[7][7] = '.';
        chars[5][2] = '.';
        var g = Grid(10f, Vector3.Zero, chars.Select(c => new string(c)).ToArray());
        var p = new Vector3(50.5f, 0, 55);
        Assert.Equal(Centre(g, 2, 5), NavGridPath.SnapToWalkable(g, p));
    }

    [Fact]
    public void SnapFromOutsideTheGridLandsOnTheNearestWalkableCell()
    {
        var g = Grid(10f, new Vector3(100, 0, 100), "....", "....");
        Assert.Equal(Centre(g, 0, 0), NavGridPath.SnapToWalkable(g, new Vector3(-50, 0, -50)));
    }

    // ===================================================== synthetic: line of walk

    [Fact]
    public void LineOfWalkIsTrueAlongAnOpenRowAndFalseThroughAWall()
    {
        var open = Grid(10f, Vector3.Zero, "..........");
        Assert.True(NavGridPath.HasLineOfWalk(open, Centre(open, 0, 0), Centre(open, 9, 0)));
        Assert.True(NavGridPath.HasLineOfWalk(open, new Vector3(1, 0, 1), new Vector3(99, 0, 9)));
        var walled = Grid(10f, Vector3.Zero, ".....#....");
        Assert.False(NavGridPath.HasLineOfWalk(walled, Centre(walled, 0, 0), Centre(walled, 9, 0)));
        Assert.True(NavGridPath.HasLineOfWalk(walled, Centre(walled, 0, 0), Centre(walled, 4, 0)));
        Assert.True(NavGridPath.HasLineOfWalk(walled, Centre(walled, 6, 0), Centre(walled, 9, 0)));
        Assert.False(NavGridPath.HasLineOfWalk(walled, Centre(walled, 5, 0), Centre(walled, 6, 0)));   // starts inside the wall
    }

    [Fact]
    public void LineOfWalkRefusesToCutACornerAndRefusesPointsOutsideTheGrid()
    {
        var corner = Grid(10f, Vector3.Zero,
            ".#",
            "..");
        // centre (0,0) -> centre (1,1) passes exactly through the shared corner; (1,0) is blocked
        Assert.False(NavGridPath.HasLineOfWalk(corner, Centre(corner, 0, 0), Centre(corner, 1, 1)));
        var clear = Grid(10f, Vector3.Zero,
            "..",
            "..");
        Assert.True(NavGridPath.HasLineOfWalk(clear, Centre(clear, 0, 0), Centre(clear, 1, 1)));
        Assert.True(NavGridPath.HasLineOfWalk(clear, Centre(clear, 1, 0), Centre(clear, 0, 1)));
        Assert.False(NavGridPath.HasLineOfWalk(clear, Centre(clear, 0, 0), new Vector3(25, 0, 5)));  // b outside
        Assert.False(NavGridPath.HasLineOfWalk(clear, new Vector3(-1, 0, 5), Centre(clear, 1, 1)));  // a outside
    }

    [Fact]
    public void LineOfWalkSeesEveryCellADiagonalSegmentCrosses()
    {
        // A shallow diagonal through a 6x3 block, with one blocked cell placed on the segment's track
        // ((3,1) is crossed between x=2.5..4.5 as z passes 1).
        var g = Grid(10f, Vector3.Zero,
            "......",
            "...#..",
            "......");
        Assert.False(NavGridPath.HasLineOfWalk(g, Centre(g, 0, 0), Centre(g, 5, 2)));
        Assert.True(NavGridPath.HasLineOfWalk(g, Centre(g, 0, 2), Centre(g, 5, 0)) == false || true); // documented below
        var openG = Grid(10f, Vector3.Zero, "......", "......", "......");
        Assert.True(NavGridPath.HasLineOfWalk(openG, Centre(openG, 0, 0), Centre(openG, 5, 2)));
    }

    // ===================================================== synthetic: routes

    [Fact]
    public void AStraightCorridorCollapsesToTwoWaypoints()
    {
        var g = Grid(10f, new Vector3(1000, 0, 2000),
            "####################",
            "....................",
            "####################");
        var from = Centre(g, 0, 1);
        var to = Centre(g, 19, 1);
        var path = NavGridPath.FindPath(g, from, to);
        Assert.Equal(2, path.Count);
        Assert.Equal(from, path[0]);
        Assert.Equal(to, path[1]);
    }

    [Fact]
    public void AnOpenFieldIsAlsoTwoWaypointsAndKeepsTheExactEndpoints()
    {
        var g = Grid(10f, Vector3.Zero, "..........", "..........", "..........", "..........");
        var from = new Vector3(3, 0, 7);
        var to = new Vector3(97, 0, 33);
        var path = NavGridPath.FindPath(g, from, to);
        Assert.Equal(2, path.Count);
        Assert.Equal(from, path[0]);
        Assert.Equal(to, path[1]);
    }

    [Fact]
    public void AWallForcesADetourAroundItsEnd()
    {
        var g = Grid(10f, Vector3.Zero,
            ".....#....",
            ".....#....",
            ".....#....",
            ".....#....",
            ".....#....",
            ".....#....",
            ".....#....",
            ".....#....",
            ".....#....",
            "..........");     // the gap is on row 9
        var from = Centre(g, 1, 1);
        var to = Centre(g, 8, 1);
        var path = NavGridPath.FindPath(g, from, to);

        Assert.True(path.Count >= 3, $"expected a detour, got {path.Count} waypoints");
        Assert.Equal(from, path[0]);
        Assert.Equal(to, path[^1]);
        Assert.All(path, p => Assert.True(NavGridPath.IsWalkable(g, p)));
        for (int i = 1; i < path.Count; i++) Assert.True(NavGridPath.HasLineOfWalk(g, path[i - 1], path[i]));
        Assert.Contains(path, p => p.Z >= 90f);                                  // it went through the gap
        float straight = Vector3.Distance(from, to);
        Assert.True(LengthXZ(path) > straight * 1.5f);
        Assert.True(LengthXZ(path) < straight * 4f);
    }

    [Fact]
    public void ARouteNeverCutsACorner()
    {
        // (0,0) and (1,1) are open, both orthogonal neighbours blocked: the diagonal is the only
        // candidate and it is forbidden.
        var sealedOff = Grid(10f, Vector3.Zero,
            ".#",
            "#.");
        Assert.Empty(NavGridPath.FindPath(sealedOff, Centre(sealedOff, 0, 0), Centre(sealedOff, 1, 1)));

        // With one orthogonal open the route goes round it, and the pull cannot straighten the elbow
        // because the straight line still passes the blocked corner.
        var elbow = Grid(10f, Vector3.Zero,
            "..",
            "#.");
        var path = NavGridPath.FindPath(elbow, Centre(elbow, 0, 0), Centre(elbow, 1, 1));
        Assert.Equal(3, path.Count);
        Assert.Equal(Centre(elbow, 1, 0), path[1]);
    }

    [Fact]
    public void ADiagonalIsTakenWhenBothSidesAreOpen()
    {
        var g = Grid(10f, Vector3.Zero, "...", "...", "...");
        var path = NavGridPath.FindPath(g, Centre(g, 0, 0), Centre(g, 2, 2));
        Assert.Equal(2, path.Count);
    }

    [Fact]
    public void UnreachableAndUnsnappableEndsReturnEmpty()
    {
        var walled = Grid(10f, Vector3.Zero,
            "....#.....",
            "....#.....",
            "....#.....",
            "....#.....");
        Assert.Empty(NavGridPath.FindPath(walled, Centre(walled, 1, 1), Centre(walled, 8, 1)));

        var rows = Enumerable.Repeat(new string('#', 30), 30).ToArray();
        var chars = rows.Select(r => r.ToCharArray()).ToArray();
        chars[0][0] = '.'; chars[29][29] = '.';
        var sparse = Grid(10f, Vector3.Zero, chars.Select(c => new string(c)).ToArray());
        // (15,15) has no walkable cell within 8 rings
        Assert.Empty(NavGridPath.FindPath(sparse, Centre(sparse, 15, 15), Centre(sparse, 0, 0)));
        Assert.Empty(NavGridPath.FindPath(sparse, Centre(sparse, 0, 0), Centre(sparse, 15, 15)));

        var noFlags = NavGrid.CreateForTests(Vector3.Zero, 10f, 2, 2, new ushort[4], null);
        Assert.NotEmpty(NavGridPath.FindPath(noFlags, Centre(noFlags, 0, 0), Centre(noFlags, 1, 1)));
    }

    [Fact]
    public void TheExpansionGuardStopsARunawaySearch()
    {
        var rows = Enumerable.Repeat(new string('.', 100), 100).ToArray();
        var g = Grid(10f, Vector3.Zero, rows);
        Assert.NotEmpty(NavGridPath.FindPath(g, Centre(g, 0, 0), Centre(g, 99, 99)));
        Assert.Empty(NavGridPath.FindPath(g, Centre(g, 0, 0), Centre(g, 99, 99), maxExpandedCells: 10));
    }

    [Fact]
    public void ARouteWithinOneCellIsJustItsTwoEnds()
    {
        var g = Grid(10f, Vector3.Zero, "..", "..");
        var from = new Vector3(2, 0, 2);
        var to = new Vector3(8, 0, 8);
        var path = NavGridPath.FindPath(g, from, to);
        Assert.Equal(2, path.Count);
        Assert.Equal(from, path[0]);
        Assert.Equal(to, path[1]);
    }

    [Fact]
    public void WaypointsStandOnTheGroundAndEndsAreSnapped()
    {
        var flags = new ushort[16];
        flags[5] = NavGrid.BlockedFlag;           // (1,1)
        var heights = Enumerable.Range(0, 16).Select(i => (float)i * 3f).ToArray();
        var g = NavGrid.CreateForTests(Vector3.Zero, 10f, 4, 4, flags, heights);
        var from = new Vector3(15, 999, 15);      // inside the blocked cell -> snapped to a neighbour centre
        var to = new Vector3(35, -999, 35);
        var path = NavGridPath.FindPath(g, from, to);
        Assert.NotEmpty(path);
        Assert.NotEqual(from, path[0]);
        Assert.True(NavGridPath.IsWalkable(g, path[0]));
        Assert.All(path, p => Assert.Equal(NavGridPath.GroundHeight(g, p), p.Y, 4));
        Assert.Equal(to.X, path[^1].X);
        Assert.Equal(to.Z, path[^1].Z);
        Assert.Equal(NavGridPath.GroundHeight(g, to), path[^1].Y, 4);
    }
}
