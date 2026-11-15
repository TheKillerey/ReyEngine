using System.Collections;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Lighting;

/// <summary>
/// M900: one <c>MapDynamicSpotlight</c> placement (class 0x9063268e), in the terms the editor uses.
/// <see cref="Transform"/> is the authored matrix (row-vector, translation in the last row); the light shines
/// along its local -Z. The cone is the <c>MapFrustum</c> base class: a SQUARE frustum from <see cref="Fov"/> and
/// <see cref="Aspect"/>, between <see cref="Near"/> and <see cref="Far"/>.
/// </summary>
/// <param name="HasTransform">False for an entry that authors no transform (read as identity).</param>
/// <param name="CookieHash">The <c>ProjectedTexture</c> wad-chunk hash (64-bit on 16.22; the hash of the String on older
/// bins), 0 = none authored.</param>
/// <param name="CookiePath">The path to show: the String as authored, the resolved name of the link, or its hex form.</param>
public sealed record DynamicSpotlight(
    MapPlacementId Id, string Name, Matrix4x4 Transform, Vector4 Color, float Fov, float Far, float Near, float Aspect,
    ulong CookieHash, string CookiePath, uint VisibilityController, int VisibilityFlags, bool HasVisibilityFlags)
{
    public Vector3 Position => Transform.Translation;

    /// <summary>World direction the light shines in: local -Z (confirmed in game on 16.22).</summary>
    public Vector3 Forward => DynamicSpotlights.ForwardOf(Transform);
}

/// <summary>
/// M900: read and write the League spotlights, <c>MapDynamicSpotlight</c> - the sibling of
/// <see cref="DynamicPointLights"/>.
///
/// <para><b>Measured, not guessed.</b> Riot ships ZERO of these (0 of 26,196 PROP bins on 16.22), so the wire form is the
/// point-light form (a Hash-keyed Pointer entry of <c>MapPlaceableContainer.items</c>) plus fields checked against the LTK meta
/// schema: transform, name, then only the non-default fields - fov, far, Near, Aspect, ProjectedTexture (File), Color (Vec4),
/// VisibilityController (Link). Defaults (type 1, HdrScale 5, intensity 1, Impact 3, LightChannels 1, Aspect 1, Orthographic
/// false, OrthoHeight 200, mVisibilityFlags 255) are left ABSENT, exactly like the 168 in-game point lights. A 16.22 replay
/// confirmed: the light projects along local -Z, the frustum is square, falloff is linear (1 - dist / far), and the shader does
/// not read <c>intensity</c> - brightness lives in <c>Color</c>.</para>
///
/// <para>Reading and writing go through the same identity (container, item key) as every other placement, so an entry the
/// editor did not touch round-trips byte for byte: <see cref="Write"/> is a diff turned into <see cref="MapPlacementEdit"/>s and
/// applied by <see cref="MapPlaceableWriter"/>, which refuses a rewrite that changed anything not asked for.</para>
/// </summary>
public static class DynamicSpotlights
{
    public const float DefaultFov = 60f;
    public const float DefaultFar = 500f;
    public const float DefaultNear = 10f;
    public const float DefaultAspect = 1f;
    public static readonly Vector4 DefaultColor = Vector4.One;

    public static readonly uint SpotlightClass = HashAlgorithms.Fnv1a("MapDynamicSpotlight");   // 0x9063268e
    private static readonly uint ContainerClass = HashAlgorithms.Fnv1a("MapPlaceableContainer");
    private static readonly uint F_transform = HashAlgorithms.Fnv1a("transform");
    private static readonly uint F_name = HashAlgorithms.Fnv1a("name");
    private static readonly uint F_color = HashAlgorithms.Fnv1a("Color");
    private static readonly uint F_fov = HashAlgorithms.Fnv1a("fov");
    private static readonly uint F_far = HashAlgorithms.Fnv1a("far");
    private static readonly uint F_near = HashAlgorithms.Fnv1a("Near");
    private static readonly uint F_aspect = HashAlgorithms.Fnv1a("Aspect");
    private static readonly uint F_projected = HashAlgorithms.Fnv1a("ProjectedTexture");
    private static readonly uint F_controller = HashAlgorithms.Fnv1a("VisibilityController");
    private static readonly uint F_visibilityFlags = HashAlgorithms.Fnv1a("mVisibilityFlags");
    private static readonly uint F_items = HashAlgorithms.Fnv1a("items");

