using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Characters;

/// <summary>One entry of a skin's <c>materialOverride</c> list: what that submesh draws with.</summary>
public sealed record CharacterSubmeshMaterial(string Submesh, string? MaterialPath, string? Texture);

/// <summary>
/// M703: give a character's submesh a material of its own.
///
/// <para><b>Why there is often nothing to edit.</b> A character material is optional. Riot's scenery
/// characters - the Golem, the waterwheel, the Yonkey - ship no StaticMaterialDef at all and draw from
/// the skin's own <c>skinMeshProperties</c> block, and so does everything the Character Creator writes.
/// A shader can only be changed on a material that exists, so this is the step that makes "change the
/// shader of this character" possible at all.</para>
///
/// <para><b>The shape is Riot's.</b> Over 30 champion WADs a skin's <c>materialOverride</c> holds
/// <c>SkinMeshDataProperties_MaterialOverride</c> entries in two forms: 1,997 name a material
/// (<c>Material</c> object link + <c>submesh</c> string) and 3,297 only repoint a texture
/// (<c>texture</c> + <c>submesh</c>). This writes the first form and leaves a texture already on the
/// entry alone. The material object is authored by <see cref="MapMaterialFactory.CreateFromShader"/>,
/// the same builder the map side uses - a bin is a bin - and is named the way Riot names them,
/// <c>&lt;skin object path&gt;/Materials/&lt;Submesh&gt;_inst</c>.</para>
/// </summary>
public static class CharacterMaterialBinder
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private static readonly uint F_skinMeshProperties = H("skinMeshProperties");
    private static readonly uint F_materialOverride = H("materialOverride");
    private static readonly uint F_material = H("Material");      // FNV-1a lower-cases: Material == material
    private static readonly uint F_submesh = H("submesh");
    private static readonly uint F_texture = H("texture");
    private static readonly uint OverrideClass = H("SkinMeshDataProperties_MaterialOverride");
    private static readonly uint SkinClass = H("SkinCharacterDataProperties");

    /// <summary>
    /// The shader a brand-new character material starts on, in preference order.
    ///
    /// <para>Measured over 30 champion WADs: <c>FresnelAlpha_Basic</c> is the only widely used skinned
    /// shader (47 materials) that declares exactly ONE sampler, a diffuse - so the new material needs
    /// nothing the skin does not already have. <c>Diffuse_Bloom</c> is the most used of all (273) and is
    /// the fallback; it asks for a mask as well. Whatever is chosen, the point is to have a material the
    /// shader picker can then change.</para>
    /// </summary>
    public static IReadOnlyList<string> PreferredShaders { get; } = new[]
    {
        "Shaders/SkinnedMesh/FresnelAlpha_Basic",
        "Shaders/SkinnedMesh/Diffuse_Bloom",
    };

    /// <summary>The preferred shader the catalogue has, else any skinned shader with a single sampler,
    /// else any skinned shader at all. Null when the catalogue has no skinned shader.</summary>
    public static LeagueShaderDef? PickShader(ShaderCatalog? catalog)
    {
        if (catalog is null) return null;
        foreach (var wanted in PreferredShaders)
            if (catalog.Shaders.FirstOrDefault(s => s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is { } hit)
                return hit;
        var skinned = catalog.Shaders.Where(s => s.Name.StartsWith(ShaderFamilies.Skinned, StringComparison.OrdinalIgnoreCase)).ToList();
        return skinned.FirstOrDefault(s => s.Textures.Count == 1) ?? skinned.FirstOrDefault();
    }

    /// <summary>Riot's own naming: <c>Characters/X/Skins/Skin0/Materials/Body_inst</c>.</summary>
    public static string MaterialPathFor(string skinObjectPath, string submesh)
    {
        var clean = new string(submesh.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
        if (clean.Length == 0) clean = "Submesh";
        if (char.IsAsciiDigit(clean[0])) clean = "S" + clean;
        return $"{skinObjectPath.TrimEnd('/')}/Materials/{char.ToUpperInvariant(clean[0])}{clean[1..]}_inst";
    }

    /// <summary>
    /// The skin's own object path - what the material is named after, and what a placement stores.
    ///
    /// <para>The hash database is asked first, because it spells the path the way Riot does. It does not
    /// know a character somebody made, though, and those are exactly the ones this is for: M706 was
    /// reported on a custom urf_ghost, where "no name in the hash database" stopped the whole feature.
    /// So the FILE says it instead - <c>data/characters/urf/skins/skin0.bin</c> is
    /// <c>Characters/Urf/Skins/Skin0</c> - and the derivation is only accepted when it hashes to the
    /// object actually in the bin, which is proof rather than a guess.</para>
    /// </summary>
    public static string? SkinObjectPath(byte[] skinBin, Func<uint, string?>? resolveBinName, string? binPath = null)
    {
        uint objectHash = 0;
        try
        {
            foreach (var o in SafeBinTree.Parse(skinBin).Objects.Values)
                if (o.ClassHash == SkinClass) { objectHash = o.PathHash; break; }
        }
        catch { return null; }
        if (objectHash == 0) return null;

        if (resolveBinName?.Invoke(objectHash) is { Length: > 0 } known) return known;
        if (ObjectPathFromBinPath(binPath) is { } derived && HashAlgorithms.Fnv1a(derived) == objectHash) return derived;
        return null;
    }

    /// <summary>
    /// The object path a skin bin's own file path implies: <c>data/characters/urf/skins/skin0.bin</c> ->
    /// <c>Characters/Urf/Skins/Skin0</c>. Riot's own casing differs (URF), which costs nothing: every bin
    /// name is hashed case-insensitively, and the caller proves the result against the bin anyway.
    /// </summary>
    public static string? ObjectPathFromBinPath(string? binPath)
    {
        if (string.IsNullOrWhiteSpace(binPath)) return null;
        string path = binPath.Replace('\\', '/').Trim('/');
        if (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0].Equals("data", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        if (parts.Count < 2) return null;
        return string.Join('/', parts.Select(Capitalise));
    }

    private static string Capitalise(string segment) =>
        segment.Length == 0 ? segment : char.ToUpperInvariant(segment[0]) + segment[1..];

    /// <summary>Every materialOverride entry the skin carries.</summary>
    public static IReadOnlyList<CharacterSubmeshMaterial> Overrides(byte[] skinBin, Func<uint, string?>? resolveBinName = null)
    {
        var found = new List<CharacterSubmeshMaterial>();
        BinTree tree;
        try { tree = SafeBinTree.Parse(skinBin); }
        catch { return found; }
        foreach (var o in tree.Objects.Values)
        {
            if (o.Properties.GetValueOrDefault(F_skinMeshProperties) is not BinTreeStruct smp) continue;
            if (smp.Properties.GetValueOrDefault(F_materialOverride) is not BinTreeContainer list) continue;
            foreach (var el in list.Elements.OfType<BinTreeStruct>())
            {
                string submesh = (el.Properties.GetValueOrDefault(F_submesh) as BinTreeString)?.Value ?? "";
                string? material = el.Properties.GetValueOrDefault(F_material) is BinTreeObjectLink link
                    ? resolveBinName?.Invoke(link.Value) ?? $"0x{link.Value:x8}"
                    : null;
                string? texture = BinTexturePath.Read(el.Properties.GetValueOrDefault(F_texture)) is { Length: > 0 } t ? t : null;
                found.Add(new CharacterSubmeshMaterial(submesh, material, texture));
            }
        }
        return found;
    }

    /// <summary>
    /// Point <paramref name="submesh"/> at <paramref name="materialPath"/>, adding the materialOverride
    /// list and the entry when they are not there. An entry that already exists keeps whatever else it
    /// carries - a texture override on it is the user's, not ours to drop. Returns the new bytes, or null
    /// with the reason.
    /// </summary>
    public static byte[]? Bind(byte[] skinBin, string submesh, string materialPath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(submesh)) { error = "The submesh has no name."; return null; }
        if (string.IsNullOrWhiteSpace(materialPath)) { error = "The material has no name."; return null; }
        BinTree tree;
        try { tree = SafeBinTree.Parse(skinBin); }
        catch (Exception ex) { error = "The skin bin could not be read: " + ex.Message; return null; }

        BinTreeStruct? meshProperties = null;
        foreach (var o in tree.Objects.Values)
            if (o.Properties.GetValueOrDefault(F_skinMeshProperties) is BinTreeStruct smp) { meshProperties = smp; break; }
        if (meshProperties is null) { error = "This bin has no skinMeshProperties, so it is not a character skin."; return null; }

        var link = new BinTreeObjectLink(F_material, H(materialPath));
        if (meshProperties.Properties.GetValueOrDefault(F_materialOverride) is BinTreeContainer existing)
        {
            var entry = existing.Elements.OfType<BinTreeStruct>().FirstOrDefault(e =>
                (e.Properties.GetValueOrDefault(F_submesh) as BinTreeString)?.Value?.Equals(submesh, StringComparison.OrdinalIgnoreCase) == true);
            if (entry is not null) entry.Properties[F_material] = link;
            else
            {
                // The list is rebuilt because a container's element list is not mutable in place.
                var elements = existing.Elements.ToList();
                elements.Add(NewEntry(link, submesh));
                meshProperties.Properties[F_materialOverride] = new BinTreeContainer(F_materialOverride, BinPropertyType.Embedded, elements);
            }
        }
        else
        {
            // list[embed] - the wire form every shipped skin uses. A Container (0x80) of Embedded (0x83);
            // the client silently drops a property whose form disagrees with its class.
            meshProperties.Properties[F_materialOverride] =
                new BinTreeContainer(F_materialOverride, BinPropertyType.Embedded, new BinTreeProperty[] { NewEntry(link, submesh) });
        }

        using var ms = new MemoryStream();
        try { tree.Write(ms); }
        catch (Exception ex) { error = "The edited skin bin could not be written: " + ex.Message; return null; }
        return ms.ToArray();
    }

    private static BinTreeEmbedded NewEntry(BinTreeObjectLink material, string submesh) =>
        // Riot's field order on this class: the material, then the submesh it is for.
        new(0, OverrideClass, new BinTreeProperty[] { material, new BinTreeString(F_submesh, submesh) });

    /// <summary>
    /// The whole step: author a material for this submesh from <paramref name="shader"/> and point the
    /// submesh at it. <paramref name="diffuse"/> seeds the shader's diffuse sampler, so the new material
    /// starts by drawing what the submesh already drew. Returns the new skin bin, or null with a reason.
    /// </summary>
    public static byte[]? AddMaterial(byte[] skinBin, string skinObjectPath, string submesh, LeagueShaderDef shader,
        string? diffuse, out string? error, out string materialPath)
    {
        materialPath = MaterialPathFor(skinObjectPath, submesh);
        var withMaterial = MapMaterialFactory.CreateFromShader(skinBin, materialPath, shader, out error, diffuse);
        if (withMaterial is null) return null;
        return Bind(withMaterial, submesh, materialPath, out error);
    }
}
