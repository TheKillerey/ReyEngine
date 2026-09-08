using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Particles;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.Services;

public sealed record WorkshopMaterialTemplate(
    string Shader, string MaterialName, uint MaterialHash, ulong SourceBinHash, string SourceBinPath, string SourceWad,
    string Profile, string Features, int Samplers, int Parameters, IReadOnlyList<string> TexturePaths,
    ShaderMaterialSetup CommonSetup, int ShaderUsageCount, int SetupUsageCount);

/// <summary><paramref name="IsLegacy"/> marks an effect recovered from a <c>.troybin</c> (M419). Those
/// have no source .bin - <paramref name="SourceBinPath"/> is the .troybin itself, and the import path
/// converts it on demand rather than copying an object graph.</summary>
public sealed record WorkshopParticleTemplate(
    uint SystemHash, string Name, string ParticlePath, ulong SourceBinHash, string SourceBinPath,
    string SourceWad, int Emitters, int VisualEmitters, string? PreviewTexturePath,
    bool IsLegacy = false,
    /// <summary>M425: the legacy body decoded, so rates/lifetimes/motion are the file's own values
    /// rather than engine defaults. The UI must not claim "defaults" for these.</summary>
    bool IsDecoded = false,
    /// <summary>M655: this came off the user's own shelf rather than out of the installed game, so it
    /// can be deleted and it survives a patch. See <see cref="WorkshopUserLibrary"/>.</summary>
    bool IsUser = false,
    string? UserEntryId = null,
    /// <summary>M655: false when the user's file has moved or been deleted since it was added. The row
    /// stays listed and says so - quietly dropping it would look like the Workshop lost it.</summary>
    bool UserFileExists = true);

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
    private sealed record MaterialCandidate(WorkshopMaterialTemplate Item, int Count, int Score);
    private static readonly JsonSerializerOptions CacheJson = new() { IncludeFields = true };
    private static readonly JsonSerializerOptions CacheJsonCompact = new()
        { IncludeFields = true, WriteIndented = false };
    private static readonly uint StaticMaterialClass = HashAlgorithms.Fnv1a("StaticMaterialDef");
    private readonly IHashResolver _resolver;
    private readonly Func<uint, string?> _resolveBinName;
    private readonly ConcurrentDictionary<ulong, string> _assetWads = new();
    private IReadOnlyList<string> _wads = Array.Empty<string>();

    public WorkshopCatalogService(IHashResolver resolver, Func<uint, string?> resolveBinName)
    { _resolver = resolver; _resolveBinName = resolveBinName; }

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReyEngine", "Cache", "workshop-catalog-v7.json");

    public async Task<WorkshopCatalog> LoadAsync(string finalDirectory, bool rebuild,
        IProgress<WorkshopCatalogProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _assetWads.Clear();
        _wads = Directory.EnumerateFiles(finalDirectory, "*.wad.client", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        string fingerprint = Fingerprint(finalDirectory, _wads);

        WorkshopCatalog? cached = !rebuild ? ReadCache(fingerprint) : null;
        // shader -> canonical authored setup -> representative + frequency. The old Workshop selected the
        // richest single material, which made that outlier look like the shader's default. Counting real
        // setups makes the winning template representative instead.
        var materials = new ConcurrentDictionary<string, ConcurrentDictionary<string, MaterialCandidate>>(
            StringComparer.OrdinalIgnoreCase);
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
                    // M419: legacy .troybin effects. 1,189 of them ship in DATA.wad.client and nothing in
                    // the live game references them (measured in M201), so they are invisible to the
                    // bin-graph harvest below - they are their own format, not PROP objects.
                    foreach (var entry in wad.Entries.Where(e => e.IsResolved
                                 && e.Path.EndsWith(".troybin", StringComparison.OrdinalIgnoreCase)))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (!TroyBinFile.TryParse(wad.Extract(entry), out var troy, out _)) continue;
                            string name = Path.GetFileNameWithoutExtension(entry.Path);
                            string? preview = troy!.TexturePaths.FirstOrDefault();
                            // M425: count the DECODED emitters. The string heuristic over-counts badly -
                            // firetorch_purple has 4 emitters and the heuristic reported 11 - and showing
                            // the heuristic figure made the list look like the old path was still running.
                            int emitters = troy.HasDecodedBody
                                ? troy.Emitters.Count
                                : Math.Max(1, troy.EmitterNames.Count);
                            var item = new WorkshopParticleTemplate(
                                HashAlgorithms.Fnv1a(entry.Path), name, entry.Path,
                                entry.PathHash, entry.Path, wadPath,
                                emitters, troy.TexturePaths.Any() ? emitters : 0,
                                preview is null ? null : Normalize(preview), IsLegacy: true, IsDecoded: troy.HasDecodedBody);
                            particles.AddOrUpdate("troy:" + entry.Path, (item, int.MaxValue),
                                (_, old) => old);
                        }
                        catch { /* one unreadable legacy file must not hide the rest */ }
                    }

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
                                    var setup = ShaderMaterialSetups.Capture(binding);
                                    var item = new WorkshopMaterialTemplate(binding.ShaderName, binding.Name, binding.ObjectPathHash,
                                        entry.PathHash, entry.Path, wadPath, binding.Profile.ProfileLabel,
                                        binding.Profile.FeatureSummary, binding.Slots.Count, binding.Parameters.Count, textures,
                                        setup, 1, 1);
                                    int score = (textures.Length > 0 ? 1000 : 0) + binding.Slots.Count * 25
                                        + binding.Parameters.Count * 5 + binding.Switches.Count;
                                    string signature = ShaderMaterialSetups.CanonicalSignature(setup);
                                    var bySetup = materials.GetOrAdd(binding.ShaderName,
                                        _ => new ConcurrentDictionary<string, MaterialCandidate>(StringComparer.Ordinal));
                                    bySetup.AddOrUpdate(signature, new MaterialCandidate(item, 1, score), (_, old) =>
                                        new MaterialCandidate(score > old.Score ? item : old.Item, old.Count + 1,
                                            Math.Max(score, old.Score)));
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
        var materialTemplates = materials.Select(pair =>
        {
            int total = pair.Value.Values.Sum(candidate => candidate.Count);
            var winner = pair.Value.Values.OrderByDescending(candidate => candidate.Count)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Item.MaterialName, StringComparer.OrdinalIgnoreCase).First();
            var setup = winner.Item.CommonSetup with
            {
                SourceMaterialCount = total,
                MatchingSetupCount = winner.Count,
                ExampleMaterial = winner.Item.MaterialName,
            };
            return winner.Item with
            {
                CommonSetup = setup,
                ShaderUsageCount = total,
                SetupUsageCount = winner.Count,
            };
        }).OrderBy(x => x.Shader, StringComparer.OrdinalIgnoreCase).ToArray();
        var catalog = new WorkshopCatalog(fingerprint, DateTime.UtcNow,
            materialTemplates,
            particles.Values.Select(x => x.Item).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        WriteCache(catalog);
        return catalog;
    }

    /// <summary>M655: a source that is a plain file on disk, not an archive - the user's own .troybin or
    /// .bin. Kept in one place so every read path treats the shelf the same way, and so the rule is one
    /// sentence: an absolute path to an existing file that is not a wad IS the bytes.</summary>
    private static byte[]? ReadLooseFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)) return null;
        try { return Path.IsPathRooted(path) && File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    public byte[]? ReadAsset(string virtualPath)
    {
        if (ReadLooseFile(virtualPath) is { } loose) return loose;
        ulong hash = HashAlgorithms.WadPath(Normalize(virtualPath));
        if (!_assetWads.TryGetValue(hash, out var wadPath)) return null;
        try { using var wad = WadArchive.Open(wadPath); return wad.Extract(hash); }
        catch { return null; }
    }

    public byte[]? ReadBin(ulong hash, string sourceWad)
    {
        if (ReadLooseFile(sourceWad) is { } loose) return loose;
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
            // M655: the ROOT may be one of the user's own files. Its dependencies still resolve through
            // the whole-install index, which is what makes a custom bin that references shipped textures
            // importable at all.
            if (ReadLooseFile(wadPath) is { } loose) bytes = loose;
            else
            {
                try { using var wad = WadArchive.Open(wadPath!); bytes = wad.Extract(hash); }
                catch { continue; }
            }
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
            var result = JsonSerializer.Deserialize<WorkshopCatalog>(File.ReadAllText(CachePath), CacheJson);
            return result?.Fingerprint == fingerprint ? result : null;
        }
        catch { return null; }
    }

    private static void WriteCache(WorkshopCatalog catalog)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(catalog, CacheJsonCompact));
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
