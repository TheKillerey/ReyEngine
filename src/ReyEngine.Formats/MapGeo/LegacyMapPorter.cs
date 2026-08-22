using System.Numerics;
using System.Security.Cryptography;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Memory;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.MapGeo;

public enum LegacyMaterialRole { Normal, Decal, Grass, FourBlendTerrain }

public sealed record LegacyPortShaderOptions(
    string NormalShader,
    string DecalShader,
    string GrassShader,
    string TerrainShader)
{
    public static LegacyPortShaderOptions Defaults { get; } = new(
        LegacyMapPorter.NormalShader,
        LegacyMapPorter.DecalShader,
        LegacyMapPorter.GrassShader,
        LegacyMapPorter.TerrainShader);
}

/// <summary>Controls which content from the modern destination container is retained underneath a
/// legacy NVR/WGEO import. Render-region meshes are structural and are always retained.</summary>
public sealed record LegacyPortCleanupOptions(
    bool RemoveOriginalMeshes,
    bool RemoveOriginalBushes,
    bool RemoveUnusedOriginalMaterials,
    bool RemoveOriginalParticles,
    bool RemoveOriginalProps,
    bool RemoveOriginalSounds,
    bool RemoveOriginalProbes)
{
    public static LegacyPortCleanupOptions FullReplacement { get; } = new(true, true, true, true, true, true, true);
    public static LegacyPortCleanupOptions KeepDestinationSupport { get; } = new(true, false, true, false, false, false, false);
    public static LegacyPortCleanupOptions KeepEverything { get; } = new(false, false, false, false, false, false, false);
}

public sealed record LegacyMeshCleanupResult(
    byte[] MapGeoBytes,
    int RemovedMeshCount,
    int RemovedBushMeshCount,
    int RetainedOriginalMeshCount,
    int PreservedRenderRegionMeshCount,
    int RemovedSubmeshCount = 0);

public sealed record LegacyTextureCopy(string SourcePath, string TargetPath, byte[] Bytes);

public sealed record LegacyMaterialPlan(
    string Name,
    LegacyMaterialRole Role,
    string Shader,
    IReadOnlyDictionary<string, string> Samplers,
    IReadOnlyDictionary<string, Vector4> Parameters,
    IReadOnlyDictionary<string, bool> Switches,
    IReadOnlyDictionary<string, bool> Macros,
    bool BlendEnabled = false,
    int? SourceBlendFactor = null,
    int? DestinationBlendFactor = null,
    int? SamplerAddressMode = null);

public sealed record LegacyMapPortResult(
    byte[] MapGeoBytes,
    IReadOnlyList<LegacyTextureCopy> Textures,
    IReadOnlyList<LegacyMaterialPlan> Materials,
    string SourceFile,
    string SourceFormat,
    int SourceMeshCount,
    int ImportedMeshCount,
    int RemovedBaseMeshCount,
    int PreservedRenderRegionMeshCount,
    int SourceMaterialCount,
    IReadOnlyList<string> Warnings,
    int DestinationMeshCount = 0,
    /// <summary>M474: imported meshes that carried a SECOND UV set from the source into Texcoord7.
    /// Reported because it is legitimately small and the number invites a wrong conclusion: measured on
    /// two real rooms, only 82 of 2,351 (Map10) and 647 of 3,017 (Map8) source meshes declare the channel
    /// at all - roughly 0.3% and 2% of vertices. It is the four-blend terrain's mask UV, not a map-wide
    /// lightmap unwrap, so "most meshes still have no Texcoord7" is the source being sparse rather than
    /// the port dropping anything.</summary>
    int ImportedMeshesWithSecondUv = 0);

/// <summary>
/// Converts Riot's pre-mapgeo NVR/WGEO environments into a modern mapgeo container. The destination
/// remains authoritative: its v18 render-region meshes and non-geometry tail are retained. Imported
/// geometry is grouped by effective texture set and split only at the u16 vertex limit, which collapses
/// thousands of old NVR draw objects without duplicating materials.
/// </summary>
public static class LegacyMapPorter
{
    /// <summary>Ordinary surfaces whose diffuse alpha is a genuine CUTOUT mask. See
    /// <see cref="SolidShader"/> for everything else and why the distinction matters.</summary>
    public const string NormalShader = "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest";

    /// <summary>
    /// M528: ordinary surfaces whose alpha is NOT a cutout mask.
    ///
    /// <para>M338 gave every normal surface the alpha-tested shader with a 0.35 cutoff. That is right
    /// for a cutout and destructive for anything else, because legacy League art routinely packs
    /// SPECULAR/GLOSS into the diffuse alpha channel - the test then discards the surface. Measured on
    /// a real ported map: 86 textures, only 14 genuine cutouts, and an 0.35 cutoff eats 40-72% of the
    /// 20 gradient ones. That is ground with holes punched through it.</para>
    ///
    /// <para>Riot's own shipped Map453 splits the same way round: 57 DefaultEnv_Flat against 31
    /// DefaultEnv_Flat_AlphaTest.</para>
    /// </summary>
    public const string SolidShader = "Shaders/StaticMesh/DefaultEnv_Flat";
    public const string DecalShader = "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest";
    public const string GrassShader = "Shaders/StaticMesh/VertexDeform";

    /// <summary>M529: the NVR material record's declared type. 1 is a decal and 2 is grass; 0 is an
    /// ordinary surface. These are read from the file rather than inferred from the material name.</summary>
    public const int NvrDecalType = 1;
    public const int NvrGrassType = 2;
    public const string TerrainShader = "Shaders/StaticMesh/4TextureBlend_WorldProjected";

    /// <summary>
    /// M490: the sampler address value the DECAL role authors, so a ported decal stops at its edge instead
    /// of repeating across the surface it is stamped on.
    ///
    /// <para>1 = Clamp in Riot's enum, which is Unity's TextureWrapMode ordering rather than D3D11's:
    /// 0 = Wrap and 2 = Mirror were read off Riot's own NAMED shared samplers (M184), leaving 1. Censused
    /// over the nine shipped map WADs (16,904 samplers) the clamp triple addressU=1/addressV=1/addressW=1
    /// is authored 2,870 times, so this is a shipped form, not an invented one.</para>
    ///
    /// <para><b>Riot does NOT clamp their own decals, and that is deliberate on their side.</b> Every one of
    /// the 103 samplers in the shipped Map453 jade_container.materials.bin carries addressW=1 and NOTHING
    /// else, and 2 of its 27 decal meshes run u from -0.01 to 2.99 - a road strip meant to tile three times.
    /// Riot's remaining 25 decals simply keep uv0 inside the unit square, so wrap and clamp look identical
    /// on them and the question never arises. Legacy NVR decals do not: their uv0 leaves the square, which
    /// is the repeat the reporter saw. Clamping is therefore the right default HERE and would be the wrong
    /// one for a decal authored to tile - which is why it is a named constant on the decal role rather than
    /// a blanket rule over every ported material.</para>
    /// </summary>
    public const int ClampAddressMode = 1;

    /// <summary>M500: the identity value of DefaultEnv_Flat's TintColor overlay — Riot's own shaders.bin
    /// default, 128/255. See <see cref="MaterialParameters"/> for why 1.0 is not neutral.</summary>
    public const float NeutralTint = 0.5019608f;
    /// <summary>
    /// Default world-space correction applied to imported WGEO/NVR geometry.
    ///
    /// <para><b>M473: (+1000.834, -51.318, +499.388)</b>, measured by the user against a ported map and
    /// reported as the correct full fix. It REPLACES the previous (600.406, -66.972, 293.744) — which was
    /// itself two stacked passes, an initial (+473.120, -66.972, +237.756) plus a (+127.286, 0, +55.988)
    /// refinement — rather than stacking on top of it.</para>
    ///
    /// <para><b>That replace-vs-add reading is an assumption, and it is stated because it is not
    /// provable from here.</b> The screenshots it came from are of MapGeo_Instance_458, which is not in
    /// any map in this workspace, so nothing on disk can say whether the old state those numbers were
    /// measured against already had the previous constant applied. "The port had a value; these are the
    /// correct values" reads as a replacement, and the two differ by ~600 units on X, so a wrong reading
    /// is obvious the first time a port is checked rather than subtly wrong forever. It is also no longer
    /// permanent either way: the value is now a PARAMETER (see the overload of
    /// <see cref="ApplyImportedPositionCorrection"/>) and editable in the port window, so correcting it
    /// costs a text box rather than a rebuild.</para>
    /// </summary>
    public static readonly Vector3 LegacyPositionCorrection = new(1000.834f, -51.318f, 499.388f);

