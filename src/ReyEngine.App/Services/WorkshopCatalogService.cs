using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.Services;

public sealed record WorkshopMaterialTemplate(
    string Shader, string MaterialName, uint MaterialHash, ulong SourceBinHash, string SourceBinPath, string SourceWad,
    string Profile, string Features, int Samplers, int Parameters, IReadOnlyList<string> TexturePaths);

public sealed record WorkshopParticleTemplate(
    uint SystemHash, string Name, string ParticlePath, ulong SourceBinHash, string SourceBinPath,
    string SourceWad, int Emitters, int VisualEmitters, string? PreviewTexturePath);

public sealed record WorkshopCatalog(string Fingerprint, DateTime BuiltUtc,
    IReadOnlyList<WorkshopMaterialTemplate> Materials, IReadOnlyList<WorkshopParticleTemplate> Particles);

public sealed record WorkshopCatalogProgress(int CompletedWads, int TotalWads, int Materials, int Particles, string Current)
{
    public int Percent => TotalWads == 0 ? 0 : (int)Math.Round(CompletedWads * 100.0 / TotalWads);
}

/// <summary>Whole-install, patch-aware Workshop index. The project VFS intentionally mounts only the
/// current map and shared WADs; this service indexes all champion/map WADs on demand and caches only the
/// compact de-duplicated templates. A WAD timestamp/size fingerprint invalidates it after a Riot patch.</summary>
public sealed class WorkshopCatalogService
{
    private static readonly uint StaticMaterialClass = HashAlgorithms.Fnv1a("StaticMaterialDef");
    private readonly IHashResolver _resolver;
    private readonly Func<uint, string?> _resolveBinName;
    private readonly ConcurrentDictionary<ulong, string> _assetWads = new();
    private IReadOnlyList<string> _wads = Array.Empty<string>();

    public WorkshopCatalogService(IHashResolver resolver, Func<uint, string?> resolveBinName)
    { _resolver = resolver; _resolveBinName = resolveBinName; }

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReyEngine", "Cache", "workshop-catalog-v3.json");