    /// <summary>The direction the light shines in: local -Z in world space (row-vector matrix, so the local Z axis is the
    /// third ROW). A degenerate matrix falls back to world down, so nothing downstream normalises a zero vector.</summary>
    public static Vector3 ForwardOf(in Matrix4x4 m)
    {
        var f = -new Vector3(m.M31, m.M32, m.M33);
        float len = f.Length();
        return len > 1e-6f && float.IsFinite(len) ? f / len : -Vector3.UnitY;
    }

    /// <summary>Every spotlight in a map bin, in file order. Never throws - an unreadable bin yields none.</summary>
    /// <param name="resolveWadPath">Names the cookie's wad-chunk hash (the 16.17+ File form); null shows the hex.</param>
    public static IReadOnlyList<DynamicSpotlight> Read(byte[] materialsBin, Func<ulong, string?>? resolveWadPath = null)
    {
        var found = new List<DynamicSpotlight>();
        if (materialsBin is null || materialsBin.Length == 0) return found;

        BinTree tree;
        try { tree = SafeBinTree.Parse(materialsBin); }
        catch { return found; }

        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != ContainerClass || !o.Properties.TryGetValue(F_items, out var ip) || ip is not BinTreeMap map) continue;
            foreach (var kv in map)
                if (kv.Value is BinTreeStruct s && s.ClassHash == SpotlightClass)
                    found.Add(FromStruct(s, new MapPlacementId(o.PathHash, kv.Key is BinTreeHash h ? h.Value : 0u), resolveWadPath));
        }
        return found;
    }

    private static DynamicSpotlight FromStruct(BinTreeStruct s, MapPlacementId id, Func<ulong, string?>? resolve)
    {
        var p = s.Properties;
        var transform = p.TryGetValue(F_transform, out var tp) && tp is BinTreeMatrix44 m ? m.Value : Matrix4x4.Identity;
        string name = p.TryGetValue(F_name, out var np) && np is BinTreeString ns ? ns.Value : "";
        var color = p.TryGetValue(F_color, out var cp) && cp is BinTreeVector4 cv ? cv.Value : DefaultColor;
        float F(uint field, float fallback) => p.TryGetValue(field, out var fp) && fp is BinTreeF32 f ? f.Value : fallback;
        p.TryGetValue(F_projected, out var tex);
        ulong cookieHash = BinTexturePath.HashOf(tex);
        string cookie = BinTexturePath.Read(tex, resolve);
        uint controller = p.TryGetValue(F_controller, out var vc) && vc is BinTreeObjectLink l ? l.Value : 0u;
        bool hasFlags = p.TryGetValue(F_visibilityFlags, out var vf);
        int flags = vf switch
        {
            BinTreeU8 u8 => u8.Value,
            BinTreeU16 u16 => u16.Value,
            BinTreeU32 u32 => unchecked((int)u32.Value),
            _ => 255,
        };
        return new DynamicSpotlight(id, name, transform, color, F(F_fov, DefaultFov), F(F_far, DefaultFar),
            F(F_near, DefaultNear), F(F_aspect, DefaultAspect), cookieHash, cookie, controller, flags, hasFlags);
    }

    /// <summary>
    /// Replace the bin's spotlights with <paramref name="spotlights"/>: an entry whose <c>Id</c> names an existing spotlight is
    /// EDITED in place (only the fields that differ are rewritten, so unknown fields survive), one with no Id is CREATED in the
    /// container that already holds the most spotlights (else the first container), and every existing spotlight not listed is
    /// REMOVED.
    /// </summary>
    /// <returns>The rewritten bin, or null with <paramref name="error"/> when it could not be parsed or the rewrite was refused;
    /// callers keep the original. An unchanged list returns the input bytes untouched.</returns>
    public static byte[]? Write(byte[] materialsBin, IEnumerable<DynamicSpotlight> spotlights, out string? error)
    {
        error = null;
        ArgumentNullException.ThrowIfNull(spotlights);
        if (materialsBin is null || materialsBin.Length == 0) { error = "empty bin"; return null; }

        var existing = Read(materialsBin).ToDictionary(s => s.Id);
        var wanted = spotlights.ToList();
        var edits = new List<MapPlacementEdit>();

        var kept = new HashSet<MapPlacementId>();
        var creating = new List<DynamicSpotlight>();
        foreach (var w in wanted)
        {
            if (w.Id.IsValid && existing.TryGetValue(w.Id, out var old))
            {
                kept.Add(w.Id);
                var e = EditFor(old, w);
                if (e is not null) edits.Add(e);
            }
            else creating.Add(w);
        }
        foreach (var gone in existing.Keys.Where(k => !kept.Contains(k)))
            edits.Add(new MapPlacementEdit(gone) { Remove = true });

        if (creating.Count > 0)
        {
            BinTree tree;
            try { tree = SafeBinTree.Parse(materialsBin); }
            catch (Exception ex) { error = $"could not parse the .bin: {ex.Message}"; return null; }
            var container = TargetContainer(tree);
            if (container is null) { error = "the bin has no placeable container to put a spotlight in"; return null; }
            var items = (BinTreeMap)container.Properties[F_items];
            var used = items.Where(e => e.Key is BinTreeHash).Select(e => ((BinTreeHash)e.Key).Value).ToHashSet();
            uint seed = 0x5107u;
            foreach (var c in creating)
            {
                uint key = c.Id.IsValid && c.Id.ContainerHash == container.PathHash && !used.Contains(c.Id.ItemKey)
                    ? c.Id.ItemKey : NextKey(used, ref seed);
                used.Add(key);
                edits.Add(CreateEdit(new MapPlacementId(container.PathHash, key), c));
            }
        }

        if (edits.Count == 0) return materialsBin;
        return MapPlaceableWriter.WriteEdits(materialsBin, edits, out error);
    }

    private static uint NextKey(HashSet<uint> used, ref uint seed)
    {
        uint candidate = seed++ * 2654435761u + 0x9E3779B9u;
        while (candidate == 0 || used.Contains(candidate)) candidate++;
        return candidate;
    }

    /// <summary>The placeable container new spotlights go into: the one that already holds the most, else the one with the most
    /// content (the point-light rule), so the client demonstrably loads it.</summary>
    public static BinTreeObject? TargetContainer(BinTree tree)
    {
        BinTreeObject? best = null;
        int bestSpots = -1, bestCount = -1;
        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != ContainerClass || !o.Properties.TryGetValue(F_items, out var ip) || ip is not BinTreeMap m) continue;
            int spots = m.Count(e => e.Value is BinTreeStruct s && s.ClassHash == SpotlightClass);
            if (spots > bestSpots || (spots == bestSpots && m.Count > bestCount)) { best = o; bestSpots = spots; bestCount = m.Count; }
        }
        return best;
    }

    /// <summary>The creation edit for a spotlight at a known id: transform, name, Color, fov and far always; Near, Aspect and the
    /// cookie only when they differ from the schema default / are set - absent stays absent.</summary>
    public static MapPlacementEdit CreateEdit(MapPlacementId id, DynamicSpotlight s) => new(id)
    {
        CreateSpotlight = true,
        Transform = s.Transform,
        Name = s.Name,
        SpotColor = s.Color,
        SpotFov = s.Fov,
        SpotFar = s.Far,
        SpotNear = s.Near != DefaultNear ? s.Near : null,
        SpotAspect = s.Aspect != DefaultAspect ? s.Aspect : null,
        SpotCookie = s.CookiePath.Length > 0 ? s.CookiePath : null,
        VisibilityFlags = s.HasVisibilityFlags ? s.VisibilityFlags : null,
    };

    /// <summary>The edit that turns <paramref name="old"/> into <paramref name="now"/>, or null when nothing differs.</summary>
    public static MapPlacementEdit? EditFor(DynamicSpotlight old, DynamicSpotlight now)
    {
        var e = new MapPlacementEdit(old.Id)
        {
            Transform = now.Transform != old.Transform ? now.Transform : null,
            Name = now.Name != old.Name ? now.Name : null,
            SpotColor = now.Color != old.Color ? now.Color : null,
            SpotFov = now.Fov != old.Fov ? now.Fov : null,
            SpotFar = now.Far != old.Far ? now.Far : null,
            SpotNear = now.Near != old.Near ? now.Near : null,
            SpotAspect = now.Aspect != old.Aspect ? now.Aspect : null,
            SpotCookie = now.CookiePath != old.CookiePath ? now.CookiePath : null,
            VisibilityFlags = now.HasVisibilityFlags && (!old.HasVisibilityFlags || now.VisibilityFlags != old.VisibilityFlags)
                ? now.VisibilityFlags : null,
        };
        return e.Transform is null && e.Name is null && e.SpotColor is null && e.SpotFov is null && e.SpotFar is null
               && e.SpotNear is null && e.SpotAspect is null && e.SpotCookie is null && e.VisibilityFlags is null
            ? null : e;
    }

    // ------------------------------------------------------------------ projection (the Riot spot record)

    /// <summary>The light's orthonormal frame: forward = local -Z, right = local X, up = local Y, each made perpendicular to the
    /// forward axis so an authored scale or shear cannot skew the projection.</summary>
    private static void Basis(in Matrix4x4 m, out Vector3 right, out Vector3 up, out Vector3 fwd)
    {
        fwd = ForwardOf(m);
        right = new Vector3(m.M11, m.M12, m.M13);
        right -= fwd * Vector3.Dot(right, fwd);
        if (!(right.LengthSquared() > 1e-10f))
        {
            right = Vector3.Cross(Vector3.UnitY, fwd);
            if (!(right.LengthSquared() > 1e-8f)) right = Vector3.UnitX;
        }
        right = Vector3.Normalize(right);
        up = new Vector3(m.M21, m.M22, m.M23);
        up -= fwd * Vector3.Dot(up, fwd);
        up -= right * Vector3.Dot(up, right);
        up = up.LengthSquared() > 1e-10f ? Vector3.Normalize(up) : Vector3.Normalize(Vector3.Cross(fwd, right));
    }

    /// <summary>Half-extents of the frustum at <paramref name="distance"/> along the light's axis: height = d * tan(fov / 2),
    /// width = height * Aspect. (<c>fov</c> is read as the full vertical angle - an inference; with the default Aspect 1 the
    /// frustum is square either way.)</summary>
    public static (float HalfWidth, float HalfHeight) HalfExtentsAt(float distance, float fovDegrees, float aspect)
    {
        float fov = Math.Clamp(float.IsFinite(fovDegrees) ? fovDegrees : DefaultFov, 1f, 170f);
        float half = distance * MathF.Tan(fov * MathF.PI / 360f);
        float a = float.IsFinite(aspect) && aspect > 0.01f ? aspect : DefaultAspect;
        return (half * a, half);
    }

    /// <summary>
    /// The world-to-light projection Riot's spot loop expects (light-system.md 2.5; verified against the DefaultEnv_Flat
    /// disassembly): <c>clip = [P, 1] * M</c> with <c>uv = clip.xy / clip.w</c> in [0,1] across the cone and
    /// <c>clip.z / clip.w</c> in [0,1] from the Near to the Far plane. The shader accepts a pixel only when that depth is
    /// &lt;= 1 and the uv is inside [0,1]; a point BEHIND the light gets a depth above 1 and is rejected for free.
    /// The shader's four 16-byte words are the ROWS of this matrix.
    /// </summary>
    public static Matrix4x4 WorldToLight(in Matrix4x4 transform, float fovDegrees, float far, float near, float aspect)
    {
        var pos = transform.Translation;
        Basis(transform, out var right, out var up, out var fwd);

        float n = Math.Max(near, 0.01f), f = Math.Max(far, n + 0.01f);
        var (hw, hh) = HalfExtentsAt(1f, fovDegrees, aspect);   // tangents
        float A = f / (f - n), B = -n * f / (f - n);

        var view = new Matrix4x4(
            right.X, up.X, fwd.X, 0,
            right.Y, up.Y, fwd.Y, 0,
            right.Z, up.Z, fwd.Z, 0,
            -Vector3.Dot(pos, right), -Vector3.Dot(pos, up), -Vector3.Dot(pos, fwd), 1);
        var proj = new Matrix4x4(
            0.5f / hw, 0, 0, 0,
            0, -0.5f / hh, 0, 0,
            0.5f, 0.5f, A, 1,
            0, 0, B, 0);
        return view * proj;
    }

    /// <summary>The 8 corners of the frustum in world space (near plane first, then far), for the selection wireframe and the
    /// cluster bounds. Order per plane: -x/-y, +x/-y, +x/+y, -x/+y in light space.</summary>
    public static Vector3[] FrustumCorners(in Matrix4x4 transform, float fovDegrees, float far, float near, float aspect)
    {
        var pos = transform.Translation;
        Basis(transform, out var right, out var up, out var fwd);

        float n = Math.Max(near, 0.01f), f = Math.Max(far, n + 0.01f);
        var corners = new Vector3[8];
        for (int plane = 0; plane < 2; plane++)
        {
            float d = plane == 0 ? n : f;
            var (hw, hh) = HalfExtentsAt(d, fovDegrees, aspect);
            var centre = pos + fwd * d;
            corners[plane * 4 + 0] = centre - right * hw - up * hh;
            corners[plane * 4 + 1] = centre + right * hw - up * hh;
            corners[plane * 4 + 2] = centre + right * hw + up * hh;
            corners[plane * 4 + 3] = centre - right * hw + up * hh;
        }
        return corners;
    }

    /// <summary>
    /// Line-list vertices (x,y,z per vertex, two per segment) for a selected spotlight: the Near and Far rectangles, the four
    /// edges joining them, and the axis - the way a point light draws a range sphere. Empty for a degenerate light.
    /// </summary>
    public static float[] BuildFrustumLines(in Matrix4x4 transform, float fovDegrees, float far, float near, float aspect)
    {
        if (!(far > 0f) || !float.IsFinite(far) || !float.IsFinite(transform.M41) || !float.IsFinite(transform.M42) || !float.IsFinite(transform.M43))
            return Array.Empty<float>();
        var c = FrustumCorners(transform, fovDegrees, far, near, aspect);
        var lines = new List<float>(13 * 6);
        void Seg(Vector3 a, Vector3 b) { lines.Add(a.X); lines.Add(a.Y); lines.Add(a.Z); lines.Add(b.X); lines.Add(b.Y); lines.Add(b.Z); }
        for (int plane = 0; plane < 2; plane++)
            for (int i = 0; i < 4; i++) Seg(c[plane * 4 + i], c[plane * 4 + (i + 1) % 4]);
        for (int i = 0; i < 4; i++) Seg(c[i], c[4 + i]);
        Seg(transform.Translation, transform.Translation + ForwardOf(transform) * far);
        return lines.ToArray();
    }

    /// <summary>A conservative world AABB of the whole frustum (apex included), for the cluster binning.</summary>
    public static (Vector3 Min, Vector3 Max) Bounds(in Matrix4x4 transform, float fovDegrees, float far, float near, float aspect)
    {
        var c = FrustumCorners(transform, fovDegrees, far, near, aspect);
        var lo = transform.Translation; var hi = lo;
        foreach (var v in c) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
        return (lo, hi);
    }
}

