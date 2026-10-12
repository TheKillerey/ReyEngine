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

    /// <summary>M834: files for the GLOBAL WAD (Global.wad.client): <c>data/shaders/shaders.bin</c> with the graphs' custom parameters declared, when any graph has some.
    /// Empty otherwise. Written to the staging folder of the build only, in the base layer, like <see cref="Files"/>.</summary>
    public IReadOnlyList<(string Path, byte[] Bytes)> GlobalFiles { get; init; } = Array.Empty<(string, byte[])>();
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
    public static IReadOnlyList<SgAssignedMaterial> FindAssigned(IEnumerable<string> stagedDirs, Func<uint, string?> resolveBinName, List<string> errors,
        IReadOnlyDictionary<string, List<(string Stem, string Base)>>? custom = null, IReadOnlyDictionary<string, List<(string Stem, string Base)>>? customTextures = null)
    {
        var found = new List<SgAssignedMaterial>();
        var customNeedles = (custom?.Keys ?? Enumerable.Empty<string>()).Concat(customTextures?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(k => (Name: k, Bytes: Encoding.UTF8.GetBytes(k))).ToList();
        foreach (string dir in stagedDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.bin", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); } catch (Exception ex) { errors.Add($"{rel}: cannot be read to check it for Shader Graphs: {ex.Message}"); continue; }
                bool mentionsGraph = bytes.AsSpan().IndexOf(Needle) >= 0;
                bool mentionsCustom = customNeedles.Any(n => bytes.AsSpan().IndexOf(n.Bytes) >= 0);
                if (!mentionsGraph && !mentionsCustom) continue;
                int before = found.Count;
                MaterialDocument doc;
                try { doc = MaterialDocument.Parse(bytes, resolveBinName); }
                catch (Exception ex)
                {
                    // a bin that only happens to hold a custom parameter's name is not necessarily a materials bin
                    if (mentionsGraph) errors.Add($"{rel}: mentions {SgShip.Macro} but cannot be read as a materials bin: {ex.Message}");
                    continue;
                }
                foreach (var m in doc.Materials)
                {
                    if (m.IsLinked) continue;
                    var macro = m.AllMacros.FirstOrDefault(x => SgShip.IsGraphMacro(x.Name));
                    if (macro is null)
                    {
                        // M834: a material that is NOT assigned to a graph but carries one of the project's custom parameters on a base shader Shader Graphs support:
                        // no shaders.bin declares it for this material (the mod only declares the parameters of ASSIGNED graphs) - the game stops with
                        // "Missing shader constant". Typical causes: the graph was unassigned, the parameter was renamed or deleted in the graph, or an editor-only undo.
                        string sh = m.RenderShader ?? m.ShaderName ?? "";
                        if (custom is not null && SgBase.IsSupported(sh))
                            foreach (var p in m.Parameters)
                                if (custom.TryGetValue(p.Name, out var info) && DeclaringGraphs(info, sh) is { Length: > 0 } graphs)
                                    errors.Add($"Material '{m.Name}' ({rel}) carries the parameter '{p.Name}', a custom parameter of the {graphs}, but it is not assigned to a graph that declares it, so no shaders.bin declares it and the game stops with \"Missing shader constant\". Remove the parameter from the material, or assign the graph.");
                        // M835: the same for a custom TEXTURE: a samplerValues entry no definition declares (the graph was unassigned, or the texture renamed or deleted)
                        if (customTextures is not null && SgBase.IsSupported(sh))
                            foreach (var sl in m.Slots)
                                if (customTextures.TryGetValue(sl.SamplerName, out var tinfo) && DeclaringGraphs(tinfo, sh) is { Length: > 0 } tgraphs)
                                    errors.Add($"Material '{m.Name}' ({rel}) carries the texture '{sl.SamplerName}', a custom texture of the {tgraphs}, but it is not assigned to a graph that declares it, so no shaders.bin declares it. Remove the texture entry from the material, or assign the graph.");
                        continue;
                    }
                    found.Add(new SgAssignedMaterial(rel, m.Name, m.RenderShader ?? m.ShaderName, macro.Value, m.Parameters.Select(p => p.Name).ToList(),
                        m.Slots.Select(sl => sl.SamplerName).ToList(), m.Parameters.Where(p => p.TypeName != "Vector4").Select(p => p.Name).ToList()));
                }
                if (mentionsGraph && found.Count == before) errors.Add($"{rel}: mentions {SgShip.Macro} but no material was recognised (it is not a StaticMaterialDef macro, or the bin did not parse as materials). Blocked rather than shipped without its shader.");
            }
        }
        return found;
    }

    private static void AddDeclaring(Dictionary<string, List<(string Stem, string Base)>> map, string name, string stem, string baseShader)
    {
        if (!map.TryGetValue(name, out var list)) map[name] = list = new List<(string Stem, string Base)>();
        list.Add((stem, baseShader));
    }

    /// <summary>M839: "Shader Graph 'A'" / "Shader Graphs 'A', 'B'" - EVERY graph built for the material's base shader that declares the name (the first one alone
    /// could be a graph the material was never assigned to). Empty when none is built for that base.</summary>
    internal static string DeclaringGraphs(IReadOnlyList<(string Stem, string Base)> declaring, string shader)
    {
        var stems = declaring.Where(g => SgBase.Normalize(g.Base) == SgBase.Normalize(shader)).Select(g => g.Stem)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (stems.Count == 0) return "";
        return (stems.Count == 1 ? "Shader Graph " : "Shader Graphs ") + string.Join(", ", stems.Select(x => $"'{x}'"));
    }

    /// <summary>Every custom parameter name of every Shader Graph file of the project (whether or not any material is assigned to it): name -> (graph file stem, base shader).</summary>
    public static IReadOnlyDictionary<string, List<(string Stem, string Base)>> ProjectCustomParameters(string? projectRoot)
    {
        var map = new Dictionary<string, List<(string Stem, string Base)>>(StringComparer.OrdinalIgnoreCase);
        if (projectRoot is null) return map;
        string dir = ShaderGraphRegistry.FolderIn(projectRoot);
        if (!Directory.Exists(dir)) return map;
        foreach (string file in Directory.EnumerateFiles(dir, "*" + ShaderGraphDocument.FileSuffix))
        {
            try
            {
                var doc = ShaderGraphJson.TryDeserialize(File.ReadAllText(file), out _);
                if (doc is null) continue;
                foreach (var p in doc.Params) AddDeclaring(map, p.Name, ShaderGraphRegistry.StemOf(file), doc.BaseShader);
            }
            catch { /* an unreadable graph file declares nothing; the assigned ones are reported by the validator */ }
        }
        return map;
    }

    /// <summary>M835: every custom TEXTURE name of every Shader Graph file of the project: name -> (graph file stem, base shader).</summary>
    public static IReadOnlyDictionary<string, List<(string Stem, string Base)>> ProjectCustomTextures(string? projectRoot)
    {
        var map = new Dictionary<string, List<(string Stem, string Base)>>(StringComparer.OrdinalIgnoreCase);
        if (projectRoot is null) return map;
        string dir = ShaderGraphRegistry.FolderIn(projectRoot);
        if (!Directory.Exists(dir)) return map;
        foreach (string file in Directory.EnumerateFiles(dir, "*" + ShaderGraphDocument.FileSuffix))
        {
            try
            {
                var doc = ShaderGraphJson.TryDeserialize(File.ReadAllText(file), out _);
                if (doc is null) continue;
                foreach (var t in doc.Textures) AddDeclaring(map, t.Name, ShaderGraphRegistry.StemOf(file), doc.BaseShader);
            }
            catch { /* an unreadable graph file declares nothing; the assigned ones are reported by the validator */ }
        }
        return map;
    }

    /// <summary>Null when no staged material names a graph (nothing to do). Throws <see cref="InvalidOperationException"/> with every problem
    /// when the export must be blocked.</summary>
    public static ShaderGraphShipResult? Prepare(string? projectRoot, string? gameFinalDir, IReadOnlyList<string> stagedDirs,
        Func<uint, string?> resolveBinName, IHashResolver? hashes, Func<string, bool>? assetExists = null)
    {
        var errors = new List<string>();
        var assigned = FindAssigned(stagedDirs, resolveBinName, errors, ProjectCustomParameters(projectRoot), ProjectCustomTextures(projectRoot));
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

        try { return PrepareCore(projectRoot, gameFinalDir, stagedDirs, assigned, errors, cache, perms, assetExists); }
        finally { perms.Dispose(); }
    }

    private static ShaderGraphShipResult PrepareCore(string projectRoot, string gameFinalDir, IReadOnlyList<string> stagedDirs, IReadOnlyList<SgAssignedMaterial> assigned,
        List<string> errors, ShaderCacheReader cache, ShaderPermutationIndex perms, Func<string, bool>? assetExists)
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
        // M835: a custom texture's default image is the link the shipped shaders.bin carries: it must name an asset the game or the project has
        var archives = new List<ReyEngine.Core.Wad.WadArchive>();
        bool opened = false;
        bool Exists(string path)
        {
            if (assetExists?.Invoke(path) == true) return true;
            if (stagedDirs.Any(d => File.Exists(Path.Combine(d, path.Replace('/', Path.DirectorySeparatorChar))))) return true;
            ulong hash = HashAlgorithms.WadPath(path);
            if (!opened)
            {
                opened = true;
                // the game's own archives a default image can come from: the top-level ones (Global, DATA, ...) and the maps (not the 170 champions: name those by a project file)
                foreach (string dir in new[] { gameFinalDir, Path.Combine(gameFinalDir, "Maps", "Shipping") })
                    if (Directory.Exists(dir))
                        foreach (string wad in Directory.EnumerateFiles(dir, "*.wad.client"))
                            try { archives.Add(ReyEngine.Core.Wad.WadArchive.Open(wad)); } catch { /* an archive that does not open names nothing */ }
            }
            return archives.Any(a => a.TryGetEntry(hash, out _));
        }
        try
        {
            foreach (var ((_, n), (g, material)) in graphs)
                foreach (var t in g.Document!.Textures)
                    if (t.NormalizedPath.Length > 0 && !Exists(t.NormalizedPath))
                        errors.Add($"Shader Graph '{g.Entry!.File}' (#{n}, used by '{material}'): the default image of custom texture '{t.Name}' is '{t.NormalizedPath}', which is not an asset of the game or of this project. Fix the path in the graph's texture settings, or clear it.");
        }
        finally { foreach (var a in archives) a.Dispose(); }
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

        // ---- M834: custom parameters -> the base shader's declarations in a shipped shaders.bin (Riot's, regenerated from the installed file, never cached)
        var globalFiles = new List<(string, byte[])>();
        var report = new List<string>();
        var declare = new Dictionary<string, (SgBase Base, Dictionary<string, SgDeclaredParam> Params, List<string> Graphs, Dictionary<string, SgDeclaredTexture> Textures)>(StringComparer.Ordinal);
        foreach (var ((key, n), (g, material)) in graphs)
        {
            if (g.Document!.Params.Count == 0 && g.Document.Textures.Count == 0) continue;
            if (!declare.TryGetValue(key, out var d)) declare[key] = d = (bases[key]!, new Dictionary<string, SgDeclaredParam>(StringComparer.OrdinalIgnoreCase), new List<string>(), new Dictionary<string, SgDeclaredTexture>(StringComparer.OrdinalIgnoreCase));
            d.Graphs.Add($"#{n} {g.Entry!.File}");
            foreach (var t in SgCustomTextures.Declarations(g.Document))
            {
                if (d.Textures.TryGetValue(t.Name, out var otherTex) && (otherTex.Name != t.Name || otherTex.DefaultPath != t.DefaultPath))
                    errors.Add($"Two Shader Graphs on {d.Base.Shader} declare the custom texture '{t.Name}' differently (name case or default path): rename one.");
                d.Textures[t.Name] = t;
            }
            foreach (var p in SgCustomParams.Declarations(g.Document))
            {
                if (d.Params.TryGetValue(p.Name, out var other))
                {
                    if (!string.Equals(other.Name, p.Name, StringComparison.Ordinal))
                        errors.Add($"Two Shader Graphs on {d.Base.Shader} declare custom parameters that differ only in case ('{other.Name}' and '{p.Name}'): the game and the editor compare names without regard to case. Rename one.");
                    else if (other.Components != p.Components)
                        errors.Add($"Two Shader Graphs on {d.Base.Shader} declare the custom parameter '{p.Name}' with different types ({SgCatalog.TypeName(other.Components)} and {SgCatalog.TypeName(p.Components)}): rename one.");
                }
                d.Params[p.Name] = p;
            }
        }
        if (declare.Count > 0)
        {
            // merging with a project's own shaders.bin is not supported: refuse rather than guess
            ulong shadersHash = HashAlgorithms.WadPath(SgShadersBin.GamePath);
            foreach (string dir in stagedDirs)
                if (Directory.Exists(dir) && ReyEngine.Core.Build.WadPackService.EnumerateChunkFiles(dir).Any(f => f.hash == shadersHash))   // the named file AND the hash-named 0x....bin form
                    errors.Add($"The project already ships its own {SgShadersBin.GamePath}, and Shader Graph custom parameters need to add to it (merging is not supported). Remove that file from the project, or remove the custom parameters from the graphs.");
            if (errors.Count > 0) throw new InvalidOperationException(Blocked(errors));
            byte[] riotBin;
            try { riotBin = ReadInstalledShadersBin(gameFinalDir); }
            catch (Exception ex) { throw new InvalidOperationException($"The installed {SgShadersBin.GamePath} cannot be read ({ex.Message}). Refusing to ship custom parameters without it."); }
            byte[] bin = riotBin;
            foreach (var (_, d) in declare)
            {
                var add = d.Params.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                var addTex = d.Textures.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
                if (addTex.Count > 0 && !SgShip.CustomTexturesProven)
                    throw new InvalidOperationException(SgShip.CustomTexturesBlocked + ".");
                byte[] next;
                try { next = SgShadersBin.Patch(bin, d.Base.Shader, add, addTex); }
                catch (InvalidDataException ex) { throw new InvalidOperationException($"The custom parameters of {string.Join(", ", d.Graphs)} cannot be declared in shaders.bin: {ex.Message}"); }
                var problems = SgShadersBin.Verify(bin, next, d.Base.Shader, add, addTex);
                if (problems.Count > 0) throw new InvalidOperationException("The patched shaders.bin is not Riot's plus the custom parameters, so it is not shipped: " + string.Join("; ", problems));
                bin = next;
                report.Add($"Shader Graph: {d.Base.Shader} declares {add.Count} custom parameter(s) [{string.Join(", ", add.Select(p => p.Name + ":" + SgCatalog.TypeName(p.Components)))}]"
                    + (addTex.Count > 0 ? $" and {addTex.Count} custom texture(s) [{string.Join(", ", addTex.Select(t => t.Name))}]" : "") + $" for {string.Join(", ", d.Graphs)} in {SgShadersBin.GamePath}.");
            }
            globalFiles.Add((SgShadersBin.GamePath, bin));
            report.Add($"Shader Graph: {SgShadersBin.GamePath} {riotBin.Length:n0} -> {bin.Length:n0} bytes (the installed file with only those definitions changed; written to {SgShadersBin.WadName} in the base layer). {SgCustomParams.ShipRisk}.");
        }

        // ---- one patch per base shader
        var files = new List<(string, byte[])>();
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
        return new ShaderGraphShipResult { Files = files, GlobalFiles = globalFiles, Report = report, Materials = assigned };
    }

    /// <summary>Riot's shaders.bin from the installed game's Global WAD (read fresh each time).</summary>
    private static byte[] ReadInstalledShadersBin(string gameFinalDir)
    {
        string wad = Path.Combine(gameFinalDir, SgShadersBin.WadName);
        if (!File.Exists(wad)) throw new FileNotFoundException(wad + " does not exist");
        using var archive = ReyEngine.Core.Wad.WadArchive.Open(wad);
        ulong h = HashAlgorithms.WadPath(SgShadersBin.GamePath);
        if (!archive.TryGetEntry(h, out _)) throw new InvalidDataException(SgShadersBin.GamePath + " is not in " + SgShadersBin.WadName);
        return archive.Extract(h);
    }

    private static string Blocked(IReadOnlyList<string> errors) =>
        "The export is blocked by Shader Graph problems (the game crashes or draws nothing on them):\n - " + string.Join("\n - ", errors.Distinct());
}
