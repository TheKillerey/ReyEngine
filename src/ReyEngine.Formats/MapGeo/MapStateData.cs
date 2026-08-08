using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.MapGeo;

/// <summary>One entry of <c>MapSkin.mAlternateAssets</c> — the assets that replace the skin's defaults
/// while a named visibility flag is active. Verified against Riot's shipped
/// <c>data/maps/shipping/map11/map11.bin</c>; the property names below are the real ones.</summary>
public sealed record MapAlternateAsset(
    /// <summary>mGrassTintTextureName. NOTE the name differs from the default's mGrassTintTexture.</summary>
    string? GrassTintTexture,
    string? FowOverlayTexture,
    /// <summary>mVisibilityFlagName, stored as a HASH (of "Fire", "earth", "Ocean", …), not a string.</summary>
    uint VisibilityFlagNameHash);

/// <summary>One entry of <c>Map.VisibilityFlagDefines.FlagDefinitions</c>.</summary>
/// <param name="NameHash">Hash of the internal name ("Fire", "earth", "Ocean", "CLOUD", …). This is what
/// <see cref="MapAlternateAsset.VisibilityFlagNameHash"/> points at.</param>
/// <param name="PublicName">The display name ("Infernal", "Mountain", …). Absent on the base entry.</param>
/// <param name="BitIndex">Which bit of a mapgeo group's visibility mask this flag owns.</param>
/// <param name="TransitionTime">Riot's authored transition duration IN SECONDS for entering this state.
/// Null when the bin does not author one — Hextech and Chemtech genuinely omit it, which is not the same
/// as zero and must not be defaulted to one.</param>
public sealed record MapVisibilityFlagDefinition(
    uint NameHash, string? PublicName, int BitIndex, float? TransitionTime);

/// <summary>A <c>MapSkin</c> object's asset set.</summary>
public sealed record MapSkinAssets(
    string Name,
    /// <summary>mGrassTintTexture — the skin's default grass tint.</summary>
    string? GrassTintTexture,
    IReadOnlyList<MapAlternateAsset> AlternateAssets,
    /// <summary>mMapContainerLink, e.g. "Maps/MapGeometry/Map11/Base_SRX" — which mapgeo this skin
    /// dresses. NOT unique: Default, SR_Seasonal_Map, LunarRevel and AprilFools2019 all point at
    /// Base_SRX, so it narrows the candidates rather than identifying one.</summary>
    string? MapContainerLink = null,
    /// <summary>The MapSkin's own <c>name</c> property ("Default", "SocialSR", …).</summary>
    string? SkinName = null);

