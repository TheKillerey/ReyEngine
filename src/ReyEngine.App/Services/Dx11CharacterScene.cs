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
    bool UsedFallbackShader,
    /// <summary>M633: the material's OWN render state, off its technique/pass in the skin bin. The map path
    /// has carried this on its slice since M279; this one threw it away and gave every submesh the same
    /// hardcoded state, which is why an opaque champion material was alpha-blended and a two-sided one was
    /// indistinguishable from a single-sided one. See <see cref="Commit"/> for what is honoured.</summary>
    MaterialProfile Profile);

/// <summary>Everything the scene needs that does not touch D3D.</summary>
public sealed class PreparedCharacterScene
{
    public required PreviewMesh Mesh { get; init; }
    public required List<CharacterSlice> Slices { get; init; }
    public required Dictionary<string, TextureImage> Textures { get; init; }
    public SkinMeshProperties? SkinMesh { get; init; }
    public int SubmeshCount { get; init; }

    /// <summary>M647: every condition this skin's material drivers ask about - what a state switch can
    /// offer for it. Empty for a skin whose materials carry no dynamicMaterial, which is most of them
    /// (71 of 690 skin bins across every champion wad).</summary>
    public IReadOnlyList<MaterialDriverCondition> Conditions { get; init; } = Array.Empty<MaterialDriverCondition>();

    /// <summary>The longest transition any driver in this skin takes to settle after its condition flips.
    /// The range a "seconds since" control needs to cover.</summary>
    public float LongestTransitionSeconds { get; init; }

    /// <summary>Per condition, the longest ramp among the parameters that ask about it - what "halfway
    /// through THIS transition" means when that one switch is turned on.</summary>
    public IReadOnlyDictionary<string, float> TransitionByCondition { get; init; } =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The situation this scene was built for.</summary>
    public MaterialDriverState DriverState { get; init; } = MaterialDriverState.Rest;
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
    /// <summary>M624: the shader a skin's materials fall back to when they name none of their own.
    ///
    /// <para>Not invented here - it is the same constant the Shader Preview has used since M240, which is
    /// the one configuration known to draw a champion through this renderer. It matters more than it
    /// sounds: a skin bin's default diffuse and every inline per-submesh override carry TEXTURES and no
    /// shader, so with no stand-in those submeshes resolve to nothing at all. Measured on Ahri: 2 of her
    /// 4 submeshes resolved without it, 4 of 4 with it - half the character was simply absent.</para></summary>
    public const string DefaultCharacterShader = "shaders/skinnedmesh/diffuse_alpha";

