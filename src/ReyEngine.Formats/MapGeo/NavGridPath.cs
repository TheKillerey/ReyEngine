using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M636: walking routes over the navigation grid — A* on the 8-connected cell lattice, string-pulled
/// into world-space waypoints.
///
/// <para>The lattice is <see cref="NavGrid"/>'s: cell (x, z) covers <c>Min.xz + (x, z) * CellSize</c>,
/// flags are row-major with X fastest. A cell is walkable when it carries none of
/// <see cref="ImpassableMask"/>, which was chosen by measuring the shipped grids rather than by reading
/// the labels — see that property for the numbers.</para>
///
/// <para>Diagonal moves are allowed only when BOTH orthogonal neighbours are walkable, so a route never
/// cuts the corner of a wall; <see cref="HasLineOfWalk"/> applies the same rule when a segment passes
/// through a lattice corner, which is what keeps string-pulling consistent with the search that
/// produced the cells.</para>
///
/// <para>Measured on Summoner's Rift (Map11, 295x296): the route from (1000, 1000) to (13700, 13700)
/// — cells (20,19) to (274,273), both already walkable — expands 7,251 cells, comes back as 7 waypoints
/// and is 18,178 units long against a straight line of 17,961 (ratio 1.01, it is the mid lane), in
/// about 5 ms. Howling Abyss (Map12, 248x237) end to end — cells (8,9) to (237,227) — expands 2,617
/// cells and collapses to exactly 2 waypoints, 15,809 units, because the lane is straight.</para>
/// </summary>
public static class NavGridPath
{
    private const float Sqrt2 = 1.41421356f;

    /// <summary>How far <see cref="FindPath"/> looks for a walkable cell around each end.</summary>
    private const int DefaultSnapRadius = 8;

    /// <summary>
    /// Bits that make a cell impassable for a walking unit: <c>0x0002</c> (not walkable) and
    /// <c>0x0200</c> (not walkable, one area). Decided by census of every shipped grid, M636.
    ///
    /// <para><b>0x0002 is the wall.</b> It is the only bit that fills the border — 1,178 of 1,178 border
    /// cells on Summoner's Rift, 966 of 966 on Howling Abyss — and what it leaves is a single
    /// 4-connected region on both maps: 53,241 cells (61.0%) on SR, 11,831 (20.1%) on the Abyss. Fountain
    /// to fountain exists under it (see the class remarks).</para>
    ///
    /// <para><b>0x0200 is included on its label alone.</b> "Not walkable (one area)" was identified by
    /// drawing it (M568), but it is absent from Map11, Map12 and Map22 (0 cells) and appears only on
    /// Map453 (459 cells), so including it changes nothing measurable on the two maps that decide this
    /// and honours the one identification there is.</para>
    ///
    /// <para><b>0x0040 ("not-walkable outline") is excluded, and it would NOT have broken the route.</b>
    /// 2,670 cells on SR, 2,534 of them (95%) already inside 0x0002 and 7 on the border; on the Abyss all
    /// 500 lie inside 0x0002. Including it removes the remaining 136 SR cells — the fountain route is
    /// identical (7 waypoints, 7,251 expanded, same length) and the walkable region stays one component
    /// (53,105 cells) — so it is a marker of where the wall's rim is, not a walkability statement, and
    /// taking those 136 cells away would only push snapping 50 units off the wall for no identified
    /// reason.</para>
    ///
    /// <para><b>0x0080 (unidentified, 30.6% of SR) is excluded on the strongest evidence.</b> 26,725
    /// cells on SR, 0 on the border, 23,306 of them outside 0x0002. Treating it as impassable shatters
    /// the walkable region into 545 components (largest 25,478), lengthens the fountain route to 25
    /// waypoints at ratio 1.21, and leaves the other diagonal — (1274, 12808) to (12574, 1358) — with no
    /// route at all; on the Abyss (834 cells, 778 outside 0x0002) it splits the lane in two (7,113 +
    /// 3,926 cells) and the end-to-end route fails. Whatever bit 7 is, units walk through it.</para>
    ///
    /// <para><b>The team bits are excluded</b> because a walking unit of SOME team may stand there:
    /// 0x0400 (70 cells), 0x0800 (66) and 0x1000 (76) lie entirely outside 0x0002 and off the border;
    /// 0x0004 (1,085 cells) is moot, 1,079 of them being inside 0x0002 already; 0x0008 has 0 cells on
    /// Map11/Map12 (144 on Map453). Including all of them leaves the fountain route unchanged. The bush
    /// (0x0001, 2,015 cells, 1,760 outside 0x0002) is walkable by definition.</para>
    /// </summary>
    public static ushort ImpassableMask => NavGrid.BlockedFlag | 0x0200;

