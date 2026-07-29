using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Shaders;

/// <summary>One cooked permutation: the key the engine looks up, and where its bytecode lives.</summary>
public sealed record ShaderPermutation(ulong Key, uint BlobIndex)
{
    /// <summary>Filled in by <see cref="ShaderCacheReader.DescribePermutations"/> when the define set could
    /// be recovered by enumerating the pool. Null means "not recovered", never "no defines".</summary>
    public IReadOnlyList<string>? Defines { get; init; }

    public string DefineSummary => Defines is null
        ? "(define set not recovered)"
        : Defines.Count == 0 ? "(base permutation, no defines)" : string.Join(" ", Defines);
}

/// <summary>A parsed <c>TOC3.0</c> — one shader stage and every permutation Riot cooked for it.</summary>
public sealed class ShaderStageToc
{
    public required string Path { get; init; }
    public required string ShaderName { get; init; }
    public DxbcStage Stage { get; init; }
    /// <summary>The raw (name, value) pool. The same name appears once per distinct value.</summary>
    public required IReadOnlyList<(string Key, string Value)> DefinePool { get; init; }
    public required IReadOnlyList<ShaderPermutation> Permutations { get; init; }
    public uint DeclaredBlobCount { get; init; }
    public uint Flag { get; init; }

    /// <summary>Pool collapsed to name → distinct values, which is the axis list a UI wants.</summary>
    public IReadOnlyList<(string Name, IReadOnlyList<string> Values)> Axes
    {
        get
        {
            var by = new List<(string Name, List<string> Values)>();
            foreach (var (k, v) in DefinePool)
            {
                int i = by.FindIndex(x => x.Name == k);
                if (i < 0) by.Add((k, new List<string> { v }));
                else if (!by[i].Values.Contains(v)) by[i].Values.Add(v);
            }
            return by.Select(x => (x.Name, (IReadOnlyList<string>)x.Values)).ToList();
        }
    }
}

/// <summary>M210: reads compiled shader bytecode out of <c>ShaderCache.dx11.wad.client</c>.
///
/// <para>League never compiles shaders at runtime. It ships a closed set of precooked permutations, each
/// keyed by <c>XXH64(seed 0, concat of ordinal-sorted "NAME=VALUE")</c> over the material's COMPLETE define
/// set — the layout <see cref="ReyEngine.Formats.Materials.ShaderPermutationIndex"/> decoded and verified
/// bit-exact against known triples (the empty set hashes to 0xef46db3751d8e999). This class adds the half
/// that index did not need: the blob-index array, and the containers the bytecode actually lives in.</para>
///
/// <para><b>Storage layout, confirmed against the shipped files.</b> The TOC is at
/// <c>assets/shaders/generated/{shader}.{vs|ps}.dx11</c>; the bytecode is NOT in it. Blobs live in sibling
/// containers named <c>{tocPath}_{N}</c> where N is the blob index rounded down to a multiple of 100, each
/// container holding up to 100 length-prefixed DXBC blobs back to back. Blob 128 is therefore the 29th
/// entry of <c>..._100</c>.</para>
///
/// <para><b>M277: the stage separator is not stable.</b> The 2026-07-29 patch renamed every entry from
/// <c>.vs.dx11</c> to <c>.vs-dx11</c>, containers included. Both spellings are supported — see
/// <see cref="StageSuffixes"/> and <see cref="ResolveCachePath"/> — and every lookup here resolves the
/// name it will actually use rather than assuming the caller's.</para>
///
/// <para><b>The gotcha that costs an afternoon.</b> A container's length prefix runs one byte longer than
/// the DXBC it wraps (1,757 vs the 1,756 the DXBC header declares at offset 24). D3D rejects any bytecode
/// whose buffer length disagrees with its own <c>totalSize</c>, with a bare <c>E_INVALIDARG</c> and no
/// diagnostics. Chunk parsing never notices, because chunk offsets are absolute — so disassembly tools work
/// fine on untrimmed blobs and only shader CREATION fails. <see cref="LoadBlob"/> trims.</para>
/// </summary>
public sealed class ShaderCacheReader : IDisposable
{
    private readonly WadArchive? _wad;
    private readonly Dictionary<string, ShaderStageToc?> _tocCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, byte[]?> _containerCache = new();

