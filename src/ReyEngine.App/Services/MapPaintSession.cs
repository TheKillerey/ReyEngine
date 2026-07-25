using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Painting;
using ReyEngine.Rendering;

namespace ReyEngine.App.Services;

/// <summary>One texture changed by a stroke, and by how much.</summary>
public sealed record PaintedTexture(TextureImage Image, string AssetPath, PaintDirtyRect Rect);

/// <summary>What the user is about to paint on, for the hover badge.</summary>
public sealed record PaintProbe(
    string AssetPath, string MaterialName, int Width, int Height,
    int SubmeshCount, bool Blocked, string? Warning);

/// <summary>M172c: a live painting session over the open map.
///
/// Owns the bridge from "the cursor is here" to "these texels changed": it ray-casts through the map's
/// BVH, finds every triangle the brush sphere covers, groups them by the texture they sample, and hands
/// each one to <see cref="TexturePainter"/>. A dab genuinely spans several textures — a radius-200 dab
/// measured on Summoner's Rift mid-lane touched 230 triangles across four different textures — and that
/// is correct: a stroke crossing from ground onto river should paint both.
///
/// Textures are edited IN PLACE, in the very <see cref="TextureImage"/> instances the viewport uploaded,
/// which is what makes a stroke show up without a reload. Because BuildMapTextures hands the same
/// instance to every submesh sharing a path, painting through one submesh automatically updates all of
/// them — the same sharing that means a stroke on one bush appears on all 73.</summary>
public sealed class MapPaintSession
{
    private readonly MeshRayIndex _index;
    private readonly IReadOnlyList<TextureImage?> _texturesBySubmesh;
    private readonly IReadOnlyList<string?> _pathsBySubmesh;
    private readonly IReadOnlyList<string> _materialsBySubmesh;
    private readonly IReadOnlyList<bool>? _visible;
    private readonly HashSet<string> _blocked;

    /// <summary>Undo tiles captured since <see cref="BeginStroke"/>, keyed by image then tile index.</summary>
    private readonly Dictionary<TextureImage, Dictionary<int, byte[]>> _strokeTiles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TextureImage, PaintDirtyRect> _strokeRects = new(ReferenceEqualityComparer.Instance);
    private bool _inStroke;
    private Vector3? _lastDabCenter;

    /// <summary>Undo granularity. 64x64 RGBA is 16 KiB, so a radius-64 dab costs at most 9 tiles
    /// (0.14 MiB) instead of snapshotting a whole 16 MiB 2048² texture per step.</summary>
    public const int TileSize = 64;

