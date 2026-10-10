using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Materials.ShaderGraph;

/// <summary>
/// M833: what shipping a Shader Graph to the game needs, proven in game on 2026-10-11 (the probe's checkerboard on the blue base).
///
/// <para>A material gets <c>shaderMacros { REY_GRAPH: "&lt;n&gt;" }</c>. The base shader's pixel TOC (read from the CURRENTLY INSTALLED game)
/// gains the pool pair (REY_GRAPH, n) and a twin of EVERY unique Riot key - XXH64 of the define set with REY_GRAPH=n added. A colour twin
/// points at the graph's compiled pixel shader for that key's input signature; a shadow-pass twin (GENERATE_SHADOW_MAP=1) points at Riot's
/// own blob. The new blobs are appended to the LAST container (length = DXBC + 1, the DXBC, a trailing 0x35). The vertex shader is untouched.</para>
///
/// <para>Hard rule from the test1 crash: a material parameter name, or a custom pixel shader's $Globals variable, that the shader does not
/// declare in shaders.bin crashes the game ("Missing shader constant"). <see cref="SgShipValidator"/> blocks the export on it.</para>
/// </summary>
public static class SgShip
{
    public const string Macro = "REY_GRAPH";
    public const byte RecordTrailer = 0x35;
    public const string GeneratedFolder = "ShaderCache.dx11";
    public const string ShadowDefine = "GENERATE_SHADOW_MAP=1";

    public static bool IsGraphMacro(string name) => name.Equals(Macro, StringComparison.Ordinal);