    public bool IsAvailable => _wad is not null;
    public string? LoadError { get; }

    /// <summary>Every stage-TOC path in the cache, sorted. Only resolved paths appear — an unresolved hash
    /// cannot be turned back into the <c>_N</c> container names.</summary>
    public IReadOnlyList<string> TocPaths { get; } = Array.Empty<string>();

    public ShaderCacheReader(WadArchive shaderCacheWad)
    {
        _wad = shaderCacheWad;
        TocPaths = _wad.Entries
            .Where(e => e.IsResolved && IsTocPath(e.Path))
            .Select(e => e.Path)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <param name="gameDataFinalDir">…/Game/DATA/FINAL — holds ShaderCache.dx11.wad.client.</param>
    public static ShaderCacheReader? Open(string gameDataFinalDir, IHashResolver? resolver, out string? error)
    {
        error = null;
        string path = Path.Combine(gameDataFinalDir, "ShaderCache.dx11.wad.client");
        if (!File.Exists(path)) { error = $"ShaderCache.dx11.wad.client not found under {gameDataFinalDir}"; return null; }
        try { return new ShaderCacheReader(WadArchive.Open(path, resolver)); }
        catch (Exception ex) { error = $"could not open the shader cache: {ex.Message}"; return null; }
    }

    /// <summary>Distinct shader names (TOC path minus the stage suffix), for a picker.</summary>
    public IReadOnlyList<string> ShaderNames() => TocPaths
        .Select(StripStage)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// <para>M277: every stage suffix the shipped cache has used, because it has now used two.</para>
    ///
    /// <para>Up to the 2026-07-29 patch every entry was <c>{shader}.vs.dx11</c>; that patch renamed all
    /// 2,176 of them to <c>{shader}.vs-dx11</c> (and the blob containers with them, <c>....vs-dx11_0</c>).
    /// Both spellings are kept rather than swapping to the new one: mods and older installs still carry the
    /// dotted form, and the naming has now demonstrably changed once, so treating either as "the" spelling
    /// is a bet this file has already lost.</para>
    /// </summary>
    private static readonly (string Suffix, DxbcStage Stage)[] StageSuffixes =
    {
        (".vs-dx11", DxbcStage.Vertex), (".ps-dx11", DxbcStage.Pixel),
        (".vs.dx11", DxbcStage.Vertex), (".ps.dx11", DxbcStage.Pixel),
    };

    /// <summary>Split a TOC path into shader name and stage. False (and the path unchanged) when it carries
    /// no stage suffix at all, which is how a caller tells "unknown layout" from "pixel shader" — the
    /// distinction the old suffix chain silently lost by falling through to Pixel.</summary>
    public static bool TryStripStage(string tocPath, out string shaderName, out DxbcStage stage)
    {
        foreach (var (suffix, st) in StageSuffixes)
            if (tocPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            { shaderName = tocPath[..^suffix.Length]; stage = st; return true; }
        shaderName = tocPath;
        stage = DxbcStage.Unknown;
        return false;
    }

    public static string StripStage(string tocPath) =>
        TryStripStage(tocPath, out var name, out _) ? name : tocPath;

    /// <summary>Is this WAD path a stage TOC (either spelling)? Blob containers end <c>_N</c> and are not.</summary>
    public static bool IsTocPath(string path) => TryStripStage(path, out _, out _);

    /// <summary>Both spellings of a cache path, the caller's first. Works on blob containers too, because
    /// the token to swap is <c>.dx11</c>/<c>-dx11</c> wherever it sits, not a trailing suffix —
    /// <c>foo.vs.dx11_0</c> has to become <c>foo.vs-dx11_0</c>, and a suffix rule cannot do that.</summary>
    public static IReadOnlyList<string> CachePathCandidates(string path)
    {
        char other = '-';
        int i = path.LastIndexOf(".dx11", StringComparison.OrdinalIgnoreCase);
        if (i < 0) { i = path.LastIndexOf("-dx11", StringComparison.OrdinalIgnoreCase); other = '.'; }
        if (i < 0) return new[] { path };
        return new[] { path, path[..i] + other + path[(i + 1)..] };
    }

    /// <summary>Which spelling of <paramref name="path"/> the cache actually holds, or null when neither
    /// does. <paramref name="exists"/> is the archive's own lookup; passing it in keeps the naming rule
    /// testable against real shipped names without needing a WAD on disk.</summary>
    public static string? ResolveCachePath(string path, Func<string, bool> exists)
    {
        foreach (var candidate in CachePathCandidates(path))
            if (exists(candidate)) return candidate;
        return null;
    }

    /// <summary>The canonical request form. Callers never have to know which spelling shipped — every
    /// lookup below runs it through <see cref="ResolveCachePath"/> first.</summary>
    public static string TocPathFor(string shaderName, DxbcStage stage) =>
        $"{shaderName}.{(stage == DxbcStage.Vertex ? "vs" : "ps")}.dx11";

    private bool WadHas(string path) =>
        _wad is not null && _wad.TryGetEntry(HashAlgorithms.WadPath(path.ToLowerInvariant()), out _);

    /// <summary>
    /// <para>M231: the name of the OTHER stage, for shaders whose two stages are separate cache entries.</para>
    ///
    /// <para>Most of the cache pairs both stages under one name - 371 of 462 - and those need nothing from
    /// this. The <c>assets/shaders/hlsl/</c> families instead ship the stage in the NAME, so
    /// <c>particlesystem/quad_vs</c> has only a <c>.vs.dx11</c> TOC and its partner
    /// <c>particlesystem/quad_ps</c> only a <c>.ps.dx11</c>. Asking for the missing stage of either returns
    /// null, which read as "this shader cannot be previewed".</para>
    ///
    /// <para>The rule is the LAST <c>_vs</c>/<c>_ps</c> token, not a suffix: it has to also turn
    /// <c>quad_vs_fixedalphauv</c> into <c>quad_ps_fixedalphauv</c>. Verified over the whole cache - every
    /// vertex-only name under particlesystem/ and skinnedmesh/ pairs this way. Returns null when the name
    /// carries no such token (<c>renderer/vs_screenvertex</c>, <c>ui/animation</c>) rather than guessing.</para>
    /// </summary>
    public static string? PartnerStageName(string shaderName, DxbcStage want)
    {
        string from = want == DxbcStage.Vertex ? "_ps" : "_vs";
        string to = want == DxbcStage.Vertex ? "_vs" : "_ps";
        int i = shaderName.LastIndexOf(from, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        return shaderName[..i] + to + shaderName[(i + from.Length)..];
    }

    /// <summary>The TOC for <paramref name="stage"/> of <paramref name="shaderName"/>, falling back to the
    /// partner name when this one does not ship that stage. <paramref name="resolvedName"/> reports which
    /// name actually supplied it, so the UI can say so instead of quietly previewing a different shader.</summary>
    public ShaderStageToc? ReadTocOrPartner(string shaderName, DxbcStage stage, out string resolvedName)
    {
        resolvedName = shaderName;
        var toc = ReadToc(TocPathFor(shaderName, stage));
        if (toc is not null) return toc;

        if (PartnerStageName(shaderName, stage) is not { } partner) return null;
        toc = ReadToc(TocPathFor(partner, stage));
        if (toc is not null) resolvedName = partner;
        return toc;
    }

    /// <summary>Parse a stage TOC. Returns null when the cache has no such entry — which for a shader that
    /// exists in the other stage is normal and not an error.</summary>
    public ShaderStageToc? ReadToc(string tocPath)
    {
        if (_wad is null) return null;
        if (_tocCache.TryGetValue(tocPath, out var cached)) return cached;

        ShaderStageToc? toc = null;
        try
        {
            // M277: ask for whichever spelling this install actually ships (see StageSuffixes). A reader
            // that knows only one form does not degrade gracefully - it misses EVERY shader at once, which
            // is how the rename surfaced: "0 material(s), 21 unresolved" and a status line stuck on
            // "preparing scene...", with nothing saying why, because a lookup that finds nothing looks
            // exactly like an asset that was never there.
            if (ResolveCachePath(tocPath, WadHas) is { } actual)
            {
                var bytes = _wad.Extract(HashAlgorithms.WadPath(actual.ToLowerInvariant()));
                // Stamp the path that RESOLVED, not the one that was asked for: ShaderStageToc.Path is
                // what a UI shows, and claiming a spelling the cache does not hold sends the next reader
                // looking for a file that is not there.
                if (bytes is { Length: > 0 }) toc = ParseToc(bytes, actual);
            }
        }
        catch { toc = null; }
        _tocCache[tocPath] = toc;
        return toc;
    }

    /// <summary>TOC3.0: sizedString "TOC3.0" | u32 permCount | u32 defineCount | u32 blobCount | u32 flag |
    /// sizedString "baseDefines" | defineCount x (sizedString, sizedString) | sizedString "shaders" |
    /// permCount x u64 key | permCount x u32 blobIndex. 833/833 shipped TOCs consume byte-exactly.</summary>
    public static ShaderStageToc? ParseToc(byte[] b, string tocPath)
    {
        int p = 0;
        string ReadStr()
        {
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4)); p += 4;
            var s = Encoding.UTF8.GetString(b, p, (int)n); p += (int)n;
            return s;
        }
        uint ReadU32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4)); p += 4; return v; }

        try
        {
            if (ReadStr() != "TOC3.0") return null;
            uint permCount = ReadU32(), defineCount = ReadU32(), blobCount = ReadU32(), flag = ReadU32();
            if (ReadStr() != "baseDefines") return null;

            var pool = new List<(string, string)>((int)defineCount);
            for (int i = 0; i < defineCount; i++) pool.Add((ReadStr(), ReadStr()));
            if (ReadStr() != "shaders") return null;

            var keys = new ulong[permCount];
            for (int i = 0; i < permCount; i++)
            { keys[i] = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p, 8)); p += 8; }
            var blobs = new uint[permCount];
            for (int i = 0; i < permCount; i++)
            { blobs[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4)); p += 4; }

            var perms = new List<ShaderPermutation>((int)permCount);
            for (int i = 0; i < permCount; i++) perms.Add(new ShaderPermutation(keys[i], blobs[i]));

            // M277: read the stage off whichever suffix this path carries. The old test was a single
            // ".vs.dx11" EndsWith with Pixel as the else, so a hyphenated VERTEX toc reported itself as a
            // pixel shader - a silently wrong answer rather than a missing one.
            TryStripStage(tocPath, out string name, out var stage);

            return new ShaderStageToc
            {
                Path = tocPath,
                ShaderName = name,
                Stage = stage,
                DefinePool = pool,
                Permutations = perms,
                DeclaredBlobCount = blobCount,
                Flag = flag,
            };
        }
        catch { return null; }
    }

    /// <summary>The engine's permutation key. Ordinal sort, then concatenate <c>NAME=VALUE</c> with no
    /// separator, then XXH64 seed 0.</summary>
    public static ulong PermutationKey(IEnumerable<string> nameEqualsValue)
    {
        var parts = nameEqualsValue.ToList();
        parts.Sort(StringComparer.Ordinal);
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(string.Concat(parts)), 0);
    }

    /// <summary>Recover which defines each cooked permutation corresponds to, by enumerating the pool and
    /// hashing. The TOC stores only key hashes, so this is the only way back to readable names.
    ///
    /// <para>The pool is a product space and can be large, so this is bounded. <paramref name="truncated"/>
    /// reports whether the cap was hit — a caller must not present a partial map as complete.</para></summary>
    public static IReadOnlyList<ShaderPermutation> DescribePermutations(
        ShaderStageToc toc, out bool truncated, long maxCombinations = 1_000_000)
    {
        truncated = false;
        var axes = toc.Axes;

        long space = 1;
        foreach (var (_, values) in axes)
        {
            // each axis may also be absent entirely, which is a distinct permutation input
            space *= values.Count + 1;
            if (space > maxCombinations) { truncated = true; break; }
        }
        if (truncated) return toc.Permutations;

        var byKey = new Dictionary<ulong, List<string>>();
        var parts = new List<string>();
        for (long c = 0; c < space; c++)
        {
            long rem = c;
            parts.Clear();
            foreach (var (name, values) in axes)
            {
                int sel = (int)(rem % (values.Count + 1));
                rem /= values.Count + 1;
                if (sel > 0) parts.Add(name + "=" + values[sel - 1]);
            }
            ulong key = PermutationKey(parts);
            if (!byKey.ContainsKey(key)) byKey[key] = new List<string>(parts);
        }

        return toc.Permutations
            .Select(p => byKey.TryGetValue(p.Key, out var d) ? p with { Defines = d } : p)
            .ToList();
    }

    /// <summary>Pin a boolean axis, honouring how League actually encodes "off".
    ///
    /// <para>Almost every axis in a shipped TOC is presence/absence — the pool carries the value <c>1</c> and
    /// nothing else — so a switch that is OFF means the define is simply <b>absent</b>, not <c>NAME=0</c>.
    /// Emitting <c>=0</c> against such an axis produces a key that exists nowhere and reports a perfectly
    /// ordinary material as unresolvable. Where <c>0</c> IS a cooked value (a handful of axes ship both
    /// polarities) the explicit form is used instead.</para>
    ///
    /// <para>Found by a test, not by inspection: the shipped corpus happens not to exercise the broken
    /// branch, so the census reported 100% resolution either way.</para></summary>
    private static bool TryPinBool(string name, bool on, IReadOnlyList<string> values, string source,
        List<string> fixedParts, List<string> pinned, out string explanation)
    {
        explanation = "";
        string sv = on ? "1" : "0";
        if (values.Contains(sv))
        {
            fixedParts.Add(name + "=" + sv);
            pinned.Add($"{name}={sv} ({source})");
            return true;
        }
        if (!on)
        {
            // off, and "0" was never cooked -> the define is absent. Contributes nothing to the key.
            pinned.Add($"{name} absent ({source} = off)");
            return true;
        }
        explanation = $"the {source} {name}=1 was never cooked (cooked values: {string.Join("/", values)})";
        return false;
    }

    /// <summary>M213: which cooked permutation would the engine pick for THIS material?
    ///
    /// <para>This is the difference between previewing a shader and previewing a material. A material
    /// authors only the switches it changes; the rest of the define set comes from the shader's own
    /// featureDefines and staticSwitch defaults, and some macros are injected per-mesh by the engine and
    /// appear in neither. So the resolution fixes every axis it can pin, leaves the rest free, and
    /// enumerates the free ones until a combination hashes to a key the TOC actually contains.</para>
    ///
    /// <para>Returns null when nothing matched, which is a real and meaningful answer: it is exactly the
    /// condition that makes the live client fail with "Unable to find correct hash for shader" and render
    /// nothing. <paramref name="explanation"/> always describes what was tried.</para></summary>
    public static ShaderPermutation? ResolvePermutation(
        ShaderStageToc toc,
        IReadOnlyDictionary<string, string>? macros,
        IReadOnlyDictionary<string, bool>? switches,
        IReadOnlyDictionary<string, string>? featureDefines,
        IReadOnlyDictionary<string, bool>? switchDefaults,
        out string explanation,
        long maxCombinations = 2_000_000,
        IReadOnlySet<string>? forcedAbsent = null)
    {
        var byKey = new Dictionary<ulong, ShaderPermutation>();
        foreach (var perm in toc.Permutations) byKey.TryAdd(perm.Key, perm);

        var fixedParts = new List<string>();
        var freeAxes = new List<(string Name, List<string?> Options)>();
        var pinned = new List<string>();

        foreach (var (name, values) in toc.Axes)
        {
            // M225: pinned absent. Without this the free-axis enumeration below would happily add the very
            // define we are trying to remove back in, find the original permutation, and report success -
            // the same vacuous test M166 had to guard against.
            if (forcedAbsent is not null && forcedAbsent.Contains(name))
            {
                pinned.Add($"{name} forced absent");
                continue;
            }

            if (macros is not null && macros.TryGetValue(name, out var mv))
            {
                if (!values.Contains(mv))
                {
                    explanation = $"the material sets {name}={mv}, which was never cooked for this stage "
                                  + $"(cooked values: {string.Join("/", values)})";
                    return null;
                }
                fixedParts.Add(name + "=" + mv);
                pinned.Add($"{name}={mv} (material macro)");
            }
            else if (switches is not null && switches.TryGetValue(name, out bool on))
            {
                if (!TryPinBool(name, on, values, "material switch", fixedParts, pinned, out explanation)) return null;
            }
            else if (featureDefines is not null && featureDefines.TryGetValue(name, out var fv))
            {
                if (!values.Contains(fv)) { explanation = $"the shader's featureDefine {name}={fv} was never cooked"; return null; }
                fixedParts.Add(name + "=" + fv);
                pinned.Add($"{name}={fv} (shader featureDefine)");
            }
            else if (switchDefaults is not null && switchDefaults.TryGetValue(name, out bool dv))
            {
                if (!TryPinBool(name, dv, values, "shader default", fixedParts, pinned, out explanation)) return null;
            }
            else
            {
                // Unset. The engine injects some of these per-mesh (NO_BAKED_LIGHTING and friends), so
                // "absent" and each cooked value are all candidates - the same both-ways treatment M166
                // needed to stop reporting shipping content as broken.
                var opts = new List<string?> { null };
                opts.AddRange(values);
                freeAxes.Add((name, opts));
            }
        }

        long space = 1;
        foreach (var a in freeAxes) space *= a.Options.Count;
        if (space > maxCombinations)
        {
            explanation = $"{freeAxes.Count} unconstrained axes is too large a space to search ({space:n0})";
            return null;
        }

        var parts = new List<string>();
        for (long c = 0; c < space; c++)
        {
            long rem = c;
            parts.Clear();
            parts.AddRange(fixedParts);
            var chosen = new List<string>();
            foreach (var (name, opts) in freeAxes)
            {
                int sel = (int)(rem % opts.Count);
                rem /= opts.Count;
                if (opts[sel] is { } val) { parts.Add(name + "=" + val); chosen.Add(name + "=" + val); }
            }
            ulong key = PermutationKey(parts);
            if (byKey.TryGetValue(key, out var hit))
            {
                explanation = pinned.Count == 0 && chosen.Count == 0
                    ? "matched the base permutation (no defines)"
                    : "pinned: " + (pinned.Count == 0 ? "(nothing)" : string.Join(", ", pinned))
                      + (chosen.Count > 0 ? "  ·  inferred: " + string.Join(", ", chosen) : "");
                return hit;
            }
        }

        explanation = $"no cooked permutation matched. Pinned {fixedParts.Count} axes "
                      + $"({string.Join(", ", pinned)}) and searched {space:n0} combinations of "
                      + $"{freeAxes.Count} free ones. In the live client this is the "
                      + "\"Unable to find correct hash for shader\" failure.";
        return null;
    }

    /// <summary>Fetch one blob's DXBC, trimmed to the size its own header declares.</summary>
    /// <param name="error">Set when the blob could not be produced; the value is then null.</param>
    /// <param name="wasTrimmed">True when the container's length prefix over-reported, which is the norm.</param>
    public byte[]? LoadBlob(string tocPath, uint blobIndex, out string? error, out bool wasTrimmed)
    {
        error = null;
        wasTrimmed = false;
        if (_wad is null) { error = "no shader cache open"; return null; }

        uint containerBase = blobIndex / 100 * 100;
        int within = (int)(blobIndex % 100);
        string wanted = $"{tocPath}_{containerBase}";

        // M277: THE step the 2026-07-29 rename actually broke, and the one that hid behind the TOC. The
        // container name is derived from the TOC path, so a reader that finds the TOC by trying both
        // spellings still asks for the container by the spelling the CALLER used - measured on Map12/bloom:
        // 1,389 of 1,389 TOCs resolved and 1,389 of 1,389 permutations resolved, then 0 of 1,389 blobs
        // loaded, on "blob container not in the cache: ...DefaultEnv_Flat.vs.dx11_0". Resolve the container
        // independently instead of assuming it matches the request.
        string? containerPath = ResolveCachePath(wanted, WadHas);
        if (containerPath is null)
        {
            // Name every spelling that was tried. "not in the cache" without the paths is what made this
            // look like a scene bug rather than a naming one.
            error = "blob container not in the cache, tried: "
                    + string.Join(" and ", CachePathCandidates(wanted));
            return null;
        }
        ulong hash = HashAlgorithms.WadPath(containerPath.ToLowerInvariant());

        if (!_containerCache.TryGetValue(hash, out var cont))
        {
            try { cont = _wad.Extract(hash); }
            catch (Exception ex) { cont = null; error = $"container {containerPath}: {ex.Message}"; }
            _containerCache[hash] = cont;
        }
        if (cont is null || cont.Length == 0)
        {
            error ??= $"blob container is empty: {containerPath}";
            return null;
        }

        int off = 0;
        for (int i = 0; i < within; i++)
        {
            if (off + 4 > cont.Length) { error = $"{containerPath}: ran off the end before blob {within}"; return null; }
            int sz = BinaryPrimitives.ReadInt32LittleEndian(cont.AsSpan(off));
            if (sz < 0) { error = $"{containerPath}: negative blob length at entry {i}"; return null; }
            off += 4 + sz;
        }
        if (off + 4 > cont.Length) { error = $"{containerPath}: blob {within} is past the end"; return null; }

        int size = BinaryPrimitives.ReadInt32LittleEndian(cont.AsSpan(off));
        if (size <= 0 || off + 4 + size > cont.Length)
        { error = $"{containerPath}: blob {within} declares {size} bytes, container has {cont.Length - off - 4}"; return null; }

        var blob = new byte[size];
        Array.Copy(cont, off + 4, blob, 0, size);

        // THE trim. Without it CreatePixelShader returns E_INVALIDARG and says nothing further.
        if (DxbcReflection.LooksLikeDxbc(blob))
        {
            int declared = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(24));
            if (declared > 0 && declared < blob.Length)
            {
                Array.Resize(ref blob, declared);
                wasTrimmed = true;
            }
        }
        else
        {
            error = $"{containerPath} blob {within}: not a DXBC container";
            return null;
        }

        return blob;
    }

    /// <summary>Load and reflect one permutation in one step.</summary>
    public DxbcShader? LoadShader(string tocPath, uint blobIndex, out string? error)
    {
        var blob = LoadBlob(tocPath, blobIndex, out error, out bool trimmed);
        if (blob is null) return null;
        try { return DxbcReflection.Parse(blob, trimmed); }
        catch (Exception ex) { error = $"reflection failed: {ex.Message}"; return null; }
    }

    public void Dispose() => _wad?.Dispose();
}
