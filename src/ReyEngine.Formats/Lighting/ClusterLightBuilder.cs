using System.Numerics;

namespace ReyEngine.Formats.Lighting;

/// <summary>One light as Riot's clustered forward path wants it: a sphere with a colour and a strength.
/// Deliberately NOT <see cref="PointLight"/> - the editor's light is authored data, this is the packed
/// GPU form after the fit/scale knobs have already been applied by the caller.</summary>
public readonly record struct ClusterLight(Vector3 Position, float Radius, Vector3 Color, float Intensity);

/// <summary>The three CPU-side structures Riot's pixel shaders read, ready to upload.
/// Every field maps to exactly one binding - see <see cref="ClusterLightBuilder"/>.</summary>
public sealed class ClusterLightGrid
{
    public required int DimX { get; init; }
    public required int DimY { get; init; }
    public required int DimZ { get; init; }

    /// <summary>The <c>ClusterData</c> cbuffer's <c>WORLD_TO_CLUSTER_TRANSFORM</c>, as 16 floats in
    /// CONSTANT-REGISTER order: [0..3] is cb register 0, [4..7] register 1, [8..11] register 2.
    ///
    /// <para>The shader does <c>dp4 c.x, float4(worldPos,1), cbN[0]</c> / <c>[1]</c> / <c>[2]</c> (Mantis
    /// blob 27 lines 636-638, DefaultEnv_Flat blob 226 lines 208-210), so this array is written into the
    /// buffer verbatim and is NOT subject to any row/column-major transpose convention. Getting that wrong
    /// silently shears the grid rather than failing.</para></summary>
    public required float[] WorldToCluster { get; init; }

    /// <summary><c>CLUSTER_MAX_CLAMP</c>: (DimX-1, DimY-1, DimZ-1) as floats. The shader clamps the cell
    /// coordinate to [0, this] and then truncates, so it is the last valid INDEX, not the dimension.</summary>
    public required Vector3 MaxClamp { get; init; }

    /// <summary><c>CLUSTER_MAP_SharedTexture</c> contents: one uint per cell, laid out x fastest, then y,
    /// then z - the memory order a Texture3D upload wants. Each value is the index, in uint4 units, of that
    /// cell's header inside <see cref="Data"/>.</summary>
    public required uint[] Map { get; init; }

    /// <summary><c>CLUSTER_DATA_BUFFER_SharedDataBuffer</c> contents as raw uints; four per structured
    /// element. Element 0 is always the shared all-zero header every empty cell points at.</summary>
    public required uint[] Data { get; init; }

    /// <summary>Lights that survived validation and were binned. A light with a non-positive or
    /// non-finite radius contributes nothing and is dropped here rather than producing an infinite
    /// reciprocal in the record.</summary>
    public int LightCount { get; init; }

    /// <summary>Cells holding at least one light.</summary>
    public int NonEmptyCells { get; init; }

    /// <summary>Distinct cell CONTENTS emitted. Cells covered by the same light set share one block, so
    /// this is what actually decides the buffer size.</summary>
    public int DistinctBlocks { get; init; }

    /// <summary>(cell, light) pairs refused by the per-cell cap. Non-zero means some cells are lit by
    /// fewer lights than they should be - reported rather than silently truncated.</summary>
    public int DroppedBindings { get; init; }

    public int CellCount => DimX * DimY * DimZ;
    public int Uint4Count => Data.Length / 4;
}