    public MapPaintSession(
        MeshRayIndex index,
        IReadOnlyList<TextureImage?> texturesBySubmesh,
        IReadOnlyList<string?> pathsBySubmesh,
        IReadOnlyList<string> materialsBySubmesh,
        IReadOnlyList<bool>? visible,
        IEnumerable<string>? blockedPaths = null)
    {
        _index = index;
        _texturesBySubmesh = texturesBySubmesh;
        _pathsBySubmesh = pathsBySubmesh;
        _materialsBySubmesh = materialsBySubmesh;
        _visible = visible;
        _blocked = new HashSet<string>(blockedPaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // Precompute what used to be linear scans over every submesh. PathOf ran per painted texture per
        // mouse move and CountSubmeshesUsing ran on every hover — 600 iterations each, on the UI thread.
        for (int i = 0; i < texturesBySubmesh.Count; i++)
        {
            if (texturesBySubmesh[i] is not { } img) continue;
            _usageCount[img] = _usageCount.GetValueOrDefault(img) + 1;
            var path = pathsBySubmesh.ElementAtOrDefault(i);
            if (path is null) continue;
            _pathOf.TryAdd(img, path);
            if (_blocked.Any(b => path.Contains(b, StringComparison.OrdinalIgnoreCase))) _blockedImages.Add(img);
        }
    }

    private readonly Dictionary<TextureImage, string> _pathOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TextureImage, int> _usageCount = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TextureImage> _blockedImages = new(ReferenceEqualityComparer.Instance);

    /// <summary>Where a ray meets the map, or null.</summary>
    public MeshRayHit? Pick(Vector3 origin, Vector3 direction) => _index.ClosestHit(origin, direction, _visible);

    /// <summary>What is under the cursor — for the badge that tells the user what a stroke would change
    /// before they commit to it.</summary>
    public PaintProbe? Probe(Vector3 origin, Vector3 direction)
    {
        if (Pick(origin, direction) is not { } hit) return null;
        int sm = hit.Submesh;
        if (sm < 0 || sm >= _texturesBySubmesh.Count) return null;
        if (_texturesBySubmesh[sm] is not { } img) return null;

        string path = _pathsBySubmesh.ElementAtOrDefault(sm) ?? "";
        string material = _materialsBySubmesh.ElementAtOrDefault(sm) ?? "";
        int users = CountSubmeshesUsing(img);
        bool blocked = _blockedImages.Contains(img);
        string? warning = blocked
            ? "Blocked: this texture is stacked over itself, so a stroke would appear in several places at once."
            : users > 1
                ? $"Shared: {users} meshes draw with this texture — painting here changes all of them."
                : null;
        return new PaintProbe(path, material, img.Width, img.Height, users, blocked, warning);
    }

    private int CountSubmeshesUsing(TextureImage img) => _usageCount.GetValueOrDefault(img);

    // ------------------------------------------------------------------ strokes

    public void BeginStroke()
    {
        _inStroke = true;
        _lastDabCenter = null;
        _strokeTiles.Clear();
        _strokeRects.Clear();
    }

    /// <summary>Paint from wherever the last dab landed to <paramref name="center"/>, filling the gap with
    /// evenly spaced dabs. A drag delivers mouse positions far apart, so dabbing only at those positions
    /// would leave a dotted line rather than a stroke.</summary>
    /// <param name="spacing">Gap between interpolated dabs, as a fraction of the radius. 0.4 rather than
    /// the more usual 0.25: with a smoothstep falloff the dabs still overlap by 60% and the stroke looks
    /// identical, but it costs 40% fewer dabs — and a dab is 2.4 ms at radius 150, so that is the
    /// difference between keeping up with a drag and not.</param>
    public IReadOnlyList<PaintedTexture> StrokeTo(Vector3 center, Vector3 viewDir, PaintBrush brush, float spacing = 0.4f)
    {
        if (!_inStroke) BeginStroke();

        var touched = new Dictionary<TextureImage, PaintDirtyRect>(ReferenceEqualityComparer.Instance);
        float step = MathF.Max(1f, brush.Radius * Math.Clamp(spacing, 0.05f, 1f));

        if (_lastDabCenter is { } from)
        {
            float dist = Vector3.Distance(from, center);
            // Cap the interpolation: a camera jump or a first move after a teleport must not spawn
            // thousands of dabs and freeze the UI.
            int steps = Math.Min(64, (int)(dist / step));
            for (int i = 1; i <= steps; i++)
                Dab(Vector3.Lerp(from, center, i / (float)(steps + 1)), viewDir, brush, touched);
        }
        Dab(center, viewDir, brush, touched);
        _lastDabCenter = center;

        var result = new List<PaintedTexture>(touched.Count);
        foreach (var (img, rect) in touched)
        {
            if (rect.IsEmpty) continue;
            string path = PathOf(img) ?? "";
            result.Add(new PaintedTexture(img, path, rect));
            var acc = _strokeRects.GetValueOrDefault(img, PaintDirtyRect.Empty);
            acc.Union(rect);
            _strokeRects[img] = acc;
        }
        return result;
    }

    /// <summary>Triangles this dab touches, bucketed by the texture they paint into. Reused between dabs
    /// so a stroke doesn't allocate a list per frame.</summary>
    private readonly Dictionary<TextureImage, List<int>> _dabBuckets = new(ReferenceEqualityComparer.Instance);

    private void Dab(Vector3 center, Vector3 viewDir, PaintBrush brush, Dictionary<TextureImage, PaintDirtyRect> touched)
    {
        foreach (var list in _dabBuckets.Values) list.Clear();

        // Gather first, paint second. Splitting the two is what allows the paint to run in parallel:
        // measured, the BVH query is 0.005 ms and the painting 4.48 ms, so all the time is in the second
        // half and none of it is in the traversal.
        _index.OverlapSphere(center, brush.Radius, _visible, t =>
        {
            int sm = _index.SubmeshOf(t);
            if (sm < 0 || sm >= _texturesBySubmesh.Count) return;
            if (_texturesBySubmesh[sm] is not { } img) return;
            if (_blockedImages.Contains(img)) return;

            if (!_dabBuckets.TryGetValue(img, out var list)) _dabBuckets[img] = list = new List<int>();
            list.Add(t);
        });

        var active = new List<TextureImage>();
        foreach (var (img, list) in _dabBuckets)
        {
            if (list.Count == 0) continue;
            active.Add(img);
            // Tile dictionaries are created HERE, on one thread, so the parallel pass below only ever
            // mutates a dictionary that already exists and belongs to exactly one texture.
            if (!_strokeTiles.ContainsKey(img)) _strokeTiles[img] = new Dictionary<int, byte[]>();
        }
        if (active.Count == 0) return;

        // Serial across textures on purpose. Splitting here looked obvious but measured only ~25%,
        // because a dab's cost is not spread evenly — 425,000 of one measured dab's 907,753 scanned
        // texels sat in two triangles of two ground textures, so most workers had nothing to do.
        // TexturePainter splits those big triangles across row bands instead, which targets the actual
        // hot spot; nesting a second Parallel.For out here would just oversubscribe the pool.
        var rects = new PaintDirtyRect[active.Count];
        for (int i = 0; i < active.Count; i++)
            rects[i] = PaintOne(active[i], _dabBuckets[active[i]], center, viewDir, brush);

        for (int i = 0; i < active.Count; i++)
        {
            if (rects[i].IsEmpty) continue;
            var acc = touched.GetValueOrDefault(active[i], PaintDirtyRect.Empty);
            acc.Union(rects[i]);
            touched[active[i]] = acc;
        }
    }

    private PaintDirtyRect PaintOne(TextureImage img, List<int> triangles, Vector3 center, Vector3 viewDir, PaintBrush brush)
    {
        var rect = PaintDirtyRect.Empty;
        var tiles = _strokeTiles[img];
        foreach (int t in triangles)
        {
            _index.GetTriangle(t, out var p0, out var p1, out var p2);
            _index.GetTriangleUv(t, out var uv0, out var uv1, out var uv2);
            // Derived once, used twice — the snapshot must cover exactly what the paint will write.
            if (!TexturePainter.TryGetDabBounds(img.Width, img.Height, p0, p1, p2, uv0, uv1, uv2,
                    center, brush.Radius, brush.SeamBleedTexels,
                    out int bx0, out int by0, out int bx1, out int by1)) continue;
            var bounds = new DabBounds(bx0, by0, bx1, by1);
            // Snapshot BEFORE the first write into each tile, or undo would restore painted pixels.
            SnapshotTiles(img, tiles, bounds);
            TexturePainter.DabTriangle(img, p0, p1, p2, uv0, uv1, uv2, center, viewDir, brush, ref rect, bounds);
        }
        return rect;
    }

    /// <summary>Capture the original content of every tile the dab is about to write.
    ///
    /// Uses the painter's OWN bound, not the triangle's UV extent. Bounding by the triangle was a real
    /// bug: a Summoner's Rift ground triangle spans most of its texture, so the capture had to be capped
    /// to avoid snapshotting 16 MiB per dab — and the cap then skipped those triangles, so paint landed
    /// with no undo data behind it. Measured: 24,275 bytes survived an undo that should have restored
    /// everything.</summary>
    private static void SnapshotTiles(TextureImage img, Dictionary<int, byte[]> tiles, in DabBounds b)
    {
        int tx0 = b.MinX / TileSize, tx1 = b.MaxX / TileSize;
        int ty0 = b.MinY / TileSize, ty1 = b.MaxY / TileSize;
        if (tx1 < tx0 || ty1 < ty0) return;
        int tilesPerRow = (img.Width + TileSize - 1) / TileSize;

        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                int key = ty * tilesPerRow + tx;
                if (tiles.ContainsKey(key)) continue;
                tiles[key] = CopyTile(img, tx, ty);
            }
    }