    // ================================================================= cells and world space

    /// <summary>True when the cell exists, the grid carries a flag plane, and none of the impassable bits are set.</summary>
    public static bool IsWalkable(NavGrid grid, int x, int z) => IsWalkable(grid, x, z, ImpassableMask);

    /// <summary>World XZ to cell; false outside the grid or when the grid has no flag plane.</summary>
    public static bool IsWalkable(NavGrid grid, Vector3 world) => IsWalkable(grid, world, ImpassableMask);

    internal static bool IsWalkable(NavGrid grid, int x, int z, ushort impassable)
    {
        if (!grid.HasFlags || (uint)x >= (uint)grid.CountX || (uint)z >= (uint)grid.CountZ) return false;
        return (grid.Flags[z * grid.CountX + x] & impassable) == 0;
    }

    internal static bool IsWalkable(NavGrid grid, Vector3 world, ushort impassable) =>
        TryCellOf(grid, world, out int x, out int z) && IsWalkable(grid, x, z, impassable);

    /// <summary><c>floor((world - Min) / CellSize)</c>, clamped into the grid (and to cell 0 for a non-finite input).</summary>
    public static (int X, int Z) CellOf(NavGrid grid, Vector3 world) =>
        (FloorClamped((world.X - grid.Min.X) / grid.CellSize, grid.CountX),
         FloorClamped((world.Z - grid.Min.Z) / grid.CellSize, grid.CountZ));

    /// <summary>The unclamped cell, false when the point lies outside the lattice.</summary>
    private static bool TryCellOf(NavGrid grid, Vector3 world, out int x, out int z)
    {
        x = z = 0;
        float u = (world.X - grid.Min.X) / grid.CellSize;
        float v = (world.Z - grid.Min.Z) / grid.CellSize;
        if (!float.IsFinite(u) || !float.IsFinite(v) || u < 0f || v < 0f) return false;
        double fx = Math.Floor(u), fz = Math.Floor(v);
        if (fx >= grid.CountX || fz >= grid.CountZ) return false;
        x = (int)fx;
        z = (int)fz;
        return true;
    }

    private static int FloorClamped(float u, int count)
    {
        if (!float.IsFinite(u)) return 0;
        double f = Math.Floor(u);
        if (f < 0) return 0;
        if (f >= count) return count - 1;
        return (int)f;
    }

    /// <summary>Centre of the cell at its recorded ground height (the grid floor when the grid has none). Indices are clamped.</summary>
    public static Vector3 CellCentre(NavGrid grid, int x, int z)
    {
        x = Math.Clamp(x, 0, grid.CountX - 1);
        z = Math.Clamp(z, 0, grid.CountZ - 1);
        float y = grid.HasHeights ? grid.Heights[z * grid.CountX + x] : grid.Min.Y;
        return new Vector3(grid.Min.X + (x + 0.5f) * grid.CellSize, y, grid.Min.Z + (z + 0.5f) * grid.CellSize);
    }

