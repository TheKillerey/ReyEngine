using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M418: the checks that would have caught a mod the game refuses, when "does it parse" says yes.
///
/// <para><b>Why this exists.</b> One Map453 mod shipped four separate defects, each of which loaded
/// cleanly in every tool, round-tripped byte-identically, and diffed clean — and crashed the game at
/// map load, or rendered nothing. <see cref="BinValidator"/> did not catch any of them: it checks
/// object links and asset existence, and none of these is a link or an asset problem. They were all
/// found the same way instead, by comparing the mod against Riot's own shipped corpus.</para>
///
/// <para><b>Every rule here carries its measurement.</b> A rule that only says "this looks unusual"
/// is worse than no rule — during that investigation four such hunches were confidently wrong. So a
/// rule ships only when the shipped corpus is unanimous, and the count is written next to it. That
/// ratio is the argument, not taste.</para>
/// </summary>
public static class ModShapeValidator
{
    private static readonly uint MaterialClass = HashAlgorithms.Fnv1a("StaticMaterialDef");
    private static readonly uint F_name = HashAlgorithms.Fnv1a("name");
    private static readonly uint F_samplers = HashAlgorithms.Fnv1a("samplerValues");
    private static readonly uint F_params = HashAlgorithms.Fnv1a("paramValues");
    private static readonly uint F_switches = HashAlgorithms.Fnv1a("switches");
    private static readonly uint F_techniques = HashAlgorithms.Fnv1a("techniques");
    private static readonly uint F_passes = HashAlgorithms.Fnv1a("passes");
    private static readonly uint F_blendEnable = HashAlgorithms.Fnv1a("blendEnable");
    private static readonly uint F_srcColor = HashAlgorithms.Fnv1a("srcColorBlendFactor");
    private static readonly uint F_srcAlpha = HashAlgorithms.Fnv1a("srcAlphaBlendFactor");
    private static readonly uint F_dstColor = HashAlgorithms.Fnv1a("dstColorBlendFactor");
    private static readonly uint F_dstAlpha = HashAlgorithms.Fnv1a("dstAlphaBlendFactor");
    private static readonly uint F_addressU = HashAlgorithms.Fnv1a("addressU");
    private static readonly uint F_addressV = HashAlgorithms.Fnv1a("addressV");
    private static readonly uint F_addressW = HashAlgorithms.Fnv1a("addressW");

    /// <summary>
    /// M430: the shader contract rules. Both were found by a crash with no useful Riot log, on
    /// <c>Mantis_Env_Baked_PBR</c> - a shader Riot ships but has no material for, so there was no
    /// template to copy. Both carry a 0-of-N measurement over every shipped WAD.
    /// </summary>
    public static IReadOnlyList<BinIssue> ValidateAgainstShaders(
        BinTree tree, Func<uint, Shaders.LeagueShaderDef?> shaderOf, Func<uint, string?>? resolveName = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(shaderOf);
        var issues = new List<BinIssue>();

        foreach (var (hash, o) in tree.Objects)
        {
            if (o.ClassHash != MaterialClass) continue;
            string name = o.Properties.TryGetValue(F_name, out var np) && np is BinTreeString ns && ns.Value.Length > 0
                ? ns.Value : resolveName?.Invoke(hash) ?? $"0x{hash:x8}";

            uint shaderHash = 0;
            foreach (var pass in Passes(o))
                if (pass.Properties.TryGetValue(HashAlgorithms.Fnv1a("shader"), out var sp)
                    && sp is BinTreeObjectLink link) { shaderHash = link.Value; break; }
            if (shaderHash == 0 || shaderOf(shaderHash) is not { } shader) continue;

            // ---- a parameter the shader does not declare ----------------------------------------
            // Measured: of 31,954 shipped materials that set any paramValue, 0 set one their shader
            // does not declare. A leftover TintColor from a previous shader is how the Mantis test
            // material ended up here.
            if (o.Properties.TryGetValue(HashAlgorithms.Fnv1a("paramValues"), out var pv)
                && pv is BinTreeContainer pc)
            {
                var declared = new HashSet<string>(shader.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var el in pc.Elements)
                    if (el is BinTreeStruct s
                        && s.Properties.TryGetValue(F_name, out var pn) && pn is BinTreeString ps
                        && !declared.Contains(ps.Value))
                        issues.Add(new BinIssue("undeclared-parameter", name,
                            $"sets parameter '{ps.Value}', which '{shader.Name}' does not declare. Riot ships "
                            + "none (0 of 31,954 materials with parameters) — usually a leftover from the shader "
                            + "this material used before.", hash, o.ClassHash));
            }

            // ---- a shader with staticSwitches always has a switches container ---------------------
            if (shader.StaticSwitches.Count > 0
                && !(o.Properties.TryGetValue(HashAlgorithms.Fnv1a("switches"), out var sw)
                     && sw is BinTreeContainer sc && sc.Elements.Count > 0))
                issues.Add(new BinIssue("missing-switches", name,
                    $"'{shader.Name}' declares {shader.StaticSwitches.Count} static switch(es) "
                    + $"({string.Join(", ", shader.StaticSwitches)}) but the material has no switches container. "
                    + "All 27,295 shipped materials on a switch-declaring shader carry one.",
                    hash, o.ClassHash));

            // ---- a sampler the shader does not declare -------------------------------------------
            if (o.Properties.TryGetValue(F_samplers, out var svp) && svp is BinTreeContainer svc
                && shader.Textures.Count > 0)
            {
                var declaredTex = new HashSet<string>(shader.Textures.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var el in svc.Elements)
                    if (el is BinTreeStruct s
                        && s.Properties.TryGetValue(HashAlgorithms.Fnv1a("TextureName"), out var tn)
                        && tn is BinTreeString ts && !declaredTex.Contains(ts.Value))
                        issues.Add(new BinIssue("undeclared-sampler", name,
                            $"binds sampler '{ts.Value}', which '{shader.Name}' does not declare. The shader "
                            + "will never read it, and it is usually a leftover from a previous shader.",
                            hash, o.ClassHash));
            }
        }
        return issues;
    }