    public static string Sha1Hex(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
}

/// <summary>The compiled pixel shaders of one graph, in the order of <see cref="SgBase.Signatures"/> (verified against Riot by the compiler).</summary>
public sealed record SgGraphBlobs(int Number, string GraphName, IReadOnlyList<byte[]> BlobsBySignature);

/// <summary>The bytes a pack writes for a base shader: the twinned TOC and the last container with the graph blobs appended.</summary>
public sealed class SgShipPatch
{
    public required string TocPath { get; init; }
    public required string ContainerPath { get; init; }
    public required byte[] Toc { get; init; }
    public required byte[] Container { get; init; }
    public required string BaseTocSha1 { get; init; }
    public required string BaseContainerSha1 { get; init; }
    public required uint FirstNewBlob { get; init; }
    public required int NewBlobs { get; init; }
    public required int TwinKeys { get; init; }
    public required int ColourTwins { get; init; }
    public required int ShadowTwins { get; init; }
}

/// <summary>Builds the shipped TOC and container. Pure bytes: no device, no file, no UI.</summary>
public static class SgShipBuilder
{
    public static SgShipPatch Build(SgBase b, ShaderStageToc toc, byte[] tocBytes, byte[] container, string containerPath, IReadOnlyList<SgGraphBlobs> graphs)
    {
        if (graphs.Count == 0) throw new ArgumentException("no graph to ship", nameof(graphs));
        var numbers = new HashSet<int>();
        foreach (var g in graphs)
        {
            if (g.Number <= 0) throw new InvalidDataException($"graph '{g.GraphName}' has the number {g.Number}; a graph number is 1 or more");
            if (!numbers.Add(g.Number)) throw new InvalidDataException($"two graphs have the number {g.Number}");
            if (g.BlobsBySignature.Count != b.Signatures.Count)
                throw new InvalidDataException($"graph '{g.GraphName}' compiled {g.BlobsBySignature.Count} pixel shader(s) for {b.Signatures.Count} input signature(s)");
            foreach (var blob in g.BlobsBySignature)
                if (!DxbcReflection.LooksLikeDxbc(blob)) throw new InvalidDataException($"graph '{g.GraphName}' has a blob that is not DXBC");
        }
        if (toc.DefinePool.Any(p => p.Key == SgShip.Macro))
            throw new InvalidDataException($"the installed {toc.Path} already has a {SgShip.Macro} axis: it is not Riot's table, so nothing is added to it");

        // the starting point must be Riot's table exactly: the writer re-emits it byte for byte, or nothing is patched
        if (!ShaderCachePatchWriter.WriteToc(toc, toc.Permutations, toc.DeclaredBlobCount).AsSpan().SequenceEqual(tocBytes))
            throw new InvalidDataException($"{toc.Path} is not re-written byte for byte by the TOC writer, so it is not patched");

        // ---- the unique Riot keys, in first-seen order, with their define sets
        var described = ShaderCacheReader.DescribePermutations(toc, out bool truncated);
        if (truncated) throw new InvalidDataException($"the define pool of {toc.Path} is too large to recover the define sets of its permutations");
        var unique = new List<ShaderPermutation>();
        var seen = new Dictionary<ulong, uint>();
        foreach (var p in described)
        {
            if (p.Defines is null) throw new InvalidDataException($"the define set of key 0x{p.Key:x16} of {toc.Path} cannot be recovered from its pool");
            if (seen.TryGetValue(p.Key, out uint known)) { if (known != p.BlobIndex) throw new InvalidDataException($"key 0x{p.Key:x16} of {toc.Path} names two blobs"); continue; }
            seen[p.Key] = p.BlobIndex;
            unique.Add(p);
        }

        // ---- colour key -> input signature
        var sigOf = new Dictionary<ulong, int>();
        foreach (var s in b.Signatures)
            foreach (var p in s.Permutations) sigOf[p.Key] = s.Index;

        // ---- room in the last container
        uint count = toc.DeclaredBlobCount;
        if (count == 0) throw new InvalidDataException("the TOC declares no blobs");
        uint containerBase = (count - 1) / 100 * 100;
        long total = (long)graphs.Count * b.Signatures.Count;
        if (count + total - 1 >= containerBase + 100)
            throw new InvalidDataException($"{total} new blob(s) do not fit the last container ({containerPath}: {count - containerBase} of 100 records used)");
        var records = ReadRecords(container, containerPath);
        if (records != count - containerBase)
            throw new InvalidDataException($"{containerPath} holds {records} record(s); the TOC expects {count - containerBase}");

        // ---- twins: graph-major, Riot's key order inside a graph
        var ordered = graphs.OrderBy(g => g.Number).ToList();
        var pool = toc.DefinePool.ToList();
        var permutations = toc.Permutations.ToList();
        var taken = new HashSet<ulong>(seen.Keys);
        int colour = 0, shadow = 0;
        var newBlobs = new List<byte[]>();
        for (int gi = 0; gi < ordered.Count; gi++)
        {
            var g = ordered[gi];
            pool.Add((SgShip.Macro, g.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            uint first = count + (uint)(gi * b.Signatures.Count);
            newBlobs.AddRange(g.BlobsBySignature);
            foreach (var p in unique)
            {
                var defines = new List<string>(p.Defines!) { SgShip.Macro + "=" + g.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                ulong key = ShaderCacheReader.PermutationKey(defines);
                if (!taken.Add(key)) throw new InvalidDataException($"the twin key 0x{key:x16} of graph {g.Number} collides with another key of {toc.Path}");
                uint blob;
                if (p.Defines!.Contains(SgShip.ShadowDefine)) { blob = p.BlobIndex; shadow++; }
                else
                {
                    if (!sigOf.TryGetValue(p.Key, out int si)) throw new InvalidDataException($"colour key 0x{p.Key:x16} ({p.DefineSummary}) is in no input signature of {b.Shader}");
                    blob = first + (uint)si; colour++;
                }
                permutations.Add(new ShaderPermutation(key, blob));
            }
        }

        var patched = new ShaderStageToc
        {
            Path = toc.Path, ShaderName = toc.ShaderName, Stage = toc.Stage, DefinePool = pool, Permutations = permutations,
            DeclaredBlobCount = count + (uint)total, Flag = toc.Flag,
        };
        byte[] newToc = ShaderCachePatchWriter.WriteToc(patched, permutations, count + (uint)total);
        var back = ShaderCacheReader.ParseToc(newToc, toc.Path) ?? throw new InvalidDataException("the patched TOC does not parse back");
        if (back.Permutations.Count != permutations.Count || back.DeclaredBlobCount != count + total || back.DefinePool.Count != pool.Count)
            throw new InvalidDataException("the patched TOC does not read back as written");

        byte[] appended = ShaderCachePatchWriter.WriteContainer(newBlobs, SgShip.RecordTrailer);
        byte[] newContainer = new byte[container.Length + appended.Length];
        container.CopyTo(newContainer, 0);
        appended.CopyTo(newContainer, container.Length);

        return new SgShipPatch
        {
            TocPath = toc.Path, ContainerPath = containerPath, Toc = newToc, Container = newContainer,
            BaseTocSha1 = SgShip.Sha1Hex(tocBytes), BaseContainerSha1 = SgShip.Sha1Hex(container),
            FirstNewBlob = count, NewBlobs = newBlobs.Count, TwinKeys = colour + shadow, ColourTwins = colour, ShadowTwins = shadow,
        };
    }

    /// <summary>The number of length-prefixed records in a container; throws when the walk does not end exactly at its end.</summary>
    public static int ReadRecords(byte[] container, string name)
    {
        int off = 0, n = 0;
        while (off < container.Length)
        {
            if (off + 4 > container.Length) throw new InvalidDataException($"{name}: a record header runs past the end");
            int len = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(off));
            if (len <= 0 || (long)off + 4 + len > container.Length) throw new InvalidDataException($"{name}: record {n} declares {len} bytes");
            off += 4 + len; n++;
        }
        return n;
    }
}

// =============================================================================================== numbering

/// <summary>One graph of the project with the number its materials name. <see cref="File"/> is the graph's file stem under
/// <c>.reyengine/shadergraphs/</c>.</summary>
public sealed class SgNumberEntry
{
    public string File { get; set; } = "";
    public string Base { get; set; } = "";
    public int Number { get; set; }
}

/// <summary>
/// M833: the numbers of the project's Shader Graphs. A material names a graph by <c>REY_GRAPH=&lt;n&gt;</c>, so n must be stable and unique per
/// base shader in the project. They are kept in <c>.reyengine/shadergraphs/numbers.json</c> beside the graphs (editor data, skipped by every
/// pack) rather than in the graph document: the Shader Graph editor rewrites its whole document on save, and a number written under it would be
/// lost. A number is never reused - an entry stays when its graph file is deleted, so a material that still names it reads as "graph missing"
/// instead of silently picking up another graph.
/// </summary>
public static class ShaderGraphRegistry
{
    public const string FileName = "numbers.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private sealed class Store
    {
        public int Version { get; set; } = 1;
        public List<SgNumberEntry> Entries { get; set; } = new();
        /// <summary>Per normalised base: the highest number ever handed out, so a number is never reused even when its entry moves or goes.</summary>
        public Dictionary<string, int> HighWater { get; set; } = new();
    }

    private static Store LoadStore(string projectRoot)
    {
        string path = RegistryPath(projectRoot);
        if (!File.Exists(path)) return new Store();
        try
        {
            var s = JsonSerializer.Deserialize<Store>(File.ReadAllText(path), Options) ?? new Store();
            s.Entries = s.Entries?.Where(e => e is not null && e.Number > 0 && !string.IsNullOrEmpty(e.File)).ToList() ?? new List<SgNumberEntry>();
            s.HighWater = s.HighWater is null ? new Dictionary<string, int>() : new Dictionary<string, int>(s.HighWater, StringComparer.Ordinal);
            return s;
        }
        catch (Exception ex) { throw new InvalidDataException($"{path} cannot be read: {ex.Message}"); }
    }

    public static string FolderIn(string projectRoot) => Path.Combine(projectRoot, ".reyengine", ShaderGraphDocument.FolderName);
    public static string RegistryPath(string projectRoot) => Path.Combine(FolderIn(projectRoot), FileName);
    public static string GraphPath(string projectRoot, string stem) => Path.Combine(FolderIn(projectRoot), stem + ShaderGraphDocument.FileSuffix);
    public static string StemOf(string graphPath) => Path.GetFileName(graphPath)[..^ShaderGraphDocument.FileSuffix.Length];

    public static IReadOnlyList<SgNumberEntry> Load(string projectRoot) => LoadStore(projectRoot).Entries;

    public static SgNumberEntry? Find(string projectRoot, string baseShader, int number) =>
        Load(projectRoot).FirstOrDefault(e => e.Number == number && SgBase.Normalize(e.Base) == SgBase.Normalize(baseShader));

    public static SgNumberEntry? FindByFile(string projectRoot, string stem) =>
        Load(projectRoot).FirstOrDefault(e => e.File.Equals(stem, StringComparison.OrdinalIgnoreCase));

    /// <summary>The number of the graph file <paramref name="stem"/> on <paramref name="baseShader"/>; allocated (max of that base + 1) and written when it has none.
    /// A graph moved to another base gets a new number there.</summary>
    public static int NumberFor(string projectRoot, string stem, string baseShader)
    {
        var store = LoadStore(projectRoot);
        string nb = SgBase.Normalize(baseShader);
        var mine = store.Entries.FirstOrDefault(e => e.File.Equals(stem, StringComparison.OrdinalIgnoreCase));
        if (mine is not null && SgBase.Normalize(mine.Base) == nb) return mine.Number;
        int high = Math.Max(store.HighWater.GetValueOrDefault(nb), store.Entries.Where(e => SgBase.Normalize(e.Base) == nb).Select(e => e.Number).DefaultIfEmpty(0).Max());
        int next = high + 1;
        store.HighWater[nb] = next;
        // an entry that moves base releases its old number to nobody: that base's high-water mark keeps it
        foreach (var e in store.Entries)
        {
            string eb = SgBase.Normalize(e.Base);
            store.HighWater[eb] = Math.Max(store.HighWater.GetValueOrDefault(eb), e.Number);
        }
        if (mine is not null) { mine.Base = baseShader; mine.Number = next; }
        else store.Entries.Add(new SgNumberEntry { File = stem, Base = baseShader, Number = next });
        Save(projectRoot, store);
        return next;
    }

    private static void Save(string projectRoot, Store store)
    {
        Directory.CreateDirectory(FolderIn(projectRoot));
        string path = RegistryPath(projectRoot), tmp = path + ".tmp";
        store.Entries = store.Entries.OrderBy(e => e.Base, StringComparer.Ordinal).ThenBy(e => e.Number).ToList();
        File.WriteAllText(tmp, JsonSerializer.Serialize(store, Options) + "\n");
        File.Move(tmp, path, overwrite: true);
    }
}

// =============================================================================================== validation

/// <summary>An assigned material, as read from a bin at export time.</summary>
public sealed record SgAssignedMaterial(string BinPath, string Material, string? Shader, string GraphMacroValue, IReadOnlyList<string> ParameterNames,
    IReadOnlyList<string>? SamplerNames = null)
{
    public string Label => $"{Material} ({BinPath})";
}

/// <summary>A graph resolved for a material: its registry entry, file and document (any of them may be missing).</summary>
public sealed record SgResolvedGraph(SgNumberEntry? Entry, string? FilePath, ShaderGraphDocument? Document, string? Problem);

/// <summary>
/// M833: the export-time rules, as readable messages naming the material and the graph. Every message BLOCKS the export. The compile-time
/// rules (a graph with errors, a signature mismatch, an undeclared $Globals variable in a compiled shader) are
/// <see cref="ShaderGraphCompiler"/>'s and are added by the caller from the same build.
/// </summary>
public static class SgShipValidator
{
    public static SgResolvedGraph Resolve(string projectRoot, string shader, int number)
    {
        if (!File.Exists(ShaderGraphRegistry.RegistryPath(projectRoot)))
            return new SgResolvedGraph(null, null, null, "the graph registry is missing (.reyengine/shadergraphs/numbers.json): graph numbers cannot be told from new ones, so nothing is guessed");
        SgNumberEntry? entry;
        try { entry = ShaderGraphRegistry.Find(projectRoot, shader, number); }
        catch (Exception ex) { return new SgResolvedGraph(null, null, null, ex.Message); }
        if (entry is null)
        {
            SgNumberEntry? other = null;
            try { other = ShaderGraphRegistry.Load(projectRoot).FirstOrDefault(e => e.Number == number); } catch { }
            return new SgResolvedGraph(null, null, null, other is not null
                ? $"the Shader Graph numbered {number} belongs to {other.Base}, not to {(shader.Length == 0 ? "this material's shader" : shader)}"
                : $"no Shader Graph of this project has the number {number} on {shader} (numbers.json has no such entry)");
        }
        string path = ShaderGraphRegistry.GraphPath(projectRoot, entry.File);
        if (!File.Exists(path)) return new SgResolvedGraph(entry, path, null, $"the graph file {entry.File}{ShaderGraphDocument.FileSuffix} is missing from .reyengine/shadergraphs");
        var doc = ShaderGraphJson.TryDeserialize(File.ReadAllText(path), out string? err);
        if (doc is null) return new SgResolvedGraph(entry, path, null, $"the graph file {entry.File}{ShaderGraphDocument.FileSuffix} cannot be read: {err}");
        return new SgResolvedGraph(entry, path, doc, null);
    }

    public static bool TryParseNumber(string value, out int number) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number) && number > 0;

    /// <summary>Problems of one assigned material against its resolved graph and the base shader. <paramref name="b"/> is null when the base could not be read.</summary>
    public static IReadOnlyList<string> Validate(SgAssignedMaterial m, SgResolvedGraph graph, SgBase? b)
    {
        var bad = new List<string>();
        string who = $"Material '{m.Material}' ({m.BinPath})";
        if (!TryParseNumber(m.GraphMacroValue, out int n))
        { bad.Add($"{who}: {SgShip.Macro}='{m.GraphMacroValue}' is not a graph number (1 or more)."); return bad; }
        string gname = graph.Entry is null ? $"#{n}" : $"'{graph.Entry.File}' (#{n})";
        if (graph.Document is null)
        { bad.Add($"{who}: Shader Graph {gname} - {graph.Problem ?? "not found"}."); return bad; }
        string shader = m.Shader ?? "";
        if (SgBase.Normalize(graph.Document.BaseShader) != SgBase.Normalize(shader))
        {
            bad.Add($"{who}: Shader Graph {gname} is built for {graph.Document.BaseShader}, but the material uses {(shader.Length == 0 ? "no shader" : shader)}.");
            return bad;
        }
        if (b is null) { bad.Add($"{who}: Shader Graph {gname} - the base shader {graph.Document.BaseShader} could not be read from the installed game."); return bad; }
        foreach (string p in m.ParameterNames)
            if (b.Parameter(p) is null)
                bad.Add($"{who}: the parameter '{p}' is not declared by {b.Shader} in shaders.bin. The game stops with \"Missing shader constant\" on a material with a Shader Graph and a parameter its shader does not declare. Remove the parameter from the material.");
        foreach (string t in m.SamplerNames ?? Array.Empty<string>())
            if (b.Texture(t) is null)
                bad.Add($"{who}: the sampler '{t}' is not a texture {b.Shader} declares (unverified in game; blocked to be safe). Remove it from the material or unassign the graph.");
        var analysis = SgAnalyzer.Analyze(graph.Document, b);
        foreach (var d in analysis.Diagnostics.Where(d => d.IsError))
            bad.Add($"Shader Graph {gname} (used by '{m.Material}'): {d.Message}" + (d.NodeId.Length > 0 ? $" [node {d.NodeId}]" : ""));
        return bad;
    }
}
