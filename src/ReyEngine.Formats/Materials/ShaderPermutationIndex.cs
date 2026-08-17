using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Materials;

/// <summary>M166: answers "will the game actually be able to load a shader for this material?"
///
/// League does NOT compile shaders at runtime. It ships a closed set of precompiled permutations in
/// <c>ShaderCache.dx11.wad.client</c>, each keyed by a hash of the material's COMPLETE define set. Change
/// the define set to a combination Riot never cooked and the client fails with
/// <c>"Unable to find correct hash for shader '...' in wad"</c> and renders nothing.
///
/// That is exactly what happened when the light baker cleared NO_BAKED_LIGHTING from every material of
/// Map11/base_srx: for 20 of its 184 materials the resulting define set was never cooked.
///
/// The important consequence — verified, and the opposite of the obvious guess — is that this is NOT a
/// property of the shader. <c>SRX_DynamicEffect</c> and <c>VertexDeform</c> both SHIP lightmapped in
/// Map12, yet both break on Map11, because Map11's materials use a different switch configuration and
/// Riot only cooked those configurations with NO_BAKED_LIGHTING=1. A shader-name allowlist would be
/// wrong in both directions; only permutation membership answers it.
///
/// TOC3.0 layout (parsed from the shipped bytes; 833/833 TOCs consume byte-exactly):
///   sizedString "TOC3.0" | u32 permCount | u32 defineCount | u32 blobCount | u32 flag
///   sizedString "baseDefines" | defineCount x (sizedString key, sizedString value)
///   sizedString "shaders"     | permCount x u64 hash | permCount x u32 blobIndex
/// where a sizedString is u32 length + UTF-8 bytes, and the permutation key is
///   XXH64(seed 0, concat of ordinal-sorted "NAME=VALUE"),
/// confirmed bit-exact against known triples (the empty set hashes to 0xef46db3751d8e999).</summary>
public sealed class ShaderPermutationIndex
{
    private readonly Dictionary<string, Toc?> _tocs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _featureDefines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, bool>> _switchDefaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, float[]>> _paramDefaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _textureDefaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly WadFile? _cache;
    private readonly Dictionary<ulong, string> _cachePaths = new();

    public bool IsAvailable => _cache is not null;

    /// <summary>Macros a MATERIAL authors. If a material does not set one it is usually absent — but the
    /// engine also injects some per-mesh (measured: 52 shipped TFT_Skybox materials resolve only if
    /// NO_BAKED_LIGHTING is present despite never authoring it), so these are tried BOTH ways rather than
    /// assumed absent. Treating them as definitively absent produced false "this material is broken"
    /// verdicts on content that ships and works.</summary>
    private static readonly string[] InjectableMacros =
        { "NO_BAKED_LIGHTING", "DISABLE_DEPTH_FOG", "PREMULTIPLIED_ALPHA", "NUM_BLEND_WEIGHTS", "DISABLE_FOW", "DISABLE_SHADOWS" };

    private sealed class Toc
    {
        public List<(string Key, string Value)> Pool = new();
        public HashSet<ulong> Hashes = new();
    }

    /// <param name="gameDataFinalDir">…/Game/DATA/FINAL — holds ShaderCache.dx11.wad.client and Global.wad.client.</param>
    public ShaderPermutationIndex(string gameDataFinalDir)
    {
        try
        {
            var cachePath = Path.Combine(gameDataFinalDir, "ShaderCache.dx11.wad.client");
            if (File.Exists(cachePath)) _cache = new WadFile(File.OpenRead(cachePath));
            LoadShaderDefs(Path.Combine(gameDataFinalDir, "Global.wad.client"));
        }
        catch { _cache = null; }   // no game install / unreadable — callers fall back to "unknown"
    }

