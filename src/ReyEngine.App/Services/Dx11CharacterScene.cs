using System.Text;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.Services;

/// <summary>One submesh run drawn with one resolved pipeline.</summary>
public sealed record CharacterSlice(
    string Submesh,
    string Material,
    int Start,
    int Count,
    DxbcShader Vs,
    DxbcShader Ps,
    ShaderDescription VsDesc,
    ShaderDescription PsDesc,
    IReadOnlyList<(string Target, string Key)> Textures,
    IReadOnlyList<(string Name, float[] Value)> Parameters,
    /// <summary>The skin's own <c>initialSubmeshToHide</c>. Hidden rather than dropped, so a host can
    /// switch it back on — Kalista's Altar_Spear draws through her otherwise.</summary>
    bool Hidden,
    bool UsedFallbackShader);

/// <summary>Everything the scene needs that does not touch D3D.</summary>
public sealed class PreparedCharacterScene
{
    public required PreviewMesh Mesh { get; init; }
    public required List<CharacterSlice> Slices { get; init; }
    public required Dictionary<string, TextureImage> Textures { get; init; }
    public SkinMeshProperties? SkinMesh { get; init; }
    public int SubmeshCount { get; init; }
    public List<string> Failures { get; } = new();
    public string Report { get; set; } = "";
}

/// <summary>
/// M617: building a champion skin into a D3D11 scene, without a shader-debugging window around it.
///
/// <para>The one existing character path lives inside the Shader Preview view model, and it cannot be
/// reused: it resolves materials out of a bin the USER picked from a list, falls back to a shader the
/// USER selected, and reports into that window's own submesh rows. A character window knows its bin from
/// the mesh path and has no shader picker, so it needs the same resolution driven by the data instead of
/// by the UI.</para>
///
/// <para>Deliberately a second implementation rather than an extraction of that one. The two have
/// different jobs — that window exists to put an arbitrary material through an arbitrary shader, this
/// exists to draw a skin the way its bin says — and pulling the shared middle out would have meant
/// editing the window the user asked to leave alone. The drift risk is real and is what the tests cover:
/// they assert this resolves the same materials, shaders and texture count for the same champion.</para>
/// </summary>
public static class Dx11CharacterScene
{
    /// <summary>Decode and resolve. Returns null only when the mesh itself will not decode — a scene with
    /// materials missing is still a scene, and reports what it could not resolve.</summary>
    /// <param name="fallbackShader">Used for materials that author no <c>renderShader</c> of their own.
    /// A skin bin frequently authors none: the default diffuse and every inline per-submesh override carry
    /// textures and no shader, and 42% of the material corpus is in the same position. Null means such a
    /// material is reported as unresolved instead of drawn with a guess.</param>
    public static PreparedCharacterScene? Prepare(
        byte[] sknBytes,
        byte[]? skinBinBytes,
        ShaderCacheReader cache,
        ShaderPermutationIndex? perms,
        Func<ulong, byte[]?> readAsset,
        Func<uint, string?> resolveBinName,
        Func<ulong, string?>? resolveWadPath = null,
        string? fallbackShader = null)
    {
        MeshAsset mesh;
        try { mesh = SkinnedMeshDecoder.Decode(sknBytes); }
        catch { return null; }

        var sb = new StringBuilder();
        var geometry = PreviewGeometry.FromLeagueArrays(
            "character", mesh.VertexCount,
            mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Colors, mesh.LightmapUvs, mesh.Indices,
            mesh.BlendIndices, mesh.BlendWeights,
            // Recentred on its own bounds: a champion is authored around its own origin already, and the
            // preview camera frames what it is given.
            recentre: true);

        MaterialDocument? document = null;
        if (skinBinBytes is { Length: > 0 })
            try { document = MaterialDocument.Parse(skinBinBytes, resolveBinName, resolveWadPath); }
            catch (Exception ex) { sb.AppendLine($"skin bin: {ex.Message}"); }

        var bindings = document?.Materials ?? (IReadOnlyList<MaterialBinding>)Array.Empty<MaterialBinding>();
        var scene = new PreparedCharacterScene
        {
            Mesh = geometry,
            Slices = new List<CharacterSlice>(),
            Textures = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase),
            SkinMesh = document?.SkinMesh,
            SubmeshCount = mesh.SubMeshes.Count,
        };

        sb.AppendLine($"{mesh.VertexCount:n0} vertices, {mesh.Indices.Length / 3:n0} triangles, "
                      + $"{mesh.SubMeshes.Count} submesh(es), {bindings.Count} material(s) in the bin");

        foreach (var sub in mesh.SubMeshes)
        {
            var binding = MaterialFor(bindings, sub.Material);
            if (binding is null)
            {
                scene.Failures.Add($"{sub.Material}: no material of that name in the skin bin");
                continue;
            }

            var slice = BuildSlice(sub, binding, cache, perms, fallbackShader, scene, sb);
            if (slice is not null) scene.Slices.Add(slice);
        }

