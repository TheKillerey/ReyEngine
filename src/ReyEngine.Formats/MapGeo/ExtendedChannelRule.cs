using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M444: which materials make the client read a LONGER per-mesh channel block, and why the mapgeo alone
/// cannot answer that question.
///
/// <para><b>The rule.</b> The client picks the per-mesh channel-block reader from the submesh's MATERIAL,
/// not from anything in the mapgeo. The default reader consumes 40 bytes — <c>BakedLight</c> and
/// <c>StationaryLight</c>, each a string plus two Vec2s. The reader selected by the Mantis materials
/// consumes 48: one extra <c>{string, 4 bytes}</c> immediately after <c>StationaryLight</c>.</para>
///
/// <para><b>Evidence, and its limits.</b> Two independent legs, neither of them a corpus measurement:</para>
/// <list type="number">
///   <item>Disassembly: the alternate reader at vtable <c>0x141B44228+0x28</c> consumes 48 bytes where the
///   default consumes 40, and the adjacent .rdata holds ENV_PROBE / ENV_PROBE_SCALE / ENV_PROBE_INDEX —
///   which is where the <c>{string, 4 bytes}</c> shape comes from.</item>
///   <item>A single-variable in-game bisect on Map11: Riot's stock materials.bin + our modded mapgeo
///   LOADED, the Mantis materials.bin + the same mapgeo CRASHED at +0x136343F, and the same pair plus 8
///   zero bytes after <c>StationaryLight</c> LOADED. 48 − 40 = 8, exactly the measured over-read.</item>
/// </list>
///
/// <para><b>What is NOT established.</b> Riot ships <b>0 of 207</b> mapgeo files whose sibling
/// materials.bin so much as mentions Mantis, so there is no shipped file that exercises this and the rule
/// has no corpus confirmation. The measured 8 bytes were all zero, so only the SIZE is confirmed by
/// experiment — the <c>{string, 4 bytes}</c> split comes from the disassembly alone, and a non-empty
/// string in that slot has never been observed anywhere. Treat a non-empty
/// <see cref="MapGeoBinary.Mesh.ExtendedChannelTexture"/> as unverified territory.</para>
/// </summary>
public static class ExtendedChannelRule
{
    /// <summary>Shader-name prefixes whose materials select the 48-byte reader. Only the Mantis family is
    /// implicated; the list is a prefix match because the family has cooked variants
    /// (<c>Mantis_Env_Baked_PBR</c>, and the alpha-tested / two-sided permutations beside it).</summary>
    public static readonly string[] ShaderPrefixes = { "Mantis" };

    /// <summary>True when this shader name selects the extended reader.</summary>
    public static bool IsExtendedShader(string? shaderName) =>
        shaderName is { Length: > 0 }
        && ShaderPrefixes.Any(p => shaderName.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The material names in a map's materials.bin that select the extended reader — the set to hand to
    /// <see cref="MapGeoBinary.Read(byte[], IReadOnlySet{string}?)"/> so the mesh section stays in sync.
    /// </summary>
    /// <param name="materialsBin">The map's <c>*.materials.bin</c> bytes.</param>
    /// <param name="resolve">Hash-to-name resolver, as <see cref="MaterialDocument.Parse"/> wants it.</param>
    public static IReadOnlySet<string> From(byte[] materialsBin, Func<uint, string?> resolve)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (materialsBin is null || materialsBin.Length == 0) return set;
        try
        {
            var doc = MaterialDocument.Parse(materialsBin, resolve);
            foreach (var m in doc.Materials)
                if (IsExtendedShader(m.RenderShader ?? m.ShaderName) && m.Name.Length > 0)
                    set.Add(m.Name);
        }
        catch
        {
            // A bin we cannot parse means we cannot know - and an EMPTY set is the safe answer, because it
            // reproduces the default reader, which is right for every file Riot ships.
        }
        return set;
    }

    /// <summary>
    /// M445: the same file with every extended channel entry removed — a form LeagueToolkit's
    /// <c>EnvironmentAsset</c> can parse.
    ///
    /// <para><b>Why this exists.</b> ReyEngine has two mapgeo readers. <see cref="MapGeoBinary"/> is ours
    /// and now knows the rule; the scene decoder is LeagueToolkit's, which assumes the 40-byte block and
    /// throws <c>IndexOutOfRangeException</c> on a file that uses 48. We cannot teach a third-party reader
    /// the material-dependent rule, but the VIEWER does not need those bytes — only the shipped file does.
    /// So the decoder gets a normalized copy while the authoritative bytes stay untouched.</para>
    ///
    /// <para><b>Safe because the mesh ORDER and COUNT are unchanged</b>, so
    /// <c>MapGeoMesh.Index</c> still identifies the same mesh in the real file. Never save the result:
    /// it is a decoding aid, not an edit.</para>
    /// </summary>
    /// <returns>The normalized bytes, or the input unchanged when nothing applies or the file cannot be
    /// read — callers get today's behaviour rather than an exception.</returns>
    public static byte[] WithoutExtendedEntries(byte[] mapGeo, IReadOnlySet<string>? extendedChannelMaterials)
    {
        ArgumentNullException.ThrowIfNull(mapGeo);
        if (extendedChannelMaterials is not { Count: > 0 }) return mapGeo;
        if (!MapGeoBinary.TryReadEditable(mapGeo, out var map, extendedChannelMaterials)) return mapGeo;
        if (!map.Meshes.Any(m => m.HasExtendedChannel)) return mapGeo;

        foreach (var mesh in map.Meshes)
        {
            mesh.HasExtendedChannel = false;
            mesh.ExtendedChannelTexture = "";
            mesh.ExtendedChannelValue = 0;
        }
        try { return map.Write(); }
        catch { return mapGeo; }
    }

    /// <summary>
    /// Bring a mesh's channel block into agreement with its materials: meshes that use an extended-reader
    /// material get the extra entry, meshes that stopped using one lose it. Returns the number of meshes
    /// whose layout changed.
    /// </summary>
    /// <remarks>Call this after ANY edit that changes which material a submesh points at. Getting it wrong
    /// does not fail loudly — the client reads the wrong number of bytes and every following mesh is
    /// garbage, which surfaces as a crash deep inside map load with no useful message.</remarks>
    public static int Apply(MapGeoBinary map, IReadOnlySet<string> extendedChannelMaterials)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(extendedChannelMaterials);

        int changed = 0;
        foreach (var mesh in map.Meshes)
        {
            bool want = mesh.Submeshes.Any(s => extendedChannelMaterials.Contains(s.Material));
            if (want == mesh.HasExtendedChannel) continue;

            mesh.HasExtendedChannel = want;
            if (!want) { mesh.ExtendedChannelTexture = ""; mesh.ExtendedChannelValue = 0; }
            changed++;
        }
        return changed;
    }
}