/// <summary>A decoded cookie: straight RGBA, row-major, top-down.</summary>
public sealed record SpotCookieImage(byte[] Rgba, int Width, int Height);

/// <summary>The spotlights packed for Riot's spot loop: the records and the cookie atlas they address.</summary>
public sealed record PackedSpotlights(IReadOnlyList<ClusterSpotLight> Lights, byte[]? AtlasRgba, int AtlasWidth, int AtlasHeight,
    IReadOnlyList<ulong> CookieOrder);

/// <summary>
/// M900: turns spotlights into <see cref="ClusterSpotLight"/>s and one cookie atlas. Device-free, so the packing is tested
/// without a GPU. The shader samples ONE projected texture and picks a cell with each record's scale / bias, so each distinct
/// cookie gets a 128x128 cell side by side; cell 0 is white, which a light without a (resolvable) cookie addresses with a zero
/// scale - the game multiplies by the cookie in every spot loop, so "no cookie" has to be white, not black.
/// </summary>
public static class SpotlightPacking
{
    public const int CellSize = 128;
    public const int MaxCookies = 31;

    public static PackedSpotlights Pack(IReadOnlyList<DynamicSpotlight> spots, IReadOnlyDictionary<ulong, SpotCookieImage?> cookies)
    {
        var order = new List<ulong>();
        foreach (var s in spots)
            if (s.CookieHash != 0 && cookies.TryGetValue(s.CookieHash, out var img) && img is not null
                && img.Width > 0 && img.Height > 0 && img.Rgba.Length >= img.Width * img.Height * 4
                && !order.Contains(s.CookieHash) && order.Count < MaxCookies)
                order.Add(s.CookieHash);

        int cells = 1 + order.Count;
        int w = cells * CellSize, h = CellSize;
        byte[]? atlas = null;
        if (order.Count > 0)
        {
            atlas = new byte[w * h * 4];
            for (int i = 0; i < CellSize * h; i++)                 // cell 0: opaque white
            {
                int x = i % CellSize, y = i / CellSize, o = (y * w + x) * 4;
                atlas[o] = atlas[o + 1] = atlas[o + 2] = atlas[o + 3] = 255;
            }
            for (int c = 0; c < order.Count; c++) Blit(cookies[order[c]]!, atlas, w, (c + 1) * CellSize);
        }

        var lights = new List<ClusterSpotLight>(spots.Count);
        foreach (var s in spots)
        {
            int idx = s.CookieHash != 0 ? order.IndexOf(s.CookieHash) : -1;
            // No cookie: scale 0 and a bias at the centre of the white cell. Cookie: inset one texel so bilinear taps stay in the cell.
            var scaleBias = idx < 0 || atlas is null
                ? new Vector4(0f, 0f, 0.5f * CellSize / Math.Max(w, 1), 0.5f)
                : new Vector4((CellSize - 2f) / w, (CellSize - 2f) / h, ((idx + 1) * CellSize + 1f) / w, 1f / h);
            var (bmin, bmax) = DynamicSpotlights.Bounds(s.Transform, s.Fov, s.Far, s.Near, s.Aspect);
            lights.Add(new ClusterSpotLight(s.Position, s.Far, new Vector3(s.Color.X, s.Color.Y, s.Color.Z),
                DynamicSpotlights.WorldToLight(s.Transform, s.Fov, s.Far, s.Near, s.Aspect), scaleBias, bmin, bmax));
        }
        return new PackedSpotlights(lights, atlas, atlas is null ? 0 : w, atlas is null ? 0 : h, order);
    }

