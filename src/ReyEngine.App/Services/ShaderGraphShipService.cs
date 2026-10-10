using System.Text;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.Services;

/// <summary>The files a build adds to the package for its Shader Graphs, and the lines the build report shows.</summary>
public sealed class ShaderGraphShipResult
{
    /// <summary>(path inside the ShaderCache.dx11 WAD, bytes). Written to the STAGING folder of the build only.</summary>
    public required IReadOnlyList<(string Path, byte[] Bytes)> Files { get; init; }
    public required IReadOnlyList<string> Report { get; init; }
    public required IReadOnlyList<SgAssignedMaterial> Materials { get; init; }
}

/// <summary>
/// M833: ships Shader Graphs. At pack time it finds every material of the STAGED bins that carries <c>REY_GRAPH</c>, validates each
/// (<see cref="SgShipValidator"/>), compiles every assigned graph for every input signature of its base shader, and builds - from Riot's
/// INSTALLED shader cache - the twinned pixel TOC and the last container with the graph blobs appended (<see cref="SgShipBuilder"/>).
/// Nothing is written here: the caller puts the returned files in its staging folder. Generating at pack time means a re-export after a
/// patch picks up Riot's re-cooks by itself; an unreadable installed cache refuses the export rather than shipping stale data.
/// </summary>
public static class ShaderGraphShipService
{
    private static readonly byte[] Needle = Encoding.ASCII.GetBytes(SgShip.Macro);

