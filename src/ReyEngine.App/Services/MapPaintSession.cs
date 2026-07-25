using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    }

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
        bool blocked = _blocked.Contains(path);
        string? warning = blocked
            ? "Blocked: this texture is stacked over itself, so a stroke would appear in several places at once."
            : users > 1
                ? $"Shared: {users} meshes draw with this texture — painting here changes all of them."
                : null;
        return new PaintProbe(path, material, img.Width, img.Height, users, blocked, warning);
    }

    private int CountSubmeshesUsing(TextureImage img)
    {
        int n = 0;
        for (int i = 0; i < _texturesBySubmesh.Count; i++)
            if (ReferenceEquals(_texturesBySubmesh[i], img)) n++;
        return n;
    }

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
    public IReadOnlyList<PaintedTexture> StrokeTo(Vector3 center, Vector3 viewDir, PaintBrush brush, float spacing = 0.25f)
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

    private void Dab(Vector3 center, Vector3 viewDir, PaintBrush brush, Dictionary<TextureImage, PaintDirtyRect> touched)
    {
        _index.OverlapSphere(center, brush.Radius, _visible, t =>
        {
            int sm = _index.SubmeshOf(t);
            if (sm < 0 || sm >= _texturesBySubmesh.Count) return;
            if (_texturesBySubmesh[sm] is not { } img) return;
            if (_blocked.Contains(_pathsBySubmesh.ElementAtOrDefault(sm) ?? "")) return;

            _index.GetTriangle(t, out var p0, out var p1, out var p2);
            _index.GetTriangleUv(t, out var uv0, out var uv1, out var uv2);

            // Snapshot BEFORE the first write into each tile, or undo would restore painted pixels.
            SnapshotTilesFor(img, p0, p1, p2, uv0, uv1, uv2, center, brush);

            var rect = touched.GetValueOrDefault(img, PaintDirtyRect.Empty);
            TexturePainter.DabTriangle(img, p0, p1, p2, uv0, uv1, uv2, center, viewDir, brush, ref rect);
            touched[img] = rect;
        });
    }

    /// <summary>Capture the original content of every tile this dab could reach.
    ///
    /// Uses the painter's OWN bound, not the triangle's UV extent. Bounding by the triangle was a real
    /// bug: a Summoner's Rift ground triangle spans most of its texture, so the capture had to be capped
    /// to avoid snapshotting 16 MiB per dab — and the cap then skipped those triangles entirely, so paint
    /// landed with no undo data behind it. Measured: 24,275 bytes survived an undo that should have
    /// restored everything. Sharing the bound makes the capture both small and exact.</summary>
    private void SnapshotTilesFor(TextureImage img, in Vector3 p0, in Vector3 p1, in Vector3 p2,
        in Vector2 uv0, in Vector2 uv1, in Vector2 uv2, in Vector3 center, PaintBrush brush)
    {
        int w = img.Width, h = img.Height;
        if (!TexturePainter.TryGetDabBounds(w, h, p0, p1, p2, uv0, uv1, uv2,
                center, brush.Radius, brush.SeamBleedTexels,
                out int minX, out int minY, out int maxX, out int maxY)) return;

        int tx0 = minX / TileSize, tx1 = maxX / TileSize;
        int ty0 = minY / TileSize, ty1 = maxY / TileSize;
        if (tx1 < tx0 || ty1 < ty0) return;

        if (!_strokeTiles.TryGetValue(img, out var tiles)) _strokeTiles[img] = tiles = new Dictionary<int, byte[]>();
        int tilesPerRow = (w + TileSize - 1) / TileSize;

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

    public string? PathOf(TextureImage img)
    {
        for (int i = 0; i < _texturesBySubmesh.Count; i++)
            if (ReferenceEquals(_texturesBySubmesh[i], img)) return _pathsBySubmesh.ElementAtOrDefault(i);
        return null;
    }

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