    /// <summary>featureDefines and staticSwitch defaults per shader, from data/shaders/shaders.bin.</summary>
    private void LoadShaderDefs(string globalWad)
    {
        if (!File.Exists(globalWad)) return;
        try
        {
            using var fs = File.OpenRead(globalWad);
            using var wad = new WadFile(fs);
            if (!wad.Chunks.TryGetValue(HashAlgorithms.WadPath("data/shaders/shaders.bin"), out var chunk)) return;
            using var stream = wad.OpenChunk(chunk);
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            var tree = new BinTree(ms);

            uint pathHash = HashAlgorithms.Fnv1a("objectPath");

            foreach (var (_, obj) in tree.Objects)
            {
                string? shaderPath = obj.Properties.TryGetValue(pathHash, out var op) && op is BinTreeString ops ? ops.Value : null;
                if (shaderPath is null) continue;
                var defaults = ReadDefinitionDefaults(obj);
                _featureDefines[shaderPath] = defaults.FeatureDefines;
                _switchDefaults[shaderPath] = defaults.Switches;
                _paramDefaults[shaderPath] = defaults.Parameters;
                _textureDefaults[shaderPath] = defaults.Textures;
            }
        }
        catch { /* best effort — an unreadable shaders.bin just means fewer fixed defines */ }
    }

    private Toc? GetToc(string renderShader, string stage)
    {
        if (string.IsNullOrEmpty(renderShader) || _cache is null) return null;
        string path = $"assets/shaders/generated/{renderShader}.{stage}.dx11".ToLowerInvariant();
        if (_tocs.TryGetValue(path, out var cached)) return cached;

        // M277: the 2026-07-29 patch renamed every cache entry ".vs.dx11" -> ".vs-dx11". This index is
        // FAIL-SAFE (no TOC means "cannot prove the removal is safe"), so the rename did not corrupt
        // anything here - it just made CanRemoveMacro refuse every macro in the game, silently. Resolve
        // whichever spelling this install ships instead of hard-coding one.
        string lookup = Shaders.ShaderCacheReader.ResolveCachePath(
            path, p => _cache.Chunks.ContainsKey(HashAlgorithms.WadPath(p))) ?? path;

        Toc? toc = null;
        try
        {
            if (_cache.Chunks.TryGetValue(HashAlgorithms.WadPath(lookup), out var chunk))
            {
                using var s = _cache.OpenChunk(chunk);
                var ms = new MemoryStream();
                s.CopyTo(ms);
                toc = ParseToc(ms.ToArray());
            }
        }
        catch { toc = null; }
        _tocs[path] = toc;
        return toc;
    }