    /// <summary>The materials of the staged bins that name a graph. A bin is parsed only when its bytes hold the macro name.</summary>
    public static IReadOnlyList<SgAssignedMaterial> FindAssigned(IEnumerable<string> stagedDirs, Func<uint, string?> resolveBinName, List<string> errors)
    {
        var found = new List<SgAssignedMaterial>();
        foreach (string dir in stagedDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.bin", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); } catch (Exception ex) { errors.Add($"{rel}: cannot be read to check it for Shader Graphs: {ex.Message}"); continue; }
                if (bytes.AsSpan().IndexOf(Needle) < 0) continue;
                int before = found.Count;
                MaterialDocument doc;
                try { doc = MaterialDocument.Parse(bytes, resolveBinName); }
                catch (Exception ex) { errors.Add($"{rel}: mentions {SgShip.Macro} but cannot be read as a materials bin: {ex.Message}"); continue; }
                foreach (var m in doc.Materials)
                {
                    if (m.IsLinked) continue;
                    var macro = m.AllMacros.FirstOrDefault(x => SgShip.IsGraphMacro(x.Name));
                    if (macro is null) continue;
                    found.Add(new SgAssignedMaterial(rel, m.Name, m.RenderShader ?? m.ShaderName, macro.Value, m.Parameters.Select(p => p.Name).ToList(),
                        m.Slots.Select(sl => sl.SamplerName).ToList()));
                }
                if (found.Count == before) errors.Add($"{rel}: mentions {SgShip.Macro} but no material was recognised (it is not a StaticMaterialDef macro, or the bin did not parse as materials). Blocked rather than shipped without its shader.");
            }
        }
        return found;
    }

    /// <summary>Null when no staged material names a graph (nothing to do). Throws <see cref="InvalidOperationException"/> with every problem
    /// when the export must be blocked.</summary>
    public static ShaderGraphShipResult? Prepare(string? projectRoot, string? gameFinalDir, IReadOnlyList<string> stagedDirs,
        Func<uint, string?> resolveBinName, IHashResolver? hashes)
    {
        var errors = new List<string>();
        var assigned = FindAssigned(stagedDirs, resolveBinName, errors);
        if (assigned.Count == 0)
        {
            if (errors.Count > 0) throw new InvalidOperationException(Blocked(errors));
            return null;
        }
        if (projectRoot is null) throw new InvalidOperationException("Shader Graphs are assigned, but there is no project folder to read them from.");
        if (gameFinalDir is null || !Directory.Exists(gameFinalDir))
            throw new InvalidOperationException("Shader Graphs are assigned, but the game folder is not set, so Riot's installed shader cache cannot be read. Refusing to ship a stale shader cache patch: set the game folder in Project Settings.");

        using var cache = ShaderCacheReader.Open(gameFinalDir, hashes, out string? cacheError)
            ?? throw new InvalidOperationException($"Riot's installed shader cache cannot be read ({cacheError}). Refusing to ship a stale shader cache patch.");
        var perms = new ShaderPermutationIndex(gameFinalDir, h => hashes is not null && hashes.TryGetPath(h, out var p) ? p : null);

        try { return PrepareCore(projectRoot, stagedDirs, assigned, errors, cache, perms); }
        finally { perms.Dispose(); }
    }

    private static ShaderGraphShipResult PrepareCore(string projectRoot, IReadOnlyList<string> stagedDirs, IReadOnlyList<SgAssignedMaterial> assigned,
        List<string> errors, ShaderCacheReader cache, ShaderPermutationIndex perms)
    {
        // ---- bases, graphs, material rules
        var bases = new Dictionary<string, SgBase?>(StringComparer.Ordinal);
        var graphs = new SortedDictionary<(string Shader, int Number), (SgResolvedGraph Graph, string Material)>();
        foreach (var m in assigned)
        {
            string shader = m.Shader ?? "";
            string key = SgBase.Normalize(shader);
            if (!SgBase.IsSupported(shader))
            {
                bool unresolved = shader.Length == 0 || shader.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                errors.Add($"Material '{m.Material}' ({m.BinPath}) names a Shader Graph but uses '{(shader.Length == 0 ? "no shader" : shader)}', which is not a base shader Shader Graphs support."
                    + (unresolved ? " Its shader name could not be resolved: is the hash dictionary loaded (Preferences > hashes)?" : ""));
                continue;
            }
            if (!bases.TryGetValue(key, out var b))
            {
                b = ShaderGraphBaseResolver.Resolve(cache, perms, shader, out string why, out _);
                if (b is null) errors.Add($"The base shader {shader} cannot be read from the installed game: {why}");
                bases[key] = b;
            }
            if (!SgShipValidator.TryParseNumber(m.GraphMacroValue, out int n)) { errors.AddRange(SgShipValidator.Validate(m, new SgResolvedGraph(null, null, null, null), b)); continue; }
            var resolved = SgShipValidator.Resolve(projectRoot, shader, n);
            errors.AddRange(SgShipValidator.Validate(m, resolved, b));
            if (resolved.Document is not null && b is not null && SgBase.Normalize(resolved.Document.BaseShader) == key)
                graphs.TryAdd((key, n), (resolved, m.Material));
        }
        if (errors.Count > 0) throw new InvalidOperationException(Blocked(errors));

        // ---- compile every graph for every signature
        var blobsByShader = new Dictionary<string, List<SgGraphBlobs>>(StringComparer.Ordinal);
        foreach (var ((key, n), (g, material)) in graphs)
        {
            var b = bases[key]!;
            var doc = g.Document!;
            var build = ShaderGraphCompiler.BuildAll(doc, b);
            string gname = $"'{g.Entry!.File}' (#{n}, used by '{material}')";
            if (!build.Ok)
            {
                foreach (var d in build.Diagnostics.Where(d => d.IsError)) errors.Add($"Shader Graph {gname}: {d.Message}" + (d.NodeId.Length > 0 ? $" [node {d.NodeId}]" : ""));
                if (build.Compiled.Count == 0 && !errors.Any(e => e.Contains(gname))) errors.Add($"Shader Graph {gname}: it did not compile.");
                continue;
            }
            if (build.Compiled.Count != b.Signatures.Count || build.Compiled.Any(c => c.Bytecode is null))
            { errors.Add($"Shader Graph {gname}: it compiled for {build.Compiled.Count} of {b.Signatures.Count} pixel input signatures."); continue; }
            if (!blobsByShader.TryGetValue(key, out var list)) blobsByShader[key] = list = new List<SgGraphBlobs>();
            list.Add(new SgGraphBlobs(n, g.Entry.File, b.Signatures.Select(s => build.For(s)!.Bytecode!).ToList()));
        }
        if (errors.Count > 0) throw new InvalidOperationException(Blocked(errors));

        // ---- one patch per base shader
        var files = new List<(string, byte[])>();
        var report = new List<string>();
        foreach (var (key, list) in blobsByShader)
        {
            var b = bases[key]!;
            var toc = cache.ReadToc(b.PixelTocPath) ?? throw new InvalidOperationException($"The installed shader cache has no pixel table for {b.Shader}.");
            byte[]? tocBytes = cache.ReadEntry(toc.Path, out _);
            if (tocBytes is null) throw new InvalidOperationException($"{toc.Path} cannot be read from the installed shader cache. Refusing to ship a stale shader cache patch.");
            uint last = (toc.DeclaredBlobCount - 1) / 100 * 100;
            byte[]? container = cache.ReadContainer(toc.Path, last, out string? containerPath, out string? why);
            if (container is null || containerPath is null) throw new InvalidOperationException($"The last blob container of {toc.Path} cannot be read ({why}). Refusing to ship a stale shader cache patch.");

            // conflict: the project already ships this table or container (an RTX-style ShaderCache.dx11 folder) - no merging in v1
            foreach (string dir in stagedDirs)
                foreach (string candidate in new[] { toc.Path, containerPath }.SelectMany(ShaderCacheReader.CachePathCandidates).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(Path.Combine(dir, candidate.Replace('/', Path.DirectorySeparatorChar))))
                        throw new InvalidOperationException($"The project already ships {candidate} (its own ShaderCache.dx11 content). A Shader Graph on {b.Shader} needs to add to that table and container, and merging them is not supported yet. Remove that file from the project, or unassign the graphs of {b.Shader}.");

            SgShipPatch patch;
            try { patch = SgShipBuilder.Build(b, toc, tocBytes, container, containerPath, list); }
            catch (InvalidDataException ex) { throw new InvalidOperationException($"The shader cache patch for {b.Shader} cannot be built: {ex.Message}"); }

            files.Add((patch.TocPath, patch.Toc));
            files.Add((patch.ContainerPath, patch.Container));
            report.Add($"Shader Graph: {b.Shader} - installed table {patch.TocPath} sha1 {patch.BaseTocSha1}, container {patch.ContainerPath} sha1 {patch.BaseContainerSha1}.");
            report.Add($"Shader Graph: {list.Count} graph(s) [{string.Join(", ", list.OrderBy(g => g.Number).Select(g => $"#{g.Number} {g.GraphName}"))}] -> {patch.NewBlobs} blob(s) from #{patch.FirstNewBlob}, {patch.TwinKeys} twin keys ({patch.ColourTwins} colour, {patch.ShadowTwins} shadow -> Riot's own).");
        }
        report.Add("Shader Graph: this mod replaces Riot's pixel table of each base shader above; two mods that ship Shader Graphs on the same base overwrite each other, so enable only one.");
        foreach (var m in assigned)
            report.Add($"Shader Graph: material '{m.Material}' ({m.BinPath}) uses graph #{m.GraphMacroValue}.");
        return new ShaderGraphShipResult { Files = files, Report = report, Materials = assigned };
    }

    private static string Blocked(IReadOnlyList<string> errors) =>
        "The export is blocked by Shader Graph problems (the game crashes or draws nothing on them):\n - " + string.Join("\n - ", errors.Distinct());
}