/// <summary>
/// <para>M456: builds Riot's clustered-forward light data on the CPU, because nothing on the GPU side can.
/// The shader cache contains zero compute and zero geometry shaders (light-system.md §2.6), so the cluster
/// map, the data stream and the visibility masks are all engine-authored and uploaded - there is no Riot
/// shader to copy for this half.</para>
///
/// <para><b>Scope: plain point lights only</b> - loop 1 of the six the shader runs. Spot lights, stationary
/// masks, cube shadows, PCF spot shadows and light regions (loops 2-6) are deliberately absent; their
/// header counts are written as zero, which makes the shader skip each of those loops outright.</para>
///
/// <para><b>The layout, from light-system.md §2.2 and §2.4, re-verified against the disassembly:</b></para>
/// <code>
/// header uint4 at index C = CLUSTER_MAP[cell]:
///   .x &amp; 0xFFFF  point light count      (loop 1, 2 uint4s each)
///   .x &gt;&gt; 16      point + stationary     -> 0
///   .y &amp; 0xFFFF  spot + cookie          -> 0
///   .y &gt;&gt; 16      spot + cookie + mask   -> 0
///   .z &amp; 0xFFFF  point + cube shadow    -> 0
///   .z &gt;&gt; 16      spot + cookie + PCF    -> 0
///   .w &amp; 0xFFFF  the cluster's aggregate visibility mask
///   .w &gt;&gt; 16      uint4s occupied by the packed 16-bit per-light mask list
/// index C+1 .. C+maskWords          : the mask list, 8 x uint16 per uint4
/// index C+1+maskWords ..            : the light records, 2 uint4s each:
///   word0 = float4(position.xyz, 1/radius)
///   word1 = float4(colour.rgb,   intensity)
/// </code>
///
/// <para><b>The visibility mask is a two-byte AND and both halves must overlap</b> (§2.3): the shader
/// tests <c>(mask &amp; (ENV_LIGHTING_MASK &amp; 0x00FF)) != 0 &amp;&amp; (mask &amp; (ENV_LIGHTING_MASK &amp;
/// 0xFF00)) != 0</c>, first against the cluster's aggregate mask and then per light. Every mask here is
/// 0xFFFF, which passes for any object mask that also has a bit set in each byte. What the two bytes MEAN
/// is not measured, so nothing is assigned to them.</para>
///
/// <para>Renderer-free on purpose: this is where the bugs live (bit packing, float bit-casts, cell ranges),
/// and none of it needs a device to be wrong. It is tested directly.</para>
/// </summary>
public static class ClusterLightBuilder
{
    /// <summary>Default cells along each axis. World-space and uniform - the doc is explicit that this is
    /// NOT a camera froxel volume (§2.1), so the grid can be built once per light change rather than per
    /// frame. 32x8x32 is 8,192 cells: a 15,000-unit League map gets ~470-unit cells in XZ, which is the
    /// order of a Light.dat radius, while Y gets far fewer because maps are flat.</summary>
    public const int DefaultDimX = 32;
    public const int DefaultDimY = 8;
    public const int DefaultDimZ = 32;

    /// <summary>Ceiling on lights per cell. A ported Light.dat can carry ~365 lights; without a cap a
    /// pathological overlap would emit 365 x 2 uint4s in one block and the count is a uint16 anyway.</summary>
    public const int DefaultMaxLightsPerCell = 64;

    /// <summary>Every mask this builder writes, per §2.3. Both bytes set, so the AND passes for any object
    /// mask that also sets a bit in each byte - which is what <c>ENV_LIGHTING_MASK = 0xFFFF</c> does.</summary>
    public const uint VisibilityMaskAll = 0xFFFF;

    /// <summary>What every object's <c>ENV_LIGHTING_MASK</c> must carry for any light to survive §2.3.
    /// Leaving it zero culls every light in silence, which is the single easiest way to build all of this
    /// correctly and see nothing.</summary>
    public const uint EnvLightingMaskAll = 0xFFFF;

    /// <summary>
    /// Pack a light so that <paramref name="strength"/> survives in BOTH shader families - by folding it
    /// into the COLOUR and leaving <c>intensity</c> at 1.
    ///
    /// <para><b>This is not a style choice; word1.w is not read by half the shaders.</b> The PBR family
    /// (Mantis blob 27 line 698) loads word1 as <c>.xyzw</c> and multiplies radiance by <c>.w</c>. The
    /// Lambert family - <c>DefaultEnv_Flat</c>, which is what nearly every shipped map material actually
    /// uses - loads it at blob 226 line 263 as</para>
    /// <code>ld_structured ... r13.xyw, r5.w, l(0), t6.xyxz</code>
    /// <para>whose swizzle fetches buffer components x, y and z only. Component w is never read, in loop 1
    /// or in the shadowed loop (line 321). Writing the strength there and nowhere else would make the
    /// intensity slider and every per-light strength silently do nothing on the majority of map surfaces -
    /// and do something on the minority, which is worse than either.</para>
    ///
    /// <para>Folding into the colour applies it exactly once in both: Lambert computes
    /// <c>colour * albedo * NdotL * atten</c>, PBR computes <c>colour * atten * 1.0</c>. Putting it in both
    /// places would square it on Mantis. It is also how this project's own Light.dat writer already
    /// expresses a brighter light, since that format has no strength field either.</para>
    /// </summary>
    public static ClusterLight MakeLight(Vector3 position, float radius, Vector3 colour, float strength)
        => new(position, radius, colour * strength, 1f);

