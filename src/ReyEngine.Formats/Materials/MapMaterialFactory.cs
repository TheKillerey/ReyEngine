using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Materials;

/// <summary>
/// M123: creates new StaticMaterialDef objects in a map's .materials.bin by cloning an existing one
/// (the template) under a new object name. Cloning is the only safe way to author a material here —
/// a from-scratch object would need the full property schema; a clone keeps every switch, technique
/// and render-state block of a material that provably works on this map.
/// </summary>
public static class MapMaterialFactory
{
    /// <summary>Write an RGBA image as an uncompressed BGRA .dds (the game loads plain DDS fine).</summary>
    public static byte[] WriteDds(int width, int height, byte[] rgba)
    {
        var dds = new byte[128 + width * height * 4];
        void U32(int off, uint v) { dds[off] = (byte)v; dds[off+1] = (byte)(v >> 8); dds[off+2] = (byte)(v >> 16); dds[off+3] = (byte)(v >> 24); }
        U32(0, 0x20534444);              // "DDS "
        U32(4, 124);                     // header size
        U32(8, 0x0000100F);              // CAPS|HEIGHT|WIDTH|PITCH|PIXELFORMAT
        U32(12, (uint)height);
        U32(16, (uint)width);
        U32(20, (uint)(width * 4));      // pitch
        U32(76, 32);                     // pixel format size
        U32(80, 0x41);                   // DDPF_RGB | DDPF_ALPHAPIXELS
        U32(88, 32);                     // bit count
        U32(92, 0x00FF0000);             // R mask (A8R8G8B8 layout -> bytes on disk are BGRA)
        U32(96, 0x0000FF00);             // G
        U32(100, 0x000000FF);            // B
        U32(104, 0xFF000000);            // A
        U32(108, 0x1000);                // DDSCAPS_TEXTURE
        for (int i = 0; i < width * height; i++)
        {
            dds[128 + i * 4 + 0] = rgba[i * 4 + 2];   // B
            dds[128 + i * 4 + 1] = rgba[i * 4 + 1];   // G
            dds[128 + i * 4 + 2] = rgba[i * 4 + 0];   // R
            dds[128 + i * 4 + 3] = rgba[i * 4 + 3];   // A
        }
        return dds;
    }

    /// <summary>
    /// Clone <paramref name="templateName"/> as <paramref name="newName"/> inside the materials bin.
    /// Optionally repoints the clone's diffuse sampler and its pass shader. Returns the new bin bytes,
    /// or null with <paramref name="error"/> set.
    /// </summary>
    public static byte[]? CloneMaterial(byte[] materialsBin, string templateName, string newName,
        Func<uint, string?> resolve, out string? error, string? diffusePath = null, string? shaderPath = null)
    {
        error = null;
        try
        {
            // Tolerant parse (M123d): modded bins can carry duplicate struct fields (RitoBin permits
            // them; LT's strict dictionary throws). The map loader reads such bins fine via SafeBinTree,
            // so the factory must too - the duplicate field is dropped and the rewrite comes out clean.
            var tree = SafeBinTree.Parse(materialsBin);
            uint templateHash = HashAlgorithms.Fnv1a(templateName);
            if (!tree.Objects.TryGetValue(templateHash, out var template))
            {
                template = tree.Objects.Values.FirstOrDefault(o =>
                    string.Equals(resolve(o.PathHash), templateName, StringComparison.OrdinalIgnoreCase));
                if (template is null) { error = $"Template material '{templateName}' not found in the bin."; return null; }
            }

            uint newHash = HashAlgorithms.Fnv1a(newName);
            if (tree.Objects.ContainsKey(newHash)) { error = $"A material named '{newName}' already exists."; return null; }

            var clone = new BinTreeObject(newHash, template.ClassHash,
                template.Properties.Select(kv => BinTreeCloner.Clone(kv.Value, kv.Key)));

            // the material's own 'name' string mirrors its object path
            uint nameField = HashAlgorithms.Fnv1a("name");
            if (clone.Properties.TryGetValue(nameField, out var np) && np is BinTreeString ns)
                ns.Value = newName;

            if (diffusePath is not null) RepointDiffuse(clone, diffusePath, resolve);
            if (shaderPath is not null) RepointShader(clone, shaderPath, resolve);

            tree.Objects[newHash] = clone;
            using var outMs = new MemoryStream();
            tree.Write(outMs);
            return outMs.ToArray();
        }
        catch (Exception ex) { error = $"{ex.Message} ({ex.GetType().Name} in {ex.StackTrace?.Split((char)10).FirstOrDefault()?.Trim()})"; return null; }
    }

