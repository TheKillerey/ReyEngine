using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.Services;

/// <summary>What a viewport draws a Shader Graph material with: the graph's compiled pixel shader for the Riot shader's input signature. The graph's own parameters and textures
/// are the MATERIAL's to carry - like the game, which declares them with no value (zero, no texture) - so nothing here fills a gap the game would not.</summary>
public sealed class SgViewportOverride
{
    public required DxbcShader Shader { get; init; }

    /// <summary>The names of the graph's custom parameters. A material that does not carry one gets ZERO (what the shipped definition declares), and the editor says so.</summary>
    public required IReadOnlyList<string> CustomParameters { get; init; }

    /// <summary>The graph's custom parameters at their defaults, as four floats: what the Shader Graph WINDOW shows while no material is picked, and what "Assign" writes into a material.</summary>
    public required IReadOnlyList<(string Name, float[] Value)> DefaultParameters { get; init; }

    /// <summary>The graph's custom textures that name a default image: (the <c>Name__TX</c> resource, the asset path). Used by the Shader Graph window with no material picked; a
    /// viewport leaves a texture the material does not carry unbound.</summary>
    public required IReadOnlyList<(string Target, string Path)> DefaultTextures { get; init; }

    public required string GraphName { get; init; }
    public required int Number { get; init; }
    public required string Hash { get; init; }
}

/// <summary>The latest verdict on a graph, for the Material Editor to show (the reason Riot's shader is drawn instead).</summary>
public sealed record SgViewportStatus(bool Ok, string Text, DateTime Utc);

/// <summary>
/// M835: Shader Graphs in the VIEWPORTS. A material with <c>REY_GRAPH=n</c> used to draw Riot's pixel shader everywhere but the Shader Graph window, because the
/// permutation resolver ignores a macro no cooked table lists. This is the one place that turns "this material names graph n" into "draw this compiled pixel shader":
/// <list type="number">
/// <item>the number is looked up in the project's registry and the graph file read (or the open editor's unsaved document, when one is registered through
/// <see cref="SetLive"/> - an edit shows before it is saved);</item>
/// <item>the base shader is read once per shader cache (<see cref="ShaderGraphBaseResolver"/>);</item>
/// <item>the graph is compiled for the Riot pixel shader's input signature with the same compiler and the same Verify as the export, and the result is cached by
/// the graph's content hash. The compile runs OUTSIDE any lock that guards the cache (a per-key <see cref="Lazy{T}"/>), so a map build compiling does not block the editor's own compile;</item>
/// <item>any problem returns null with the reason: the caller draws Riot's shader and the reason is published for the Material Editor.</item>
/// </list>
/// Thread-safe: the map builders call it from a worker thread. The project folder is a process-wide hook (<see cref="ProjectRoot"/>) set by the main window.
/// </summary>
public static class ShaderGraphViewport
{
    /// <summary>The open project's folder, or null. Set by the main window view model; tests set it to a scratch project.</summary>
    public static Func<string?>? ProjectRoot { get; set; }

    private sealed record Resolved(string Root, string Shader, int Number, string FilePath, ShaderGraphDocument Doc, string Name);

    private sealed class StampedGraph
    {
        public DateTime RegistryStamp, FileStamp;
        public string? FailFile;
        public Resolved? Value;
        public string? Problem;
    }

    private sealed class GraphEntry
    {
        public Lazy<SgAnalysis> Analysis = null!;
        public readonly ConcurrentDictionary<int, Lazy<SgCompiled>> BySignature = new();
    }

    private static readonly ConcurrentDictionary<string, StampedGraph> Graphs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ShaderGraphDocument> Live = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SgViewportStatus> Statuses = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<ShaderCacheReader, ConcurrentDictionary<string, SgBase?>> Bases = new();
    private static readonly ConditionalWeakTable<ShaderGraphDocument, StrongBox<(SgBase Base, string Hash)>> Hashes = new();
    private static readonly object EntriesLock = new();
    private static readonly Dictionary<string, GraphEntry> Entries = new(StringComparer.Ordinal);
    private static string? _lastRoot;
    private static int _compileCount;