    /// <summary>Shape rules over one already-parsed .bin. <paramref name="raw"/> is the file's bytes,
    /// needed for the null-form check, which is a wire-level question the parsed tree cannot answer.</summary>
    public static IReadOnlyList<BinIssue> ValidateBin(BinTree tree, byte[] raw, Func<uint, string?>? resolveName = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var issues = new List<BinIssue>();
        string Name(uint h, BinTreeObject o) =>
            o.Properties.TryGetValue(F_name, out var p) && p is BinTreeString s && s.Value.Length > 0
                ? s.Value
                : resolveName?.Invoke(h) ?? $"0x{h:x8}";

        // ---- 1. long-form nullable structs -------------------------------------------------------
        // A 0x82 struct is NULLABLE: class hash 0 means null and NOTHING follows. Written in the long
        // form it carries six extra bytes, and every byte after the first one in that container is
        // misread. Riot's reader stops after the hash, so this is an instant crash at map load.
        if (raw is { Length: > 0 } && BinNullStructRepair.TryRepair(raw, out _, out var repair) && repair.NullsFixed > 0)
            issues.Add(new BinIssue("null-struct-form",
                "(file)",
                $"{repair.NullsFixed:n0} nullable struct(s) across {repair.ObjectsFixed} object(s) are written in the long "
                + "form (class hash 0 followed by a size and field count). The format omits those six bytes; the game "
                + "misreads everything after the first one and crashes at map load. Saving this bin from ReyEngine repairs it."));

        // M556: blend factors that read the DESTINATION ALPHA.
        //
        // A decal that went black in game turned out to carry dstColorBlendFactor = 9 (InvDstAlpha). That
        // factor makes the result src*srcAlpha + dst*(1 - dstAlpha), so wherever the framebuffer alpha is
        // 1 the destination collapses to ZERO and the surface composites over black instead of over what
        // is behind it. Nothing about the material looks wrong; it just draws onto nothing.
        //
        // Censused over 18 shipped map WADs: of every blended map material, the factors Riot uses are
        // 1, 4, 6 and 7. DstAlpha (8) and InvDstAlpha (9) appear ZERO times on either side, and 164 of
        // Riot's 165 decal materials are exactly 6/7. A map's framebuffer alpha is not a channel the map
        // pipeline guarantees, which is presumably why.
        uint[] destinationAlphaFields =
        {
            HashAlgorithms.Fnv1a("srcColorBlendFactor"), HashAlgorithms.Fnv1a("dstColorBlendFactor"),
            HashAlgorithms.Fnv1a("srcAlphaBlendFactor"), HashAlgorithms.Fnv1a("dstAlphaBlendFactor"),
        };
        foreach (var (hash, o) in tree.Objects)
            foreach (var (ph, prop) in o.Properties)
                CheckBlendFactors(issues, Name(hash, o), hash, o, ph, prop, destinationAlphaFields, resolveName);

        foreach (var (hash, o) in tree.Objects)
        {
            // ---- 2. empty containers and maps ----------------------------------------------------
            // 0 of 33,645 shipped StaticMaterialDef objects contain an empty switches / paramValues /
            // samplerValues / techniques / shaderMacros. Riot omits the field instead, and one empty
            // container crashed Map453 at load.
            foreach (var (ph, prop) in o.Properties)
                if (BinEmptyProperty.IsEmpty(prop))
                    issues.Add(new BinIssue("empty-container", Name(hash, o),
                        $"'{resolveName?.Invoke(ph) ?? $"0x{ph:x8}"}' is an EMPTY {prop.GetType().Name}. Riot ships none "
                        + "(0 of 33,645 materials) — it omits the field instead. Saving this bin from ReyEngine removes it.",
                        hash, o.ClassHash));

            if (o.ClassHash != MaterialClass) continue;

            // ---- 3. container element type ------------------------------------------------------
            // Material containers hold EMBEDDED (0x83) elements. The struct/pointer form (0x82) parses,
            // round-trips and diffs identically — and the game renders NOTHING for the material. This is
            // what made 117 ported terrain materials invisible while every value in them was correct.
            foreach (var (field, label) in new[]
                     {
                         (F_samplers, "samplerValues"), (F_params, "paramValues"),
                         (F_switches, "switches"), (F_techniques, "techniques"),
                     })
            {
                if (!o.Properties.TryGetValue(field, out var prop) || prop is not BinTreeContainer c) continue;
                if (c.Elements.Count > 0 && c.ElementType != BinPropertyType.Embedded)
                    issues.Add(new BinIssue("pointer-element", Name(hash, o),
                        $"'{label}' holds {c.ElementType} elements; Riot always uses Embedded here. The file loads "
                        + "everywhere and the game renders nothing for this material.",
                        hash, o.ClassHash));

                // ---- 3b. the CONTAINER's own wire form (M507) -----------------------------------
                // Rule 3 above checks what is INSIDE the container. This checks the container itself:
                // Container (0x80, "list") and UnorderedContainer (0x81, "list2") parse the same here and
                // the client SKIPS the property when the tag disagrees with the schema. A skipped
                // 'switches' is a material whose feature set silently reverts to the shader's defaults -
                // which is how a ported Map453 asked DefaultEnv_Flat_AlphaTest for a permutation Riot
                // never cooked and failed to load ("Unable to find correct hash for shader").
                if (Materials.MaterialContainerShape.Matches(label, prop) == false)
                    issues.Add(new BinIssue("container-wire-form", Name(hash, o),
                        $"'{label}' is a {prop.GetType().Name}; Riot always writes "
                        + $"{(Materials.MaterialContainerShape.IsUnordered(label) == true ? "UnorderedContainer (list2)" : "Container (list)")} "
                        + $"({Materials.MaterialContainerShape.Evidence(label)}). The client SKIPS a container "
                        + "whose tag disagrees with the schema, so this field does not reach the game at all.",
                        hash, o.ClassHash));
            }

            // ---- 3c. a CLAMPED sampler authors all three axes (M599) ---------------------------
            // Censused over every shipped map wad, 569 decal samplers: 379 author nothing at all (Wrap),
            // 115 are U2/V2 (Mirror, no W), 58 are W1 alone, and every one of the 17 that CLAMP writes the
            // full U1/V1/W1 triple. Zero author U1/V1 without W.
            //
            // A ported map arrived with 17 of 19 decals at U1/V1/W- and 2 at U1/V1/W1, and only those 2
            // clamped in game - the other 17 tiled their texture across the surface. The porter writes the
            // three together, so a partial triple means something edited the sampler afterwards; the
            // editor's own preset apply removes an axis the preset leaves unset, which produces exactly
            // this shape. Whatever wrote it, the file no longer says what it means to say.
            if (o.Properties.TryGetValue(F_samplers, out var samplerProp) && samplerProp is BinTreeContainer samplers)
                foreach (var element in samplers.Elements)
                {
                    if (element is not BinTreeStruct sampler) continue;
                    bool u = sampler.Properties.ContainsKey(F_addressU);
                    bool v = sampler.Properties.ContainsKey(F_addressV);
                    bool w = sampler.Properties.ContainsKey(F_addressW);
                    if (!u && !v) continue;               // authoring nothing is Wrap, and Riot's commonest case
                    if (u && v && w) continue;            // the full triple - what Riot writes when it clamps
                    if (u && v && !w && IsClamp(sampler)) // U2/V2 Mirror without W is a real shipped shape
                        issues.Add(new BinIssue("partial-address-triple", Name(hash, o),
                            "a sampler authors addressU/addressV as Clamp but leaves addressW out. Riot writes all "
                            + "three whenever it clamps (17 of 17 clamped decal samplers), and 0 of 569 ship this "
                            + "shape. Measured in game, a decal with the partial triple TILES instead of clamping.",
                            hash, o.ClassHash));
                    else if (u != v)
                        issues.Add(new BinIssue("partial-address-triple", Name(hash, o),
                            $"a sampler authors address{(u ? "U" : "V")} without address{(u ? "V" : "U")}. Riot "
                            + "always moves the U and V axes together.", hash, o.ClassHash));
                }

            foreach (var pass in Passes(o))
            {
                // nested one level down, and written by a separate code path
                if (pass.Properties.TryGetValue(F_passes, out var pp) && pp is BinTreeContainer pc
                    && pc.Elements.Count > 0 && pc.ElementType != BinPropertyType.Embedded)
                    issues.Add(new BinIssue("pointer-element", Name(hash, o),
                        $"'passes' holds {pc.ElementType} elements; Riot always uses Embedded.", hash, o.ClassHash));

                // ---- 4. blend factors move in pairs ---------------------------------------------
                // Of 3,192 shipped materials on DefaultEnv_Flat_AlphaTest with PREMULTIPLIED_ALPHA +
                // MULTIPLY_ALPHA, all 3,192 author dstAlphaBlendFactor beside dstColorBlendFactor. With
                // premultiplied alpha the missing half leaves the surface fully transparent.
                bool blends = pass.Properties.TryGetValue(F_blendEnable, out var be)
                              && be is BinTreeBool bb && bb.Value;
                if (!blends) continue;
                foreach (var (colour, alpha, pair) in new[]
                         {
                             (F_srcColor, F_srcAlpha, "src"), (F_dstColor, F_dstAlpha, "dst"),
                         })
                {
                    bool hasColour = pass.Properties.ContainsKey(colour);
                    bool hasAlpha = pass.Properties.ContainsKey(alpha);
                    if (hasColour != hasAlpha)
                        issues.Add(new BinIssue("half-blend-equation", Name(hash, o),
                            $"blendEnable is on and only the {(hasColour ? "colour" : "alpha")} half of the {pair} blend "
                            + $"factor is authored. Riot always writes both. Half an equation can leave the surface "
                            + "fully transparent in game.",
                            hash, o.ClassHash));
                }
            }
        }
        return issues;
    }