/// <summary>
/// M384: the map-state half of a Map*.bin — grass tint (default + per-state alternates) and the
/// visibility-flag table that names the states and carries Riot's transition durations.
///
/// Renderer-agnostic on purpose: OpenGL and D3D11 both consume the SAME resolved values, and only differ
/// in how they hand them to the GPU.
/// </summary>
public sealed record MapStateData(
    IReadOnlyList<MapSkinAssets> Skins,
    IReadOnlyList<MapVisibilityFlagDefinition> FlagDefinitions)
{
    public static readonly MapStateData Empty =
        new(Array.Empty<MapSkinAssets>(), Array.Empty<MapVisibilityFlagDefinition>());

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // Class hashes
    private static readonly uint CMapSkin = H("MapSkin");
    private static readonly uint CMap = H("Map");
    // Field hashes — spelled exactly as they appear in the shipped bin.
    private static readonly uint FGrassTint = H("mGrassTintTexture");
    private static readonly uint FAltAssets = H("mAlternateAssets");
    private static readonly uint FAltGrassTint = H("mGrassTintTextureName");
    private static readonly uint FAltFow = H("mFowOverlayTextureName");
    private static readonly uint FAltFlag = H("mVisibilityFlagName");
    private static readonly uint FVisDefines = H("VisibilityFlagDefines");
    private static readonly uint FFlagDefs = H("FlagDefinitions");
    private static readonly uint FName = H("name");
    private static readonly uint FPublicName = H("PublicName");
    private static readonly uint FBitIndex = H("BitIndex");
    private static readonly uint FTransition = H("TransitionTime");
    private static readonly uint FContainerLink = H("mMapContainerLink");
    private static readonly uint FSkinName = H("name");

    /// <summary>Parse a Map*.bin. Never throws on unexpected shapes — a field that is missing or the wrong
    /// type is simply absent from the result, because this runs against modded bins too.</summary>
    public static MapStateData Parse(byte[] binBytes, Func<uint, string?>? resolveName = null)
    {
        BinTree tree;
        try { tree = new BinTree(new MemoryStream(binBytes, writable: false)); }
        catch { return Empty; }

        var skins = new List<MapSkinAssets>();
        var flags = new List<MapVisibilityFlagDefinition>();

        foreach (var (hash, obj) in tree.Objects)
        {
            if (obj.ClassHash == CMapSkin) skins.Add(ParseSkin(hash, obj, resolveName));
            else if (obj.ClassHash == CMap && flags.Count == 0) flags.AddRange(ParseFlags(obj));
        }

        return new MapStateData(skins, flags);
    }

    private static MapSkinAssets ParseSkin(uint hash, BinTreeObject obj, Func<uint, string?>? resolveName)
    {
        string? tint = Str(obj.Properties, FGrassTint);
        var alts = new List<MapAlternateAsset>();

        // MapSkin.mAlternateAssets is an EMBEDDED STRUCT of class MapAlternateAssets (plural) whose own
        // mAlternateAssets property holds the container. Two levels, same name - reading the outer one as
        // a container silently yields nothing, which is exactly how this was wrong the first time.
        var altNode = Get(obj.Properties, FAltAssets);
        if (altNode is BinTreeStruct wrapper) altNode = Get(wrapper.Properties, FAltAssets);

        if (altNode is BinTreeContainer c)
            foreach (var el in c.Elements)
            {
                if (el is not BinTreeStruct st) continue;
                alts.Add(new MapAlternateAsset(
                    Str(st.Properties, FAltGrassTint),
                    Str(st.Properties, FAltFow),
                    Get(st.Properties, FAltFlag) is BinTreeHash h ? h.Value : 0u));
            }

        return new MapSkinAssets(resolveName?.Invoke(hash) ?? $"0x{hash:x8}", tint, alts,
            Str(obj.Properties, FContainerLink), Str(obj.Properties, FSkinName));
    }

    private static IEnumerable<MapVisibilityFlagDefinition> ParseFlags(BinTreeObject obj)
    {
        // Map.VisibilityFlagDefines is an EMBEDDED struct, not a link.
        if (Get(obj.Properties, FVisDefines) is not BinTreeStruct defines) yield break;
        if (Get(defines.Properties, FFlagDefs) is not BinTreeContainer c) yield break;

        foreach (var el in c.Elements)
        {
            if (el is not BinTreeStruct st) continue;
            uint nameHash = Get(st.Properties, FName) is BinTreeHash nh ? nh.Value : 0u;
            if (nameHash == 0) continue;
            yield return new MapVisibilityFlagDefinition(
                nameHash,
                Str(st.Properties, FPublicName),
                Get(st.Properties, FBitIndex) is BinTreeU8 b ? b.Value
                    : Get(st.Properties, FBitIndex) is BinTreeU32 b32 ? (int)b32.Value : -1,
                Get(st.Properties, FTransition) is BinTreeF32 f ? f.Value : null);
        }
    }

    private static BinTreeProperty? Get(IReadOnlyDictionary<uint, BinTreeProperty> props, uint key)
        => props.TryGetValue(key, out var p) ? p : null;

    private static string? Str(IReadOnlyDictionary<uint, BinTreeProperty> props, uint key)
        => Get(props, key) is BinTreeString s && s.Value.Length > 0 ? s.Value : null;

    // ---- resolution -------------------------------------------------------------------------------

    /// <summary>
    /// The MapSkin that dresses a given mapgeo, chosen by the stem of its mMapContainerLink — for
    /// "…/map11/base_srx.mapgeo" that is "base_srx", matched against "Maps/MapGeometry/Map11/Base_SRX".
    ///
    /// <para>Several skins can claim the same container (Base_SRX is claimed by Default,
    /// SR_Seasonal_Map, LunarRevel and AprilFools2019), so this prefers the one literally named
    /// "Default" and otherwise takes the first candidate that actually authors a grass tint. Skins that
    /// author nothing are never preferred, because picking one would blank a map that has a tint.</para>
    /// </summary>
    /// <param name="mapGeoPath">Any path or name whose file stem identifies the mapgeo.</param>
    public MapSkinAssets? SkinForMapGeo(string? mapGeoPath)
    {
        if (string.IsNullOrWhiteSpace(mapGeoPath)) return null;
        string stem = Stem(mapGeoPath);
        if (stem.Length == 0) return null;

        var candidates = Skins
            .Where(s => s.MapContainerLink is { } l
                     && string.Equals(Stem(l), stem, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return null;

        return candidates.FirstOrDefault(s =>
                   string.Equals(s.SkinName, "Default", StringComparison.OrdinalIgnoreCase)
                   && s.GrassTintTexture is not null)
            ?? candidates.FirstOrDefault(s => s.GrassTintTexture is not null)
            ?? candidates.FirstOrDefault(s =>
                   string.Equals(s.SkinName, "Default", StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];
    }

    /// <summary>Last path segment without its extension. Handles both separators; Riot's links use '/'
    /// while the mapgeo path on disk may use '\'.</summary>
    private static string Stem(string path)
    {
        string p = path.Replace('\\', '/').TrimEnd('/');
        int slash = p.LastIndexOf('/');
        if (slash >= 0) p = p[(slash + 1)..];
        int dot = p.LastIndexOf('.');
        return dot > 0 ? p[..dot] : p;
    }

    public MapVisibilityFlagDefinition? FlagByBit(int bitIndex)
        => FlagDefinitions.FirstOrDefault(f => f.BitIndex == bitIndex);

    public MapVisibilityFlagDefinition? FlagByNameHash(uint nameHash)
        => FlagDefinitions.FirstOrDefault(f => f.NameHash == nameHash);

    /// <summary>Riot's authored transition duration for entering the state owned by
    /// <paramref name="bitIndex"/>, or null when the bin authors none. Null means "no transition
    /// authored" — the caller must NOT substitute a made-up default.</summary>
    public float? TransitionTimeForBit(int bitIndex) => FlagByBit(bitIndex)?.TransitionTime;

    /// <summary>
    /// The grass tint that should be active, given the visibility bits currently set.
    /// <para>Generic by construction: an alternate is chosen because its mVisibilityFlagName resolves to a
    /// flag definition whose BitIndex is set — no dragon name is ever compared. A map skin that invents its
    /// own states works with no code change.</para>
    /// </summary>
    /// <param name="skin">The active MapSkin.</param>
    /// <param name="isBitActive">Is the flag at this BIT INDEX currently on? Note INDEX, not mask — if
    /// you hold a mask use <see cref="ResolveGrassTintForMask"/> rather than converting at the call site,
    /// which is what M385 got wrong and shifted every state by one.</param>
    public GrassTintChoice ResolveGrassTint(MapSkinAssets? skin, Func<int, bool> isBitActive)
    {
        if (skin is null) return new GrassTintChoice(null, null, 0, -1, false);

        foreach (var alt in skin.AlternateAssets)
        {
            if (alt.GrassTintTexture is null || alt.VisibilityFlagNameHash == 0) continue;
            var def = FlagByNameHash(alt.VisibilityFlagNameHash);
            if (def is null || def.BitIndex < 0 || !isBitActive(def.BitIndex)) continue;
            return new GrassTintChoice(alt.GrassTintTexture, skin.GrassTintTexture,
                alt.VisibilityFlagNameHash, def.BitIndex, true);
        }

        return new GrassTintChoice(skin.GrassTintTexture, skin.GrassTintTexture, 0, -1, false);
    }

    /// <summary>
    /// M387: the same resolution from a visibility MASK — the form ReyEngine's UI actually holds, since
    /// VisibilityLayer.Bit is built as <c>1 &lt;&lt; i</c> and tested with <c>mask &amp; bit</c>.
    ///
    /// <para>Exists so callers never hand a mask to the index-taking overload. That mistake is silent:
    /// mask 1 equals index 1, mask 2 equals index 2, so it resolves a real (wrong) state for every
    /// selection instead of failing, and shows a plausible tint for a different dragon.</para>
    /// </summary>
    public GrassTintChoice ResolveGrassTintForMask(MapSkinAssets? skin, int activeMask)
        => ResolveGrassTint(skin, i => i >= 0 && i < 32 && (activeMask & (1 << i)) != 0);
}

/// <summary>What <see cref="MapStateData.ResolveGrassTint"/> decided, with enough context for the map
/// inspector to explain it without a separate debug window.</summary>
public sealed record GrassTintChoice(
    string? ActivePath, string? DefaultPath, uint VisibilityFlagNameHash, int BitIndex, bool FromAlternate)
{
    public string SourceLabel => FromAlternate ? "Alternate Asset" : "mGrassTintTexture";
}
