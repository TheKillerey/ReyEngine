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

    /// <param name="resolveWadPath">M590: simpleSkin/skeleton/texture became WadChunkLink in 16.17.</param>
    public static SkinMeshRef? Extract(byte[] skinBin, Func<ulong, string?>? resolveWadPath = null)
    {
        BinTree tree;
        try { tree = SafeBinTree.Parse(skinBin); }
        catch { return null; }

        foreach (var o in tree.Objects.Values)
        {
            if (Get(o.Properties, F_skinMeshProperties) is not BinTreeStruct smp) continue;
            var hidden = ParseHidden((Get(smp.Properties, F_hide) as BinTreeString)?.Value);
            return new SkinMeshRef(
                Meta.BinTexturePath.Read(Get(smp.Properties, F_simpleSkin), resolveWadPath) is { Length: > 0 } sk ? sk : null,
                Meta.BinTexturePath.Read(Get(smp.Properties, F_skeleton), resolveWadPath) is { Length: > 0 } sl ? sl : null,
                Meta.BinTexturePath.Read(Get(smp.Properties, F_texture), resolveWadPath) is { Length: > 0 } tx ? tx : null,
                hidden);
        }
        return null;
    }

    /// <summary>initialSubmeshToHide is a space-separated list of submesh names. M724: through the one
    /// splitter, which also accepts the comma/semicolon forms hand-edited mod bins carry.</summary>
    private static IReadOnlyList<string> ParseHidden(string? s) =>
        Skeletons.ChampionAnimationData.SplitSubmeshList(s);

    private static BinTreeProperty? Get(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => p.TryGetValue(hash, out var v) ? v : null;
}
