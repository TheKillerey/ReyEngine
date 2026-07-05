using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Meshes;

/// <summary>What a character skin's <c>skinMeshProperties</c> points at (M41): the mesh, skeleton and
/// default diffuse, plus any submeshes the skin hides. Enough to load and render the prop mesh.</summary>
public sealed record SkinMeshRef(string? SimpleSkin, string? Skeleton, string? DefaultTexture, IReadOnlyList<string> HiddenSubmeshes);

/// <summary>
/// Reads the <c>simpleSkin</c> / <c>skeleton</c> / default <c>texture</c> from a champion/creature skin .bin
/// (M41), so a placed animated prop (SRU_Baron, dragons, camps…) can be resolved to its SKN mesh. Never throws.
/// </summary>
public static class SkinMeshExtractor
{
    private static readonly uint F_skinMeshProperties = HashAlgorithms.Fnv1a("skinMeshProperties");
    private static readonly uint F_simpleSkin = HashAlgorithms.Fnv1a("simpleSkin");   // 0xd6a00df6
    private static readonly uint F_skeleton = HashAlgorithms.Fnv1a("skeleton");        // 0xb14c976e
    private static readonly uint F_texture = HashAlgorithms.Fnv1a("texture");          // 0x3c6468f4
    private static readonly uint F_hide = HashAlgorithms.Fnv1a("initialSubmeshToHide"); // 0x80b7f78f

    public static SkinMeshRef? Extract(byte[] skinBin)
    {
        BinTree tree;
        try { tree = SafeBinTree.Parse(skinBin); }
        catch { return null; }

        foreach (var o in tree.Objects.Values)
        {
            if (Get(o.Properties, F_skinMeshProperties) is not BinTreeStruct smp) continue;
            var hidden = ParseHidden((Get(smp.Properties, F_hide) as BinTreeString)?.Value);
            return new SkinMeshRef(
                (Get(smp.Properties, F_simpleSkin) as BinTreeString)?.Value,
                (Get(smp.Properties, F_skeleton) as BinTreeString)?.Value,
                (Get(smp.Properties, F_texture) as BinTreeString)?.Value,
                hidden);
        }
        return null;
    }

    /// <summary>initialSubmeshToHide is a space-separated list of submesh names.</summary>
    private static IReadOnlyList<string> ParseHidden(string? s) =>
        string.IsNullOrWhiteSpace(s) ? Array.Empty<string>()
        : s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static BinTreeProperty? Get(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => p.TryGetValue(hash, out var v) ? v : null;
}