    /// <summary>Decode and resolve. Returns null only when the mesh itself will not decode — a scene with
    /// materials missing is still a scene, and reports what it could not resolve.</summary>
    /// <param name="fallbackShader">Used for materials that author no <c>renderShader</c> of their own.
    /// A skin bin frequently authors none: the default diffuse and every inline per-submesh override carry
    /// textures and no shader, and 42% of the material corpus is in the same position. Null means such a
    /// material is reported as unresolved instead of drawn with a guess.</param>
    /// <param name="driverState">M647: the situation to draw - which of the skin's driver conditions hold
    /// and for how long. Null is <see cref="MaterialDriverState.Rest"/>: alive, unbuffed, idle.</param>
    public static PreparedCharacterScene? Prepare(
        byte[] sknBytes,
        byte[]? skinBinBytes,
        ShaderCacheReader cache,
        ShaderPermutationIndex? perms,
        Func<ulong, byte[]?> readAsset,
        Func<uint, string?> resolveBinName,
        Func<ulong, string?>? resolveWadPath = null,
        string? fallbackShader = null,
        MaterialDriverState? driverState = null)
    {
        var state = driverState ?? MaterialDriverState.Rest;
        MeshAsset mesh;
        try { mesh = SkinnedMeshDecoder.Decode(sknBytes); }
        catch { return null; }

        var sb = new StringBuilder();
        var geometry = PreviewGeometry.FromLeagueArrays(
            "character", mesh.VertexCount,
            mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Colors, mesh.LightmapUvs, mesh.Indices,
            mesh.BlendIndices, mesh.BlendWeights,
            // NOT recentred, and this is not a preference.
            //
            // A bone matrix maps BIND-POSE object space to posed space. Shifting every vertex by -centre
            // first means (p - c) * skin is not (p * skin) - c, so with 127 bones each rotating about a
            // pivot that is no longer where the skeleton thinks it is, the mesh scatters - measured on
            // Ahri mid-clip, 263 units, on a champion 275 units tall. That is the whole model out of
            // frame, which reads as "nothing is drawn" rather than as a wrong pose, and is why the bones
            // and the VFX looked fine while the character was simply absent.
            // The Shader Preview recentres and gets away
            // with it only because it never animates: identity times a shifted vertex is still just a
            // shifted vertex.
            //
            // It is also what the GL preview does. Both viewports draw the character at its authored
            // coordinates and let the shared camera frame it, which is the only way the two can agree.
            recentre: false);

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
            Conditions = MaterialDrivers.ConditionsOf(bindings),
            TransitionByCondition = bindings
                .SelectMany(b => b.DynamicParameters)
                .Where(p => p.Enabled)
                .SelectMany(p => p.Conditions.Select(c => (c.Key, p.TransitionSeconds)))
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(x => x.TransitionSeconds), StringComparer.OrdinalIgnoreCase),
            LongestTransitionSeconds = bindings
                .SelectMany(b => b.DynamicParameters)
                .Where(p => p.Enabled)
                .Aggregate(0f, (a, p) => MathF.Max(a, p.TransitionSeconds)),
            DriverState = state,
        };

        sb.AppendLine($"{mesh.VertexCount:n0} vertices, {mesh.Indices.Length / 3:n0} triangles, "
                      + $"{mesh.SubMeshes.Count} submesh(es), {bindings.Count} material(s) in the bin");

        // Without the permutation index there are no shader parameter DEFAULTS, so every parameter the
        // material does not author stays zero - and zero is a value the shader multiplies by, not an
        // absence. The usual result is a model that draws perfectly and is entirely black, which on a
        // dark viewport is indistinguishable from not drawing at all. Said out loud for that reason.
        if (perms is null)
            scene.Failures.Add("No shader permutation index: unauthored parameters default to zero, "
                               + "which usually renders the character black.");

        foreach (var sub in mesh.SubMeshes)
        {
            var binding = MaterialFor(bindings, sub.Material);
            if (binding is null)
            {
                scene.Failures.Add($"{sub.Material}: no material of that name in the skin bin");
                continue;
            }

            var slice = BuildSlice(sub, binding, cache, perms, fallbackShader, scene, sb, state);
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

    /// <summary>Upload the scene. Returns how many materials drew.
    ///
    /// <para><b>The blend state stays hardcoded, and that is a MEASURED decision, not an oversight.</b>
    /// 68 of the 420 base-skin materials across the roster author <c>blendEnable</c> with SrcAlpha /
    /// OneMinusSrcAlpha (6/7, all 68 of them); the other 352 are the inline default-texture binding and
    /// author no blend at all. Every one of the 420 is drawn here through the renderer's fixed
    /// SrcAlpha/InvSrcAlpha, so honouring the flag looked like an obvious fix.</para>
    ///
    /// <para>It changes nothing. Rendered headless on a real device across 21 champions, feeding each
    /// material its own authored factors - One/Zero for the ones that author no blend - moved <b>0 pixels</b>
    /// on all 21. The control rules out a dead code path: forcing the same materials to One/One through the
    /// same field moves 46,690 px on Aatrox and 201,250 on Garen. The reason is that Riot's champion pixel
    /// shaders resolve their own coverage with a <c>discard</c> and then write opaque alpha - every
    /// permutation in play carries one - so the blend equation has nothing to interpolate. Aatrox's five
    /// diffuse and mask textures are 100.0% alpha-255; the masking is entirely in the shader.</para>
    ///
    /// <para>Depth is left alone for a different reason. The profile calls those 68 materials Transparent
    /// and would stop them writing depth, but M557/M558 CONFIRMED against the live client that the game
    /// derives depth-write from the shader CLASS and not from a material's blend state. Taking the mask off
    /// here would make this viewport kinder than the target, which is the one thing
    /// <c>editor-must-not-be-kinder</c> exists to stop.</para></summary>
    public static int Commit(ShaderPreviewRenderer renderer, PreparedCharacterScene scene, string gameVersion)
    {
        renderer.GameVersion = gameVersion;
        renderer.ClearMaterials();
        renderer.SetMesh(scene.Mesh);
        CommitSlices(renderer, scene);

        // The RENDERER's count, not the local one. Returning what this method built rather than what the
        // renderer holds is what let the bug hide: the window logged "after commit: 0 material(s)" and,
        // three lines later, "5 material(s) drawing". A return value that cannot disagree with the
        // renderer cannot tell that lie again.
        return renderer.MaterialCount;
    }

    /// <summary>M676: build and register one material per slice over whatever geometry the caller arranged -
    /// THE mesh for the character window (<see cref="Commit"/>), or a geometry of the prop's own for a map
    /// placement, where <paramref name="configure"/> points each material at it. Returns the materials
    /// registered, so a caller sharing the renderer with a map can take exactly its own back out.</summary>
    public static List<PreviewMaterial> CommitSlices(ShaderPreviewRenderer renderer, PreparedCharacterScene scene,
        Action<PreviewMaterial>? configure = null)
    {
        var made = new List<PreviewMaterial>();
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

            // M633: the material's own cullEnable, which this path threw away - every champion submesh drew
            // two-sided, so interior faces showed through and back faces lit that the game never rasterises.
            // The map path has honoured the same field per material since M358; this is its character half.
            //
            // Riot leaves the field ABSENT on 416 of the 420 base-skin materials, and the schema default is
            // "cull". The four exceptions are the argument that absent really means cull rather than
            // unspecified: they are Locke's hair, casket and weapon and Morgana's bush diffuse, all
            // cullEnable=FALSE - Riot writes the field exactly where a surface is meant to be seen from
            // behind, which is the classic hair-and-foliage-card list.
            //
            // Safe to switch on here for two measured reasons, because M624 pinned it off on the honest
            // grounds that "the character winding on this renderer has never been measured" and M354's
            // precedent is a milestone that turned culling on and deleted the terrain:
            //
            //  - The winding is the SAME as the map's. Checked against the authored vertex normals, which
            //    every League mesh ships: cross(p1-p0, p2-p0) agrees with the mean vertex normal on 99.9%
            //    of Aatrox's 8,594 triangles, 100.0% of Ahri's 21,678, 99.9% of Ezreal's and Garen's and
            //    99.6% of Lux's - against 98.8% of Map11's 910,649 and 94.8% of Map12's. Same convention,
            //    same sign, so M357's measured FrontCounterClockwise and the _rasterCull state built from
            //    it carry over rather than needing to be settled again.
            //  - The GL viewport in this same window has culled champions per submesh since M34, off the
            //    same authored flag and the same default-on toggle. If champion winding were wrong there,
            //    every character in the editor would already be inside-out.
            //
            // And then rendered, rather than argued: headless on a real device over 21 champions, culling
            // changes pixels on 13 of them without ever collapsing the silhouette - 25,752 px on Locke,
            // 1,461 on Kayle, 1,393 on Aatrox, down to 1 px on Zed, with covered area moving by at most
            // 0.2%. An inverted cull cannot look like that.
            mat.CullBackFaces = slice.Profile.CullEnabled;
            foreach (var (name, value) in slice.Parameters) mat.Params[name] = value;

            foreach (var (target, key) in slice.Textures)
            {
                if (renderer.TryBindCached(mat, target, key)) continue;
                if (!scene.Textures.TryGetValue(key, out var img)) continue;
                renderer.SetTexture(mat, target, key, img.Rgba, img.Width, img.Height);
            }

            // M626: REGISTER it. BuildMaterial constructs a material and hands it back; AddMaterial is the
            // only path into the renderer's draw list, and without this line every character material was
            // built, textured, parameterised - and then dropped on the floor. Nothing drew, and nothing
            // was disposed either, so each scene leaked its constant buffers as well.
            configure?.Invoke(mat);
            renderer.AddMaterial(mat);
            made.Add(mat);
        }
        return made;
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
        string? fallbackShader, PreparedCharacterScene scene, StringBuilder sb, MaterialDriverState state)
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

        // M646/M647: what the material's dynamicMaterial drives, in the situation being drawn. The
        // authored paramValues entry is the editor's value, not the game's: Locke authors
        // VCDissolve_Value = 1.23, which dissolves everything below a quarter of his height, and drives it
        // to -0.8 while alive - drawing the authored value draws him dead. The driver's value replaces the
        // authored one whenever this reader can work it out; when it cannot, the authored value stands and
        // the report says which driver was not evaluated.
        foreach (var dp in b.DynamicParameters)
        {
            if (!dp.Enabled) continue;
            string material = b.Name.Split('/').Last();
            var got = dp.Evaluate(state);
            if (got.Value is not { } value)
            {
                sb.AppendLine($"   {material}: {dp.Name} driven by {dp.Driver}; {got.Reason} - the authored value stands");
                continue;
            }
            int at = parameters.FindIndex(p => p.Name.Equals(dp.Name, StringComparison.OrdinalIgnoreCase));
            string was = at < 0 ? "unauthored" : "authored " + MaterialDrivers.Fmt(new System.Numerics.Vector4(parameters[at].Value[0], parameters[at].Value[1], parameters[at].Value[2], parameters[at].Value[3]));
            if (at >= 0) parameters.RemoveAt(at);
            parameters.Add((dp.Name, new[] { value.X, value.Y, value.Z, value.W }));
            sb.AppendLine($"   {material}: {dp.Name} = {MaterialDrivers.Fmt(value)} ({got.Reason}); {dp.Driver}; {was}");
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
            textures, parameters, hidden, usedFallback, b.Profile);
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