    /// <summary>
    /// Cross-file rules: a mapgeo against the materials bin that is supposed to define its materials.
    /// These cannot live in <see cref="ValidateBin"/> because neither file can answer them alone.
    /// </summary>
    /// <param name="materialShader">Material name -> the shader its first pass links to, or null when
    /// the bin does not define that material at all.</param>
    public static IReadOnlyList<BinIssue> ValidateMapGeo(string mapGeoName, byte[] mapGeo,
        Func<string, string?> materialShader)
    {
        ArgumentNullException.ThrowIfNull(mapGeo);
        var issues = new List<BinIssue>();
        EnvironmentAsset env;
        try
        {
            using var ms = new MemoryStream(mapGeo, writable: false);
            env = new EnvironmentAsset(ms);
        }
        catch (Exception ex)
        {
            issues.Add(new BinIssue("mapgeo-unreadable", mapGeoName, $"{ex.GetType().Name}: {ex.Message}"));
            return issues;
        }

        var reportedMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reportedDeform = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in env.Meshes)
        {
            // Texcoord5 is the grass-clump pivot: it puts the mesh on the engine's vertex-deform path.
            // 8,873 shipped meshes carry it and ALL 8,873 use Shaders/StaticMesh/VertexDeform.
            bool hasT5 = mesh.VerticesView.Buffers
                .SelectMany(b => b.Description.Elements)
                .Any(e => e.Name.ToString().Contains("Texcoord5", StringComparison.OrdinalIgnoreCase));

            foreach (var sub in mesh.Submeshes)
            {
                string? shader = materialShader(sub.Material);
                if (shader is null)
                {
                    if (reportedMissing.Add(sub.Material))
                        issues.Add(new BinIssue("missing-material", mapGeoName,
                            $"mesh '{mesh.Name}' uses material '{sub.Material}', which the materials bin does not define. "
                            + "The game has nothing to bind and can crash at map load."));
                    continue;
                }
                if (hasT5 && !shader.EndsWith("VertexDeform", StringComparison.OrdinalIgnoreCase)
                    && reportedDeform.Add(sub.Material))
                    issues.Add(new BinIssue("texcoord5-without-vertexdeform", mapGeoName,
                        $"mesh '{mesh.Name}' carries Texcoord5 (the grass-clump pivot) but its material "
                        + $"'{sub.Material}' uses '{shader}'. Every one of the 8,873 shipped meshes with that channel "
                        + "uses Shaders/StaticMesh/VertexDeform; the mismatch crashed Map453 at load."));
            }
        }
        return issues;
    }

    /// <summary>
    /// M431: the VERTEX contract. A shader's <c>featureDefines</c> name the vertex data it needs, and a
    /// mesh that cannot supply it is an input-layout mismatch the game does not survive.
    ///
    /// <para><b>FEATURE_BAKED_PAINT needs the baked UV set (Texcoord7).</b> Measured: all 18 shipped
    /// meshes using <c>DefaultEnv_Flat_BakedTerrain</c> - the only other shader declaring that feature -
    /// carry Texcoord7. This is what stops <c>Mantis_Env_Baked_PBR</c> working on map11's base_srx,
    /// where 0 of 586 meshes have one.</para>
    ///
    /// <para>Deliberately narrow. FEATURE_TANGENT is NOT checked: no shipped mapgeo carries a Tangent
    /// stream at all (0 across 40,512 meshes in 205 files), so the engine derives tangents and a rule
    /// would fire on everything.</para>
    /// </summary>
    public static IReadOnlyList<BinIssue> ValidateMeshFeatures(
        string mapGeoName, byte[] mapGeo,
        Func<string, Shaders.LeagueShaderDef?> shaderForMaterial)
    {
        ArgumentNullException.ThrowIfNull(mapGeo);
        ArgumentNullException.ThrowIfNull(shaderForMaterial);
        var issues = new List<BinIssue>();
        EnvironmentAsset env;
        try
        {
            using var ms = new MemoryStream(mapGeo, writable: false);
            env = new EnvironmentAsset(ms);
        }
        catch { return issues; }   // ValidateMapGeo already reports an unreadable file

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in env.Meshes)
        {
            var streams = mesh.VerticesView.Buffers
                .SelectMany(b => b.Description.Elements)
                .Select(e => e.Name.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var sub in mesh.Submeshes)
            {
                if (shaderForMaterial(sub.Material) is not { } shader) continue;
                if (shader.FeatureDefines is not { } fd || !fd.ContainsKey("FEATURE_BAKED_PAINT")) continue;
                if (streams.Contains("Texcoord7")) continue;
                if (!reported.Add(sub.Material)) continue;
                issues.Add(new BinIssue("mesh-missing-vertex-stream", mapGeoName,
                    $"mesh '{mesh.Name}' uses material '{sub.Material}' on shader '{shader.Name}', which "
                    + "declares FEATURE_BAKED_PAINT and therefore reads the baked UV set — but the mesh has "
                    + $"no Texcoord7 (it carries {string.Join(", ", streams.OrderBy(s => s))}). All 18 shipped "
                    + "meshes on the only other FEATURE_BAKED_PAINT shader carry Texcoord7. The map must "
                    + "supply that stream before this shader can be used on it — select the mesh and use "
                    + "\"Add Texcoord7\" in the inspector (M432), which adds the channel without a lightmap bake."));
            }
        }
        return issues;
    }

    /// <summary>Material name -> shader path, for <see cref="ValidateMapGeo"/>. Null for names the bin
    /// does not define, which is itself one of the findings.</summary>
    public static Func<string, string?> MaterialShaderLookup(IEnumerable<BinTree> bins, Func<uint, string?> resolveName)
    {
        var byName = new Dictionary<uint, string?>();
        foreach (var tree in bins)
            foreach (var (hash, o) in tree.Objects)
            {
                if (o.ClassHash != MaterialClass) continue;
                string? shader = null;
                foreach (var pass in Passes(o))
                    if (pass.Properties.TryGetValue(HashAlgorithms.Fnv1a("shader"), out var sp)
                        && sp is BinTreeObjectLink link)
                    { shader = resolveName(link.Value) ?? $"0x{link.Value:x8}"; break; }
                byName[hash] = shader ?? "";
            }
        return name => byName.TryGetValue(HashAlgorithms.Fnv1a(name), out var s) ? s : null;
    }

    /// <summary>Every pass struct under a material's techniques, plus the technique structs themselves so
    /// the caller can inspect the nested 'passes' container.</summary>
    /// <summary>Clamp is 1 in Riot's enum (Unity's TextureWrapMode ordering: 0 Wrap, 1 Clamp, 2 Mirror).
    /// Only Clamp is checked here - Riot really does ship 115 decal samplers at U2/V2 with no W.</summary>
    private static bool IsClamp(BinTreeStruct sampler) =>
        sampler.Properties.TryGetValue(F_addressU, out var p)
        && p is BinTreeU32 { Value: 1 };

    private static IEnumerable<BinTreeStruct> Passes(BinTreeObject o)
    {
        if (!o.Properties.TryGetValue(F_techniques, out var tp) || tp is not BinTreeContainer tc) yield break;
        foreach (var t in tc.Elements)
        {
            if (t is not BinTreeStruct technique) continue;
            yield return technique;                       // carries the 'passes' container
            if (!technique.Properties.TryGetValue(F_passes, out var pp) || pp is not BinTreeContainer pc) continue;
            foreach (var p in pc.Elements)
                if (p is BinTreeStruct pass) yield return pass;
        }
    }

    /// <summary>
    /// M556: walk a property tree for a blend factor of 8 (DstAlpha) or 9 (InvDstAlpha).
    ///
    /// <para>Recursive because the factors live on a pass, which lives in a container inside a technique,
    /// which lives in a container on the material - so a flat scan of the object's own properties never
    /// reaches them.</para>
    /// </summary>
    private static void CheckBlendFactors(List<BinIssue> issues, string objectName, uint hash,
        BinTreeObject owner, uint propertyHash, BinTreeProperty property, uint[] fields,
        Func<uint, string?>? resolveName)
    {
        switch (property)
        {
            case BinTreeU32 u when fields.Contains(propertyHash) && u.Value is 8 or 9:
                issues.Add(new BinIssue("blend-factor", objectName,
                    $"'{resolveName?.Invoke(propertyHash) ?? $"0x{propertyHash:x8}"}' is "
                    + $"{(u.Value == 9 ? "9 (InvDstAlpha)" : "8 (DstAlpha)")}, a factor that reads the "
                    + "framebuffer's ALPHA. Where that alpha is 1, InvDstAlpha makes the destination "
                    + "contribute nothing and the surface draws over BLACK instead of over what is behind "
                    + "it. Censused over 18 shipped map WADs, Riot uses 1, 4, 6 and 7 and neither 8 nor 9 "
                    + "even once; 164 of its 165 decal materials are 6 (SrcAlpha) / 7 (InvSrcAlpha).",
                    hash, owner.ClassHash));
                break;
            // BinTreeEmbedded derives from BinTreeStruct, so this one case covers both forms.
            case BinTreeStruct st:
                foreach (var (h, child) in st.Properties)
                    CheckBlendFactors(issues, objectName, hash, owner, h, child, fields, resolveName);
                break;
            case BinTreeContainer c:
                foreach (var child in c.Elements)
                    CheckBlendFactors(issues, objectName, hash, owner, propertyHash, child, fields, resolveName);
                break;
        }
    }
}