    public async Task<WorkshopCatalog> LoadAsync(string finalDirectory, bool rebuild,
        IProgress<WorkshopCatalogProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _assetWads.Clear();
        _wads = Directory.EnumerateFiles(finalDirectory, "*.wad.client", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        string fingerprint = Fingerprint(finalDirectory, _wads);

        WorkshopCatalog? cached = !rebuild ? ReadCache(fingerprint) : null;
        var materials = new ConcurrentDictionary<string, (WorkshopMaterialTemplate Item, int Score)>(StringComparer.OrdinalIgnoreCase);
        var particles = new ConcurrentDictionary<string, (WorkshopParticleTemplate Item, int Score)>(StringComparer.OrdinalIgnoreCase);
        int completed = 0;

        await Parallel.ForEachAsync(_wads, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6),
            CancellationToken = cancellationToken,
        }, async (wadPath, token) =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            try
            {
                using var wad = WadArchive.Open(wadPath, _resolver);
                foreach (var entry in wad.Entries)
                    _assetWads.TryAdd(entry.PathHash, wadPath);

                if (cached is null)
                {
                    foreach (var entry in wad.Entries.Where(e => e.IsResolved
                                 && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
                    {
                        token.ThrowIfCancellationRequested();
                        byte[] bytes;
                        try { bytes = wad.Extract(entry); } catch { continue; }

                        // StaticMaterialDefs are not confined to *.materials.bin and /skins/. Riot also
                        // places test/utility materials in ordinary bins. The cheap class-hash probe keeps
                        // us exhaustive without parsing every non-material bin a second time.
                        if (ContainsU32(bytes, StaticMaterialClass))
                        {
                            try
                            {
                                var doc = MaterialDocument.Parse(bytes, _resolveBinName);
                                foreach (var binding in doc.Materials.Where(m => m.IsStaticMaterialDef
                                             && !string.IsNullOrWhiteSpace(m.ShaderName)))
                                {
                                    var textures = binding.Slots
                                        .OrderByDescending(s => ReferenceEquals(s, binding.Diffuse))
                                        .Select(s => Normalize(s.Path))
                                        .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                                    var item = new WorkshopMaterialTemplate(binding.ShaderName, binding.Name, binding.ObjectPathHash,
                                        entry.PathHash, entry.Path, wadPath, binding.Profile.ProfileLabel,
                                        binding.Profile.FeatureSummary, binding.Slots.Count, binding.Parameters.Count, textures);
                                    int score = (textures.Length > 0 ? 1000 : 0) + binding.Slots.Count * 25
                                        + binding.Parameters.Count * 5 + binding.Switches.Count;
                                    materials.AddOrUpdate(binding.ShaderName, (item, score), (_, old) => score > old.Score ? (item, score) : old);
                                }
                            }
                            catch { /* a skin/map bin without StaticMaterialDefs */ }
                        }

                        try
                        {
                            foreach (var system in VfxSystemResolver.ExtractAll(bytes).Values)
                            {
                                string key = CanonicalParticle(system);
                                if (key.Length == 0) continue;
                                int visual = system.Emitters.Count(e => e.IsVisual);
                                string? preview = system.Emitters.SelectMany(e => new[]
                                    { e.TexturePath, e.ParticleColorTexturePath, e.TextureMultPath,
                                      e.Distortion?.NormalMapTexturePath }).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                                var item = new WorkshopParticleTemplate(system.PathHash, system.Name, system.ParticlePath,
                                    entry.PathHash, entry.Path, wadPath, system.Emitters.Count, visual,
                                    preview is null ? null : Normalize(preview));
                                int score = visual * 100 + system.Emitters.Count + (preview is null ? 0 : 1000);
                                particles.AddOrUpdate(key, (item, score), (_, old) => score > old.Score ? (item, score) : old);
                            }
                        }
                        catch { /* ordinary non-VFX bin */ }
                    }
                }
            }
            catch { /* one damaged/locked WAD must not hide the rest of the Workshop */ }
            finally
            {
                int done = Interlocked.Increment(ref completed);
                progress?.Report(new WorkshopCatalogProgress(done, _wads.Count,
                    cached?.Materials.Count ?? materials.Count, cached?.Particles.Count ?? particles.Count,
                    Path.GetFileName(wadPath)));
            }
        });

        if (cached is not null) return cached;
        var catalog = new WorkshopCatalog(fingerprint, DateTime.UtcNow,
            materials.Values.Select(x => x.Item).OrderBy(x => x.Shader, StringComparer.OrdinalIgnoreCase).ToArray(),
            particles.Values.Select(x => x.Item).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        WriteCache(catalog);
        return catalog;
    }

    public byte[]? ReadAsset(string virtualPath)
    {
        ulong hash = HashAlgorithms.WadPath(Normalize(virtualPath));
        if (!_assetWads.TryGetValue(hash, out var wadPath)) return null;
        try { using var wad = WadArchive.Open(wadPath); return wad.Extract(hash); }
        catch { return null; }
    }

    public byte[]? ReadBin(ulong hash, string sourceWad)
    {
        try { using var wad = WadArchive.Open(sourceWad); return wad.Extract(hash); }
        catch { return null; }
    }

    /// <summary>Source bin plus PROP dependencies, breadth-first, using the whole-install hash index.</summary>
    public IReadOnlyList<byte[]> ReadBinClosure(ulong rootHash, string sourceWad, int limit = 128)
    {
        var result = new List<byte[]>();
        var seen = new HashSet<ulong>();
        var queue = new Queue<(ulong Hash, string? Wad)>();
        queue.Enqueue((rootHash, sourceWad));
        while (queue.Count > 0 && result.Count < limit)
        {
            var (hash, knownWad) = queue.Dequeue();
            if (!seen.Add(hash)) continue;
            string? wadPath = knownWad;
            if (wadPath is null && !_assetWads.TryGetValue(hash, out wadPath)) continue;
            byte[]? bytes;
            try { using var wad = WadArchive.Open(wadPath!); bytes = wad.Extract(hash); }
            catch { continue; }
            result.Add(bytes);
            foreach (var dependency in VfxSystemResolver.ExtractDependencies(bytes))
                queue.Enqueue((HashAlgorithms.WadPath(Normalize(dependency)), null));
        }
        return result;
    }

    private WorkshopCatalog? ReadCache(string fingerprint)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var result = JsonSerializer.Deserialize<WorkshopCatalog>(File.ReadAllText(CachePath));
            return result?.Fingerprint == fingerprint ? result : null;
        }
        catch { return null; }
    }

    private static void WriteCache(WorkshopCatalog catalog)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch { /* cache failure does not make the Workshop unusable */ }
    }

    private static string Fingerprint(string finalDirectory, IEnumerable<string> wads)
    {
        var text = new StringBuilder(finalDirectory.ToLowerInvariant());
        foreach (var wad in wads)
        {
            var info = new FileInfo(wad);
            text.Append('|').Append(Path.GetRelativePath(finalDirectory, wad).ToLowerInvariant())
                .Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string CanonicalParticle(VfxSystemDefinition system)
    {
        string key = !string.IsNullOrWhiteSpace(system.ParticlePath) ? system.ParticlePath : system.Name;
        return key.Trim().Replace('\\', '/').ToLowerInvariant();
    }

    private static bool ContainsU32(ReadOnlySpan<byte> bytes, uint value)
    {
        Span<byte> needle = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, value);
        return bytes.IndexOf(needle) >= 0;
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return path.Trim().Replace('\\', '/').TrimStart('/');
    }
}
