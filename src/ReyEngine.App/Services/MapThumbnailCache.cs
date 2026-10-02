using System.IO.Hashing;
using System.Text;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M807: the identity of one Content Browser map thumbnail - everything that decides what its picture looks like,
/// folded into the file name it is cached under, so a stale picture is never shown for a changed input.</para>
///
/// <para><b>In the key:</b> the renderer's version and sizes (bump <see cref="RendererVersion"/> when the camera, lighting or
/// what is drawn changes), the app version, the start state, the mapgeo's path and identity, its materials bin's and the
/// shipping map bin's identity, and the shader cache's. An asset's identity is a file's length and write time, or a WAD
/// chunk's sizes with the WAD's length and write time - cheap to read for a tile, and different for a patched or edited
/// asset. <b>Not in the key:</b> the textures and prop skins a map names (hundreds of reads); a texture edited under a
/// map is picked up by the tile's Refresh Thumbnail command.</para>
/// </summary>
public static class MapThumbnailKey
{
    /// <summary>Bump to retire every cached picture after a change to how a thumbnail is drawn.</summary>
    public const int RendererVersion = 1;

    /// <summary>The size a thumbnail is rendered at, and the size it is stored and shown at (the Content Browser's
    /// tile card is 66 by 46: this is its aspect at three times the pixels).</summary>
    public const int RenderWidth = 384, RenderHeight = 268, ThumbWidth = 192, ThumbHeight = 134;

    public const string StartState = "start";

    public static string FileIdentity(long length, long lastWriteUtcTicks) => $"file:{length}:{lastWriteUtcTicks}";

    public static string WadChunkIdentity(long wadLength, long wadWriteUtcTicks, long compressedSize, long uncompressedSize) =>
        $"wad:{wadLength}:{wadWriteUtcTicks}:{compressedSize}:{uncompressedSize}";

    /// <summary>The key: 32 lower-case hex digits. The same inputs always give the same key; changing any one changes it.</summary>
    public static string Compute(string appVersion, string state, string mapPath, string mapIdentity,
        string materialsPath, string materialsIdentity, string shippingIdentity, string shaderCacheIdentity)
    {
        var text = new StringBuilder()
            .Append("renderer:").Append(RendererVersion).Append('\n')
            .Append("size:").Append(RenderWidth).Append('x').Append(RenderHeight).Append('>')
            .Append(ThumbWidth).Append('x').Append(ThumbHeight).Append('\n')
            .Append("app:").Append(appVersion).Append('\n')
            .Append("state:").Append(state).Append('\n')
            .Append("map:").Append(mapPath.ToLowerInvariant()).Append('|').Append(mapIdentity).Append('\n')
            .Append("materials:").Append(materialsPath.ToLowerInvariant()).Append('|').Append(materialsIdentity).Append('\n')
            .Append("shipping:").Append(shippingIdentity).Append('\n')
            .Append("shadercache:").Append(shaderCacheIdentity)
            .ToString();
        var hash = XxHash128.Hash(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// <para>M807: the per-user disk cache of map thumbnails - <c>%LocalAppData%\ReyEngine\Cache\map-thumbnails\v1\</c>, beside the
/// Workshop's catalog cache. Never the project, never <c>data/</c>, never <c>settings.json</c>: a thumbnail is a derived
/// picture, safe to delete at any time (the next look regenerates it).</para>
///
/// <para>A picture is <c>&lt;key&gt;.png</c>. A map that cannot be drawn leaves <c>&lt;key&gt;.fail</c> with the reason, so it is
/// not retried every session - the key changes with the map, the patch or the app, which is when it should be tried again,
/// and Refresh Thumbnail clears it by hand. Writes go to a temporary file and are moved into place, so a crash cannot
/// leave a half-written picture that a later session would read. Every method swallows I/O errors: a cache that cannot be
/// written is a cache that is cold, never an exception in the Content Browser.</para>
/// </summary>
public sealed class MapThumbnailCache
{
    public string Root { get; }

    public MapThumbnailCache(string root) => Root = root;

    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReyEngine", "Cache", "map-thumbnails", "v1");

    public string PngPath(string key) => Path.Combine(Root, key + ".png");
    public string FailPath(string key) => Path.Combine(Root, key + ".fail");

    public bool TryReadPng(string key, out byte[] png)
    {
        png = Array.Empty<byte>();
        try
        {
            string path = PngPath(key);
            if (!File.Exists(path)) return false;
            png = File.ReadAllBytes(path);
            return png.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>The recorded reason a map could not be drawn, or null when no failure is recorded for the key.</summary>
    public string? TryReadFailure(string key)
    {
        try
        {
            string path = FailPath(key);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    public bool WritePng(string key, ReadOnlySpan<byte> png)
    {
        if (png.Length == 0) return false;
        string tmp = "";
        try
        {
            Directory.CreateDirectory(Root);
            tmp = Path.Combine(Root, $"{key}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(tmp, png.ToArray());
            File.Move(tmp, PngPath(key), overwrite: true);
            DeleteQuiet(FailPath(key));   // a picture supersedes an old failure
            return true;
        }
        catch
        {
            DeleteQuiet(tmp);
            return false;
        }
    }

    public bool WriteFailure(string key, string reason)
    {
        string tmp = "";
        try
        {
            Directory.CreateDirectory(Root);
            tmp = Path.Combine(Root, $"{key}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tmp, reason);
            File.Move(tmp, FailPath(key), overwrite: true);
            return true;
        }
        catch
        {
            DeleteQuiet(tmp);
            return false;
        }
    }

    /// <summary>Forget the picture and the failure for a key (Refresh Thumbnail).</summary>
    public void Delete(string key)
    {
        DeleteQuiet(PngPath(key));
        DeleteQuiet(FailPath(key));
    }

    /// <summary>Keep the cache from growing without bound: when more than <paramref name="maxFiles"/> pictures and failures
    /// are stored, delete the least recently written down to 80% of that. Leftover temporary files from an interrupted write
    /// older than a day go too. Returns how many files were deleted.</summary>
    public int Prune(int maxFiles)
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(Root)) return 0;
            var now = DateTime.UtcNow;
            var kept = new List<(string Path, DateTime Written)>();
            foreach (var file in Directory.EnumerateFiles(Root))
            {
                string ext = Path.GetExtension(file);
                var written = File.GetLastWriteTimeUtc(file);
                if (ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    if (now - written > TimeSpan.FromDays(1) && DeleteQuiet(file)) deleted++;
                }
                else if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) || ext.Equals(".fail", StringComparison.OrdinalIgnoreCase))
                    kept.Add((file, written));
            }
            if (kept.Count <= maxFiles) return deleted;
            int keep = (int)(maxFiles * 0.8);
            foreach (var (path, _) in kept.OrderBy(f => f.Written).Take(kept.Count - keep))
                if (DeleteQuiet(path)) deleted++;
        }
        catch { /* best effort */ }
        return deleted;
    }

    private static bool DeleteQuiet(string path)
    {
        try
        {
            if (path.Length == 0 || !File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }
}