    /// <summary>Raised (on any thread) when a graph's status changed.</summary>
    public static event Action? StatusChanged;

    /// <summary>Raised (on the UI thread, by the Shader Graph editor) when an open graph that has a file was edited, recompiled, closed or saved: the main window refreshes the viewport
    /// when materials of the open map name it. Argument: the graph file's full path.</summary>
    public static event Action<string>? GraphChanged;
    public static void NotifyGraphChanged(string filePath) => GraphChanged?.Invoke(filePath);

    /// <summary>How many compiles were run (not served from the cache) - for tests.</summary>
    public static int CompileCount => Volatile.Read(ref _compileCount);

    public static void Reset()
    {
        Graphs.Clear(); Live.Clear(); Statuses.Clear();
        lock (EntriesLock) Entries.Clear();
        Interlocked.Exchange(ref _compileCount, 0);
        _lastRoot = null;
        ProjectRoot = null;
    }

    private static string Norm(string path) => Path.GetFullPath(path).Replace('\\', '/');

    /// <summary>Register (or, with null, remove) the open editor's current document for the graph file <paramref name="filePath"/>.</summary>
    public static void SetLive(string filePath, ShaderGraphDocument? doc)
    {
        string k = Norm(filePath);
        if (doc is null) Live.TryRemove(k, out _); else Live[k] = doc.Clone();
    }

    /// <summary>True while an editor's document stands in for the file <paramref name="filePath"/> (tests; the editor releases it on close, on opening another graph and on dispose).</summary>
    public static bool IsLive(string filePath) => Live.ContainsKey(Norm(filePath));

    public static SgViewportStatus? StatusOf(string graphFilePath) => Statuses.TryGetValue(Norm(graphFilePath), out var s) ? s : null;

    public static void Report(string graphFilePath, bool ok, string text)
    {
        string k = Norm(graphFilePath);
        if (Statuses.TryGetValue(k, out var old) && old.Ok == ok && old.Text == text) return;
        Statuses[k] = new SgViewportStatus(ok, text, DateTime.UtcNow);
        StatusChanged?.Invoke();
    }

    /// <summary>The one status line of a graph that is drawn - the editor and the viewport both write exactly this, so <see cref="Report"/>'s de-duplication holds.</summary>
    public static string DrawnText(string graphName) => $"Shader Graph '{graphName}' is drawn in the viewport.";

    /// <summary>The one status line of a graph with errors.</summary>
    public static string ErrorsText(string graphName, string firstError) => $"Shader Graph '{graphName}' has errors ({firstError}): Riot's shader is drawn.";

    /// <summary>The content hash of a document on a base (what the compile cache is keyed by), computed once per document instance.</summary>
    public static string HashOf(ShaderGraphDocument doc, SgBase b)
    {
        var box = Hashes.GetValue(doc, _ => new StrongBox<(SgBase, string)>());
        lock (box)
        {
            if (box.Value.Base is { } known && ReferenceEquals(known, b)) return box.Value.Hash;
            string hash = SgShip.Sha1Hex(System.Text.Encoding.UTF8.GetBytes(ShaderGraphJson.Serialize(doc) + "\n" + b.Shader + "\n" + b.PixelTocPath + "\n" + b.PixelBlobCount));
            box.Value = (b, hash);
            return hash;
        }
    }