    /// <summary>
    /// Ground height under a world XZ: bilinear over <see cref="NavGrid.Heights"/>, each height taken at
    /// its cell's centre, when the grid has heights; <c>Min.Y</c> when it does not. Outside the lattice
    /// the edge cells extend outward, so the function is total.
    ///
    /// <para>Samples on impassable cells are left out of the blend while any passable one remains,
    /// because the shipped grids do not record ground under walls: on Howling Abyss 47,030 of 58,776
    /// cells hold a height of exactly 0 (46,939 of them blocked) while the map's real ground runs -187
    /// to -124, so a plain blend within half a cell of any wall would climb toward 0, above the map.
    /// Summoner's Rift has the same stub (33,650 zero cells, 33,163 blocked) — it just happens to have
    /// ground at 0 too, so nothing looked wrong there. A point whose four samples are all impassable
    /// gets the plain blend, which is the wall's own stub.</para>
    /// </summary>
    public static float GroundHeight(NavGrid grid, Vector3 world)
    {
        if (!grid.HasHeights) return grid.Min.Y;
        int w = grid.CountX, h = grid.CountZ;
        double u = (world.X - grid.Min.X) / grid.CellSize - 0.5;
        double v = (world.Z - grid.Min.Z) / grid.CellSize - 0.5;
        if (!double.IsFinite(u)) u = 0;
        if (!double.IsFinite(v)) v = 0;
        u = Math.Clamp(u, 0, w - 1);
        v = Math.Clamp(v, 0, h - 1);
        int x0 = (int)Math.Floor(u), z0 = (int)Math.Floor(v);
        int x1 = Math.Min(x0 + 1, w - 1), z1 = Math.Min(z0 + 1, h - 1);
        float fx = (float)(u - x0), fz = (float)(v - z0);
        float[] hs = grid.Heights;
        float h00 = hs[z0 * w + x0], h10 = hs[z0 * w + x1], h01 = hs[z1 * w + x0], h11 = hs[z1 * w + x1];
        float w00 = (1f - fx) * (1f - fz), w10 = fx * (1f - fz), w01 = (1f - fx) * fz, w11 = fx * fz;

        if (grid.HasFlags)
        {
            ushort mask = ImpassableMask;
            bool p00 = IsWalkable(grid, x0, z0, mask), p10 = IsWalkable(grid, x1, z0, mask);
            bool p01 = IsWalkable(grid, x0, z1, mask), p11 = IsWalkable(grid, x1, z1, mask);
            if (p00 || p10 || p01 || p11)
            {
                if (!p00) w00 = 0f;
                if (!p10) w10 = 0f;
                if (!p01) w01 = 0f;
                if (!p11) w11 = 0f;
                float sum = w00 + w10 + w01 + w11;
                if (sum > 1e-6f) return (w00 * h00 + w10 * h10 + w01 * h01 + w11 * h11) / sum;
                // All the weight sat on wall samples (the point is at a wall cell's centre): the ground
                // the wall stands on is the mean of the passable samples beside it.
                float acc = 0f; int n = 0;
                if (p00) { acc += h00; n++; }
                if (p10) { acc += h10; n++; }
                if (p01) { acc += h01; n++; }
                if (p11) { acc += h11; n++; }
                return acc / n;
            }
        }
        return w00 * h00 + w10 * h10 + w01 * h01 + w11 * h11;
    }

    /// <summary>
    /// The input when it already stands on a walkable cell; otherwise the centre of the nearest walkable
    /// cell within <paramref name="maxRadiusCells"/> (Chebyshev rings, nearest by XZ distance to the
    /// point); the input unchanged when there is none.
    /// </summary>
    public static Vector3 SnapToWalkable(NavGrid grid, Vector3 world, int maxRadiusCells = 8) =>
        SnapToWalkable(grid, world, maxRadiusCells, ImpassableMask);

    internal static Vector3 SnapToWalkable(NavGrid grid, Vector3 world, int maxRadiusCells, ushort impassable)
    {
        if (IsWalkable(grid, world, impassable)) return world;
        if (!grid.HasFlags) return world;

        var (cx, cz) = CellOf(grid, world);
        int bestX = -1, bestZ = -1;
        float best = float.PositiveInfinity;

        void Consider(int x, int z)
        {
            if (!IsWalkable(grid, x, z, impassable)) return;
            float dx = grid.Min.X + (x + 0.5f) * grid.CellSize - world.X;
            float dz = grid.Min.Z + (z + 0.5f) * grid.CellSize - world.Z;
            float d = dx * dx + dz * dz;
            if (d < best) { best = d; bestX = x; bestZ = z; }
        }

        for (int r = 0; r <= Math.Max(0, maxRadiusCells); r++)
        {
            // Every centre in ring r is at least (r - 0.5) cells from a point inside the origin cell, so
            // once the best so far is closer than that, no later ring can beat it.
            if (bestX >= 0 && (r - 0.5f) * grid.CellSize >= MathF.Sqrt(best)) break;
            if (r == 0) { Consider(cx, cz); continue; }
            for (int dx = -r; dx <= r; dx++) { Consider(cx + dx, cz - r); Consider(cx + dx, cz + r); }
            for (int dz = -r + 1; dz <= r - 1; dz++) { Consider(cx - r, cz + dz); Consider(cx + r, cz + dz); }
        }

        return bestX >= 0 ? CellCentre(grid, bestX, bestZ) : world;
    }

    // ================================================================= line of walk

    /// <summary>
    /// True when every cell the segment crosses is walkable — a supercover walk over the lattice
    /// (Amanatides &amp; Woo), with a corner crossing treated like a diagonal move: both cells beside the
    /// corner must be walkable. Both endpoints must lie inside the grid.
    /// </summary>
    public static bool HasLineOfWalk(NavGrid grid, Vector3 a, Vector3 b) => HasLineOfWalk(grid, a, b, ImpassableMask);