    /// <summary>Bin <paramref name="lights"/> into a world-space uniform grid covering
    /// <paramref name="boundsMin"/>..<paramref name="boundsMax"/> EXPANDED to contain every light sphere,
    /// so a light placed outside the geometry still lands in a real cell instead of being clamped onto the
    /// border and lighting the whole edge of the map.</summary>
    public static ClusterLightGrid Build(
        IReadOnlyList<ClusterLight> lights,
        Vector3 boundsMin, Vector3 boundsMax,
        int dimX = DefaultDimX, int dimY = DefaultDimY, int dimZ = DefaultDimZ,
        int maxLightsPerCell = DefaultMaxLightsPerCell)
    {
        dimX = Math.Clamp(dimX, 1, 256);
        dimY = Math.Clamp(dimY, 1, 256);
        dimZ = Math.Clamp(dimZ, 1, 256);
        maxLightsPerCell = Math.Clamp(maxLightsPerCell, 1, 0xFFFF);

        // Drop what cannot be packed. A zero or negative radius has no reciprocal to write, and a NaN
        // anywhere in the record poisons the shader's distance test for the whole cell.
        var usable = new List<ClusterLight>(lights.Count);
        foreach (var l in lights)
        {
            if (!(l.Radius > 0f) || !float.IsFinite(l.Radius)) continue;
            if (!float.IsFinite(l.Position.X) || !float.IsFinite(l.Position.Y) || !float.IsFinite(l.Position.Z))
                continue;
            usable.Add(l);
        }

        var lo = boundsMin;
        var hi = boundsMax;
        if (!float.IsFinite(lo.X) || !float.IsFinite(hi.X) || hi.X < lo.X) { lo = Vector3.Zero; hi = Vector3.Zero; }
        foreach (var l in usable)
        {
            lo = Vector3.Min(lo, l.Position - new Vector3(l.Radius));
            hi = Vector3.Max(hi, l.Position + new Vector3(l.Radius));
        }

        // A degenerate axis would divide by zero. One unit of slack keeps the affine map finite and puts
        // everything in cell 0 of that axis, which is exactly right for a flat scene.
        var extent = hi - lo;
        if (!(extent.X > 1e-4f)) { extent.X = 1f; }
        if (!(extent.Y > 1e-4f)) { extent.Y = 1f; }
        if (!(extent.Z > 1e-4f)) { extent.Z = 1f; }

        float sx = dimX / extent.X, sy = dimY / extent.Y, sz = dimZ / extent.Z;
        float bx = -lo.X * sx, by = -lo.Y * sy, bz = -lo.Z * sz;

        // Constant-register order, NOT a Matrix4x4 - see ClusterLightGrid.WorldToCluster.
        var xform = new[]
        {
            sx, 0f, 0f, bx,
            0f, sy, 0f, by,
            0f, 0f, sz, bz,
            0f, 0f, 0f, 1f,
        };

        int cellCount = dimX * dimY * dimZ;
        var cells = new List<int>?[cellCount];
        int dropped = 0, nonEmpty = 0;

        for (int i = 0; i < usable.Count; i++)
        {
            var l = usable[i];
            // Conservative AABB of the sphere. Over-inclusion is free: a light whose sphere misses the
            // cell still evaluates to atten <= 0 in the shader and is skipped there. Under-inclusion
            // would be a hard edge where a pool of light stops mid-air.
            int x0 = CellIndex(l.Position.X - l.Radius, sx, bx, dimX);
            int x1 = CellIndex(l.Position.X + l.Radius, sx, bx, dimX);
            int y0 = CellIndex(l.Position.Y - l.Radius, sy, by, dimY);
            int y1 = CellIndex(l.Position.Y + l.Radius, sy, by, dimY);
            int z0 = CellIndex(l.Position.Z - l.Radius, sz, bz, dimZ);
            int z1 = CellIndex(l.Position.Z + l.Radius, sz, bz, dimZ);

            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int c = x + y * dimX + z * dimX * dimY;
                var list = cells[c];
                if (list is null) { cells[c] = list = new List<int>(4); nonEmpty++; }
                if (list.Count >= maxLightsPerCell) { dropped++; continue; }
                list.Add(i);
            }
        }

        var map = new uint[cellCount];                 // 0 = the shared empty header, written below
        var data = new List<uint>(4 + nonEmpty * 8);
        data.AddRange(new uint[] { 0, 0, 0, 0 });      // element 0: counts 0, mask 0 -> the shader skips it

        var blocks = new Dictionary<int[], uint>(IndexListComparer.Instance);
        for (int c = 0; c < cellCount; c++)
        {
            var list = cells[c];
            if (list is null || list.Count == 0) continue;

            // Cells covered by the same light set share one block. Neighbours usually do, and this is what
            // keeps a 8,192-cell grid over a few hundred lights to a few thousand uint4s.
            var key = list.ToArray();
            if (!blocks.TryGetValue(key, out uint at))
            {
                at = (uint)(data.Count / 4);
                blocks[key] = at;
                AppendBlock(data, key, usable);
            }
            map[c] = at;
        }

