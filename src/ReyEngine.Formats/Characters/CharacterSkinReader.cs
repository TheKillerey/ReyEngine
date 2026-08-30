using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Characters;

/// <summary>One submesh whose texture (and sometimes material) differs from the skin's default.
/// Ahri's base skin has three; most Lux skins have none.</summary>
public sealed record CharacterMaterialOverride(string Submesh, string? TexturePath, string? Material);

/// <summary>
/// M609: what one champion skin is, read from its <c>data/characters/&lt;name&gt;/skins/skinN.bin</c>.
///
/// <para>The skin bin is the character's index: it names the mesh, the skeleton, the default texture and
/// the per-submesh overrides, and it carries the identity a browser needs to show. Pieces of this were
/// already read in four places for four purposes — <see cref="Meshes.SkinMeshExtractor"/> for the mesh,
/// <see cref="Materials.ChampionMaterialResolver"/> for the overrides, <see cref="Skeletons.ChampionAnimationData"/>
/// for hidden submeshes and audio banks — but nothing read it as one thing you could name and list.</para>
/// </summary>
public sealed record CharacterSkinInfo(
    int Number,
    string BinPath,
    /// <summary>Riot's internal name: "AhriHanbok", "WitchLux", sometimes just "AhriSkin04". NOT the
    /// marketing name — that lives in the client's skins.json, not in the game data.</summary>
    string CodeName,
    string MetaDataTags,
    string? MeshPath,
    string? SkeletonPath,
    string? TexturePath,
    string? LoadScreenPath,
    string? IconSquarePath,
    IReadOnlyList<string> InitiallyHiddenSubmeshes,
    IReadOnlyList<CharacterMaterialOverride> MaterialOverrides)
{
    /// <summary>The <c>skinline:</c> tag, when there is one — "wondersoftheworld", "infernal", "base".
    /// Riot writes these on nearly every skin, so they make a usable grouping when nothing better exists.</summary>
    public string? SkinLine => Tag("skinline");

    public string? Tag(string key)
    {
        foreach (var part in MetaDataTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                return part[(key.Length + 1)..];
        return null;
    }

    /// <summary>A skin with no mesh is a template, not something you can load. <c>root.bin</c> is the
    /// obvious case — it carries the champion's shared defaults and every champion ships one.</summary>
    public bool IsLoadable => !string.IsNullOrEmpty(MeshPath);
}

/// <summary>Reads <see cref="CharacterSkinInfo"/> out of a skin bin. Never throws on content — a skin
/// that cannot be understood comes back null rather than taking a browser listing down with it.</summary>
public static class CharacterSkinReader
{
    private static readonly uint ClassSkin = HashAlgorithms.Fnv1a("SkinCharacterDataProperties");
    private static readonly uint FSkinName = HashAlgorithms.Fnv1a("championSkinName");
    private static readonly uint FTags = HashAlgorithms.Fnv1a("metaDataTags");
    private static readonly uint FSkinMesh = HashAlgorithms.Fnv1a("skinMeshProperties");
    private static readonly uint FSimpleSkin = HashAlgorithms.Fnv1a("simpleSkin");
    private static readonly uint FSkeleton = HashAlgorithms.Fnv1a("skeleton");
    private static readonly uint FTexture = HashAlgorithms.Fnv1a("texture");
    private static readonly uint FInitialHide = HashAlgorithms.Fnv1a("initialSubmeshToHide");
    private static readonly uint FMaterialOverride = HashAlgorithms.Fnv1a("materialOverride");
    private static readonly uint FSubmesh = HashAlgorithms.Fnv1a("submesh");
    private static readonly uint FMaterial = HashAlgorithms.Fnv1a("material");
    private static readonly uint FLoadscreen = HashAlgorithms.Fnv1a("loadscreen");
    private static readonly uint FIconSquare = HashAlgorithms.Fnv1a("iconSquare");

    /// <param name="resolveWadPath">M590: <c>simpleSkin</c>, <c>skeleton</c> and <c>texture</c> became
    /// 64-bit WadChunkLinks in patch 16.17. Without this every path on a current skin reads as empty.</param>
    public static CharacterSkinInfo? Read(byte[] skinBin, string binPath, Func<ulong, string?>? resolveWadPath = null)
    {
        try
        {
            var tree = new BinTree(new MemoryStream(skinBin));
            var skin = tree.Objects.Values.FirstOrDefault(o => o.ClassHash == ClassSkin);
            return skin is null ? null : ReadObject(skin, binPath, resolveWadPath);
        }
        catch
        {
            return null;
        }
    }

    private static CharacterSkinInfo ReadObject(BinTreeObject skin, string binPath, Func<ulong, string?>? resolveWad)
    {
        string? mesh = null, skeleton = null, texture = null;
        var hidden = Array.Empty<string>();
        var overrides = new List<CharacterMaterialOverride>();

        if (Get(skin.Properties, FSkinMesh) is BinTreeStruct smp)
        {
            mesh = Path(Get(smp.Properties, FSimpleSkin), resolveWad);
            skeleton = Path(Get(smp.Properties, FSkeleton), resolveWad);
            texture = Path(Get(smp.Properties, FTexture), resolveWad);

            // Space-separated in one string, not a list: "Tail_Large Body_Proxy".
            if (Get(smp.Properties, FInitialHide) is BinTreeString hide && hide.Value.Length > 0)
                hidden = hide.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (Get(smp.Properties, FMaterialOverride) is BinTreeContainer list)
                foreach (var element in list.Elements)
                {
                    if (element is not BinTreeEmbedded o) continue;
                    string submesh = Str(o.Properties, FSubmesh);
                    if (submesh.Length == 0) continue;
                    overrides.Add(new CharacterMaterialOverride(
                        submesh,
                        Path(Get(o.Properties, FTexture), resolveWad),
                        Link(Get(o.Properties, FMaterial))));
                }
        }

        return new CharacterSkinInfo(
            NumberFromBinPath(binPath),
            binPath,
            Str(skin.Properties, FSkinName),
            Str(skin.Properties, FTags),
            mesh, skeleton, texture,
            Nested(Get(skin.Properties, FLoadscreen), resolveWad),
            Path(Unwrap(Get(skin.Properties, FIconSquare)), resolveWad),
            hidden,
            overrides);
    }

    /// <summary>The skin number from the file name — skin0.bin is 0, skin301.bin is 301. Riot names the
    /// files by number and nothing inside the bin repeats it, so this is the only source.
    /// <c>root.bin</c> and anything else unnumbered comes back -1.</summary>
    public static int NumberFromBinPath(string binPath)
    {
        string file = System.IO.Path.GetFileNameWithoutExtension(binPath.Replace('\\', '/'));
        return file.StartsWith("skin", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(file.AsSpan(4), out int n)
            ? n
            : -1;
    }

    // ---- property helpers -------------------------------------------------------------------------

    private static BinTreeProperty? Get(IReadOnlyDictionary<uint, BinTreeProperty> props, uint key) =>
        props.TryGetValue(key, out var value) ? value : null;

    /// <summary>iconSquare and friends are Optional — the payload is one level in.</summary>
    private static BinTreeProperty? Unwrap(BinTreeProperty? property) =>
        property is BinTreeOptional optional ? optional.Value : property;

    private static string Str(IReadOnlyDictionary<uint, BinTreeProperty> props, uint key) =>
        Get(props, key) is BinTreeString s ? s.Value : "";

    private static string? Path(BinTreeProperty? property, Func<ulong, string?>? resolveWad) =>
        BinTexturePath.Read(Unwrap(property), resolveWad) is { Length: > 0 } path ? path : null;

    /// <summary>The loadscreen is wrapped in a CensoredImage whose single field holds the texture.</summary>
    private static string? Nested(BinTreeProperty? property, Func<ulong, string?>? resolveWad) =>
        Unwrap(property) is BinTreeEmbedded embedded
            ? embedded.Properties.Values.Select(v => Path(v, resolveWad)).FirstOrDefault(p => p is not null)
            : null;

    private static string? Link(BinTreeProperty? property) => property switch
    {
        BinTreeObjectLink link when link.Value != 0 => $"0x{link.Value:x8}",
        BinTreeString s when s.Value.Length > 0 => s.Value,
        _ => null,
    };
}
