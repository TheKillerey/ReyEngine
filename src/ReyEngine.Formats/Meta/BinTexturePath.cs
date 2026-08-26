using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M590: a texture reference that is EITHER a string path or a 64-bit wad-path hash.
///
/// <para>Patch 16.17 changed <c>StaticMaterialShaderSamplerDef.texturePath</c> from <c>String</c> to
/// <c>WadChunkLink</c>. Measured across the installed patch: <b>12,688 of 12,688</b> texturePath
/// properties in 199 shipped materials bins are WadChunkLink and <b>zero</b> are String, where the same
/// <c>base_srx.materials.bin</c> was 237 String / 0 link at both 16.14 and 16.15. Nothing gradual about
/// it — every map material flipped at once.</para>
///
/// <para>The hash is exactly <c>WadPath(the old string)</c>: hashing 16.15's 237 strings reproduces 235 of
/// the 277 links in the 16.17 file, the remaining 42 being textures added since. So the link is not a new
/// identifier, it is the same wad chunk hash the loader always derived from the string — which makes
/// <see cref="HashOf"/> the reliable way to LOAD a texture regardless of which form the file uses, and
/// better than the old path, which had to be hashed anyway.</para>
///
/// <para>Both forms stay supported rather than migrating one to the other: a mod project can hold bins
/// from either era, and a 16.15-based bin being edited must keep writing strings or the client that ships
/// with it reads nothing.</para>
/// </summary>
public static class BinTexturePath
{
    /// <summary>Is this property one of the two texture-reference forms?</summary>
    public static bool Is(BinTreeProperty? property) => property is BinTreeString or BinTreeWadChunkLink;

    /// <summary>
    /// The path to show and to look assets up by. An unresolvable hash stays hex — which is honest
    /// (the path genuinely is not known) and round-trips, because <see cref="Write"/> parses that form back.
    /// </summary>
    public static string Read(BinTreeProperty? property, Func<ulong, string?>? resolveWadPath = null)
        => property switch
        {
            BinTreeString s => s.Value,
            BinTreeWadChunkLink w when w.Value == 0 => "",
            BinTreeWadChunkLink w => resolveWadPath?.Invoke(w.Value) is { Length: > 0 } known ? known : Hex(w.Value),
            _ => "",
        };

    /// <summary>
    /// The wad chunk hash this reference points at, whichever form it takes — 0 when empty.
    /// This is what a loader should use: it is what the client resolves, and it works even when the path
    /// dictionary cannot name the chunk.
    /// </summary>
    public static ulong HashOf(BinTreeProperty? property) => property switch
    {
        BinTreeWadChunkLink w => w.Value,
        BinTreeString s => string.IsNullOrWhiteSpace(s.Value) ? 0UL : HashAlgorithms.WadPath(s.Value),
        _ => 0UL,
    };

    /// <summary>
    /// Set the reference, keeping the form the property already has. A hex literal is taken as a raw hash
    /// so a chunk whose path is unknown can still be round-tripped through the editor unchanged.
    /// </summary>
    /// <returns>false when the property is neither form.</returns>
    public static bool Write(BinTreeProperty? property, string? path)
    {
        string text = path ?? "";
        switch (property)
        {
            case BinTreeString s:
                s.Value = text;
                return true;
            case BinTreeWadChunkLink w:
                w.Value = text.Length == 0 ? 0UL
                        : TryParseHex(text, out ulong raw) ? raw
                        : HashAlgorithms.WadPath(text);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Build a reference of the same FORM as <paramref name="like"/>, for cloning a sampler into
    /// a bin whose era we must not change. Falls back to WadChunkLink, which is what 16.17 ships.</summary>
    public static BinTreeProperty Create(uint nameHash, string path, BinTreeProperty? like)
        => like is BinTreeString
            ? new BinTreeString(nameHash, path)
            : new BinTreeWadChunkLink(nameHash, path.Length == 0 ? 0UL
                : TryParseHex(path, out ulong raw) ? raw : HashAlgorithms.WadPath(path));

    /// <summary>Whether two references mean the same chunk, across forms.</summary>
    public static bool SameTarget(BinTreeProperty? a, BinTreeProperty? b) => HashOf(a) == HashOf(b);

    public static string Hex(ulong hash) => $"0x{hash:x16}";

    /// <summary>A 64-bit hash written as <c>0x…</c>. Deliberately strict about the prefix: a bare 16-digit
    /// filename-looking token is far more likely to be a path than a hash, and guessing wrong would
    /// silently repoint a texture.</summary>
    public static bool TryParseHex(string text, out ulong hash)
    {
        hash = 0;
        string t = text.Trim();
        if (!t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
        return ulong.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out hash);
    }
}