    private static Toc? ParseToc(byte[] b)
    {
        int p = 0;
        string ReadStr()
        {
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4)); p += 4;
            var s = Encoding.UTF8.GetString(b, p, (int)n); p += (int)n;
            return s;
        }
        uint ReadU32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4)); p += 4; return v; }

        if (ReadStr() != "TOC3.0") return null;
        uint permCount = ReadU32(); uint defineCount = ReadU32(); ReadU32(); ReadU32();
        if (ReadStr() != "baseDefines") return null;
        var toc = new Toc();
        for (int i = 0; i < defineCount; i++) toc.Pool.Add((ReadStr(), ReadStr()));
        if (ReadStr() != "shaders") return null;
        for (int i = 0; i < permCount; i++)
        { toc.Hashes.Add(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p, 8))); p += 8; }
        return toc;
    }

    private static ulong PermutationHash(List<string> sortedParts) =>
        XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(string.Concat(sortedParts)), 0);

    /// <summary>Is there a cooked permutation for this material's define set, in both stages?
    /// <paramref name="sawEvidence"/> reports whether any TOC was actually found — a caller deciding
    /// whether it is SAFE to change a material must not read "no TOC" as "fine".</summary>
    public bool IsCooked(MaterialBinding material, IReadOnlyDictionary<string, string> macros,
        out bool sawEvidence, IReadOnlySet<string>? forcedAbsent = null)
    {
        sawEvidence = false;
        string shader = material.RenderShader ?? material.ShaderName ?? "";
        if (shader.Length == 0 || _cache is null) return true;
        foreach (var stage in new[] { "vs", "ps" })
        {
            var toc = GetToc(shader, stage);
            if (toc is null) continue;                            // stage not in the cache -> no evidence
            sawEvidence = true;
            if (!StageCooked(toc, shader, material, macros, forcedAbsent)) return false;
        }
        return true;
    }

    private bool StageCooked(Toc toc, string shader, MaterialBinding material,
        IReadOnlyDictionary<string, string> macros, IReadOnlySet<string>? forcedAbsent)
    {
        _featureDefines.TryGetValue(shader, out var features);
        _switchDefaults.TryGetValue(shader, out var switchDefaults);

        // Collapse the pool to name -> distinct values.
        var byName = new List<(string Name, List<string> Values)>();
        foreach (var (k, v) in toc.Pool)
        {
            int i = byName.FindIndex(x => x.Name == k);
            if (i < 0) byName.Add((k, new List<string> { v }));
            else if (!byName[i].Values.Contains(v)) byName[i].Values.Add(v);
        }

        var fixedParts = new List<string>();
        var freeAxes = new List<(string Name, List<string?> Options)>();
        foreach (var (name, values) in byName)
        {
            if (macros.TryGetValue(name, out var mv))
            {
                if (!values.Contains(mv)) return false;            // this exact value was never cooked
                fixedParts.Add(name + "=" + mv);
            }
            // M507: ClientVisibleSwitches, not Switches. A container written with the wrong wire form is
            // SKIPPED by the client, so pinning an axis from a switch the game never reads answers a
            // different question than the one that crashes the map.
            else if (material.ClientVisibleSwitches.TryGetValue(name, out bool on)
                     || (switchDefaults?.TryGetValue(name, out on) ?? false))
            {
                string sv = on ? "1" : "0";
                if (!values.Contains(sv)) return false;
                fixedParts.Add(name + "=" + sv);
            }
            else if (features is not null && features.TryGetValue(name, out var fv))
            {
                if (!values.Contains(fv)) return false;
                fixedParts.Add(name + "=" + fv);
            }
            else if (forcedAbsent is not null && forcedAbsent.Contains(name))
            {
                // Pinned absent. Without this the enumeration below would happily add the very macro we
                // are asking about back in, find the ORIGINAL permutation, and declare the removal safe —
                // a vacuous test that passed all 184 Map11 materials including the 20 that break.
            }
            else
            {
                // Unset. Runtime axes vary freely; injectable macros may still be supplied by the engine,
                // so both "absent" and each cooked value are candidates.
                var opts = new List<string?> { null };
                opts.AddRange(values);
                freeAxes.Add((name, opts));
            }
        }

        long space = 1;
        foreach (var a in freeAxes) space *= a.Options.Count;
        if (space > 2_000_000) return true;                        // too big to enumerate — don't block

        var parts = new List<string>();
        for (long c = 0; c < space; c++)
        {
            long rem = c;
            parts.Clear();
            parts.AddRange(fixedParts);
            foreach (var (name, opts) in freeAxes)
            {
                int sel = (int)(rem % opts.Count); rem /= opts.Count;
                if (opts[sel] is { } val) parts.Add(name + "=" + val);
            }
            parts.Sort(StringComparer.Ordinal);
            if (toc.Hashes.Contains(PermutationHash(parts))) return true;
        }
        return false;
    }

    /// <summary>THE question the light baker needs: if we remove <paramref name="macro"/> from this
    /// material, will the game still find a cooked shader?
    ///
    /// FAIL-SAFE: returns false unless we positively proved the result is cooked. No shader cache, an
    /// unresolved shader name (the hash never got resolved to a path), or no TOC for the shader all mean
    /// "cannot prove it is safe", and the macro is left alone. The cost of a false negative is one mesh
    /// that stays unlit; the cost of a false positive is a shader the client cannot load at all.</summary>
    public bool CanRemoveMacro(MaterialBinding material, string macro)
    {
        if (_cache is null) return false;
        var without = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in material.Macros)
            if (!string.Equals(name, macro, StringComparison.OrdinalIgnoreCase))
                without[name] = value;
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { macro };
        bool cooked = IsCooked(material, without, out bool sawEvidence, pinned);
        return cooked && sawEvidence;
    }

    /// <summary>M491: the same question in the other direction — if we ADD <paramref name="macro"/> at
    /// <paramref name="value"/>, will the game still find a cooked shader?
    ///
    /// <para>Adding a macro asks the client for a define set exactly as much as removing one does, and it
    /// had no check. M486 authored NO_BAKED_LIGHTING=1 across 78 DefaultEnv_Flat_AlphaTest materials on the
    /// strength of a permutation COUNT, and League answered
    /// <c>Unable to find correct hash for shader '...DefaultEnv_Flat_AlphaTest.ps-dx11' in wad</c> followed
    /// by <c>Failed to compile shader</c>. Counting permutations that mention an axis is not the same
    /// question as whether the exact key a material requests was cooked; IsCooked asks the exact question,
    /// and its "this exact value was never cooked" branch is the one that catches this.</para>
    ///
    /// <para>FAIL-SAFE in the same direction as its counterpart: false unless positively proved cooked. The
    /// cost of a false negative is a material that keeps its baked lightmap; the cost of a false positive is
    /// a shader the client cannot load at all, which is a map that renders nothing.</para></summary>
    /// <summary>
    /// M502: does this shader's cooked TOC declare <paramref name="macro"/> as a permutation axis at all?
    ///
    /// <para>The distinction matters and <see cref="CanSetMacro"/> cannot express it, because it is
    /// fail-safe and answers false for two opposite situations:</para>
    /// <list type="bullet">
    ///   <item><b>Axis not declared</b> — the client's ResolvePermutation only honours a macro whose name is
    ///   in the TOC, so authoring it is INERT. Harmless to the game, but it lies to every later reader: the
    ///   material says "no baked lighting" and the shader lights it anyway.</item>
    ///   <item><b>Axis declared, this value never cooked</b> — authoring it is FATAL. This is M486, where
    ///   League answered "Unable to find correct hash for shader ... Failed to compile shader".</item>
    /// </list>
    /// <para>Callers that write macros need to tell those apart: one is a pointless line to omit, the other
    /// is a map that will not load.</para>
    /// </summary>
    public bool DeclaresMacroAxis(string shader, string macro)
    {
        if (_cache is null || string.IsNullOrEmpty(shader)) return false;
        foreach (string stage in new[] { "ps", "vs" })
        {
            var toc = GetToc(shader, stage);
            if (toc is null) continue;
            foreach (var (key, _) in toc.Pool)
                if (string.Equals(key, macro, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // M502: there is deliberately NO shader-level "can this macro be set" shortcut here.
    //
    // The obvious one — scan the TOC's define pool for (macro, value) — was written and then removed,
    // because it answers a DIFFERENT question and answers it wrongly. Measured against the live cache it
    // called DefaultEnv_Flat_AlphaTest safe for NO_BAKED_LIGHTING=1, while CanSetMacro below refuses it and
    // M486 measured League itself refusing it: "Unable to find correct hash for shader ... Failed to
    // compile shader". The pool proves the axis is cooked at that value in SOME permutation; it says
    // nothing about the exact key a given material requests, which combines every axis at once.
    //
    // That is the same mistake M486 made with permutation counts, in a new costume. Callers that need this
    // answer must build the material they intend to write and ask CanSetMacro about it.

    /// <summary>
    /// M507: the EXACT define key a live client asks for, and whether the game ships it.
    ///
    /// <para><see cref="IsCooked"/> is deliberately optimistic — it enumerates the axes a material does not
    /// pin and answers "could this ever resolve", which is the right question before an EDIT. It is the
    /// wrong question for "will this map load", and answering the wrong one is why a Map453 shipped with
    /// fourteen materials the client could not compile.</para>
    ///
    /// <para>The model here is taken from a real r3d log, and reproduces it exactly — same keys, same
    /// 64-bit hashes, same verdicts (14 of 16 AlphaTest materials refused, matching the crash):</para>
    /// <list type="bullet">
    ///   <item>every staticSwitch the shader declares contributes NAME=0 or NAME=1, from the material's
    ///   CLIENT-VISIBLE switches or the shader's own default — a switch always has a value;</item>
    ///   <item>a shaderMacro contributes only when the material sets it — absent is absent, not 0;</item>
    ///   <item>the shader's featureDefines are added as they ship (FEATURE_MASKED=1);</item>
    ///   <item>the client's own globals are added; the observed one that is a real axis here is
    ///   USE_DYNAMIC_LIGHTING=1 (COLORPALETTE_COLORBLIND and MRT_SUPPORTED were filtered out as
    ///   non-axes);</item>
    ///   <item>the result is filtered to the axes this shader's TOC declares, then hashed.</item>
    /// </list>
    /// <para>Returns false when there is no cache or no TOC — no evidence is not a verdict.</para>
    /// </summary>
    public bool TryExactKey(MaterialBinding material, out string key, out bool cooked)
        => TryExactKey(material, out key, out cooked, out _);

    /// <param name="stage">Which stage the verdict came from — "vs" or "ps". The failing stage is the one
    /// worth naming, because its axis pool is what the key was filtered against.</param>
    public bool TryExactKey(MaterialBinding material, out string key, out bool cooked, out string stage)
    {
        key = ""; cooked = false; stage = "";
        if (_cache is null) return false;

        // M510: BOTH stages, each filtered to ITS OWN pool.
        //
        // The client resolves a vertex shader and a pixel shader separately, and the two declare different
        // axes - SRX_Blend_Chemtech_Decal's VS has 6, its PS has 15. Checking only the pixel stage passed a
        // material whose VERTEX stage had no cooked permutation, and the map failed to load with the key
        // "FEATURE_WORLD_POSITION=1" that the PS pool would never have produced.
        bool sawAny = false;
        foreach (string s in new[] { "vs", "ps" })
        {
            if (!TryStageKey(material, s, null, out string stageKey, out bool stageCooked)) continue;
            sawAny = true;
            if (key.Length == 0 || !stageCooked) { key = stageKey; stage = s; }
            if (!stageCooked) { cooked = false; return true; }   // the first stage that fails is the answer
        }
        if (!sawAny) return false;
        cooked = true;
        return true;
    }

    /// <summary>One stage's key: the axes THAT stage declares, filled from the material and the client's
    /// globals, hashed the way the cache is keyed.</summary>
    private bool TryStageKey(MaterialBinding material, string stage,
        (string Macro, string? Value)? with, out string key, out bool cooked)
    {
        key = ""; cooked = false;
        string shader = material.RenderShader ?? "";
        var toc = GetToc(shader, stage);
        if (toc is null) return false;

        var declared = new HashSet<string>(toc.Pool.Select(p => p.Key), StringComparer.Ordinal);
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);

        if (_switchDefaults.TryGetValue(shader, out var switchDefaults))
            foreach (var (name, on) in switchDefaults)
                if (declared.Contains(name)) parts[name] = on ? "1" : "0";
        foreach (var (name, on) in material.ClientVisibleSwitches)
            if (declared.Contains(name)) parts[name] = on ? "1" : "0";

        if (_featureDefines.TryGetValue(shader, out var features))
            foreach (var (name, value) in features)
                if (declared.Contains(name)) parts[name] = value;

        foreach (var (name, value) in material.Macros)
            if (declared.Contains(name)) parts[name] = value;

        foreach (string global in ClientGlobals)
            if (declared.Contains(global)) parts.TryAdd(global, "1");

        if (with is { } change && declared.Contains(change.Macro))
        {
            if (change.Value is null) parts.Remove(change.Macro);
            else parts[change.Macro] = change.Value;
        }

        var sorted = parts.Select(p => p.Key + "=" + p.Value).ToList();
        sorted.Sort(StringComparer.Ordinal);
        key = string.Join(" ", sorted);
        cooked = toc.Hashes.Contains(PermutationHash(sorted));
        return true;
    }

    /// <summary>Re-ask <see cref="TryExactKey"/> with one macro added or removed, without mutating the
    /// material — the caller is looking for advice, not applying it.</summary>
    private bool WouldBeCooked(MaterialBinding material, string macro, string? value)
    {
        if (_cache is null) return false;
        bool sawAny = false;
        foreach (string stage in new[] { "vs", "ps" })
        {
            if (!TryStageKey(material, stage, (macro, value), out _, out bool cooked)) continue;
            sawAny = true;
            if (!cooked) return false;      // a change that fixes one stage and breaks the other is no fix
        }
        return sawAny;
    }

    /// <summary>
    /// M510: the defines EVERY cooked permutation of a stage carries — the shader's non-negotiables.
    ///
    /// <para>Measured rather than assumed, and it is where the real advice comes from. All 16 cooked vertex
    /// permutations of SRX_Blend_Chemtech_Decal carry NO_BAKED_LIGHTING and FEATURE_WORLD_POSITION; all 256
    /// pixel permutations additionally carry DISABLE_DEPTH_FOG and PREMULTIPLIED_ALPHA. A material missing
    /// any of them has no shader, and no amount of looking at the material says why.</para>
    ///
    /// <para>Empty when the pool is too large to enumerate — a silent cap would read as "nothing required".</para>
    /// </summary>
    public IReadOnlyList<string> RequiredDefines(string shader, string stage)
    {
        var toc = GetToc(shader, stage);
        if (toc is null) return Array.Empty<string>();

        var axes = new List<(string Name, List<string> Values)>();
        foreach (var (k, v) in toc.Pool)
        {
            int i = axes.FindIndex(x => x.Name == k);
            if (i < 0) axes.Add((k, new List<string> { v }));
            else if (!axes[i].Values.Contains(v)) axes[i].Values.Add(v);
        }

        long space = 1;
        foreach (var a in axes) space *= a.Values.Count + 1;
        if (space > 1_000_000) return Array.Empty<string>();

        var cooked = new List<List<string>>();
        for (long c = 0; c < space; c++)
        {
            long rem = c;
            var parts = new List<string>();
            foreach (var (name, values) in axes)
            {
                int sel = (int)(rem % (values.Count + 1)); rem /= values.Count + 1;
                if (sel > 0) parts.Add(name + "=" + values[sel - 1]);
            }
            parts.Sort(StringComparer.Ordinal);
            if (toc.Hashes.Contains(PermutationHash(parts))) cooked.Add(parts);
        }
        if (cooked.Count == 0) return Array.Empty<string>();

        var required = new List<string>();
        foreach (var (name, values) in axes)
        {
            if (!cooked.All(k => k.Any(x => x.StartsWith(name + "=", StringComparison.Ordinal)))) continue;
            // Name it with its value only when every cooked permutation agrees on one.
            var distinct = cooked.Select(k => k.First(x => x.StartsWith(name + "=", StringComparison.Ordinal)))
                                 .Distinct().ToList();
            required.Add(distinct.Count == 1 ? distinct[0] : name);
        }
        return required;
    }

    /// <summary>Defines the client contributes itself. Read off a live r3d log; only those the shader
    /// declares as an axis survive the filter, so listing one that does not apply is harmless.</summary>
    private static readonly string[] ClientGlobals = { "USE_DYNAMIC_LIGHTING", "MRT_SUPPORTED" };

    /// <summary>
    /// M507: this material's define set is not cooked — what single change would make it so?
    ///
    /// <para>Derived from the cache, never from a rule of thumb. On DefaultEnv_Flat_AlphaTest, for example,
    /// all 256 cooked permutations carrying PREMULTIPLIED_ALPHA=1 also carry MULTIPLY_ALPHA and
    /// DISABLE_DEPTH_FOG — so a premultiplied decal without fog disabled has no shader, and the answer
    /// "add DISABLE_DEPTH_FOG=1" falls out of the data rather than out of a guess about decals.</para>
    ///
    /// <para>Empty when nothing single-step helps, which is honest: it means the combination needs a real
    /// look, not that it is fine.</para>
    /// </summary>
    public IReadOnlyList<string> SuggestFixes(MaterialBinding material)
    {
        if (_cache is null) return Array.Empty<string>();
        // Judged on the EXACT key, not on IsCooked's optimistic enumeration: a suggestion that assumes the
        // client will choose a convenient value for an axis it actually pins is not a suggestion.
        if (!TryExactKey(material, out _, out bool nowCooked) || nowCooked) return Array.Empty<string>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in material.Macros) current[name] = value;

        var candidates = new List<string>(current.Keys);
        string shader = material.RenderShader ?? "";
        foreach (string stage in new[] { "ps", "vs" })
            if (GetToc(shader, stage) is { } toc)
                foreach (var (key, _) in toc.Pool)
                    if (!candidates.Contains(key, StringComparer.OrdinalIgnoreCase)) candidates.Add(key);

        var fixes = new List<string>();
        foreach (string axis in candidates)
        {
            // Removing one the material sets.
            if (current.ContainsKey(axis))
            {
                if (WouldBeCooked(material, axis, null)) fixes.Add($"remove {axis}");
            }
            else if (WouldBeCooked(material, axis, "1")) fixes.Add($"add {axis}=1");
        }
        if (fixes.Count > 0) return fixes;

        // M510: no single change is enough, which is common - a shader family often has several
        // non-negotiables at once. Say which ones this material is missing rather than "no idea".
        foreach (string stage in new[] { "vs", "ps" })
        {
            if (!TryStageKey(material, stage, null, out string stageKey, out bool ok) || ok) continue;
            var have = new HashSet<string>(stageKey.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
            foreach (string required in RequiredDefines(shader, stage))
            {
                if (have.Contains(required)) continue;
                string name = required.Split('=')[0];
                bool present = have.Any(h => h.StartsWith(name + "=", StringComparison.Ordinal));
                // A required axis with no single agreed value is satisfied by ANY value, so the material
                // already having one means there is nothing to advise. Saying otherwise is noise, and this
                // list is only worth reading if every line on it is actionable.
                if (present && !required.Contains('=')) continue;
                string advice = required.Contains('=')
                    ? (present ? $"set {required}" : $"add {required}")
                    : $"set {name} (every cooked {stage} permutation has it)";
                if (!fixes.Contains(advice)) fixes.Add(advice);
            }
        }
        return fixes;
    }

    public bool CanSetMacro(MaterialBinding material, string macro, string value)
    {
        if (_cache is null) return false;
        var with = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, v) in material.Macros) with[name] = v;
        with[macro] = value;
        bool cooked = IsCooked(material, with, out bool sawEvidence, forcedAbsent: null);
        return cooked && sawEvidence;
    }

    /// <summary>M213: the shader's own featureDefines and staticSwitch defaults, from shaders.bin. The DX11
    /// preview needs these to reconstruct a material's COMPLETE define set - a material only authors the
    /// switches it changes, and the rest come from the shader definition.</summary>
    /// <summary>M257: the shader's declared defaults for parameters a material does not author. Empty when
    /// shaders.bin is unavailable, which is a reason to bind nothing rather than to bind zero.</summary>
    public bool TryGetParameterDefaults(string shader, out IReadOnlyDictionary<string, float[]> defaults)
    {
        if (_paramDefaults.TryGetValue(shader, out var d)) { defaults = d; return true; }
        defaults = EmptyParams;
        return false;
    }

    private static readonly Dictionary<string, float[]> EmptyParams = new();

    /// <summary>The shader's own fallback texture paths from <c>textures[].defaultTexturePath</c>.
    /// A raw shader preview has no material to supply these, so leaving them unread changes the shader's
    /// authored starting point into a set of white stand-ins.</summary>
    public bool TryGetTextureDefaults(string shader, out IReadOnlyDictionary<string, string> defaults)
    {
        if (_textureDefaults.TryGetValue(shader, out var d)) { defaults = d; return true; }
        defaults = EmptyTextures;
        return false;
    }

    private static readonly Dictionary<string, string> EmptyTextures = new();

    /// <summary>Convert a generated-cache name back to the <c>objectPath</c> spelling used by
    /// <c>shaders.bin</c>.</summary>
    public static string DefinitionPathForCacheShader(string cacheShader)
    {
        string path = Shaders.ShaderCacheReader.StripStage(cacheShader).Replace('\\', '/');
        const string generated = "assets/shaders/generated/";
        return path.StartsWith(generated, StringComparison.OrdinalIgnoreCase)
            ? path[generated.Length..]
            : path;
    }

    /// <summary>All authored defaults from one <c>CustomShaderDef</c>. Public so the byte-level contract
    /// can be regression-tested without constructing a WAD.</summary>
    public static ShaderDefinitionDefaults ReadDefinitionDefaults(BinTreeObject obj)
    {
        uint fdHash = HashAlgorithms.Fnv1a("featureDefines");
        uint swHash = HashAlgorithms.Fnv1a("staticSwitches");
        uint nameHash = HashAlgorithms.Fnv1a("name");
        uint defHash = HashAlgorithms.Fnv1a("onByDefault");
        uint paramsHash = HashAlgorithms.Fnv1a("parameters");
        uint dataHash = HashAlgorithms.Fnv1a("data");
        uint texturesHash = HashAlgorithms.Fnv1a("textures");
        uint texturePathHash = HashAlgorithms.Fnv1a("defaultTexturePath");

        var features = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (obj.Properties.TryGetValue(fdHash, out var fdp) && fdp is BinTreeMap map)
            foreach (var e in map)
                if (e.Key is BinTreeString k && e.Value is BinTreeString v) features[k.Value] = v.Value;

        var switches = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (obj.Properties.TryGetValue(swHash, out var swp) && swp is BinTreeContainer switchesContainer)
            foreach (var el in switchesContainer.Elements.OfType<BinTreeStruct>())
            {
                string? name = el.Properties.TryGetValue(nameHash, out var np) && np is BinTreeString ns ? ns.Value : null;
                bool on = el.Properties.TryGetValue(defHash, out var dp)
                          && (dp is BinTreeBool b ? b.Value : dp is BinTreeBitBool bb && bb.Value);
                if (name is not null) switches[name] = on;
            }

        // M257: parameters[].data is the value a material means when it authors nothing.
        var parameters = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        if (obj.Properties.TryGetValue(paramsHash, out var prp) && prp is BinTreeContainer paramsContainer)
            foreach (var el in paramsContainer.Elements.OfType<BinTreeStruct>())
            {
                string? name = el.Properties.TryGetValue(nameHash, out var np) && np is BinTreeString ns ? ns.Value : null;
                if (name is not null && el.Properties.TryGetValue(dataHash, out var dv) && dv is BinTreeVector4 v4)
                    parameters[name] = new[] { v4.Value.X, v4.Value.Y, v4.Value.Z, v4.Value.W };
            }

        var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (obj.Properties.TryGetValue(texturesHash, out var txp) && txp is BinTreeContainer texturesContainer)
            foreach (var el in texturesContainer.Elements.OfType<BinTreeStruct>())
            {
                string? name = el.Properties.TryGetValue(nameHash, out var np) && np is BinTreeString ns ? ns.Value : null;
                string? path = el.Properties.TryGetValue(texturePathHash, out var pp) && pp is BinTreeString ps ? ps.Value : null;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path)) textures[name] = path;
            }

        return new ShaderDefinitionDefaults(features, switches, parameters, textures);
    }

    public bool TryGetShaderDefs(string shader,
        out IReadOnlyDictionary<string, string> featureDefines,
        out IReadOnlyDictionary<string, bool> switchDefaults)
    {
        bool a = _featureDefines.TryGetValue(shader, out var f);
        bool b = _switchDefaults.TryGetValue(shader, out var sd);
        featureDefines = f ?? new Dictionary<string, string>();
        switchDefaults = sd ?? new Dictionary<string, bool>();
        return a || b;
    }

    public void Dispose() => _cache?.Dispose();
}

public sealed record ShaderDefinitionDefaults(
    Dictionary<string, string> FeatureDefines,
    Dictionary<string, bool> Switches,
    Dictionary<string, float[]> Parameters,
    Dictionary<string, string> Textures);