        return new ClusterLightGrid
        {
            DimX = dimX, DimY = dimY, DimZ = dimZ,
            WorldToCluster = xform,
            MaxClamp = new Vector3(dimX - 1, dimY - 1, dimZ - 1),
            Map = map,
            Data = data.ToArray(),
            LightCount = usable.Count,
            NonEmptyCells = nonEmpty,
            DistinctBlocks = blocks.Count,
            DroppedBindings = dropped,
        };
    }

    /// <summary>The empty grid: one all-zero header, every cell pointing at it. What a scene with no
    /// lights uploads, and what the shader must see for the light loop to be a no-op rather than a read of
    /// whatever was bound last frame.</summary>
    public static ClusterLightGrid Empty(
        int dimX = DefaultDimX, int dimY = DefaultDimY, int dimZ = DefaultDimZ)
        => Build(Array.Empty<ClusterLight>(), Vector3.Zero, Vector3.Zero, dimX, dimY, dimZ);

    /// <summary>One light record: 2 uint4s, §2.4. Public so the packing can be asserted directly rather
    /// than fished back out of a whole buffer.</summary>
    public static void WriteLightRecord(Span<uint> dst, in ClusterLight l)
    {
        dst[0] = BitConverter.SingleToUInt32Bits(l.Position.X);
        dst[1] = BitConverter.SingleToUInt32Bits(l.Position.Y);
        dst[2] = BitConverter.SingleToUInt32Bits(l.Position.Z);
        // The shader computes atten = 1 - saturate(dist * word0.w), so this is 1/radius and the falloff
        // is LINEAR. Not inverse-square, not smoothstep (light-system.md §7.3).
        dst[3] = BitConverter.SingleToUInt32Bits(1f / l.Radius);
        dst[4] = BitConverter.SingleToUInt32Bits(l.Color.X);
        dst[5] = BitConverter.SingleToUInt32Bits(l.Color.Y);
        dst[6] = BitConverter.SingleToUInt32Bits(l.Color.Z);
        // Read by the PBR family (Mantis) and ignored by the Lambert family (DefaultEnv_Flat), which loads
        // word1 as .xyz only. Writing it is right for both; what it MEANS is item 1 of the doc's unknowns.
        dst[7] = BitConverter.SingleToUInt32Bits(l.Intensity);
    }

    /// <summary>How many uint4s a block of <paramref name="lightCount"/> point lights occupies: the header,
    /// the packed mask list, and two words per light.</summary>
    public static int BlockUint4Count(int lightCount) => 1 + MaskWordCount(lightCount) + lightCount * 2;

    /// <summary>uint4s the packed 16-bit mask list needs - 8 masks per uint4, rounded up.</summary>
    public static int MaskWordCount(int lightCount) => (lightCount + 7) / 8;

    /// <summary>The header uint4 for a block that holds only plain point lights.</summary>
    public static (uint X, uint Y, uint Z, uint W) Header(int pointLightCount)
    {
        int maskWords = MaskWordCount(pointLightCount);
        return (
            (uint)pointLightCount & 0xFFFFu,   // loop 1 count; the high half (loop 2) stays 0
            0u,                                 // loops 4 and 5
            0u,                                 // loops 3 and 6
            ((uint)maskWords << 16) | VisibilityMaskAll);
    }

    private static void AppendBlock(List<uint> data, int[] indices, List<ClusterLight> lights)
    {
        var (hx, hy, hz, hw) = Header(indices.Length);
        data.Add(hx); data.Add(hy); data.Add(hz); data.Add(hw);

        // The packed per-light mask list, all bits set. The exact bit-walk is item 3 of the doc's unknowns
        // (it was reconstructed through heavy register aliasing), and an all-ones list is correct under
        // every reading of it - which is why loop 1 can ship before that is settled.
        int maskWords = MaskWordCount(indices.Length);
        for (int i = 0; i < maskWords * 4; i++) data.Add(0xFFFFFFFFu);

        Span<uint> rec = stackalloc uint[8];
        foreach (int idx in indices)
        {
            WriteLightRecord(rec, lights[idx]);
            for (int i = 0; i < 8; i++) data.Add(rec[i]);
        }
    }

    /// <summary>World coordinate to a clamped integer cell index along one axis, matching the shader's
    /// own <c>max(0) / min(clamp) / ftoi</c> (ftoi truncates toward zero, and the value is never negative
    /// after the max).</summary>
    private static int CellIndex(float world, float scale, float bias, int dim)
    {
        float c = world * scale + bias;
        if (!float.IsFinite(c)) return 0;
        int i = (int)MathF.Floor(c);
        return Math.Clamp(i, 0, dim - 1);
    }

    /// <summary>Structural equality over the per-cell light index list, so two cells covered by the same
    /// lights share a block. Ordinary reference equality would defeat the dedup entirely.</summary>
    private sealed class IndexListComparer : IEqualityComparer<int[]>
    {
        public static readonly IndexListComparer Instance = new();

        public bool Equals(int[]? a, int[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public int GetHashCode(int[] a)
        {
            unchecked
            {
                int h = 17;
                foreach (int v in a) h = h * 31 + v;
                return h;
            }
        }
    }
}
