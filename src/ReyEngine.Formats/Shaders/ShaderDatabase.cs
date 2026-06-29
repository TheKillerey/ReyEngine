using System.Text.Json;
using LeagueToolkit.Core.Renderer;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Shaders;

public enum ShaderStage { Vertex, Pixel, Unknown }

/// <summary>One Riot shader entry from ShaderCache.dx11.wad (a <c>.vs.dx11</c>/<c>.ps.dx11</c> shader TOC).</summary>
public sealed class ShaderDefinition
{
    public required string Name { get; init; }          // path without the .vs.dx11/.ps.dx11 suffix
    public required ulong PathHash { get; init; }
    public required string Path { get; init; }
    public ShaderStage Stage { get; init; }
    public string Platform { get; init; } = "dx11";
    public int VariantCount { get; init; }
    public long BytecodeSize { get; init; }             // size of the TOC chunk
    public List<string> BaseDefines { get; init; } = new();

    public string ShortName
    {
        get { int s = Name.LastIndexOf('/'); return s < 0 ? Name : Name[(s + 1)..]; }
    }
}

/// <summary>The scanned Riot shader catalogue (vertex + pixel shaders) keyed for lookup by name.</summary>
public sealed class ShaderDatabase
{
    public List<ShaderDefinition> Shaders { get; init; } = new();
    public int VertexCount => Shaders.Count(s => s.Stage == ShaderStage.Vertex);
    public int PixelCount => Shaders.Count(s => s.Stage == ShaderStage.Pixel);

    /// <summary>Best-effort match by short name (e.g. a material technique → a shader).</summary>
    public IEnumerable<ShaderDefinition> FindByName(string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) yield break;
        foreach (var s in Shaders)
            if (s.ShortName.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                || s.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                yield return s;
    }

    public int CountMatches(string fragment) => FindByName(fragment).Count();
}

public static class ShaderScanner
{
    /// <summary>Scan a ShaderCache WAD: each <c>.vs.dx11</c>/<c>.ps.dx11</c> entry is a shader TOC.</summary>
    public static ShaderDatabase Scan(WadArchive shaderCache, Action<float>? progress = null)
    {
        var db = new ShaderDatabase();
        var tocs = shaderCache.Entries
            .Where(e => e.IsResolved && (e.Path.EndsWith(".vs.dx11", StringComparison.OrdinalIgnoreCase)
                                         || e.Path.EndsWith(".ps.dx11", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        int i = 0;
        foreach (var e in tocs)
        {
            bool vs = e.Path.EndsWith(".vs.dx11", StringComparison.OrdinalIgnoreCase);
            string name = e.Path[..^(vs ? ".vs.dx11".Length : ".ps.dx11".Length)];

            int variants = 0;
            var defines = new List<string>();
            try
            {
                var toc = new ShaderToc(new MemoryStream(shaderCache.Extract(e), false));
                variants = toc.ShaderIds.Count;
                defines = toc.BaseDefines.Select(d => d.ToString() ?? "").Where(s => s.Length > 0).Take(32).ToList();
            }
            catch { /* keep the entry even if its TOC won't parse */ }

            db.Shaders.Add(new ShaderDefinition
            {
                Name = name,
                Path = e.Path,
                PathHash = e.PathHash,
                Stage = vs ? ShaderStage.Vertex : ShaderStage.Pixel,
                VariantCount = variants,
                BytecodeSize = e.UncompressedSize,
                BaseDefines = defines,
            });
            progress?.Invoke((float)(++i) / Math.Max(1, tocs.Count));
        }
        return db;
    }
}

/// <summary>Persists the scanned shader catalogue to <c>.reyengine/shader_cache.json</c>.</summary>
public static class ShaderCacheService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(ShaderDatabase db, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(db, Options));
    }

    public static ShaderDatabase? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ShaderDatabase>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }
}