    internal static bool HasLineOfWalk(NavGrid grid, Vector3 a, Vector3 b, ushort impassable)
    {
        if (!grid.HasFlags) return false;
        if (!TryCellOf(grid, a, out int x, out int z) || !TryCellOf(grid, b, out int xEnd, out int zEnd)) return false;
        if (!IsWalkable(grid, x, z, impassable)) return false;

        double u0 = (a.X - grid.Min.X) / (double)grid.CellSize, v0 = (a.Z - grid.Min.Z) / (double)grid.CellSize;
        double u1 = (b.X - grid.Min.X) / (double)grid.CellSize, v1 = (b.Z - grid.Min.Z) / (double)grid.CellSize;
        double du = u1 - u0, dv = v1 - v0;
        int stepX = du > 0 ? 1 : du < 0 ? -1 : 0;
        int stepZ = dv > 0 ? 1 : dv < 0 ? -1 : 0;

        // t (0..1 along the segment) at which the next X / Z boundary is crossed, and the t per cell.
        double tDeltaX = stepX == 0 ? double.PositiveInfinity : 1.0 / Math.Abs(du);
        double tDeltaZ = stepZ == 0 ? double.PositiveInfinity : 1.0 / Math.Abs(dv);
        double tMaxX = stepX == 0 ? double.PositiveInfinity : (stepX > 0 ? (x + 1) - u0 : u0 - x) * tDeltaX;
        double tMaxZ = stepZ == 0 ? double.PositiveInfinity : (stepZ > 0 ? (z + 1) - v0 : v0 - z) * tDeltaZ;

        const double tie = 1e-9;
        int guard = Math.Abs(xEnd - x) + Math.Abs(zEnd - z) + 2;
        while ((x != xEnd || z != zEnd) && guard-- > 0)
        {
            if (stepX != 0 && stepZ != 0 && Math.Abs(tMaxX - tMaxZ) <= tie)
            {
                // Through (or within rounding of) a lattice corner: no corner cutting, so both side
                // cells must be walkable before the diagonal cell is entered.
                if (!IsWalkable(grid, x + stepX, z, impassable) || !IsWalkable(grid, x, z + stepZ, impassable)) return false;
                x += stepX; z += stepZ;
                tMaxX += tDeltaX; tMaxZ += tDeltaZ;
            }
            else if (tMaxX < tMaxZ) { x += stepX; tMaxX += tDeltaX; }
            else { z += stepZ; tMaxZ += tDeltaZ; }

            if (!IsWalkable(grid, x, z, impassable)) return false;
        }
        return x == xEnd && z == zEnd;
    }

    // ================================================================= the route

    /// <summary>
    /// A* over the 8-connected lattice, 1 per orthogonal step and sqrt(2) per diagonal with the octile
    /// heuristic. Both ends are snapped to walkable (<see cref="SnapToWalkable"/>, 8 cells); the result
    /// holds world-space waypoints INCLUDING both ends, each at ground height, after greedy
    /// string-pulling with <see cref="HasLineOfWalk"/> so a straight corridor is two points. Empty when
    /// no route exists, when either end has no walkable cell within reach, or when the search expands
    /// more than <paramref name="maxExpandedCells"/> cells (the fountain-to-fountain route on Summoner's
    /// Rift expands 7,251; the default is a runaway guard, not a budget).
    /// </summary>
    public static IReadOnlyList<Vector3> FindPath(NavGrid grid, Vector3 from, Vector3 to, int maxExpandedCells = 200_000) =>
        FindPathCore(grid, from, to, maxExpandedCells, ImpassableMask, out _);