    private static byte[] CopyTile(TextureImage img, int tx, int ty)
    {
        int x0 = tx * TileSize, y0 = ty * TileSize;
        int tw = Math.Min(TileSize, img.Width - x0), th = Math.Min(TileSize, img.Height - y0);
        var buf = new byte[tw * th * 4];
        for (int y = 0; y < th; y++)
            Array.Copy(img.Rgba, ((y0 + y) * img.Width + x0) * 4, buf, y * tw * 4, tw * 4);
        return buf;
    }

    /// <summary>Finish the stroke and hand back what it changed plus how to undo it. Null when the stroke
    /// painted nothing (a click on empty space).</summary>
    public PaintStrokeRecord? EndStroke()
    {
        _inStroke = false;
        _lastDabCenter = null;
        if (_strokeRects.Count == 0) { _strokeTiles.Clear(); return null; }

        var entries = new List<PaintStrokeEntry>();
        foreach (var (img, rect) in _strokeRects)
        {
            if (rect.IsEmpty) continue;
            var before = _strokeTiles.GetValueOrDefault(img) ?? new Dictionary<int, byte[]>();
            // The tiles currently hold the PAINTED result; capture it so redo works too.
            var after = new Dictionary<int, byte[]>(before.Count);
            int tilesPerRow = (img.Width + TileSize - 1) / TileSize;
            foreach (var key in before.Keys)
                after[key] = CopyTile(img, key % tilesPerRow, key / tilesPerRow);
            entries.Add(new PaintStrokeEntry(img, PathOf(img) ?? "", rect, before, after));
        }
        _strokeTiles.Clear();
        _strokeRects.Clear();
        return entries.Count == 0 ? null : new PaintStrokeRecord(entries);
    }

