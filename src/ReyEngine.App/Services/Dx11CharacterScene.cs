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
    MaterialProfile Profile,
    /// <summary>M724: which mesh submesh this slice draws, so runtime visibility can address it. -1 on a
    /// slice built by a path that has no per-submesh visibility of its own (map props).</summary>
    int SubmeshIndex = -1);

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

    /// <summary>M825: the parameter arrays that carry one of the SKIN BLOCK's own values (<c>fresnelColor</c> -> <c>Fresnel_Color</c>) into a
    /// slice - the very arrays the committed materials read, one per slice that does not author the parameter itself. The Chroma Studio
    /// rewrites them in place to preview a recolour of the skin block, which has no material to patch.</summary>
    public List<(string Name, float[] Value)> SkinBoundParameters { get; } = new();

    /// <summary>M763: Riot's five bloom blobs, loaded here in the CPU half exactly as the map scene loads
    /// them (<see cref="Dx11SceneBuilder.LoadBloomShaders"/>). Until M763 the character window never set
    /// them, so its renderer had no chain and a champion's glow (diffuse_bloom tails, fresnel rims) was
    /// discarded. Null switches the chain off.</summary>
    public byte[]?[]? BloomShaders { get; set; }
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

    /// <summary>M779: what a STAND-IN slice (see <see cref="DefaultCharacterShader"/>) actually draws
    /// with — the engine's own default pair for a submesh whose material names no shader at all, living
    /// under <c>assets/shaders/hlsl/</c> rather than <c>assets/shaders/generated/</c> because no material
    /// ever names it (so it has no generated/ entry of its own).
    ///
    /// <para>Not <see cref="DefaultCharacterShader"/>'s generated/diffuse_alpha pair, which is what this
    /// used to load. Verified against a real D3D11 device (Inhibitor Skin26's cage, the stand-in
    /// submesh): diffuse_alpha's PS samples only the diffuse texture's <c>.rgb</c> and discards on the
    /// MATERIAL's own <c>Alpha</c> constant, never the texture's alpha channel — so a cutout texture (the
    /// cage is 5.0% alpha-0 texels) drew fully opaque, reported as "still not transparent". lit_uber_ps
    /// discards on <c>DIFFUSE_MAP__TX.a == 0</c>, which is what a cutout needs, and made the cage
    /// see-through when swapped in (centre pixel (0,0,0,255) opaque black -> (255,62,215) over a magenta
    /// clear colour).</para>
    ///
    /// <para>M780: the vertex half is <c>lit_uber_vs</c>, lit_uber_ps's own partner - NOT <c>default_vs</c>,
    /// which M779 paired it with. default_vs (NUM_BLEND_WEIGHTS=4, blob 1) writes only SV_Position and
    /// TEXCOORD0.xy, while lit_uber_ps reads TEXCOORD1.zw (the fog-of-war UV) and COLOR0 (the lightgrid
    /// ambient cube, from LIGHTGRID_COLORS). Those inputs were never written, so every stand-in submesh lit
    /// from undefined values: on the real Map12 lightgrid the turrets, inhibitors and nexus came out at
    /// 0.24-0.71 of their pre-M779 luminance, reported as "now a bit more dark". lit_uber_vs
    /// (NUM_BLEND_WEIGHTS=4, blob 39) outputs exactly the signature lit_uber_ps reads, and computes COLOR0
    /// with the same six-face cube the old generated diffuse_alpha VS did - measured through the map prop
    /// path, turrets and nexus are back to a luminance ratio of 1.000, and the cage stays see-through.</para></summary>
    public const string DefaultStandInVertexShader = "assets/shaders/hlsl/skinnedmesh/lit_uber_vs";
    public const string DefaultStandInPixelShader = "assets/shaders/hlsl/skinnedmesh/lit_uber_ps";

    /// <summary>M779: not a real texture path — a synthetic key so a stand-in slice's unauthored
    /// EMISSIVE_MAP__TX binds to BLACK instead of falling through to the renderer's unbound-texture
    /// fallback, which is a 1x1 WHITE SRV (<c>ShaderPreviewRenderer._white</c>). lit_uber_ps treats
    /// EMISSIVE_MAP__TX as a lighting bypass — sampling white drives the term to fully self-lit — so an
    /// unbound slot would make every stand-in submesh full-bright regardless of the scene's actual
    /// lights. <see cref="Prepare"/> resolves this key to a 1x1 opaque-black pixel directly, without going
    /// through <c>readAsset</c>.</summary>
    private const string BlackEmissiveKey = "reyengine://stand-in/black-emissive";

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
        MaterialDriverState? driverState = null,
        // M732: the caller's already-decoded mesh, when it has one. A placed prop is decoded off the UI
        // thread while the prop set is built (BuildPropRenderSet), and this then decoded the SAME bytes a
        // second time - on the UI thread, inside the render frame, once per distinct prop mesh, every time
        // props are switched on or the scene is rebuilt. Null keeps the old behaviour.
        MeshAsset? decodedMesh = null,
        // M805: a bin the game has loaded beside this skin - the map's shipping bin for a banner placed on the map,
        // whose materials live there and nowhere the skin bin links. Already parsed by the caller, so a prop driver
        // that prepares the mesh again on every republish does not parse it again. Null changes nothing.
        LoadedBin? hostBin = null)
    {
        var state = driverState ?? MaterialDriverState.Rest;
        MeshAsset mesh;
        if (decodedMesh is not null) mesh = decodedMesh;
        else
        {
            try { mesh = SkinnedMeshDecoder.Decode(sknBytes); }
            catch { return null; }
        }

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

        // M777: follow a materialOverride/skinMeshProperties link into a LINKED bin (tree.Dependencies)
        // when the skin bin does not define the target itself - Nexus skin31's glass/glass_out point at
        // Glass_inst, which lives only in Nexus_Multi_Skins_Skin30_Skins_Skin31.bin. Dependency paths are
        // plain wad paths (never the texture "0x…" hex form), so they hash with WadPath, not
        // BinTexturePath.HashOfReference. Goes through the SAME readAsset as every texture here, so a
        // project override on the linked bin is honoured.
        byte[]? ReadBin(string path)
        {
            try { return readAsset(HashAlgorithms.WadPath(path)); }
            catch { return null; }
        }

        MaterialDocument? document = null;
        if (skinBinBytes is { Length: > 0 })
            try { document = MaterialDocument.Parse(skinBinBytes, resolveBinName, resolveWadPath, ReadBin, hostBin); }   // M805: hostBin
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

        for (int submeshIndex = 0; submeshIndex < mesh.SubMeshes.Count; submeshIndex++)
        {
            var sub = mesh.SubMeshes[submeshIndex];
            var binding = MaterialFor(bindings, sub.Material);
            if (binding is null)
            {
                scene.Failures.Add($"{sub.Material}: no material of that name in the skin bin");
                continue;
            }

            var slice = BuildSlice(sub, binding, cache, perms, fallbackShader, scene, sb, state);
            // M724: the submesh this slice draws, carried so runtime visibility can address it. Without it
            // the D3D11 character had no way to be told which range to stop drawing, and every hide - the
            // checkboxes, Show All / None, and every per-clip visibility event - moved only the GL image.
            if (slice is not null) scene.Slices.Add(slice with { SubmeshIndex = submeshIndex });
        }

        // M777: submeshRenderOrder - the skin's own front-to-back DRAW order (glass drawn after the dome
        // it sits on, for instance). Named submeshes move to that order; everything else keeps its file
        // order, after the named ones.
        if (scene.SkinMesh?.SubmeshRenderOrder is { Count: > 0 } order)
        {
            var reordered = ApplySubmeshRenderOrder(scene.Slices, s => s.Submesh, order);
            scene.Slices.Clear();       // Slices is `init`-only - reorder the existing list in place
            scene.Slices.AddRange(reordered);
            sb.AppendLine($"submeshRenderOrder applied: {string.Join(' ', order)}");
        }

        // Every distinct texture the scene will ask for, decoded once. Failures are recorded rather than
        // thrown: a missing texture costs one slot, not the whole character.
        foreach (var key in scene.Slices.SelectMany(s => s.Textures).Select(t => t.Key).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (scene.Textures.ContainsKey(key)) continue;
            // M779: the stand-in emissive fallback is not a wad asset — see BlackEmissiveKey.
            if (key.Equals(BlackEmissiveKey, StringComparison.OrdinalIgnoreCase))
            {
                scene.Textures[key] = new TextureImage(1, 1, new byte[] { 0, 0, 0, 255 });
                continue;
            }
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
        scene.BloomShaders = Dx11SceneBuilder.LoadBloomShaders(cache);   // M763
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

        // M763: the bloom chain, on the map scene's all-or-nothing contract
        if (scene.BloomShaders is { } bs && bs.Length == 5)
            renderer.SetBloomShaders(bs[0], bs[1], bs[2], bs[3], bs[4]);
        else
            renderer.SetBloomShaders(null, null, null, null, null);

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

            // M777: the pass's OWN depthEnable/writeMask (schema default true/31 when the material
            // authors neither, which is most of them - so this changes nothing for a material that
            // authors nothing). Only 19 linked champion glass materials author depthEnable=false today,
            // but every material is read the same way rather than special-casing linked ones.
            mat.TestsDepth = slice.Profile.AuthoredDepthTest;
            mat.WritesDepth = slice.Profile.AuthoredWritesDepth;
            // A material that doesn't write depth keeps SUBMISSION order rather than sorting by pipeline -
            // the same M279 rule the map path's Dx11SceneBuilder applies off its own depthWrite flag
            // (mat.SortableByPipeline = depthWrite), for the same reason: a transparent slice sorted by
            // pipeline can land in front of the opaque geometry it is meant to composite over.
            mat.SortableByPipeline = mat.WritesDepth;
            mat.Visible = !slice.Hidden;
            // M724: which submesh this material is, so the host can drive Visible per frame from the same
            // array the GL viewport reads. Without it the line above was the only thing that ever set
            // Visible on a character material.
            mat.CharacterSubmeshIndex = slice.SubmeshIndex;

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

    /// <summary>
    /// M777: reorder <paramref name="items"/> per a skin's authored <c>submeshRenderOrder</c> - named
    /// submeshes move to that order (front to back); everything else keeps its original position, placed
    /// after every named one. A stable sort over (listed rank, original index) does exactly that: listed
    /// names by their rank in <paramref name="order"/>, unlisted names by <see cref="int.MaxValue"/> so
    /// they sort after all of them, and ties (unlisted vs. unlisted) keep file order because the sort is
    /// stable. Pulled out of <see cref="Prepare"/> so the rule can be tested on its own, without a shader
    /// cache or a decoded mesh.
    /// </summary>
    public static List<T> ApplySubmeshRenderOrder<T>(
        IReadOnlyList<T> items, Func<T, string> submeshOf, IReadOnlyList<string> order)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        return items
            .Select((item, fileIndex) => (Item: item, Rank: rank.TryGetValue(submeshOf(item), out var r) ? r : int.MaxValue, fileIndex))
            .OrderBy(x => x.Rank).ThenBy(x => x.fileIndex)
            .Select(x => x.Item)
            .ToList();
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

        // M779: which two blobs actually get bound, and under which names. A stand-in slice (usedFallback)
        // does NOT resolve `shader` (still "shaders/skinnedmesh/diffuse_alpha") through generated/ - it
        // draws through the engine's own lit_uber_vs/lit_uber_ps pair instead (DefaultStandInVertexShader/
        // DefaultStandInPixelShader; see their doc comments for why - M780 for the vertex half). `shader` stays the fallback name
        // below only because parameter/feature DEFAULTS are still looked up under it - both pixel shaders
        // declare the same CharacterPerDrawPS cbuffer (kGrassFade, SELF_ILLUMINATION, LIGHTGRID_SCALE, ...),
        // so those defaults still apply to lit_uber_ps.
        string vsFull, psFull, vsPath, psPath;
        ShaderPermutation vsPerm, psPerm;
        DxbcShader vs, ps;
        IReadOnlyDictionary<string, string> vsDefines, psDefines;

        if (usedFallback)
        {
            vsFull = DefaultStandInVertexShader;
            psFull = DefaultStandInPixelShader;
            vsPath = ShaderCacheReader.TocPathFor(vsFull, DxbcStage.Vertex);
            psPath = ShaderCacheReader.TocPathFor(psFull, DxbcStage.Pixel);

            var vsToc = cache.ReadToc(vsPath);
            var psToc = cache.ReadToc(psPath);
            if (vsToc is null || psToc is null)
            {
                scene.Failures.Add($"{b.Name}: the stand-in shader pair is missing from the shader cache");
                return null;
            }

            // Fixed permutations, not resolved from the material: this pair belongs to no material and
            // carries no macros/switches of its own. NUM_BLEND_WEIGHTS=4 matches every League skinned mesh
            // (4 blend weights per vertex); the pixel shader's base permutation authors no defines at all.
            ulong vsKey = ShaderCacheReader.PermutationKey(new[] { "NUM_BLEND_WEIGHTS=4" });
            ulong psKey = ShaderCacheReader.PermutationKey(Array.Empty<string>());
            var vsFound = vsToc.Permutations.FirstOrDefault(p => p.Key == vsKey);
            var psFound = psToc.Permutations.FirstOrDefault(p => p.Key == psKey);
            if (vsFound is null || psFound is null)
            {
                scene.Failures.Add($"{b.Name}: the stand-in shader pair has no cooked base permutation");
                return null;
            }
            vsPerm = vsFound;
            psPerm = psFound;
            vsDefines = new Dictionary<string, string> { ["NUM_BLEND_WEIGHTS"] = "4" };
            psDefines = new Dictionary<string, string>();

            var loadedVs = cache.LoadShader(vsPath, vsPerm.BlobIndex, out var vsErr0);
            var loadedPs = cache.LoadShader(psPath, psPerm.BlobIndex, out var psErr0);
            if (loadedVs is null || loadedPs is null)
            {
                scene.Failures.Add($"{b.Name}: stand-in bytecode would not load ({(loadedVs is null ? vsErr0 : psErr0)})");
                return null;
            }
            vs = loadedVs;
            ps = loadedPs;
        }
        else
        {
            vsFull = psFull = "assets/shaders/generated/" + shader!.Trim('/');
            vsPath = ShaderCacheReader.TocPathFor(vsFull, DxbcStage.Vertex);
            psPath = ShaderCacheReader.TocPathFor(psFull, DxbcStage.Pixel);

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

            var vsFound = ShaderCacheReader.ResolvePermutation(vsToc, b.Macros, b.Switches, feat, swDef, out var vwhy);
            var psFound = ShaderCacheReader.ResolvePermutation(psToc, b.Macros, b.Switches, feat, swDef, out var pwhy);
            if (vsFound is null || psFound is null)
            {
                scene.Failures.Add($"{b.Name}: no cooked permutation ({(vsFound is null ? vwhy : pwhy)})");
                return null;
            }
            vsPerm = vsFound;
            psPerm = psFound;
            vsDefines = psDefines = b.Macros;

            var loadedVs = cache.LoadShader(vsPath, vsPerm.BlobIndex, out var vsErr1);
            var loadedPs = cache.LoadShader(psPath, psPerm.BlobIndex, out var psErr1);
            if (loadedVs is null || loadedPs is null)
            {
                scene.Failures.Add($"{b.Name}: bytecode would not load ({(loadedVs is null ? vsErr1 : psErr1)})");
                return null;
            }
            vs = loadedVs;
            ps = loadedPs;
        }

        // M790: one texture per slot, and a repeated sampler's FIRST copy takes it (the map path's rule, from
        // the same helper). This loop used to replace, so the LAST copy won.
        var textures = Dx11SceneBuilder.MaterialTextures(b, sampler => ResolveTextureTarget(sampler, ps, vs));

        if (usedFallback)
        {
            // M779: lit_uber_ps reads EMISSIVE_MAP__TX as a lighting bypass (sampling white drives the
            // term fully self-lit), and the renderer's fallback for an unbound texture is a 1x1 WHITE SRV -
            // so leaving this slot unbound would draw every stand-in submesh full-bright regardless of the
            // scene's actual lights. The ordinary slot loop above never reaches it: an authored
            // "emissiveTexture" field does not resolve against lit_uber_ps by name (only the literal
            // "texture" sampler gets the generic diffuse fallback - see ResolveTextureTarget), so it has to
            // be bound explicitly here - to the skin's own emissive texture when authored, else black.
            string? emissivePath = b.Emissive?.Path;
            textures.RemoveAll(t => t.Target.Equals("EMISSIVE_MAP__TX", StringComparison.OrdinalIgnoreCase));
            textures.Add(("EMISSIVE_MAP__TX",
                !string.IsNullOrWhiteSpace(emissivePath) ? emissivePath!.ToLowerInvariant() : BlackEmissiveKey));
        }

        // M790: likewise one value per name, the FIRST copy.
        var parameters = Dx11SceneBuilder.MaterialParameters(b);
        var authored = new HashSet<string>(parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

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
                var bound = new[] { x, y, z, w };
                parameters.Add((name, bound));
                scene.SkinBoundParameters.Add((name, bound));   // M825
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

        return new CharacterSlice(
            sub.Material, b.Name, sub.StartIndex, sub.IndexCount,
            vs, ps,
            new ShaderDescription(vsFull, DxbcStage.Vertex, vsPerm.Key, vsPerm.BlobIndex, vsDefines, vs),
            new ShaderDescription(psFull, DxbcStage.Pixel, psPerm.Key, psPerm.BlobIndex, psDefines, ps),
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