    /// <summary>The pre-M473 value, kept so a map ported by an older build can be reconciled: the
    /// difference between the two is exactly what such a map is out by.</summary>
    public static readonly Vector3 LegacyPositionCorrectionPreM473 = new(600.406f, -66.972f, 293.744f);
    private const int MaxVertices = 65535;

    /// <summary>What the shader cache says about authoring a macro on a given shader.</summary>
    public enum MacroSupport
    {
        /// <summary>No shader cache to ask — fall back to the role rule.</summary>
        Unknown,
        /// <summary>Declared as a permutation axis AND cooked at this value. Safe to author.</summary>
        Cooked,
        /// <summary>Not a permutation axis for this shader. The client ignores the macro, so authoring it is
        /// INERT — harmless to the game but a lie to every later reader.</summary>
        NotDeclared,
        /// <summary>Declared, but this value was never cooked. Authoring it is FATAL (M486).</summary>
        NotCooked,
    }

    /// <summary>
    /// Whether the default shader for a generated role has a cooked no-lightmap permutation.
    ///
    /// <para>M502 measured all four shaders the porter uses, and the old role list was right for the wrong
    /// reason on one of them:</para>
    /// <list type="table">
    ///   <item><term>VertexDeform (Grass)</term><description>declares the axis, cooked — correct to author</description></item>
    ///   <item><term>4TextureBlend_WorldProjected (Terrain)</term><description>does NOT declare the axis, so
    ///   the macro is ignored by the client. Authoring it was pointless, and actively misleading: the
    ///   material claims "no baked lighting" while the shader lights the surface anyway, which is exactly
    ///   what made the DX11 sun investigation hard to read.</description></item>
    ///   <item><term>DefaultEnv_Flat_AlphaTest (Normal/Decal)</term><description>declares the axis but never
    ///   cooked it — authoring it is the M486 crash. Already excluded, now for a measured reason.</description></item>
    /// </list>
    ///
    /// <para>Kept as the FALLBACK for when no shader cache is available (no game directory, or the Formats
    /// tests), so behaviour without an install is unchanged.</para>
    /// </summary>
    public static bool UsesNoBakedLightingByDefault(LegacyMaterialRole role) =>
        role is LegacyMaterialRole.Grass or LegacyMaterialRole.FourBlendTerrain;