    /// <summary>
    /// M123b: author a StaticMaterialDef FROM a shader definition (data/shaders/shaders.bin), no map
    /// template involved. Schema surveyed across all 184 base_srx materials: type=0 always, technique
    /// named 'normal', minimal pass = just the shader objlink (54/184 ship exactly that), samplers are
    /// StaticMaterialShaderSamplerDef { TextureName, texturePath }, params carry the shader's vector4
    /// defaults, shaderMacros is a String->String map (empty is valid), switches may be omitted.
    /// </summary>
    public static byte[]? CreateFromShader(byte[] materialsBin, string newName,
        Shaders.LeagueShaderDef shader, out string? error, string? diffuseOverride = null)
    {
        error = null;
        try
        {
            var tree = SafeBinTree.Parse(materialsBin);   // tolerant - see CloneMaterial (M123d)
            uint newHash = HashAlgorithms.Fnv1a(newName);
            if (tree.Objects.ContainsKey(newHash)) { error = $"A material named '{newName}' already exists."; return null; }

            uint F(string n) => HashAlgorithms.Fnv1a(n);
            const uint SamplerClass = 0x0904b150;   // StaticMaterialShaderSamplerDef
            const uint ParamClass   = 0xde480eef;   // StaticMaterialShaderParamDef
            const uint TechClass    = 0x060a4413;   // StaticMaterialTechniqueDef
            const uint PassClass    = 0x8537d0c2;   // StaticMaterialPassDef
            const uint MaterialClass = 0xad4b8ac0;  // StaticMaterialDef (overwritten below from a real object when present)
            uint materialClass = tree.Objects.Values
                .Select(o => o.ClassHash)
                .FirstOrDefault(h => tree.Objects.Values.Count(x => x.ClassHash == h) > 3, MaterialClass);

            // samplers: the shader's declared textures with their default paths; the diffuse-ish one
            // takes the override (imported texture) when provided
            var samplers = new List<BinTreeProperty>();
            bool overrideUsed = false;
            foreach (var t in shader.Textures)
            {
                string path = t.DefaultTexturePath;
                bool diffuseIsh = t.Name.Contains("Diffuse", StringComparison.OrdinalIgnoreCase);
                if (diffuseOverride is not null && !overrideUsed && (diffuseIsh || shader.Textures.Count == 1))
                { path = diffuseOverride; overrideUsed = true; }
                samplers.Add(new BinTreeStruct(0, SamplerClass, new BinTreeProperty[]
                {
                    new BinTreeString(F("TextureName"), t.Name),
                    new BinTreeString(F("texturePath"), path),
                }));
            }
            if (diffuseOverride is not null && !overrideUsed && samplers.Count > 0)
            {
                // no obviously-diffuse sampler: repoint the first one
                var first = (BinTreeStruct)samplers[0];
                ((BinTreeString)first.Properties[F("texturePath")]).Value = diffuseOverride;
            }

            var parameters = shader.Parameters.Select(pd => (BinTreeProperty)new BinTreeStruct(0, ParamClass, new BinTreeProperty[]
            {
                new BinTreeString(F("name"), pd.Name),
                new BinTreeVector4(F("value"), new System.Numerics.Vector4(pd.X, pd.Y, pd.Z, pd.W)),
            })).ToList();

            var pass = new BinTreeStruct(0, PassClass, new BinTreeProperty[]
            {
                new BinTreeObjectLink(F("shader"), HashAlgorithms.Fnv1a(shader.Name)),
            });
            var technique = new BinTreeStruct(0, TechClass, new BinTreeProperty[]
            {
                new BinTreeString(F("name"), "normal"),
                new BinTreeContainer(F("passes"), BinPropertyType.Struct, new BinTreeProperty[] { pass }),
            });

            var props = new List<BinTreeProperty>
            {
                new BinTreeString(F("name"), newName),
                new BinTreeU32(F("type"), 0),
                new BinTreeMap(F("shaderMacros"), BinPropertyType.String, BinPropertyType.String,
                    Enumerable.Empty<KeyValuePair<BinTreeProperty, BinTreeProperty>>()),
                new BinTreeUnorderedContainer(F("samplerValues"), BinPropertyType.Struct, samplers),
                new BinTreeUnorderedContainer(F("paramValues"), BinPropertyType.Struct, parameters),
                new BinTreeContainer(F("techniques"), BinPropertyType.Struct, new BinTreeProperty[] { technique }),
            };

            tree.Objects[newHash] = new BinTreeObject(newHash, materialClass, props);
            using var outMs = new MemoryStream();
            tree.Write(outMs);
            return outMs.ToArray();
        }
        catch (Exception ex)
        {
            var frames = (ex.StackTrace ?? "").Split((char)10).Take(3).Select(f => f.Trim());
            error = $"{ex.Message} ({ex.GetType().Name}) @ {string.Join(" | ", frames)}";
            return null;
        }
    }

