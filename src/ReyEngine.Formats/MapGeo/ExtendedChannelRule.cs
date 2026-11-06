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
    /// <param name="readMapGeo">M859: the mapgeo's own bytes, read only when a material that selects the extended
    /// reader has no name in the hash tables. Since 16.21 a material's <c>name</c> is an XXH3 hash, so a mod's own
    /// Mantis material (a path no table knows) came out as <c>0x...</c>, the set held that instead of the path the
    /// mapgeo's submeshes carry, no mesh matched, and the file was read with the 40-byte block - the editor then
    /// failed on the first extended mesh with "vertexCount must be greater than 0". The mapgeo cannot be PARSED
    /// to find those names (that is the very question), but its strings can be scanned: see
    /// <see cref="NamesFromStrings"/>.</param>
    public static IReadOnlySet<string> From(byte[] materialsBin, Func<uint, string?> resolve, Func<byte[]?>? readMapGeo = null)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (materialsBin is null || materialsBin.Length == 0) return set;
        try
        {
            var doc = MaterialDocument.Parse(materialsBin, resolve);
            var unnamed = new List<MaterialBinding>();
            foreach (var m in doc.Materials)
            {
                if (!IsExtendedShader(m.RenderShader ?? m.ShaderName) || m.Name.Length == 0) continue;
                if (m.ObjectPathHash != 0 && m.Name == $"0x{m.ObjectPathHash:x8}") unnamed.Add(m);   // no table names it
                else set.Add(m.Name);
            }
            if (unnamed.Count > 0)
            {
                var names = NamesFromStrings(unnamed.Select(m => m.ObjectPathHash).ToHashSet(), materialsBin, readMapGeo);
                foreach (var m in unnamed)
                    if (names.TryGetValue(m.ObjectPathHash, out var text)) set.Add(text);
            }
        }
        catch
        {
            // A bin we cannot parse means we cannot know - and an EMPTY set is the safe answer, because it
            // reproduces the default reader, which is right for every file Riot ships.
        }
        return set;
    }

    /// <summary>
    /// M859: the text of material paths the hash tables do not know, found among the strings the project itself
    /// holds - the materials.bin's string values, then the mapgeo's own length-prefixed strings. A material's
    /// object key is the FNV-1a of its path (the same relation M847 uses), so a string is accepted only when its hash
    /// IS the key of a wanted material; nothing is guessed.
    /// </summary>
    public static Dictionary<uint, string> NamesFromStrings(IReadOnlySet<uint> wanted, byte[] materialsBin, Func<byte[]?>? readMapGeo)
    {
        var found = new Dictionary<uint, string>();
        void Offer(string text)
        {
            if (text.Length == 0) return;
            uint a = ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a(text), b = ReyEngine.Core.Hashing.HashAlgorithms.Fnv1aRaw(text);
            if (wanted.Contains(a)) found.TryAdd(a, text);
            if (wanted.Contains(b)) found.TryAdd(b, text);
        }

        try
        {
            var tree = ReyEngine.Formats.Meta.SafeBinTree.Parse(materialsBin);
            foreach (var obj in tree.Objects.Values) WalkStrings(obj.Properties.Values, Offer);
        }
        catch { /* the materials.bin was already parsed once to get here; a second failure only loses this source */ }

        if (found.Count < wanted.Count && readMapGeo is not null)
        {
            byte[]? geo = null;
            try { geo = readMapGeo(); } catch { }
            if (geo is { Length: > 8 })
                ScanLengthPrefixedStrings(geo, Offer);
        }
        return found;
    }

    private static void WalkStrings(IEnumerable<LeagueToolkit.Core.Meta.BinTreeProperty> props, Action<string> offer)
    {
        foreach (var p in props)
            switch (p)
            {
                case LeagueToolkit.Core.Meta.Properties.BinTreeString s: offer(s.Value); break;
                case LeagueToolkit.Core.Meta.Properties.BinTreeStruct st: WalkStrings(st.Properties.Values, offer); break;
                case LeagueToolkit.Core.Meta.Properties.BinTreeContainer c: WalkStrings(c.Elements, offer); break;
                case LeagueToolkit.Core.Meta.Properties.BinTreeMap m:
                    foreach (var kv in m) WalkStrings(new[] { kv.Key, kv.Value }, offer);
                    break;
            }
    }

    /// <summary>Every <c>u32 length + printable-ASCII</c> run in <paramref name="data"/> (1..260 bytes). The mapgeo
    /// stores material, texture and path strings this way; a false positive is harmless because the caller only keeps
    /// a string whose hash matches a wanted key.</summary>
    internal static void ScanLengthPrefixedStrings(byte[] data, Action<string> offer)
    {
        for (int i = 0; i + 5 <= data.Length; i++)
        {
            byte first = data[i + 4];
            if (first < 0x20 || first > 0x7e) continue;
            uint n = (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
            if (n == 0 || n > 260 || i + 4 + n > data.Length) continue;
            bool ok = true;
            for (int j = 0; j < n; j++)
            {
                byte c = data[i + 4 + j];
                if (c < 0x20 || c > 0x7e) { ok = false; break; }
            }
            if (ok) offer(System.Text.Encoding.ASCII.GetString(data, i + 4, (int)n));
        }
    }

    /// <summary>
    /// M859: put back what <see cref="WithoutExtendedEntries"/> took out. <paramref name="strippedEdited"/> is the
    /// stripped file after an edit that leaves the mesh list alone (a move, a bucket-grid rebuild);
    /// <paramref name="original"/> is the file before the strip. Each mesh gets its extended entry back with the
    /// texture and value it had - not a guess from its materials - so the result differs from
    /// <paramref name="strippedEdited"/> by exactly those entries.
    /// </summary>
    /// <returns>The file with the extended entries restored, or <paramref name="strippedEdited"/> unchanged when
    /// the original had none. Throws when the two files do not describe the same meshes: writing a file whose
    /// channel blocks are wrong is the crash this rule exists to prevent.</returns>
    public static byte[] WithExtendedEntries(byte[] strippedEdited, byte[] original, IReadOnlySet<string> extendedChannelMaterials)
    {
        ArgumentNullException.ThrowIfNull(strippedEdited);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(extendedChannelMaterials);
        if (extendedChannelMaterials.Count == 0) return strippedEdited;
        if (!MapGeoBinary.TryReadEditable(original, out var source, extendedChannelMaterials)
            || !source.Meshes.Any(m => m.HasExtendedChannel))
            return strippedEdited;
        if (!MapGeoBinary.TryReadEditable(strippedEdited, out var target, null))
            throw new InvalidDataException("The edited mapgeo could not be re-read to restore its extended channel entries.");
        if (target.Meshes.Count != source.Meshes.Count)
            throw new InvalidDataException(
                $"The edited mapgeo has {target.Meshes.Count} meshes but the original had {source.Meshes.Count}; the extended channel entries cannot be restored by index.");

        for (int i = 0; i < source.Meshes.Count; i++)
        {
            target.Meshes[i].HasExtendedChannel = source.Meshes[i].HasExtendedChannel;
            target.Meshes[i].ExtendedChannelTexture = source.Meshes[i].ExtendedChannelTexture;
            target.Meshes[i].ExtendedChannelValue = source.Meshes[i].ExtendedChannelValue;
        }
        return target.Write();
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
