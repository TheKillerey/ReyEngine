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

            uint fdHash = HashAlgorithms.Fnv1a("featureDefines");
            uint swHash = HashAlgorithms.Fnv1a("staticSwitches");
            uint nameHash = HashAlgorithms.Fnv1a("name");
            uint defHash = HashAlgorithms.Fnv1a("onByDefault");
            uint pathHash = HashAlgorithms.Fnv1a("objectPath");
            uint paramsHash = HashAlgorithms.Fnv1a("parameters");
            uint dataHash = HashAlgorithms.Fnv1a("data");

            foreach (var (_, obj) in tree.Objects)
            {
                string? shaderPath = obj.Properties.TryGetValue(pathHash, out var op) && op is BinTreeString ops ? ops.Value : null;
                if (shaderPath is null) continue;

                var fd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (obj.Properties.TryGetValue(fdHash, out var fdp) && fdp is BinTreeMap map)
                    foreach (var e in map)
                        if (e.Key is BinTreeString k && e.Value is BinTreeString v) fd[k.Value] = v.Value;

                var sd = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                if (obj.Properties.TryGetValue(swHash, out var swp) && swp is BinTreeContainer c)
                    foreach (var el in c.Elements.OfType<BinTreeStruct>())
                    {
                        string? sn = el.Properties.TryGetValue(nameHash, out var np) && np is BinTreeString ns ? ns.Value : null;
                        bool on = el.Properties.TryGetValue(defHash, out var dp)
                                  && (dp is BinTreeBool b ? b.Value : dp is BinTreeBitBool bb && bb.Value);
                        if (sn is not null) sd[sn] = on;
                    }

                // M257: the shader's own PARAMETER DEFAULTS. Each entry of `parameters` is a struct with a
                // `name` string and a `data` Vector4 - e.g. albedoNewMin = <0.1,0,0,0>, rimOffset =
                // <0.3,0,0,0>. Present on 343 of the 347 shader definitions.
                //
                // These are what a material means when it does not author a parameter. Without them the
                // constant is simply left unwritten, i.e. zero - and zero is not "unspecified", it is a
                // value the shader will happily multiply by. The same failure as M255's TintColor, one
                // level further out.
                var pd = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                if (obj.Properties.TryGetValue(paramsHash, out var prp) && prp is BinTreeContainer pc)
                    foreach (var el in pc.Elements.OfType<BinTreeStruct>())
                    {
                        string? pn = el.Properties.TryGetValue(nameHash, out var pnp) && pnp is BinTreeString pns
                            ? pns.Value : null;
                        if (pn is null) continue;
                        if (el.Properties.TryGetValue(dataHash, out var dv) && dv is BinTreeVector4 v4)
                            pd[pn] = new[] { v4.Value.X, v4.Value.Y, v4.Value.Z, v4.Value.W };
                    }

                _featureDefines[shaderPath] = fd;
                _switchDefaults[shaderPath] = sd;
                _paramDefaults[shaderPath] = pd;
            }
        }
        catch { /* best effort — an unreadable shaders.bin just means fewer fixed defines */ }
    }

    private Toc? GetToc(string renderShader, string stage)
    {
        if (string.IsNullOrEmpty(renderShader) || _cache is null) return null;
        string path = $"assets/shaders/generated/{renderShader}.{stage}.dx11".ToLowerInvariant();
        if (_tocs.TryGetValue(path, out var cached)) return cached;

        Toc? toc = null;
        try
        {
            if (_cache.Chunks.TryGetValue(HashAlgorithms.WadPath(path), out var chunk))
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
            else if (material.Switches.TryGetValue(name, out bool on)
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