    /// <summary>Bilinear resample of a cookie into its 128x128 cell.</summary>
    private static void Blit(SpotCookieImage src, byte[] dst, int dstWidth, int x0)
    {
        for (int y = 0; y < CellSize; y++)
        for (int x = 0; x < CellSize; x++)
        {
            float fx = (x + 0.5f) / CellSize * src.Width - 0.5f, fy = (y + 0.5f) / CellSize * src.Height - 0.5f;
            int ix = (int)MathF.Floor(fx), iy = (int)MathF.Floor(fy);
            float tx = fx - ix, ty = fy - iy;
            int ix0 = Math.Clamp(ix, 0, src.Width - 1), ix1 = Math.Clamp(ix + 1, 0, src.Width - 1);
            int iy0 = Math.Clamp(iy, 0, src.Height - 1), iy1 = Math.Clamp(iy + 1, 0, src.Height - 1);
            int o = (y * dstWidth + x0 + x) * 4;
            for (int c = 0; c < 4; c++)
            {
                float a = src.Rgba[(iy0 * src.Width + ix0) * 4 + c], b = src.Rgba[(iy0 * src.Width + ix1) * 4 + c];
                float d = src.Rgba[(iy1 * src.Width + ix0) * 4 + c], e = src.Rgba[(iy1 * src.Width + ix1) * 4 + c];
                dst[o + c] = (byte)Math.Clamp(MathF.Round((a + (b - a) * tx) * (1 - ty) + (d + (e - d) * tx) * ty), 0f, 255f);
            }
        }
    }
}