    /// <summary>M502: should this role author NO_BAKED_LIGHTING, given what the shader cache says about the
    /// shader it will actually use? <paramref name="ask"/> is supplied by the app, which owns the game
    /// directory; null falls back to the role rule above.</summary>
    public static bool ShouldAuthorNoBakedLighting(LegacyMaterialRole role, string shader,
        Func<string, string, MacroSupport>? ask, out string? reason)
    {
        reason = null;
        var support = ask?.Invoke(shader, "NO_BAKED_LIGHTING") ?? MacroSupport.Unknown;
        switch (support)
        {
            case MacroSupport.Cooked:
                return true;
            case MacroSupport.NotDeclared:
                reason = $"{role}: {Short(shader)} does not declare NO_BAKED_LIGHTING as a permutation axis, "
                       + "so the client would ignore it — omitted rather than written as a claim the shader "
                       + "does not honour.";
                return false;
            case MacroSupport.NotCooked:
                reason = $"{role}: {Short(shader)} declares NO_BAKED_LIGHTING but the game ships no cooked "
                       + "permutation for it — writing it would make the client fail to compile the shader.";
                return false;
            default:
                return UsesNoBakedLightingByDefault(role);
        }

        static string Short(string s) { int i = s.LastIndexOf('/'); return i >= 0 ? s[(i + 1)..] : s; }
    }
    private static readonly IReadOnlySet<string> JadeContainerBushMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Map453's gameplay brush is intentionally DefaultEnv_Flat, not VertexDeform. Do not broaden
        // this to Jade_Foliage_*: leaves, flowers, mushrooms and vines are ordinary decorative geometry.
        "Maps/KitPieces/Jade/Base/Materials/Default/Jade_Foliage_Grass_AA_MAT",
    };

    /// <summary>Some destinations do not identify gameplay bushes by shader. A non-null result replaces
    /// shader-derived classification for that exact map; it is never applied globally.</summary>
    public static IReadOnlySet<string>? MapSpecificBushMaterials(string? destinationMapGeoPath)
    {
        if (string.IsNullOrWhiteSpace(destinationMapGeoPath)) return null;
        string path = destinationMapGeoPath.Replace('\\', '/').TrimStart('/');
        return path.EndsWith("data/maps/mapgeometry/map453/jade_container.mapgeo", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("maps/mapgeometry/map453/jade_container.mapgeo", StringComparison.OrdinalIgnoreCase)
            ? JadeContainerBushMaterials
            : null;
    }

    /// <param name="macroSupport">M502: asks the shader cache whether a macro is safe on a given shader.
    /// Null keeps the pre-M502 role rule, which is what happens with no game directory.</param>
    /// <param name="note">Receives one line per macro the porter declined to author, and why.</param>
    public static LegacyMapPortResult ApplyShaderOptions(LegacyMapPortResult result,
        LegacyPortShaderOptions options,
        Func<string, string, MacroSupport>? macroSupport = null, Action<string>? note = null,
        Func<string, LegacyAlphaKind>? alphaKind = null,
        IReadOnlyDictionary<string, string>? explicitShaders = null)
    {
        // Report each declined change ONCE per role rather than once per material: a port generates
        // dozens of materials per role and the same sentence eighty times is noise, not information.
        var reported = new HashSet<string>(StringComparer.Ordinal);

        // M528: an ordinary surface is only alpha-tested when its diffuse alpha is a genuine cutout
        // mask. Without the classifier this keeps the pre-M528 behaviour, so a caller with no way to
        // read the textures is no worse off than before.
        string ShaderFor(LegacyMaterialPlan material)
        {
            // M534: a shader the USER picked outrank everything below, including the alpha classifier.
            // Without this there was no way to say "I want the default everywhere": the classifier only
            // stands aside when the chosen Normal shader DIFFERS from the default, so re-affirming the
            // default was indistinguishable from not choosing at all.
            //
            // Routed through here rather than patched on afterwards, because the old overlay replaced only
            // the shader string and left Parameters and Macros derived from the shader it replaced - a
            // material moved onto AlphaTest never got its AlphaTestValue.
            if (explicitShaders is not null
                && explicitShaders.TryGetValue(material.Name, out string? forced)
                && !string.IsNullOrWhiteSpace(forced))
                return forced;

            switch (material.Role)
            {
                case LegacyMaterialRole.Decal: return options.DecalShader;
                case LegacyMaterialRole.Grass: return options.GrassShader;
                case LegacyMaterialRole.FourBlendTerrain: return options.TerrainShader;
            }

            if (alphaKind is null || options.NormalShader != NormalShader) return options.NormalShader;
            // M533: the plan's diffuse is keyed "__diffuse__", not by its sampler name - BuildMaterialPlans
            // only spells real sampler names for four-blend terrain. Asking for "DiffuseTexture" therefore
            // MISSED on every ordinary material, so M528's alpha classifier never ran once: every Normal
            // material took the alpha-tested shader regardless of what its alpha actually held. Both keys
            // are tried because terrain plans really do use the sampler names.
            if (!material.Samplers.TryGetValue("__diffuse__", out var texture)
                && !material.Samplers.TryGetValue("DiffuseTexture", out texture)) return options.NormalShader;

            var kind = alphaKind(texture);
            if (kind == LegacyAlphaKind.Cutout) return options.NormalShader;

            if (reported.Add("alpha|" + kind))
                note?.Invoke(kind == LegacyAlphaKind.Gradient
                    ? $"Alpha test dropped on {kind} surfaces: their diffuse alpha is a gradient, which in "
                      + "legacy art is gloss rather than transparency - testing it would discard the surface."
                    : $"Alpha test dropped on {kind} surfaces: nothing in their alpha to test against.");
            return SolidShader;
        }

        return result with
        {
            Materials = result.Materials.Select(material =>
            {
                string shader = ShaderFor(material);
                bool noBake = ShouldAuthorNoBakedLighting(material.Role, shader, macroSupport, out var why);
                if (why is not null && reported.Add(material.Role + "|" + shader)) note?.Invoke(why);

                return material with
                {
                    Shader = shader,
                    Parameters = MaterialParameters(material.Role, material.Parameters, shader),
                    Macros = noBake
                        ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["NO_BAKED_LIGHTING"] = true }
                        : new Dictionary<string, bool>(),
                };
            }).ToList(),
        };
    }

    /// <summary>
    /// Neutral authored values for generated legacy materials. Extra names are harmless because
    /// CreateFromShader only writes parameters actually declared by the selected shader.
    ///
    /// <para><b>M500: TintColor's neutral value is 0.5, not 1.0.</b> DefaultEnv_Flat applies it as a
    /// per-channel OVERLAY blend against the diffuse — disassembled from defaultenv_flat.ps.dx11 blob 141,
    /// lines 159-168: <c>1 - 2*(1-d)*(1-T)</c> above 0.5 and <c>2*d*T</c> below — and overlay's identity is
    /// 0.5, so 1.0 is a ~2x BRIGHTENER rather than "no tint". Measured over this map's real textures at
    /// tint 1.0: 2.000x on base_stone_steps, 1.882x on shrine with 6% of channels clipped to pure white.
    /// Riot's own declared default in shaders.bin is 0.5019608 (=128/255), and Riot's shipped
    /// Jade_Foliage_Grass_AA_MAT authors exactly (0.5, 0.5, 0.5, 1) across 292 meshes in this very file.
    /// ReyEngine's GL shader has always documented the same convention (ViewportMeshRenderer.cs:445-453,
    /// "0.5 = neutral (identity)").</para>
    ///
    /// <para>M347 introduced Vector4.One while correctly diagnosing that copying shaders.bin's literal
    /// zero made DX11 render every import black. Zero was indeed wrong; the correction overshot to 1.0
    /// instead of to the actual identity. The symptom hid on the GL viewport, which drops TintColor
    /// entirely for opaque diffuse-textured materials (MaterialProfile.cs:268-272), so only DX11 ever
    /// showed the doubled albedo.</para>
    ///
    /// <para>Only .xyz is read — ps blob 141 line 167 takes output alpha from the diffuse texture — so .w
    /// is left at 1 and is inert either way.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, Vector4> MaterialParameters(
        LegacyMaterialRole role, IReadOnlyDictionary<string, Vector4>? existing = null,
        string? shader = null)
    {
        var parameters = existing is null
            ? new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Vector4>(existing, StringComparer.OrdinalIgnoreCase);
        parameters["TintColor"] = new Vector4(NeutralTint, NeutralTint, NeutralTint, 1f);
        parameters["Tint"] = new Vector4(NeutralTint, NeutralTint, NeutralTint, 1f);
        if (role == LegacyMaterialRole.FourBlendTerrain)
            parameters["WS_Multiplier"] = new Vector4(0.01f, 0, 0, 0);
        else if (shader is null || shader.Contains("AlphaTest", StringComparison.OrdinalIgnoreCase))
            // M528: a cutoff on a shader that does not test is inert, but it also lies to anyone reading
            // the material about what the surface does.
            //
            // M543: a decal takes 0.3, NOT a near-zero floor. DefaultEnv_Flat_AlphaTest is an alpha-TEST
            // shader: the client runs it in the OPAQUE pass, where it writes depth. At 0.3 a transparent
            // texel is DISCARDED - never shaded, never writes depth - and the ground composites through.
            // At 0.005 nothing is discarded, so every transparent texel is still shaded (black, over
            // whatever has drawn so far) AND still stamps depth, which then rejects the very ground the
            // decal was meant to sit on. That is the reported "the black is just the transparency, and it
            // is something between the ground and the decal".
            //
            // Neither of our own viewports shows it: both derive WritesDepth from the render mode and
            // force it off for a transparent material (M279), so the depth stamp never happens here. The
            // client derives it from the shader class instead. That divergence is exactly why this looked
            // correct in the editor and wrong in game.
            //
            // 0.3 is Riot's own value on 3,091 of the 3,347 shipped materials that are
            // DefaultEnv_Flat_AlphaTest AND blended - the directly comparable set - against 170 on 0.005.
            parameters["AlphaTestValue"] = new Vector4(
                role == LegacyMaterialRole.Decal ? 0.3f : 0.35f, 0, 0, 0);
        return parameters;
    }

    /// <summary>Translate only the newly imported legacy meshes into the modern map coordinate frame.
    /// Destination meshes occupy the prefix recorded by <see cref="LegacyMapPortResult.DestinationMeshCount"/>;
    /// render regions, retained bushes and every bin placement are therefore untouched.</summary>
    /// <param name="correction">World-space translation for the imported prefix. Defaults to
    /// <see cref="LegacyPositionCorrection"/>. M473 made this a parameter because it had been a hardcoded
    /// constant that only a rebuild could change, which is why an alignment that was 400 units out had no
    /// answer short of editing source.</param>
    public static LegacyMapPortResult ApplyImportedPositionCorrection(
        LegacyMapPortResult result, Vector3? correction = null)
    {
        Vector3 shift = correction ?? LegacyPositionCorrection;
        if (!MapGeoBinary.TryReadEditable(result.MapGeoBytes, out var map))
            throw new InvalidDataException("The combined legacy mapgeo could not be reopened for position correction.");
        int firstImported = Math.Clamp(result.DestinationMeshCount, 0, map.Meshes.Count);
        if (firstImported == map.Meshes.Count) return result;
        Matrix4x4 translation = Matrix4x4.CreateTranslation(shift);
        for (int i = firstImported; i < map.Meshes.Count; i++)
        {
            var mesh = map.Meshes[i];
            mesh.Transform *= translation;
            mesh.BoundsMin += shift;
            mesh.BoundsMax += shift;
        }
        byte[] corrected = map.Write();
        corrected = MapGeoWriter.WriteWithRegeneratedBucketGrids(corrected,
            MapGeoDecoder.Decode(corrected), targetBucketSize: 1000f);
        return result with { MapGeoBytes = corrected };
    }

    /// <summary>Apply the user's destination cleanup after the legacy geometry has been converted. The
    /// first <see cref="LegacyMapPortResult.DestinationMeshCount"/> records are the original destination;
    /// imported records follow them. This avoids converting hundreds of textures a second time when the
    /// cleanup choices are changed in the review window.</summary>
    public static LegacyMeshCleanupResult ApplyMeshCleanup(LegacyMapPortResult result,
        LegacyPortCleanupOptions options, IReadOnlySet<string>? bushMaterials = null)
    {
        if (!MapGeoBinary.TryReadEditable(result.MapGeoBytes, out var map))
            throw new InvalidDataException("The combined legacy mapgeo could not be reopened for destination cleanup.");

        int originalCount = Math.Clamp(result.DestinationMeshCount, 0, map.Meshes.Count);
        bushMaterials ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retained = new List<MapGeoBinary.Mesh>(map.Meshes.Count);
        int removedMeshes = 0, removedBushes = 0, removedSubmeshes = 0, retainedOriginal = 0, renderRegions = 0;
        for (int i = 0; i < map.Meshes.Count; i++)
        {
            var mesh = map.Meshes[i];
            if (i >= originalCount) { retained.Add(mesh); continue; }
            if (mesh.HasRegionHash && mesh.RegionHash != 0)
            {
                retained.Add(mesh); renderRegions++; continue;
            }

            // A re-port must never layer a new import on top of an earlier one. In particular, generated
            // Grass_* materials contain the word "grass" and used to be mistaken for destination bushes
            // when bush deletion was disabled, duplicating those meshes on every pass.
            if (IsPreviousLegacyImport(mesh)) { removedMeshes++; continue; }

            // Riot frequently batches unlike materials into one mapgeo mesh. Jade, for example, combines
            // Order terrain and VertexDeform foliage in the same record. Classifying at mesh level retained
            // the terrain merely because another submesh was a bush. Filter the draw ranges independently;
            // unused indices/vertices may remain in the shared buffers but cannot render.
            if (mesh.Submeshes.Count == 0)
            {
                if (options.RemoveOriginalMeshes) removedMeshes++;
                else { retained.Add(mesh); retainedOriginal++; }
                continue;
            }
            var keptSubmeshes = mesh.Submeshes.Where(submesh =>
            {
                bool bush = bushMaterials.Contains(submesh.Material);
                return !(bush ? options.RemoveOriginalBushes : options.RemoveOriginalMeshes);
            }).ToList();
            removedSubmeshes += mesh.Submeshes.Count - keptSubmeshes.Count;
            if (keptSubmeshes.Count > 0)
            {
                mesh.Submeshes = keptSubmeshes;
                retained.Add(mesh);
                retainedOriginal++;
                continue;
            }

            bool wasBushOnly = mesh.Submeshes.Count > 0
                && mesh.Submeshes.All(submesh => bushMaterials.Contains(submesh.Material));
            if (wasBushOnly) removedBushes++; else removedMeshes++;
        }

        map.Meshes = retained;
        map.Compact();
        byte[] cleaned = map.Write();
        cleaned = MapGeoWriter.WriteWithRegeneratedBucketGrids(cleaned, MapGeoDecoder.Decode(cleaned), targetBucketSize: 1000f);
        var verified = MapGeoDecoder.Decode(cleaned);
        int verifiedRegions = verified.Meshes.Count(mesh => mesh.RegionHash != 0);
        if (verifiedRegions != renderRegions)
            throw new InvalidDataException($"Render-region verification failed: retained {verifiedRegions} of {renderRegions} meshes.");
        return new LegacyMeshCleanupResult(cleaned, removedMeshes, removedBushes, retainedOriginal, renderRegions, removedSubmeshes);
    }

    /// <summary>Count destination bushes using the same material-aware classification used by cleanup.</summary>
    public static int CountBushMeshes(byte[] mapGeoBytes, IReadOnlySet<string>? bushMaterials = null)
    {
        if (!MapGeoBinary.TryReadEditable(mapGeoBytes, out var map)) return 0;
        bushMaterials ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return map.Meshes.Count(mesh => (!mesh.HasRegionHash || mesh.RegionHash == 0) && !IsPreviousLegacyImport(mesh)
            && mesh.Submeshes.Any(submesh => bushMaterials.Contains(submesh.Material)));
    }

    public static int CountPreviousImportedMeshes(byte[] mapGeoBytes)
    {
        if (!MapGeoBinary.TryReadEditable(mapGeoBytes, out var map)) return 0;
        return map.Meshes.Count(mesh => (!mesh.HasRegionHash || mesh.RegionHash == 0) && IsPreviousLegacyImport(mesh));
    }

    /// <summary>The material-name prefix every generated legacy material carries. It is the only durable
    /// marker of "this geometry came from a WGEO/NVR import" once the port has been written and reopened —
    /// the mesh-count prefix that <see cref="ApplyImportedPositionCorrection"/> uses exists only inside a
    /// single port run.</summary>
    public const string LegacyMaterialPrefix = "LegacyPort/";

    public static bool IsLegacyImportMaterial(string? material) =>
        material is not null && material.StartsWith(LegacyMaterialPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsPreviousLegacyImport(MapGeoBinary.Mesh mesh) =>
        mesh.Submeshes.Any(submesh => IsLegacyImportMaterial(submesh.Material));

    /// <summary>
    /// M472: the imported-legacy meshes of an ALREADY PORTED map, so the import can be nudged as a group
    /// long after the port ran.
    ///
    /// <para><see cref="LegacyPositionCorrection"/> is a constant that was measured in two passes and then
    /// BAKED into the geometry at port time — it is not stored anywhere and cannot be re-derived from the
    /// result. So "the alignment is 30 units out" had no answer short of re-porting the whole map, or
    /// hand-editing every imported mesh through a per-mesh transform box that only takes absolute world
    /// positions. This is the set those nudges have to apply to.</para>
    ///
    /// <para>Identified by material prefix rather than by mesh order: a re-port, a cleanup pass or a mesh
    /// added later all change the ordering, and a group nudge that silently caught destination geometry
    /// would move the part of the map that was already correct.</para>
    /// </summary>
    public static IReadOnlyList<MapGeoMesh> ImportedMeshes(MapGeoAsset map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var imported = new HashSet<int>();
        foreach (var g in map.Groups)
            if (g.MeshIndex >= 0 && IsLegacyImportMaterial(g.Material)) imported.Add(g.MeshIndex);
        return map.Meshes.Where(m => imported.Contains(m.Index)).ToList();
    }

    public static LegacyMapPortResult Port(string sourceRoot, byte[] destinationMapGeo,
        string? destinationMapGeoPath = null)
    {
        string source = FindSingleSource(sourceRoot);
        byte[] sourceBytes = File.ReadAllBytes(source);
        bool isWgeo = sourceBytes.AsSpan().StartsWith("WGEO"u8);
        bool isNvr = sourceBytes.Length >= 3 && sourceBytes[0] == 'N' && sourceBytes[1] == 'V' && sourceBytes[2] == 'R';
        if (!isWgeo && !isNvr) throw new InvalidDataException("The selected room file is neither WGEO nor NVR.");

        if (!MapGeoBinary.TryReadEditable(destinationMapGeo, out var target))
            throw new InvalidDataException("The destination mapgeo is not byte-exact editable; the legacy port was not applied.");
        if (target.Version < 17)
            throw new InvalidDataException("The legacy porter currently requires a mapgeo v17 or v18 destination.");

        var textureIndex = new LegacyAssetIndex(Path.GetDirectoryName(source)!);
        var nvrMaterials = isNvr ? ParseNvrMaterials(sourceBytes) : new(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        using var stream = new MemoryStream(sourceBytes, writable: false);
        using var environment = isWgeo ? WorldGeometry.Load(stream) : SimpleEnvironment.Load(stream);

        string slug = Slug(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(source))!) ?? "legacy");
        var textureCopies = new Dictionary<string, LegacyTextureCopy>(StringComparer.OrdinalIgnoreCase);
        var textureTargetsByContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var decodedTextures = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);

        string? PortTexture(string? reference, bool required = true)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            if (!textureIndex.TryResolve(reference, out string file))
            {
                if (required) warnings.Add($"Texture '{reference}' was not found under {Path.GetDirectoryName(source)}.");
                return null;
            }
            byte[] bytes = File.ReadAllBytes(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".dds")
            {
                // Modern map materials should not keep legacy container types. DXT1/DXT5 blocks can be
                // moved losslessly into TEX after reversing their mip order; unusual DDS formats use
                // the RGBA/BC3 fallback. Existing TEX inputs remain byte-exact.
                if (!TexWriter.TryWrapDds(bytes, out var converted))
                    converted = TexWriter.Write(TextureDecoder.Decode(bytes), TexFormat.Bc3, mipmaps: true);
                bytes = converted;
                ext = ".tex";
            }
            else if (ext == ".tga")
            {
                bytes = TexWriter.Write(TextureDecoder.Decode(bytes), TexFormat.Bc3, mipmaps: true);
                ext = ".tex";
            }
            string digest = Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 6)).ToLowerInvariant();
            if (!textureTargetsByContent.TryGetValue(digest, out string? targetPath))
            {
                string stem = Slug(Path.GetFileNameWithoutExtension(file));
                targetPath = $"assets/maps/legacyimport/{slug}/textures/{stem}_{digest}{ext}";
                textureTargetsByContent[digest] = targetPath;
                textureCopies[targetPath] = new LegacyTextureCopy(file, targetPath, bytes);
            }
            return targetPath;
        }

        TextureImage? DecodeTexture(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference) || !textureIndex.TryResolve(reference, out string file)) return null;
            if (decodedTextures.TryGetValue(file, out var cached)) return cached;
            try { return decodedTextures[file] = TextureDecoder.Decode(File.ReadAllBytes(file)); }
            catch (Exception ex) { warnings.Add($"Could not inspect texture '{reference}': {ex.Message}"); return decodedTextures[file] = null; }
        }

        var accumulators = new Dictionary<SurfaceKey, List<MeshAccumulator>>();
        int sourceMaterialCount = 0;
        int rejectedTriangles = 0;
        var sourceMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terrainBlendReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int meshIndex = 0;
        foreach (var mesh in environment.Meshes)
        {
            try
            {
                var view = mesh.VerticesView;
                int vertexCount = view.VertexCount;
                if (vertexCount == 0 || mesh.Indices.Count < 3) { meshIndex++; continue; }

                var pos = ReadVector3(view.GetAccessor(ElementName.Position), vertexCount);
                var normals = view.TryGetAccessor(ElementName.Normal, out var nAcc) ? ReadVector3(nAcc, vertexCount) : null;
                var uv0 = view.TryGetAccessor(ElementName.Texcoord0, out var uvAcc) ? ReadVector2(uvAcc, vertexCount) : null;
                var uv7 = view.TryGetAccessor(ElementName.Texcoord7, out var uv7Acc) ? ReadVector2(uv7Acc, vertexCount) : null;
                var transform = mesh.Transform;
                var normalMatrix = Matrix4x4.Invert(transform, out var inverse)
                    ? Matrix4x4.Transpose(inverse) : transform;
                var transformedPositions = pos.Select(p => Vector3.Transform(p, transform)).Where(Reasonable).ToArray();
                Vector3 meshPivot = transformedPositions.Length == 0 ? transform.Translation : new(
                    (transformedPositions.Min(p => p.X) + transformedPositions.Max(p => p.X)) * 0.5f,
                    transformedPositions.Min(p => p.Y),
                    (transformedPositions.Min(p => p.Z) + transformedPositions.Max(p => p.Z)) * 0.5f);

                var submeshes = mesh.Submeshes.Count > 0
                    ? mesh.Submeshes.Select(s => (s.Material ?? "", s.StartIndex, s.IndexCount)).ToList()
                    : new List<(string, int, int)> { ("", 0, mesh.Indices.Count) };

                foreach (var (materialName, startIndex, indexCount) in submeshes)
                {
                    sourceMaterials.Add(materialName);
                    var raw = nvrMaterials.GetValueOrDefault(StripNvrPrefix(materialName));
                    string? baseRef = raw?.Base;
                    if (string.IsNullOrWhiteSpace(baseRef)) baseRef = mesh.StationaryLight.Texture;
                    string? baseTarget = PortTexture(baseRef);
                    if (baseTarget is null) continue;

                    bool fourBlend = raw is { Blend.Length: > 0, Color1.Length: > 0, Color2.Length: > 0, Color3.Length: > 0 }
                                     && uv7 is not null && DecodeTexture(raw.Blend) is not null;
                    // M529: the NVR record DECLARES what the material is, so ask it before guessing.
                    // The name heuristics below are the fallback for a material the table does not
                    // carry - and they were badly wrong on their own: LooksLikeDecal matches the
                    // substring "decal", which catches 3 of Map2's 18 declared decals. The other 15 -
                    // base_chasm1/2/3, order_seam, new_stone_road, turret_stoneBase, the tile-floor
                    // marks - came through as ordinary surfaces and were rendered as opaque geometry
                    // over the ground instead of as decals on it.
                    bool cutout = !fourBlend && HasCutoutAlpha(DecodeTexture(baseRef));
                    LegacyMaterialRole role =
                        fourBlend ? LegacyMaterialRole.FourBlendTerrain
                        : raw?.Type == NvrDecalType ? LegacyMaterialRole.Decal
                        : raw?.Type == NvrGrassType ? LegacyMaterialRole.Grass
                        : cutout && LooksLikeGrass(materialName, baseRef) ? LegacyMaterialRole.Grass
                        : cutout && LooksLikeDecal(materialName, baseRef) ? LegacyMaterialRole.Decal
                        : LegacyMaterialRole.Normal;

                    var samplers = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    TextureImage? blendMask = null;
                    if (fourBlend)
                    {
                        terrainBlendReferences.Add(raw!.Blend);
                        blendMask = DecodeTexture(raw.Blend);
                        string? middle = PortTexture(raw.Color1);
                        string? top = PortTexture(raw.Color2);
                        string? extras = PortTexture(raw.Color3);
                        if (middle is null || top is null || extras is null) { warnings.Add($"Ground material '{materialName}' was missing a layer; imported as a normal alpha-tested surface."); role = LegacyMaterialRole.Normal; samplers["DiffuseTexture"] = baseTarget; }
                        else
                        {
                            samplers["Bottom_Texture"] = baseTarget;
                            samplers["Middle_Texture"] = middle;
                            samplers["Top_Texture"] = top;
                            samplers["Extras_Texture"] = extras;
                        }
                    }
                    else samplers["DiffuseTexture"] = baseTarget;

                    var key = new SurfaceKey(role, string.Join("|", samplers.Select(kv => kv.Key + "=" + kv.Value)),
                        mesh.DisableBackfaceCulling || role == LegacyMaterialRole.Grass);
                    if (!accumulators.TryGetValue(key, out var chunks)) accumulators[key] = chunks = new();
                    if (chunks.Count == 0) chunks.Add(new MeshAccumulator(key, samplers));

                    int end = Math.Min(mesh.Indices.Count, startIndex + indexCount);
                    for (int i = startIndex; i + 2 < end; i += 3)
                    {
                        uint u0 = mesh.Indices[i], u1 = mesh.Indices[i + 1], u2 = mesh.Indices[i + 2];
                        if (u0 >= vertexCount || u1 >= vertexCount || u2 >= vertexCount) continue;
                        int i0 = (int)u0, i1 = (int)u1, i2 = (int)u2;
                        Vector3 p0 = Vector3.Transform(pos[i0], transform);
                        Vector3 p1 = Vector3.Transform(pos[i1], transform);
                        Vector3 p2 = Vector3.Transform(pos[i2], transform);
                        if (!Reasonable(p0) || !Reasonable(p1) || !Reasonable(p2)) { rejectedTriangles++; continue; }
                        var chunk = chunks[^1];
                        int needed = chunk.NewVertexCount(meshIndex, i0, i1, i2);
                        if (chunk.VertexCount + needed > MaxVertices)
                        { chunk = new MeshAccumulator(key, samplers); chunks.Add(chunk); }

                        LegacyVertex Make(int index)
                        {
                            Vector3 p = Vector3.Transform(pos[index], transform);
                            Vector3 n = normals is null ? Vector3.Zero : Vector3.TransformNormal(normals[index], normalMatrix);
                            if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n); else n = Vector3.UnitY;
                            // M479: four-blend terrain takes the CANVAS UV in Texcoord0, not the diffuse UV.
                            //
                            // Measured in 4textureblend_uvbased_basemat.vs/ps: the layer textures are
                            // WORLD-projected (o3/o4 = worldPos.xzxz x tiling x WS_Multiplier, lines
                            // 106-109), so UV0 feeds exactly one thing - the blend MASK
                            // (ps blob 8 line 136, sample v2.xyxx from Mask_Texture__TX). "UVBased" names
                            // the mask, not the layers.
                            //
                            // The mask is a map-wide canvas, so its UV must span the map once. The NVR's
                            // second UV set is precisely that, measured over room.nvr's 342 second-UV
                            // meshes: u vs worldX R^2 = 0.9997 and v vs worldZ R^2 = 0.9999, against
                            // 0.0002 and 0.0003 for the opposite pairing - a planar world-XZ projection
                            // into 0..1, not a lightmap atlas. Feeding the tiling DIFFUSE uv here instead
                            // sampled the mask with repeat coordinates, which makes the blend weights
                            // meaningless.
                            Vector2 uv = role == LegacyMaterialRole.FourBlendTerrain && uv7 is not null
                                ? uv7[index]
                                : uv0?[index] ?? Vector2.Zero;
                            Vector4 color = role switch
                            {
                                LegacyMaterialRole.FourBlendTerrain when blendMask is not null => Sample(blendMask, uv7![index]),
                                _ => Vector4.One,
                            };
                            // M474: carry the second UV set through instead of discarding it after the
                            // blend-mask sample above. It is the same uv7 that sample already reads.
                            return new LegacyVertex(p, n, uv, color, meshPivot, normals is not null,
                                uv7?[index] ?? Vector2.Zero, uv7 is not null);
                        }
                        chunk.AddTriangle(meshIndex, i0, i1, i2, Make);
                    }
                }
            }
            catch (Exception ex) { warnings.Add($"Legacy mesh {meshIndex}: {ex.Message}"); }
            meshIndex++;
        }
        if (rejectedTriangles > 0)
            warnings.Add($"Skipped {rejectedTriangles:n0} triangle(s) containing invalid legacy sentinel coordinates.");
        sourceMaterialCount = sourceMaterials.Count;

        // The modern world-projected terrain shader receives its RGB paint canvas from the engine rather
        // than from samplerValues. Legacy NVR stores that same map-wide canvas as channel 1. Publish it at
        // the path derived from the destination mapgeo so both the game and both editor viewports bind it.
        if (terrainBlendReferences.Count > 0 && !string.IsNullOrWhiteSpace(destinationMapGeoPath))
        {
            string selected = terrainBlendReferences.First();
            if (textureIndex.TryResolve(selected, out string blendFile))
            {
                var blendImage = TextureDecoder.Decode(File.ReadAllBytes(blendFile));
                string targetPath = MapGeoMaterialResolver.TerrainBlendTexturePathFor(destinationMapGeoPath);
                byte[] blendTex = TexWriter.Write(blendImage, TexFormat.Bc3, mipmaps: true);
                textureCopies[targetPath] = new LegacyTextureCopy(blendFile, targetPath, blendTex);
            }
            else warnings.Add($"Terrain blend texture '{selected}' was not found; the ground uses its bottom layer only.");
            if (terrainBlendReferences.Count > 1)
                warnings.Add($"The NVR references {terrainBlendReferences.Count:n0} terrain blend canvases; " +
                    $"'{selected}' was selected for the destination map-wide paint resource.");
        }

        var built = accumulators.Values.SelectMany(x => x).Where(x => x.IndexCount > 0).ToList();
        if (built.Count == 0) throw new InvalidDataException("The legacy environment contained no importable textured triangles.");

        // Keep the complete destination for the review stage. ApplyMeshCleanup removes only the categories
        // selected by the user after conversion, and protects render-region meshes unconditionally.
        int destinationMeshCount = target.Meshes.Count;
        int preservedCount = target.Meshes.Count(m => m.HasRegionHash && m.RegionHash != 0);

        var materialNames = BuildMaterialNames(slug, built.Select(x => new MaterialKey(x.Key.Role, x.Key.TextureSet)));
        // M474: counted here rather than inside AddMesh so the number reported is the number of meshes
        // that actually got the channel, not the number that were offered it.
        int withSecondUv = built.Count(acc => acc.Vertices.Any(v => v.HasUv2));
        foreach (var acc in built)
            AddMesh(target, acc, materialNames[new MaterialKey(acc.Key.Role, acc.Key.TextureSet)]);

        byte[] ported = target.Write();
        var decoded = MapGeoDecoder.Decode(ported);
        // Old NVR ground uses much larger triangles than modern mapgeo. A 1k culling grid avoids
        // duplicating those triangles into hundreds of 500-unit cells while retaining useful culling.
        ported = MapGeoWriter.WriteWithRegeneratedBucketGrids(ported, decoded, targetBucketSize: 1000f);
        var verified = MapGeoDecoder.Decode(ported);
        int preservedAfter = verified.Meshes.Count(m => m.RegionHash != 0);
        if (preservedAfter != preservedCount)
            throw new InvalidDataException($"Render-region verification failed: retained {preservedAfter} of {preservedCount} meshes.");

        var materialPlans = BuildMaterialPlans(materialNames, built, LegacyPortShaderOptions.Defaults);
        return new LegacyMapPortResult(ported, textureCopies.Values.ToList(), materialPlans, source,
            isWgeo ? "WGEO" : "NVR", environment.Meshes.Count, built.Count, 0, preservedCount,
            sourceMaterialCount, warnings.Distinct().ToList(), destinationMeshCount, withSecondUv);
    }

    private static string FindSingleSource(string root)
    {
        if (File.Exists(root) && (Path.GetFileName(root).Equals("room.nvr", StringComparison.OrdinalIgnoreCase)
                                 || Path.GetFileName(root).Equals("room.wgeo", StringComparison.OrdinalIgnoreCase))) return root;
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = Directory.EnumerateFiles(root, "room.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".nvr", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".wgeo", StringComparison.OrdinalIgnoreCase))
            .Take(3).ToList();
        return files.Count switch
        {
            1 => files[0],
            0 => throw new FileNotFoundException("No Scene/room.nvr or Scene/room.wgeo was found in the selected folder."),
            _ => throw new InvalidOperationException("The selected folder contains multiple legacy maps. Select the specific LEVELS/MapN folder."),
        };
    }

    private static Dictionary<MaterialKey, string> BuildMaterialNames(string slug, IEnumerable<MaterialKey> keys) =>
        keys.Distinct().ToDictionary(k => k, k =>
        {
            string source = k.Role + "|" + k.TextureSet;
            string digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)).AsSpan(0, 6))
                .ToLowerInvariant();
            return $"LegacyPort/{slug}/{k.Role}_{digest}";
        });

    private static IReadOnlyList<LegacyMaterialPlan> BuildMaterialPlans(
        IReadOnlyDictionary<MaterialKey, string> names, IReadOnlyList<MeshAccumulator> meshes,
        LegacyPortShaderOptions options)
    {
        var result = new List<LegacyMaterialPlan>();
        foreach (var (key, name) in names)
        {
            var sample = meshes.First(m => m.Key.Role == key.Role && m.Key.TextureSet == key.TextureSet).Samplers;
            string shader = key.Role switch
            {
                LegacyMaterialRole.Decal => options.DecalShader,
                LegacyMaterialRole.Grass => options.GrassShader,
                LegacyMaterialRole.FourBlendTerrain => options.TerrainShader,
                _ => options.NormalShader,
            };
            IReadOnlyDictionary<string, string> samplerPlan = key.Role == LegacyMaterialRole.FourBlendTerrain
                ? new Dictionary<string, string>(sample)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["__diffuse__"] = sample.Values.First() };
            var parameters = MaterialParameters(key.Role);
            var switches = key.Role == LegacyMaterialRole.FourBlendTerrain
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["USE_TOP"] = true, ["USE_EXTRAS"] = true }
                : new Dictionary<string, bool>();
            bool decal = key.Role == LegacyMaterialRole.Decal;
            // League 16.15 has no cooked DefaultEnv_Flat_AlphaTest permutation carrying
            // NO_BAKED_LIGHTING=1. Both ordinary imported surfaces and decals use that shader by default,
            // so only the grass and terrain roles may author the macro.
            IReadOnlyDictionary<string, bool> macros = UsesNoBakedLightingByDefault(key.Role)
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["NO_BAKED_LIGHTING"] = true }
                : new Dictionary<string, bool>();
            result.Add(new LegacyMaterialPlan(name, key.Role, shader, samplerPlan, parameters, switches,
                macros,
                BlendEnabled: decal, SourceBlendFactor: decal ? 6 : null, DestinationBlendFactor: decal ? 7 : null,
                SamplerAddressMode: decal ? ClampAddressMode : null));
        }
        return result;
    }

    private static void AddMesh(MapGeoBinary target, MeshAccumulator source, string material)
    {
        source.FinishNormals();
        bool hasColor = source.Key.Role == LegacyMaterialRole.FourBlendTerrain;
        bool hasGrassPivot = source.Key.Role == LegacyMaterialRole.Grass;

        // M477: does this mesh need the Texcoord7 stream, and can it fill it?
        //
        // BOTH 4TextureBlend vertex shaders REQUIRE it — measured from the compiled DXBC input signatures,
        // which read POSITION, NORMAL, TEXCOORD0, TEXCOORD7 and no COLOR at all:
        //     4textureblend_worldprojected.vs-dx11
        //     4textureblend_uvbased_basemat.vs-dx11
        // A terrain mesh without that element hands the shader an incomplete input layout, which is what
        // rendered white. Terrain always HAS the data — the four-blend role is only chosen when the source
        // mesh carries a second UV (see the `fourBlend` test) — so this is a guarantee, not a synthesis.
        bool hasUv2 = source.Vertices.Any(v => v.HasUv2);
        bool needsUv2 = source.Key.Role == LegacyMaterialRole.FourBlendTerrain;
        var decl = new MapGeoBinary.VertexDeclaration { Usage = 0 };
        // M476: ELEMENT ORDER IS THE BYTE LAYOUT, and it must be Riot's.
        //
        // This used to emit Texcoord5 BEFORE Texcoord0. Censused across 206 shipped mapgeos and 27 distinct
        // declaration orders, Riot ships "Position, Normal, Color0, Tex0, Tex5" 28 times and
        // "Position, Normal, Color0, Tex5, Tex0" ZERO times. Our own reader is order-driven so it read the
        // result back perfectly - stride and buffer sizes check out, no NaNs, sane bounds - which is why
        // this survived: it is invisible to every check that goes through our own code, and only the game
        // disagrees. Same shape as M416, where pointer-form container elements loaded everywhere and
        // rendered nothing.
        //
        // The write loop below follows this order exactly; the two must be changed together.
        decl.Elements.Add((MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32));
        decl.Elements.Add((MapGeoBinary.ElemNormal, MapGeoBinary.FmtXYZ_Float32));
        if (hasColor) decl.Elements.Add((MapGeoBinary.ElemPrimaryColor, MapGeoBinary.FmtBGRA_Packed8888));
        decl.Elements.Add((MapGeoBinary.ElemTexcoord0, MapGeoBinary.FmtXY_Float32));
        if (hasGrassPivot) decl.Elements.Add((MapGeoBinary.ElemTexcoord5, MapGeoBinary.FmtXYZ_Float32));
        // M477: Texcoord7 INLINE, last. M474 attached it through AddUvChannelOnly, which puts it in a
        // SEPARATE vertex buffer — a layout Riot ships 3 times against 176 for the inline
        // "Position, Normal, Tex0, Tex7" form and 16 for "Position, Normal, Color0, Tex0, Tex7". Inline is
        // both the overwhelming convention and one buffer fewer to get wrong.
        bool writeUv2 = hasUv2 || needsUv2;
        if (writeUv2) decl.Elements.Add((MapGeoBinary.ElemTexcoord7, MapGeoBinary.FmtXY_Float32));
        decl.Padding = new byte[8 * (15 - decl.Elements.Count)];
        int declId = target.Declarations.Count; target.Declarations.Add(decl);

        using var vertices = new MemoryStream(source.VertexCount * decl.Stride);
        using (var writer = new BinaryWriter(vertices, System.Text.Encoding.UTF8, leaveOpen: true))
            foreach (var v in source.Vertices)
            {
                writer.Write(v.Position.X); writer.Write(v.Position.Y); writer.Write(v.Position.Z);
                writer.Write(v.Normal.X); writer.Write(v.Normal.Y); writer.Write(v.Normal.Z);
                if (hasColor)
                {
                    writer.Write((byte)Math.Clamp((int)MathF.Round(v.Color.Z * 255), 0, 255));
                    writer.Write((byte)Math.Clamp((int)MathF.Round(v.Color.Y * 255), 0, 255));
                    writer.Write((byte)Math.Clamp((int)MathF.Round(v.Color.X * 255), 0, 255));
                    writer.Write((byte)Math.Clamp((int)MathF.Round(v.Color.W * 255), 0, 255));
                }
                // M476: Texcoord0 BEFORE Texcoord5, matching the declaration above and Riot's own layout.
                writer.Write(v.Uv.X); writer.Write(v.Uv.Y);
                if (hasGrassPivot)
                {
                    writer.Write(v.Pivot.X); writer.Write(v.Pivot.Y); writer.Write(v.Pivot.Z);
                }
                // M477: Texcoord7 last, matching the declaration. A vertex whose own source mesh had no
                // second UV falls back to UV0 rather than zeros: a group can mix source meshes, and a
                // zeroed run would collapse that whole stretch of terrain onto one texel of the mask,
                // which reads as a solid colour rather than as missing data.
                if (writeUv2)
                {
                    var uv2 = v.HasUv2 ? v.Uv2 : v.Uv;
                    writer.Write(uv2.X); writer.Write(uv2.Y);
                }
            }
        int vb = target.VertexBuffers.Count;
        target.VertexBuffers.Add(new MapGeoBinary.VertexBuffer { HasVisibility = true, Visibility = 0xff, Data = vertices.ToArray() });

        byte[] indexBytes = new byte[source.Indices.Count * 2];
        for (int i = 0; i < source.Indices.Count; i++) BitConverter.TryWriteBytes(indexBytes.AsSpan(i * 2, 2), source.Indices[i]);
        int ib = target.IndexBuffers.Count;
        target.IndexBuffers.Add(new MapGeoBinary.IndexBuffer { HasVisibility = true, Visibility = 0xff, Data = indexBytes });

        var mesh = new MapGeoBinary.Mesh
        {
            VertexCount = source.VertexCount, VertexDeclarationBase = declId, IndexCount = source.Indices.Count,
            IndexBufferId = ib, HasVisibility = true, Visibility = 0xff,
            HasRegionHash = target.Version >= 18, RegionHash = 0,
            HasVcHash = target.Version >= 15, VisibilityControllerPathHash = 0,
            HasDisableBackface = true, DisableBackfaceCulling = source.Key.DoubleSided,
            BoundsMin = source.BoundsMin, BoundsMax = source.BoundsMax, Transform = Matrix4x4.Identity,
            QualityFilter = 31, HasLayerTransition = target.Version >= 14, LayerTransition = 0,
            RenderFlags = 0, RenderFlagsIsUshort = target.Version >= 16,
            BakedLight = new MapGeoBinary.Channel { Scale = Vector2.One },
            StationaryLight = new MapGeoBinary.Channel { Scale = Vector2.One },
            BakedPaintScale = Vector2.One, BakedPaintBias = Vector2.Zero,
        };
        mesh.VertexBufferIds.Add(vb);
        mesh.Submeshes.Add(new MapGeoBinary.Submesh
        {
            Hash = HashAlgorithms.Fnv1a(material), Material = material, StartIndex = 0,
            IndexCount = source.Indices.Count, MinVertex = 0, MaxVertex = source.VertexCount - 1,
        });
        target.Meshes.Add(mesh);

        // M477: the second UV is written INLINE by the loop above, so the M474 AddUvChannelOnly call that
        // used to sit here is gone. It is not merely redundant now — running both would produce a mesh
        // declaring Texcoord7 twice, and AddUvChannelOnly refuses that outright.
        //
        // BakedLight is still deliberately left unset. The mesh now has the UV stream a shader can demand
        // as a vertex contract; pointing it at a lightmap atlas that does not exist would trade one
        // missing resource for another. The atlas comes from baking.
    }

    /// <summary>
    /// One NVR material record. <paramref name="Type"/> and <paramref name="IsGround"/> are the two
    /// integers that sit between the name and the texture list, and they are the file SAYING what the
    /// material is (M529).
    /// </summary>
    /// <param name="Type">0 = ordinary surface, 1 = DECAL, 2 = grass. Measured over Map2's 148
    /// materials: 120 type 0, 18 type 1, 1 type 2, and the type-1 set is exactly the decal art -
    /// base_chasm1/2/3, order_seam, new_stone_road, turret_stoneBase, the tile-floor marks.</param>
    /// <param name="IsGround">The terrain flag: every one of the 9 materials carrying it is named
    /// <c>ground_*</c> or <c>*_ground_*</c>.</param>
    private sealed record NvrMaterial(string Base, string Blend, string Color1, string Color2, string Color3,
        int Type = 0, bool IsGround = false);

    /// <summary>
    /// M529: the type each NVR material DECLARES, keyed by material name - 0 ordinary, 1 decal,
    /// 2 grass - plus the ground flag. Public so the rule can be checked against a real file rather
    /// than asserted about a fixture.
    /// </summary>
    public static IReadOnlyDictionary<string, (int Type, bool IsGround)> NvrMaterialTypes(byte[] nvr)
    {
        ArgumentNullException.ThrowIfNull(nvr);
        var result = new Dictionary<string, (int, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, material) in ParseNvrMaterials(nvr))
            result[name] = (material.Type, material.IsGround);
        return result;
    }

    private static Dictionary<string, NvrMaterial> ParseNvrMaterials(byte[] data)
    {
        var result = new Dictionary<string, NvrMaterial>(StringComparer.OrdinalIgnoreCase);
        if (data.Length < 28) return result;
        ushort major = BitConverter.ToUInt16(data, 4);
        int count = BitConverter.ToInt32(data, 8);
        if (major is < 8 or > 9 || count is <= 0 or > 100000) return result;
        const int start = 28, stride = 2988, nameLength = 260, textureOffset = 284, channelStride = 340;
        for (int i = 0; i < count; i++)
        {
            int record = start + i * stride;
            if (record + stride > data.Length) break;
            string name = ReadCString(data, record, nameLength);
            string Channel(int c) => ReadCString(data, record + textureOffset + c * channelStride, 256);
            // The two ints immediately after the 260-byte name: the declared type and the ground flag.
            int type = BitConverter.ToInt32(data, record + nameLength);
            bool ground = BitConverter.ToInt32(data, record + nameLength + 4) != 0;
            if (name.Length > 0)
                result[name] = new(Channel(0), Channel(1), Channel(2), Channel(4), Channel(6), type, ground);
        }
        return result;
    }

    private static string ReadCString(byte[] data, int offset, int max)
    {
        int end = offset, limit = Math.Min(data.Length, offset + max);
        while (end < limit && data[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(data, offset, end - offset);
    }

    private static string StripNvrPrefix(string value) => value.StartsWith("NVRMaterial_", StringComparison.OrdinalIgnoreCase) ? value[12..] : value;

    private static bool HasCutoutAlpha(TextureImage? image)
    {
        if (image is null) return false;
        int transparent = 0, opaque = 0, pixels = image.Width * image.Height;
        int step = Math.Max(1, pixels / 65536);
        for (int p = 0; p < pixels; p += step)
        {
            byte a = image.Rgba[p * 4 + 3];
            if (a < 245) transparent++; else opaque++;
        }
        int sampled = transparent + opaque;
        return transparent > sampled / 200 && opaque > sampled / 200;
    }

    private static bool LooksLikeGrass(string material, string? texture)
    {
        string value = (material + " " + texture).ToLowerInvariant();
        return value.Contains("grass", StringComparison.Ordinal)
            || value.Contains("tuft", StringComparison.Ordinal)
            || value.Contains("plant", StringComparison.Ordinal)
            || value.Contains("fern", StringComparison.Ordinal)
            || value.Contains("brush", StringComparison.Ordinal)
            || value.Contains("bush", StringComparison.Ordinal)
            || value.Contains("shrub", StringComparison.Ordinal)
            || value.Contains("weed", StringComparison.Ordinal)
            || value.Contains("reed", StringComparison.Ordinal);
    }

    private static bool LooksLikeDecal(string material, string? texture)
    {
        string value = (material + " " + texture).ToLowerInvariant();
        return value.Contains("decal", StringComparison.Ordinal)
            || value.Contains("overlay", StringComparison.Ordinal)
            || value.Contains("roadmark", StringComparison.Ordinal)
            || value.Contains("road_mark", StringComparison.Ordinal);
    }

    private static Vector4 Sample(TextureImage image, Vector2 uv)
    {
        float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
        int x = Math.Clamp((int)(u * image.Width), 0, image.Width - 1);
        int y = Math.Clamp((int)((1f - v) * image.Height), 0, image.Height - 1);
        int o = (y * image.Width + x) * 4;
        return new(image.Rgba[o] / 255f, image.Rgba[o + 1] / 255f, image.Rgba[o + 2] / 255f, image.Rgba[o + 3] / 255f);
    }

    private static string Slug(string value)
    {
        string clean = new(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        while (clean.Contains("__", StringComparison.Ordinal)) clean = clean.Replace("__", "_", StringComparison.Ordinal);
        return clean.Trim('_');
    }

    private static bool Reasonable(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
        && MathF.Abs(value.X) < 10_000_000f && MathF.Abs(value.Y) < 10_000_000f && MathF.Abs(value.Z) < 10_000_000f;

    private readonly record struct SurfaceKey(LegacyMaterialRole Role, string TextureSet, bool DoubleSided);
    private readonly record struct MaterialKey(LegacyMaterialRole Role, string TextureSet);
    /// <param name="Uv2">M474: the source's SECOND UV set. WGEO/NVR carry two — the diffuse UV and a
    /// second one LeagueToolkit surfaces as Texcoord7 — and the port used to read the second only to
    /// sample a blend mask into vertex colour, then throw it away. Nothing reached the ported mesh, so
    /// every imported mesh came out with no Texcoord7 at all: no lightmap UVs to bake into, and no
    /// vertex stream for the shaders that require the channel as a contract.</param>
    /// <param name="HasUv2">Whether the source mesh actually had that channel. Zeroed UVs and absent UVs
    /// are different things — a mesh whose neighbours have the channel still has to be padded, but a
    /// GROUP with no real data must not gain a fabricated one.</param>
    private readonly record struct LegacyVertex(
        Vector3 Position, Vector3 Normal, Vector2 Uv, Vector4 Color, Vector3 Pivot, bool HasNormal,
        Vector2 Uv2 = default, bool HasUv2 = false);

    private sealed class MeshAccumulator
    {
        private readonly Dictionary<long, ushort> _vertices = new();
        public SurfaceKey Key { get; }
        public IReadOnlyDictionary<string, string> Samplers { get; }
        public List<LegacyVertex> Vertices { get; } = new();
        public List<ushort> Indices { get; } = new();
        public int VertexCount => Vertices.Count;
        public int IndexCount => Indices.Count;
        public Vector3 BoundsMin { get; private set; } = new(float.MaxValue);
        public Vector3 BoundsMax { get; private set; } = new(float.MinValue);
        public MeshAccumulator(SurfaceKey key, IReadOnlyDictionary<string, string> samplers) { Key = key; Samplers = new Dictionary<string, string>(samplers); }
        private static long Id(int mesh, int vertex) => ((long)mesh << 32) | (uint)vertex;
        public int NewVertexCount(int mesh, int a, int b, int c)
        {
            int n = 0; long ia = Id(mesh, a), ib = Id(mesh, b), ic = Id(mesh, c);
            if (!_vertices.ContainsKey(ia)) n++;
            if (ib != ia && !_vertices.ContainsKey(ib)) n++;
            if (ic != ia && ic != ib && !_vertices.ContainsKey(ic)) n++;
            return n;
        }
        public void AddTriangle(int mesh, int a, int b, int c, Func<int, LegacyVertex> make)
        {
            Add(a); Add(b); Add(c);
            void Add(int source)
            {
                long id = Id(mesh, source);
                if (!_vertices.TryGetValue(id, out ushort index))
                {
                    index = checked((ushort)Vertices.Count); var vertex = make(source);
                    _vertices[id] = index; Vertices.Add(vertex);
                    BoundsMin = Vector3.Min(BoundsMin, vertex.Position); BoundsMax = Vector3.Max(BoundsMax, vertex.Position);
                }
                Indices.Add(index);
            }
        }
        public void FinishNormals()
        {
            if (Vertices.All(v => v.HasNormal)) return;
            var sums = new Vector3[Vertices.Count];
            for (int i = 0; i + 2 < Indices.Count; i += 3)
            {
                int a = Indices[i], b = Indices[i + 1], c = Indices[i + 2];
                Vector3 n = Vector3.Cross(Vertices[b].Position - Vertices[a].Position, Vertices[c].Position - Vertices[a].Position);
                if (n.LengthSquared() > 1e-12f) { sums[a] += n; sums[b] += n; sums[c] += n; }
            }
            for (int i = 0; i < Vertices.Count; i++)
            {
                Vector3 n = sums[i].LengthSquared() > 1e-12f ? Vector3.Normalize(sums[i]) : Vector3.UnitY;
                Vertices[i] = Vertices[i] with { Normal = n, HasNormal = true };
            }
        }
    }

    private static Vector3[] ReadVector3(VertexElementAccessor accessor, int count)
    {
        var result = new Vector3[count];
        try { var values = accessor.AsVector3Array(); for (int i = 0; i < count; i++) result[i] = values[i]; }
        catch { var values = accessor.AsXyzF16Array(); for (int i = 0; i < count; i++) result[i] = new((float)values[i].Item1, (float)values[i].Item2, (float)values[i].Item3); }
        return result;
    }

    private static Vector2[] ReadVector2(VertexElementAccessor accessor, int count)
    {
        var result = new Vector2[count];
        try { var values = accessor.AsVector2Array(); for (int i = 0; i < count; i++) result[i] = values[i]; }
        catch { var values = accessor.AsXyF16Array(); for (int i = 0; i < count; i++) result[i] = new((float)values[i].Item1, (float)values[i].Item2); }
        return result;
    }

}