    public string? PathOf(TextureImage img) => _pathOf.GetValueOrDefault(img);

    /// <summary>Every texture this session has painted since it was created, with its asset path.</summary>
    public IReadOnlyList<(TextureImage Image, string Path)> PaintedTextures =>
        _painted.Select(kv => (kv.Key, kv.Value)).ToList();
    private readonly Dictionary<TextureImage, string> _painted = new(ReferenceEqualityComparer.Instance);

    public void MarkPainted(TextureImage img, string path)
    {
        if (!string.IsNullOrEmpty(path)) _painted[img] = path;
    }
}

/// <summary>One texture's share of a stroke: which tiles it changed, and their content before and after.</summary>
public sealed record PaintStrokeEntry(
    TextureImage Image, string AssetPath, PaintDirtyRect Rect,
    Dictionary<int, byte[]> Before, Dictionary<int, byte[]> After);

/// <summary>A whole stroke, undoable. Tiles rather than whole textures: a 2048² RGBA copy is 16 MiB and a
/// painting session is hundreds of strokes.</summary>
public sealed class PaintStrokeRecord
{
    public IReadOnlyList<PaintStrokeEntry> Entries { get; }
    public PaintStrokeRecord(IReadOnlyList<PaintStrokeEntry> entries) => Entries = entries;

    public void Undo() => Restore(e => e.Before);
    public void Redo() => Restore(e => e.After);

    private void Restore(Func<PaintStrokeEntry, Dictionary<int, byte[]>> pick)
    {
        foreach (var e in Entries)
        {
            int tilesPerRow = (e.Image.Width + MapPaintSession.TileSize - 1) / MapPaintSession.TileSize;
            foreach (var (key, buf) in pick(e))
            {
                int tx = key % tilesPerRow, ty = key / tilesPerRow;
                int x0 = tx * MapPaintSession.TileSize, y0 = ty * MapPaintSession.TileSize;
                int tw = Math.Min(MapPaintSession.TileSize, e.Image.Width - x0);
                int th = Math.Min(MapPaintSession.TileSize, e.Image.Height - y0);
                if (tw <= 0 || th <= 0 || buf.Length < tw * th * 4) continue;
                for (int y = 0; y < th; y++)
                    Array.Copy(buf, y * tw * 4, e.Image.Rgba, ((y0 + y) * e.Image.Width + x0) * 4, tw * 4);
            }
        }
    }
}