        // Every distinct texture the scene will ask for, decoded once. Failures are recorded rather than
        // thrown: a missing texture costs one slot, not the whole character.
        foreach (var key in scene.Slices.SelectMany(s => s.Textures).Select(t => t.Key).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (scene.Textures.ContainsKey(key)) continue;
            try
            {
                var data = readAsset(BinTexturePath.HashOfReference(key));
                if (data is { Length: > 0 }) scene.Textures[key] = TextureDecoder.Decode(data);
                else scene.Failures.Add($"texture not found: {key}");
            }
            catch (Exception ex)
            {
                scene.Failures.Add($"texture {key}: {ex.Message}");
            }
        }

        sb.AppendLine($"{scene.Slices.Count} slice(s) resolved, {scene.Textures.Count} texture(s) decoded"
                      + (scene.Failures.Count > 0 ? $", {scene.Failures.Count} problem(s)" : ""));
        scene.Report = sb.ToString();
        return scene;
    }

    /// <summary>Upload the scene. Returns how many materials drew.</summary>
    public static int Commit(ShaderPreviewRenderer renderer, PreparedCharacterScene scene, string gameVersion)
    {
        renderer.GameVersion = gameVersion;
        renderer.ClearMaterials();
        renderer.SetMesh(scene.Mesh);

        int ok = 0;
        foreach (var slice in scene.Slices)
        {
            var mat = renderer.BuildMaterial(slice.Material, slice.Vs, slice.Ps, slice.Start, slice.Count,
                out var report, slice.VsDesc, slice.PsDesc, StateDescription.Geometry);
            if (mat is null)
            {
                scene.Failures.Add($"{slice.Material}: {report.Error ?? "pipeline creation failed"}");
                continue;
            }

            mat.SortableByPipeline = StateDescription.Geometry.DepthWrite;
            mat.Visible = !slice.Hidden;
            foreach (var (name, value) in slice.Parameters) mat.Params[name] = value;

            foreach (var (target, key) in slice.Textures)
            {
                if (renderer.TryBindCached(mat, target, key)) continue;
                if (!scene.Textures.TryGetValue(key, out var img)) continue;
                renderer.SetTexture(mat, target, key, img.Rgba, img.Width, img.Height);
            }
            ok++;
        }
        return ok;
    }

    // ---- resolution ---------------------------------------------------------------------------------

    /// <summary>Which material draws this submesh.
    ///
    /// <para>The same three rules the Shader Preview uses, and in the same order, including the pass that
    /// prefers a material naming a shader over one that does not — without it a skin's
    /// "(skin default texture)" pseudo-binding swallows every submesh.</para></summary>
    public static MaterialBinding? MaterialFor(IReadOnlyList<MaterialBinding> bindings, string submesh)
    {
        foreach (bool needShader in new[] { true, false })
        {
            foreach (var b in bindings)
            {
                if (needShader && string.IsNullOrWhiteSpace(b.RenderShader)) continue;
                foreach (var s in b.Submeshes)
                    if (s.Equals(submesh, StringComparison.OrdinalIgnoreCase)) return b;
            }
            foreach (var b in bindings)
            {
                if (needShader && string.IsNullOrWhiteSpace(b.RenderShader)) continue;
                if (b.Name.Equals(submesh, StringComparison.OrdinalIgnoreCase)) return b;
            }
            foreach (var b in bindings)
            {
                if (needShader && string.IsNullOrWhiteSpace(b.RenderShader)) continue;
                if (b.IsDefault) return b;
            }
        }
        return null;
    }

    private static CharacterSlice? BuildSlice(
        SubMeshInfo sub, MaterialBinding b, ShaderCacheReader cache, ShaderPermutationIndex? perms,
        string? fallbackShader, PreparedCharacterScene scene, StringBuilder sb)
    {
        bool usedFallback = false;
        string? shader = b.RenderShader;
        if (string.IsNullOrWhiteSpace(shader))
        {
            shader = fallbackShader;
            if (string.IsNullOrWhiteSpace(shader))
            {
                scene.Failures.Add($"{b.Name}: authors no renderShader and no stand-in was supplied");
                return null;
            }
            usedFallback = true;
        }

        string full = "assets/shaders/generated/" + shader!.Trim('/');
        string vsPath = ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex);
        string psPath = ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel);

        var vsToc = cache.ReadToc(vsPath);
        var psToc = cache.ReadToc(psPath);
        if (vsToc is null || psToc is null)
        {
            scene.Failures.Add($"{b.Name}: '{shader}' is missing a stage in the shader cache");
            return null;
        }

        IReadOnlyDictionary<string, string>? feat = null;
        IReadOnlyDictionary<string, bool>? swDef = null;
        perms?.TryGetShaderDefs(shader, out feat, out swDef);

        var vsPerm = ShaderCacheReader.ResolvePermutation(vsToc, b.Macros, b.Switches, feat, swDef, out var vwhy);
        var psPerm = ShaderCacheReader.ResolvePermutation(psToc, b.Macros, b.Switches, feat, swDef, out var pwhy);
        if (vsPerm is null || psPerm is null)
        {
            scene.Failures.Add($"{b.Name}: no cooked permutation ({(vsPerm is null ? vwhy : pwhy)})");
            return null;
        }

        var vs = cache.LoadShader(vsPath, vsPerm.BlobIndex, out var vsErr);
        var ps = cache.LoadShader(psPath, psPerm.BlobIndex, out var psErr);
        if (vs is null || ps is null)
        {
            scene.Failures.Add($"{b.Name}: bytecode would not load ({(vs is null ? vsErr : psErr)})");
            return null;
        }

        var textures = new List<(string Target, string Key)>();
        foreach (var slot in b.Slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Path)) continue;
            if (ResolveTextureTarget(slot.SamplerName, ps, vs) is not { } target) continue;
            string key = slot.Path!.ToLowerInvariant();
            textures.RemoveAll(t => t.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
            textures.Add((target, key));
        }

        var parameters = new List<(string Name, float[] Value)>();
        var authored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in b.Parameters)
            if (p.TryGetVector4(out var v))
            {
                parameters.Add((p.Name, new[] { v.X, v.Y, v.Z, v.W }));
                authored.Add(p.Name);
            }

        // The shader's own declared defaults for anything the material leaves out. Unwritten is not
        // "unspecified" - it is zero, and zero is a value the shader multiplies by.
        if (perms is not null && perms.TryGetParameterDefaults(shader, out var defaults))
            foreach (var (name, value) in defaults)
                if (!authored.Contains(name)) parameters.Add((name, value));

        // The skin's own scalars, which live in skinMeshProperties rather than in any material.
        if (scene.SkinMesh is { } skin)
        {
            void Put(string name, float x, float y, float z, float w)
            {
                if (authored.Contains(name)) return;
                parameters.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                parameters.Add((name, new[] { x, y, z, w }));
            }
            if (skin.SelfIllumination is { } si) Put("SELF_ILLUMINATION", si, si, si, si);
            if (skin.Fresnel is { } fr) Put("Fresnel_Size", fr, fr, fr, fr);
            if (skin.FresnelColor is { } fc) Put("Fresnel_Color", fc.X, fc.Y, fc.Z, fc.W);
        }

        bool hidden = scene.SkinMesh?.InitialSubmeshesToHide
            .Any(h => h.Equals(sub.Material, StringComparison.OrdinalIgnoreCase)) == true;
        if (hidden) sb.AppendLine($"   '{sub.Material}' hidden by initialSubmeshToHide");

        var macros = b.Macros;
        return new CharacterSlice(
            sub.Material, b.Name, sub.StartIndex, sub.IndexCount,
            vs, ps,
            new ShaderDescription(full, DxbcStage.Vertex, vsPerm.Key, vsPerm.BlobIndex, macros, vs),
            new ShaderDescription(full, DxbcStage.Pixel, psPerm.Key, psPerm.BlobIndex, macros, ps),
            textures, parameters, hidden, usedFallback);
    }

    /// <summary>Which declared texture a sampler feeds.
    ///
    /// <para>Normally <c>samplerName + "__TX"</c>. Champions break that and it is not a corner case: a
    /// skin's default diffuse and every inline per-submesh override parse as a sampler literally named
    /// <c>texture</c>, because that is the field name in <c>skinMeshProperties</c>, and no shader declares
    /// a <c>texture__TX</c>. For that generic name only, the diffuse slot is picked from what the shader
    /// declares. Names ending <c>_SharedTexture</c> are engine-supplied and never a material's
    /// diffuse.</para></summary>
    public static string? ResolveTextureTarget(string samplerName, DxbcShader ps, DxbcShader vs)
    {
        string exact = samplerName + "__TX";
        foreach (var refl in new[] { ps, vs })
            foreach (var t in refl.Textures)
                if (t.Name.Equals(exact, StringComparison.OrdinalIgnoreCase)
                    || t.Name.Equals(samplerName, StringComparison.OrdinalIgnoreCase))
                    return t.Name;

        if (!samplerName.Equals("texture", StringComparison.OrdinalIgnoreCase)) return null;

        var candidates = ps.Textures
            .Where(t => !t.Name.EndsWith("_SharedTexture", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return candidates.FirstOrDefault(t => t.Name.Contains("Diffuse", StringComparison.OrdinalIgnoreCase))?.Name
               ?? candidates.FirstOrDefault()?.Name;
    }
}