    /// <summary>The search with the mask as a parameter and the expansion count reported — what the census measures with.</summary>
    internal static IReadOnlyList<Vector3> FindPathCore(NavGrid grid, Vector3 from, Vector3 to, int maxExpandedCells,
        ushort impassable, out int expanded)
    {
        expanded = 0;
        if (!grid.HasFlags) return Array.Empty<Vector3>();

        var start = SnapToWalkable(grid, from, DefaultSnapRadius, impassable);
        var goal = SnapToWalkable(grid, to, DefaultSnapRadius, impassable);
        if (!IsWalkable(grid, start, impassable) || !IsWalkable(grid, goal, impassable)) return Array.Empty<Vector3>();

        var (sx, sz) = CellOf(grid, start);
        var (gx, gz) = CellOf(grid, goal);
        int w = grid.CountX, h = grid.CountZ;
        int s = sz * w + sx, g = gz * w + gx;
        if (s == g) return new[] { AtGround(grid, start), AtGround(grid, goal) };

        var gScore = new float[w * h];
        Array.Fill(gScore, float.PositiveInfinity);
        var parent = new int[w * h];
        Array.Fill(parent, -1);
        var closed = new bool[w * h];
        var open = new MinHeap();

        gScore[s] = 0f;
        open.Push(Octile(sx, sz, gx, gz), s);
        bool found = false;

        while (open.Count > 0)
        {
            int cur = open.Pop();
            if (closed[cur]) continue;       // a stale duplicate; the cheaper entry was already taken
            closed[cur] = true;
            expanded++;
            if (cur == g) { found = true; break; }
            if (expanded > maxExpandedCells) return Array.Empty<Vector3>();

            int cx = cur % w, cz = cur / w;
            float gc = gScore[cur];
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = cx + dx, nz = cz + dz;
                    if (!IsWalkable(grid, nx, nz, impassable)) continue;
                    bool diagonal = dx != 0 && dz != 0;
                    if (diagonal && (!IsWalkable(grid, cx + dx, cz, impassable) || !IsWalkable(grid, cx, cz + dz, impassable))) continue;
                    int ni = nz * w + nx;
                    if (closed[ni]) continue;
                    float ng = gc + (diagonal ? Sqrt2 : 1f);
                    if (ng < gScore[ni])
                    {
                        gScore[ni] = ng;
                        parent[ni] = cur;
                        open.Push(ng + Octile(nx, nz, gx, gz), ni);
                    }
                }
        }
        if (!found) return Array.Empty<Vector3>();

        var cells = new List<int>();
        for (int c = g; c != -1; c = parent[c]) cells.Add(c);
        cells.Reverse();

        // The raw polyline: the snapped start, the centres of the interior cells, the snapped goal. Every
        // consecutive pair is joined by a walkable segment - an orthogonal step stays inside the two cells
        // and a diagonal one inside the 2x2 block the no-corner-cutting rule just cleared - so the pull
        // below can always advance.
        var raw = new List<Vector3>(cells.Count + 2) { start };
        for (int i = 1; i < cells.Count - 1; i++) raw.Add(CellCentre(grid, cells[i] % w, cells[i] / w));
        raw.Add(goal);

        var pulled = new List<Vector3> { AtGround(grid, raw[0]) };
        for (int i = 0; i < raw.Count - 1;)
        {
            int j = raw.Count - 1;
            while (j > i + 1 && !HasLineOfWalk(grid, raw[i], raw[j], impassable)) j--;
            pulled.Add(AtGround(grid, raw[j]));
            i = j;
        }
        return pulled;
    }

    private static Vector3 AtGround(NavGrid grid, Vector3 p) => new(p.X, GroundHeight(grid, p), p.Z);

    private static float Octile(int x0, int z0, int x1, int z1)
    {
        int dx = Math.Abs(x1 - x0), dz = Math.Abs(z1 - z0);
        return dx + dz + (Sqrt2 - 2f) * Math.Min(dx, dz);
    }

    /// <summary>A binary min-heap on f, with lazy deletion handled by the caller's closed set.</summary>
    private sealed class MinHeap
    {
        private float[] _f = new float[256];
        private int[] _cell = new int[256];
        public int Count { get; private set; }

        public void Push(float f, int cell)
        {
            if (Count == _f.Length)
            {
                Array.Resize(ref _f, Count * 2);
                Array.Resize(ref _cell, Count * 2);
            }
            int i = Count++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_f[p] <= f) break;
                _f[i] = _f[p]; _cell[i] = _cell[p];
                i = p;
            }
            _f[i] = f; _cell[i] = cell;
        }

        public int Pop()
        {
            int top = _cell[0];
            Count--;
            if (Count > 0)
            {
                float f = _f[Count];
                int cell = _cell[Count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1;
                    if (l >= Count) break;
                    int r = l + 1;
                    int m = r < Count && _f[r] < _f[l] ? r : l;
                    if (_f[m] >= f) break;
                    _f[i] = _f[m]; _cell[i] = _cell[m];
                    i = m;
                }
                _f[i] = f; _cell[i] = cell;
            }
            return top;
        }
    }
}