    private static DateTime Stamp(string? path) { if (path is null) return DateTime.MinValue; try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; } }

    /// <summary>A fingerprint of the project's graph files (length and write time of numbers.json and every graph), or null when it has none. The Content Browser's cached map
    /// thumbnails fold it into their key: a graph edit changes the picture of a map whose materials name it, and no bin changed.</summary>
    public static string? ProjectFingerprint(string? projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) return null;
        try
        {
            string dir = ShaderGraphRegistry.FolderIn(projectRoot);
            if (!Directory.Exists(dir)) return null;
            var parts = Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => { var i = new FileInfo(f); return $"{i.Name}:{i.Length}:{i.LastWriteTimeUtc.Ticks}"; }).ToList();
            return parts.Count == 0 ? null : Convert.ToHexString(System.IO.Hashing.XxHash128.Hash(System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts)))).ToLowerInvariant();
        }
        catch { return null; }
    }

    private static (Resolved? Graph, string Why) ResolveGraph(string root, string shader, int number)
    {
        // another project: what was cached or reported belongs to the old one
        if (_lastRoot is not null && !string.Equals(_lastRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            Graphs.Clear(); Statuses.Clear(); Live.Clear();
        }
        _lastRoot = root;
        string key = root + "|" + SgBase.Normalize(shader) + "|" + number;
        DateTime reg = Stamp(ShaderGraphRegistry.RegistryPath(root));
        var cached = Graphs.GetOrAdd(key, _ => new StampedGraph());
        lock (cached)
        {
            // fresh = neither the registry nor the graph file (or, for a failure, the file it expected) changed since it was read: a missing graph is not read again for every material
            string? watched = cached.Value?.FilePath ?? cached.FailFile;
            bool fresh = (cached.Value is not null || cached.Problem is not null) && cached.RegistryStamp == reg && cached.FileStamp == Stamp(watched);
            if (!fresh)
            {
                var r = SgShipValidator.Resolve(root, shader, number);
                cached.RegistryStamp = reg;
                cached.Value = null; cached.Problem = null; cached.FailFile = r.FilePath;
                cached.FileStamp = Stamp(r.FilePath);
                if (r.Document is null || r.FilePath is null || r.Entry is null) cached.Problem = r.Problem ?? "the graph was not found";
                else cached.Value = new Resolved(root, shader, number, r.FilePath, r.Document, r.Entry.File);
            }
            if (cached.Value is null) return (null, cached.Problem ?? "the graph was not found");
            var doc = Live.TryGetValue(Norm(cached.Value.FilePath), out var live) && SgBase.Normalize(live.BaseShader) == SgBase.Normalize(shader) ? live : cached.Value.Doc;
            return (cached.Value with { Doc = doc }, "");
        }
    }

    private static SgBase? BaseOf(ShaderCacheReader cache, ShaderPermutationIndex? perms, string shader, out string why)
    {
        why = "";
        var table = Bases.GetValue(cache, _ => new ConcurrentDictionary<string, SgBase?>(StringComparer.OrdinalIgnoreCase));
        if (table.TryGetValue(shader, out var known)) { if (known is null) why = $"the base shader {shader} cannot be read from the installed game"; return known; }
        var b = ShaderGraphBaseResolver.Resolve(cache, perms, shader, out string err, out _);
        if (b is not null) table[shader] = b;
        else why = err;
        return b;
    }

    private static GraphEntry EntryFor(string hash, ShaderGraphDocument doc, SgBase b)
    {
        lock (EntriesLock)
        {
            if (Entries.TryGetValue(hash, out var e)) return e;
            if (Entries.Count > 48) Entries.Clear();
            e = new GraphEntry { Analysis = new Lazy<SgAnalysis>(() => SgAnalyzer.Analyze(doc, b)) };
            Entries[hash] = e;
            return e;
        }
    }

    /// <summary>Hand over the signatures an editor build already compiled, so the viewport does not compile them again.</summary>
    public static void Prime(ShaderGraphDocument doc, SgBase b, SgBuild build)
    {
        var entry = EntryFor(HashOf(doc, b), doc, b);
        foreach (var c in build.Compiled)
            if (c.Ok) entry.BySignature[c.Signature.Index] = new Lazy<SgCompiled>(c);
    }

    /// <summary>The graph's compiled pixel shader for <paramref name="riotPs"/>'s signature, or null with the reason. Null with an empty reason means the material names no graph.</summary>
    public static SgViewportOverride? TryOverride(string renderShader, IReadOnlyDictionary<string, string> macros, DxbcShader riotPs,
        ShaderCacheReader cache, ShaderPermutationIndex? perms, out string why)
    {
        why = "";
        if (macros is null || !macros.TryGetValue(SgShip.Macro, out string? value)) return null;
        string label = $"{SgShip.Macro}={value}";
        if (!SgBase.IsSupported(renderShader)) { why = $"{label}: '{renderShader}' is not a base shader Shader Graphs support."; return null; }
        if (!SgShipValidator.TryParseNumber(value, out int number)) { why = $"{label} is not a graph number."; return null; }
        string? root = ProjectRoot?.Invoke();
        if (root is null) { why = $"{label}: no project is open, so the graph cannot be found."; return null; }

        var (graph, gwhy) = ResolveGraph(root, renderShader, number);
        if (graph is null) { why = $"{label}: {gwhy}."; return null; }
        string stem = graph.Name;
        string Fail(string text) { Report(graph.FilePath, false, text); return text; }
        if (SgBase.Normalize(graph.Doc.BaseShader) != SgBase.Normalize(renderShader))
        { why = Fail($"Shader Graph '{stem}' is built for {graph.Doc.BaseShader}, not {renderShader}."); return null; }

        var b = BaseOf(cache, perms, renderShader, out string bwhy);
        if (b is null) { why = Fail($"Shader Graph '{stem}': {bwhy}"); return null; }
        var sig = b.SignatureOf(riotPs);
        if (sig is null) { why = Fail($"Shader Graph '{stem}': this permutation's pixel input signature is not one the graph was built for."); return null; }

        string hash = HashOf(graph.Doc, b);
        var entry = EntryFor(hash, graph.Doc, b);
        var analysis = entry.Analysis.Value;
        if (analysis.HasErrors)
        {
            why = Fail(ErrorsText(stem, analysis.Errors.First().Message));
            return null;
        }
        // the compile itself runs outside every lock: two threads asking for the same signature share one Lazy, different signatures compile side by side
        var lazy = entry.BySignature.GetOrAdd(sig.Index, _ => new Lazy<SgCompiled>(() =>
        {
            Interlocked.Increment(ref _compileCount);
            return ShaderGraphCompiler.Compile(graph.Doc, b, sig, analysis);
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        var c = lazy.Value;
        if (!c.Ok || c.Shader is null)
        {
            why = Fail($"Shader Graph '{stem}' does not compile for pixel input signature #{sig.Index} ({c.Errors.FirstOrDefault()?.Message ?? "unknown error"}): Riot's shader is drawn.");
            return null;
        }
        Report(graph.FilePath, true, DrawnText(stem));
        return new SgViewportOverride
        {
            Shader = c.Shader, GraphName = stem, Number = number, Hash = hash,
            CustomParameters = graph.Doc.Params.Select(p => p.Name).ToList(),
            DefaultParameters = graph.Doc.Params.Select(p => (p.Name, Pad4(p.DefaultValues()))).ToList(),
            DefaultTextures = graph.Doc.Textures.Where(t => t.DefaultPath.Length > 0).Select(t => (t.Resource, t.NormalizedPath)).ToList(),
        };
    }

    private static float[] Pad4(float[] v) { var r = new float[4]; for (int i = 0; i < 4 && i < v.Length; i++) r[i] = v[i]; return r; }

    /// <summary>Whether any material of <paramref name="materials"/> names the graph with registry number <paramref name="number"/> on <paramref name="shader"/>.</summary>
    public static bool AnyUses(IEnumerable<MaterialBinding> materials, string shader, int number) =>
        materials.Any(m => m.Macros.TryGetValue(SgShip.Macro, out var v) && SgShipValidator.TryParseNumber(v, out int n) && n == number
                           && SgBase.Normalize(m.RenderShader ?? "") == SgBase.Normalize(shader));
}