    private static void RepointDiffuse(BinTreeObject material, string diffusePath, Func<uint, string?> resolve)
    {
        if (material.Properties.Values.OfType<BinTreeContainer>()
                .FirstOrDefault(c => IsField(material, c, "samplerValues", resolve)) is not { } samplers) return;

        BinTreeString? best = null;
        foreach (var el in samplers.Elements.OfType<BinTreeStruct>())
        {
            string? sampler = null; BinTreeString? path = null;
            foreach (var (ph, pr) in el.Properties)
            {
                var n = resolve(ph);
                if (n is "TextureName" && pr is BinTreeString sn) sampler = sn.Value;   // the real field name (surveyed)
                if (n is "texturePath" or "textureName" && pr is BinTreeString tp) path = tp;
            }
            if (path is null) continue;
            best ??= path;
            if (sampler is not null && sampler.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)) { best = path; break; }
        }
        if (best is not null) best.Value = diffusePath;
    }

    private static void RepointShader(BinTreeObject material, string shaderPath, Func<uint, string?> resolve)
    {
        // first technique -> first pass -> shader objlink (same location the Material Editor edits)
        foreach (var (ph, pr) in material.Properties)
        {
            if (resolve(ph) != "techniques" || pr is not BinTreeContainer techs) continue;
            var tech = techs.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            if (tech is null) return;
            foreach (var (tph, tpr) in tech.Properties)
            {
                if (resolve(tph) != "passes" || tpr is not BinTreeContainer passes) continue;
                var pass = passes.Elements.OfType<BinTreeStruct>().FirstOrDefault();
                if (pass is null) return;
                foreach (var (pph, ppr) in pass.Properties)
                    if (resolve(pph) == "shader" && ppr is BinTreeObjectLink link)
                    { link.Value = HashAlgorithms.Fnv1a(shaderPath); return; }
            }
        }
    }

    private static bool IsField(BinTreeObject owner, BinTreeProperty prop, string name, Func<uint, string?> resolve)
    {
        foreach (var (ph, pr) in owner.Properties)
            if (ReferenceEquals(pr, prop)) return resolve(ph) == name;
        return false;
    }
}
