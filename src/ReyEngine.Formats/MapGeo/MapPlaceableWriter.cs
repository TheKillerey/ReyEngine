using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>Where a placement lives in the bin: which <c>MapPlaceableContainer</c> object, and its key
/// inside that container's <c>items</c> map. Measured over the shipping maps, this pair is unique -
/// 151,457 items, every key a <c>BinTreeHash</c>, and ZERO duplicate keys within a container.</summary>
public readonly record struct MapPlacementId(uint ContainerHash, uint ItemKey)
{
    public bool IsValid => ContainerHash != 0 && ItemKey != 0;
}

/// <summary>One placement edit. Every field is optional; null means "leave this alone".</summary>
public sealed record MapPlacementEdit(MapPlacementId Id)
{
    public Matrix4x4? Transform { get; init; }
    public Vector4? ColorModulate { get; init; }
    /// <summary>Re-point the placement at a different VFX system (the <c>system</c> object link).</summary>
    public uint? SystemLink { get; init; }
    public string? Name { get; init; }
    /// <summary>Replacement map visibility mask. Zero disables the placement without deleting it.</summary>
    public int? VisibilityFlags { get; init; }
    /// <summary>Replacement character skin path on an animated prop / mob placement.</summary>
    public string? Skin { get; init; }
    /// <summary>Delete the placement from its container.</summary>
    public bool Remove { get; init; }

    /// <summary>M206: insert a COPY of this placement under <see cref="Id"/>, rather than editing an
    /// existing one. The source is deep-cloned so the new placement carries every field the original had -
    /// including the ones ReyEngine does not model - and the edit's other verbs are then applied on top.
    /// <see cref="Id"/> must name a key that does not already exist in the container.</summary>
    public MapPlacementId? CloneOf { get; init; }
    /// <summary>Create a minimal MapParticle instead of cloning an existing placement. Used when the
    /// Workshop adds the first particle to a map.</summary>
    public bool CreateParticle { get; init; }

    /// <summary>M575: create a MapAudio placement - a Wwise event at a world position. Riot's own are
    /// exactly three fields (transform, name, EventName), so there is nothing to clone from.
    /// Requires <see cref="Transform"/> and <see cref="EventName"/>.</summary>
    public bool CreateSound { get; init; }

    /// <summary>The Wwise event a MapAudio placement plays. The bin stores the NAME; the bank stores only
    /// its FNV-1 hash, so a typo here is a placement that loads and stays silent.</summary>
    public string? EventName { get; init; }

    /// <summary>M696: create a decorative character placement - the form every scenery character on
    /// Map11 / Map12 / Map453 has (119 of 119 measured): transform, a hashed name, a <c>Character</c>
    /// pointer naming the record and the skin, and a <c>CharacterMesh</c> with the idle to play. No team,
    /// no attackable unit: the game draws and animates it and nothing can target it. Requires
    /// <see cref="Transform"/>, <see cref="CharacterRecord"/> and <see cref="Skin"/>.</summary>
    public bool CreateCharacter { get; init; }

    /// <summary>The record a created character placement names, e.g. "Characters/MyProp/CharacterRecords/Root".</summary>
    public string? CharacterRecord { get; init; }

    /// <summary>
    /// M723: the clip a created character placement idles with. Naming one also sets
    /// <c>PlayIdleAnimation</c>, which is what actually makes the prop move.
    ///
    /// <para>Unset means a prop that stands still: <c>IdleAnimationName</c> is written as Riot's own
    /// "Idle1" default, but without the flag - the honest form for a character whose graph we cannot
    /// name a clip in.</para>
    /// </summary>
    public string? IdleAnimation { get; init; }

    /// <summary>
    /// M747: create a <c>MapAnimatedProp</c> - Riot's other decorative placement, the one SR's ducks and
    /// Noxtorra use (3,058 on the shipped maps). It names a character by <see cref="PropName"/> and plays
    /// its idle, and it carries no <c>Character</c> component, no team and nothing attackable.
    ///
    /// <para>Why it exists here: every scenery-CHARACTER placement ReyEngine wrote on the ported Map453
    /// failed to spawn, including a plain S3Yonkey whose Riot-placed twins on the same map do spawn, and
    /// every test was a replay recorded on vanilla data. The working theory is that character placements
    /// are spawned by the server, which a client mod cannot reach, while this form is created by the client
    /// the way particles are. That is a theory until a placement written this way appears in game.</para>
    ///
    /// <para>Requires <see cref="Transform"/> and <see cref="PropName"/>. Uses <see cref="Name"/>,
    /// <see cref="IdleAnimation"/> and <see cref="SkinId"/>.</para>
    /// </summary>
    public bool CreateAnimatedProp { get; init; }

    /// <summary>M747: the character a <c>MapAnimatedProp</c> shows, by folder name ("Sru_Duckie"). All
    /// 3,058 shipped props name a character their map also lists in a <c>MapCharacterList</c>.</summary>
    public string? PropName { get; init; }

    /// <summary>M747: which <c>skins/skin&lt;N&gt;.bin</c> of <see cref="PropName"/> the prop uses. Riot
    /// never writes it as 0 (0 of 3,058) - the field is left out instead.</summary>
    public uint SkinId { get; init; }

    /// <summary>
    /// M748: make the placement appear only once the game clock passes this many seconds. Greater than
    /// zero sets it, zero removes it, null leaves it alone.
    ///
    /// <para>Written as a <c>LogicDriverVisibilityController</c> of the placement's own, whose
    /// <c>VisibilityDriver</c> is <c>FloatComparisonMaterialDriver(TimeMaterialDriver &gt; N)</c>, linked from
    /// the placement's <c>VisibilityController</c>. Riot never gates a map placement on time - 0 of the 678
    /// visibility controllers on the shipped maps are logic-driven - so this was built as an experiment,
    /// and M749 records the answer: a MapAnimatedProp gated at 60 appeared at 60 seconds in a replay of the
    /// Map453 port. So the client evaluates a logic-driven controller on a map prop, an empty
    /// TimeMaterialDriver reads the game clock, and operator 1 is "greater than". Measured on
    /// MapAnimatedProp only; a character placement is server-spawned (M747) and never reaches it.</para>
    ///
    /// <para><c>mOperator</c> 1 is read as "greater than" from Riot's own ladders: a sine compared with 0.95
    /// for brief sparkles, Rumble's velocity with 50, and the mirrored form (3, "less than") on Ezreal's
    /// health against 0.3 for the low-health effect.</para>
    /// </summary>
    public float? AppearAfterSeconds { get; init; }

    /// <summary>
    /// M751: turn an existing scenery-CHARACTER placement into a <c>MapAnimatedProp</c> in place - same
    /// container, same key, same transform - so a prop placed before M747 spawns without being placed
    /// again. Character placements are spawned by the server (M747); this form is created by the client.
    /// Only what the new form can say is carried: the character (its Root record), the skin number, the
    /// idle and whether it plays, and the visibility mask. Anything else is refused with the reason by
    /// <see cref="MapPlaceableWriter.WhyNotConvertible"/> rather than dropped.
    /// </summary>
    public bool ConvertToAnimatedProp { get; init; }
}

/// <summary>
/// M199 (tier 5.2): persists placement edits into a map's .materials.bin by editing the parsed TREE and
/// rewriting it, replacing the byte-signature patcher in <see cref="MapParticleWriter"/>.
///
/// <para><b>Why the old locator had to go.</b> It found each placement by scanning the raw file for the
/// exact 64 bytes of its original transform. Measured over the 8 shipping map WADs, <b>1,450 of 30,628
/// placements (365 groups) share an identical transform with another placement in the same bin</b> - so
/// editing one could silently patch the other. In Map11's <c>base.materials.bin</c>,
/// <c>SRUAP_Chaos_Inhibitor_runeTimer_mid1</c> and <c>SRUAP_Chaos_Inhibitor_Rubble_dust2</c> are one such
/// pair. The app already knew: it refused to save moved sounds derived from particle systems, precisely
/// because they share the particle's transform bytes. A further 2 placements carry no transform at all and
/// were therefore unaddressable. Identity by (container, item key) has none of those problems, and it is
/// what makes re-tinting, re-linking and removing possible at all - none of which has a byte signature.</para>
///
/// <para><b>The safety net.</b> A tree rewrite touches the whole file, where the old patcher touched 64
/// bytes. So the result is not trusted: it is re-parsed and compared object-by-object and
/// property-by-property against the original, and the write is REFUSED unless the only differences are the
/// ones that were asked for. That check is what makes replacing a proven byte-patcher defensible.</para>
/// </summary>
public static class MapPlaceableWriter
{
    private static readonly uint ContainerClass = HashAlgorithms.Fnv1a("MapPlaceableContainer");
    private static readonly uint F_items = HashAlgorithms.Fnv1a("items");
    private static readonly uint F_transform = HashAlgorithms.Fnv1a("transform");
    private static readonly uint F_colorModulate = HashAlgorithms.Fnv1a("colorModulate");
    private static readonly uint F_system = HashAlgorithms.Fnv1a("system");
    private static readonly uint F_name = HashAlgorithms.Fnv1a("name");
    private static readonly uint F_visibilityFlags = HashAlgorithms.Fnv1a("mVisibilityFlags");
    private static readonly uint F_characterRecord = HashAlgorithms.Fnv1a("characterRecord");
    private static readonly uint F_skin = HashAlgorithms.Fnv1a("skin");
    private static readonly uint F_groupName = HashAlgorithms.Fnv1a("groupName");
    private static readonly uint ParticleClass = HashAlgorithms.Fnv1a("MapParticle");

    /// <summary>Apply the edits. Returns the new bytes, or null with a reason.</summary>
    public static byte[]? WriteEdits(byte[] materialsBin, IReadOnlyList<MapPlacementEdit> edits, out string? error)
    {
        error = null;
        if (edits.Count == 0) return materialsBin;

        BinTree original, tree;
        try
        {
            original = SafeBinTree.Parse(materialsBin);
            tree = SafeBinTree.Parse(materialsBin);   // a second, independent parse to diff against
            // M413: placements are exactly what a lossy parse loses - they live in the map/big containers
            // whose tails get abandoned first. Rewriting the file from a partial read would delete every
            // placement that failed to parse, permanently and without a word.
            SafeBinTree.ThrowIfLossy(tree, "placement edits");
        }
        catch (Exception ex) { error = $"could not parse the .bin: {ex.Message}"; return null; }

        int applied = 0;
        var missing = new List<MapPlacementId>();
        foreach (var edit in edits)
        {
            if (!TryApply(tree, edit)) { missing.Add(edit.Id); continue; }
            applied++;
        }

        if (applied == 0)
        {
            error = "none of the edited placements could be located in the .bin.";
            return null;
        }
        if (missing.Count > 0)
            error = $"{missing.Count} of {edits.Count} placement(s) could not be located (applied {applied}).";

        byte[] result;
        try
        {
            using var ms = new MemoryStream();
            tree.Write(ms);
            result = ms.ToArray();
        }
        catch (Exception ex) { error = $"could not write the .bin: {ex.Message}"; return null; }

        // Re-parse and prove that nothing beyond the requested edits moved.
        BinTree reparsed;
        try { reparsed = SafeBinTree.Parse(result); }
        catch (Exception ex) { error = $"the rewritten .bin no longer parses: {ex.Message}"; return null; }

        if (UnintendedChange(original, reparsed, edits) is { } bad)
        {
            error = $"refusing to save: the rewrite changed something that was not edited ({bad}).";
            return null;
        }
        return result;
    }

    /// <summary>M206: a key no placement in this container is using. Derived from the source key so a
    /// given clone lands on the same key every time (a save must not churn), then walked forward on the
    /// vanishingly unlikely collision.
    ///
    /// <para>Any unused value is safe here: measured over 151,457 shipped items, the key is NOT derived
    /// from the placement's name - 0 match FNV-1a of it in either casing, and 92,079 items have no name at
    /// all - so nothing reconstructs it, and a newly added placement is by definition not referenced by
    /// anything else yet.</para></summary>
    public static uint NewItemKey(BinTree tree, MapPlacementId source)
    {
        var used = new HashSet<uint>();
        if (tree.Objects.TryGetValue(source.ContainerHash, out var c)
            && c.Properties.TryGetValue(F_items, out var ip) && ip is BinTreeMap m)
            foreach (var e in m)
                if (e.Key is BinTreeHash kh) used.Add(kh.Value);

        uint candidate = source.ItemKey * 2654435761u + 0x9E3779B9u;   // Knuth mix, so clones scatter
        while (candidate == 0 || used.Contains(candidate)) candidate++;
        return candidate;
    }

    /// <summary>Allocate a placement identity in the map's first placeable container. Returns default when
    /// the map has no container at all.</summary>
    public static MapPlacementId NewParticleId(BinTree tree, uint seed)
    {
        var container = tree.Objects.Values.FirstOrDefault(o => o.ClassHash == ContainerClass
            && o.Properties.GetValueOrDefault(F_items) is BinTreeMap);
        if (container is null) return default;
        var items = (BinTreeMap)container.Properties[F_items];
        var used = items.Where(e => e.Key is BinTreeHash).Select(e => ((BinTreeHash)e.Key).Value).ToHashSet();
        uint candidate = seed * 2654435761u + 0x9E3779B9u;
        while (candidate == 0 || used.Contains(candidate)) candidate++;
        return new MapPlacementId(container.PathHash, candidate);
    }

    /// <summary>
    /// M746: allocate a placement identity for a CHARACTER, in the container that already holds the map's
    /// characters.
    ///
    /// <para><see cref="NewParticleId"/> takes the first container the tree yields, and a map's containers
    /// are not interchangeable buckets: <c>mapContainer.chunks</c> names each one as a layer (Structures,
    /// jungle, Vfx, Audio, Design_Data, Test ...). On Map453 the first one is a scratch layer - Riot keeps
    /// a single test light at intensity 1000 in it, fifteen null entries and two bare markers, and not one
    /// character. Every prop the Character Creator placed went there, and none of them spawned in game,
    /// including a plain S3Yonkey whose Riot-placed twins on the same map do. Across the shipped maps the
    /// first container holds characters in 1 of the 25 bins that have any.</para>
    ///
    /// <para>So a character goes where the map already keeps characters: the container holding the most
    /// character placements (scenery or attackable), the same "follow the map's own layout" rule
    /// <see cref="MapCharacterListWriter"/> uses for the character lists. A map with no characters at all
    /// falls back to <see cref="NewParticleId"/>'s choice.</para>
    /// </summary>
    public static MapPlacementId NewCharacterId(BinTree tree, uint seed)
    {
        ArgumentNullException.ThrowIfNull(tree);
        BinTreeObject? best = null;
        int bestCount = 0;
        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != ContainerClass || o.Properties.GetValueOrDefault(F_items) is not BinTreeMap m) continue;
            int count = m.Count(e => e.Value is BinTreeStruct st
                && (st.ClassHash == CharacterItemClass || st.ClassHash == UnitCharacterItemClass));
            if (count > bestCount) { best = o; bestCount = count; }
        }
        if (best is null) return NewParticleId(tree, seed);

        var items = (BinTreeMap)best.Properties[F_items];
        var used = items.Where(e => e.Key is BinTreeHash).Select(e => ((BinTreeHash)e.Key).Value).ToHashSet();
        uint candidate = seed * 2654435761u + 0x9E3779B9u;
        while (candidate == 0 || used.Contains(candidate)) candidate++;
        return new MapPlacementId(best.PathHash, candidate);
    }

    /// <summary>M748: the path of the controller object that belongs to one placement. Derived from the
    /// placement's identity so a second edit finds and replaces the first one's controller rather than
    /// leaving it behind.</summary>
    public static string AppearAfterControllerPath(MapPlacementId id) =>
        $"ReyEngine/VisibilityControllers/AppearAfter/{id.ContainerHash:x8}_{id.ItemKey:x8}";

    /// <summary>M748: the seconds a placement waits before appearing, when it carries a controller this
    /// writer made; null otherwise.</summary>
    public static float? ReadAppearAfter(BinTree tree, BinTreeStruct placement)
    {
        if (placement.Properties.GetValueOrDefault(F_VisibilityController) is not BinTreeObjectLink link) return null;
        if (!tree.Objects.TryGetValue(link.Value, out var ctrl) || ctrl.ClassHash != LogicDriverVisibilityControllerClass) return null;
        if (ctrl.Properties.GetValueOrDefault(F_VisibilityDriver) is not BinTreeStruct cmp || cmp.ClassHash != FloatComparisonClass) return null;
        if (cmp.Properties.GetValueOrDefault(F_mValueA) is not BinTreeStruct { } a || a.ClassHash != TimeDriverClass) return null;
        if (cmp.Properties.GetValueOrDefault(F_mValueB) is not BinTreeStruct { } b || b.ClassHash != FloatLiteralClass) return null;
        return b.Properties.GetValueOrDefault(F_mValue) is BinTreeF32 v ? v.Value : 0f;
    }

    private static bool ApplyAppearAfter(BinTree tree, MapPlacementId id, BinTreeStruct placement, float seconds)
    {
        if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0) return false;
        uint ctrlHash = HashAlgorithms.Fnv1a(AppearAfterControllerPath(id));

        if (seconds == 0)
        {
            // Removing the gate: drop the link, and the controller if it is ours - a controller someone
            // else made stays, it may be shared.
            if (placement.Properties.GetValueOrDefault(F_VisibilityController) is BinTreeObjectLink l && l.Value == ctrlHash)
                placement.Properties.Remove(F_VisibilityController);
            tree.Objects.Remove(ctrlHash);
            return true;
        }

        // Riot writes PathHash first on every controller object it ships.
        var controller = new BinTreeObject(ctrlHash, LogicDriverVisibilityControllerClass, new BinTreeProperty[]
        {
            new BinTreeHash(F_PathHash, ctrlHash),
            new BinTreeStruct(F_VisibilityDriver, FloatComparisonClass, new BinTreeProperty[]
            {
                new BinTreeStruct(F_mValueA, TimeDriverClass, Array.Empty<BinTreeProperty>()),
                new BinTreeStruct(F_mValueB, FloatLiteralClass, new BinTreeProperty[] { new BinTreeF32(F_mValue, seconds) }),
                new BinTreeU32(F_mOperator, GreaterThan),
            }),
        });
        tree.Objects[ctrlHash] = controller;
        // On a placement the link is the LAST field (Riot's MapParticles: ..., eyeCandy, VisibilityController).
        placement.Properties.Remove(F_VisibilityController);
        placement.Properties[F_VisibilityController] = new BinTreeObjectLink(F_VisibilityController, ctrlHash);
        return true;
    }

    /// <summary>M751: why the placement cannot become a MapAnimatedProp, or null when it can.</summary>
    public static string? WhyNotConvertible(byte[] materialsBin, MapPlacementId id)
    {
        BinTree tree;
        try { tree = SafeBinTree.Parse(materialsBin); }
        catch (Exception ex) { return $"the materials bin does not parse: {ex.Message}"; }
        if (!tree.Objects.TryGetValue(id.ContainerHash, out var container)
            || container.Properties.GetValueOrDefault(F_items) is not BinTreeMap items)
            return "the placement's container is not in the materials bin.";
        foreach (var e in items)
            if (e.Key is BinTreeHash k && k.Value == id.ItemKey)
                return e.Value is BinTreeStruct s
                    ? (ConvertedFrom(s, id.ItemKey, out string? why) is null ? why : null)
                    : "the placement is not a struct.";
        return "the placement is not in its container any more.";
    }

    /// <summary>M751: whether Riot's own bin holds a placement under this (container, key). Such a placement
    /// is spawned by the server from Riot's data whatever the mod says, so converting it would have the
    /// client draw a second copy on top of the server's.</summary>
    public static bool ShippedBy(byte[] riotMaterialsBin, MapPlacementId id)
    {
        try
        {
            var tree = SafeBinTree.Parse(riotMaterialsBin);
            return tree.Objects.TryGetValue(id.ContainerHash, out var c)
                && c.Properties.GetValueOrDefault(F_items) is BinTreeMap m
                && m.Any(e => e.Key is BinTreeHash k && k.Value == id.ItemKey);
        }
        catch { return false; }
    }

    // The fields a scenery-character placement may carry and still be said as an animated prop.
    private static readonly HashSet<uint> ConvertibleFields = new()
    {
        HashAlgorithms.Fnv1a("transform"), HashAlgorithms.Fnv1a("name"), HashAlgorithms.Fnv1a("mVisibilityFlags"),
        HashAlgorithms.Fnv1a("Character"), HashAlgorithms.Fnv1a("CharacterMesh"), HashAlgorithms.Fnv1a("VisibilityController"),
    };

    /// <summary>M751: the MapAnimatedProp that says what this scenery-character placement says, or null
    /// with the reason. Refuses rather than drops: a field the new form has no place for is a reason.</summary>
    private static BinTreeStruct? ConvertedFrom(BinTreeStruct s, uint itemKey, out string? why)
    {
        why = null;
        if (s.ClassHash == AnimatedPropClass) { why = "it is already a client-side prop."; return null; }
        if (s.ClassHash == UnitCharacterItemClass)
        { why = "it is an attackable unit (a turret, inhibitor, nexus or camp) - a gameplay object, not a decoration."; return null; }
        if (s.ClassHash != CharacterItemClass) { why = "it is not a character placement."; return null; }
        if (s.Properties.Keys.FirstOrDefault(k => !ConvertibleFields.Contains(k)) is var extra && extra != 0)
        { why = $"it carries field 0x{extra:x8}, which an animated prop has no place for."; return null; }
        if (s.Properties.GetValueOrDefault(F_transform) is not BinTreeMatrix44 transform)
        { why = "it has no transform."; return null; }
        if (s.Properties.GetValueOrDefault(F_Character) is not BinTreeStruct character
            || character.Properties.GetValueOrDefault(F_characterRecord) is not BinTreeString record)
        { why = "it names no character record."; return null; }

        // An animated prop names a character, and the game takes its Root record; a placement on another
        // record (Jade_Turret's Jade_Outer, say) would silently become a different unit.
        var r = record.Value.Split('/');
        if (r.Length != 4 || !r[0].Equals("Characters", StringComparison.OrdinalIgnoreCase)
            || !r[2].Equals("CharacterRecords", StringComparison.OrdinalIgnoreCase)
            || !r[3].Equals("Root", StringComparison.OrdinalIgnoreCase))
        { why = $"it uses the record '{record.Value}', and an animated prop can only name a character's Root record."; return null; }
        string prop = r[1];

        uint skinId = 0;
        if (character.Properties.GetValueOrDefault(F_skin) is BinTreeString skin)
        {
            var k = skin.Value.Split('/');
            if (k.Length != 4 || !k[1].Equals(prop, StringComparison.OrdinalIgnoreCase)
                || !TrySkinNumber(skin.Value, out skinId))
            { why = $"its skin '{skin.Value}' is not one of {prop}'s own Skins/SkinN."; return null; }
        }

        string? idle = null;
        bool plays = false;
        if (s.Properties.GetValueOrDefault(F_CharacterMesh) is BinTreeStruct mesh)
        {
            idle = (mesh.Properties.GetValueOrDefault(F_IdleAnimationName) as BinTreeString)?.Value;
            plays = mesh.Properties.GetValueOrDefault(F_PlayIdleAnimation) is BinTreeBool { Value: true };
        }

        // Riot's field order: transform, name, mVisibilityFlags, PropName, PlayIdleAnimation,
        // IdleAnimationName, SkinID, Dimension - and a controller link last.
        var fields = new List<BinTreeProperty>
        {
            new BinTreeMatrix44(F_transform, transform.Value),
            // The character placement's name is a hash, so the text is gone; the key keeps it unique.
            new BinTreeString(F_name, $"{prop}_{itemKey:x8}"),
        };
        if (s.Properties.GetValueOrDefault(F_visibilityFlags) is { } visibility) fields.Add(visibility);
        fields.Add(new BinTreeString(F_PropName, prop));
        if (plays) fields.Add(new BinTreeBool(F_PlayIdleAnimation, true));
        if (!string.IsNullOrWhiteSpace(idle)) fields.Add(new BinTreeString(F_IdleAnimationName, idle));
        if (skinId != 0) fields.Add(new BinTreeU32(F_SkinID, skinId));
        fields.Add(new BinTreeU8(F_Dimension, RiotDimension));
        if (s.Properties.GetValueOrDefault(F_VisibilityController) is { } link) fields.Add(link);
        return new BinTreeStruct(0, AnimatedPropClass, fields);
    }

    /// <summary>M747: "Characters/X/Skins/Skin12" (or "Skin12") -> 12.</summary>
    public static bool TrySkinNumber(string skin, out uint number)
    {
        number = 0;
        string leaf = skin.Contains('/') ? skin[(skin.LastIndexOf('/') + 1)..] : skin;
        return leaf.StartsWith("Skin", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(leaf.AsSpan(4), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    /// <summary>
    /// M747: allocate a placement identity for a <c>MapAnimatedProp</c>: the container that already holds
    /// the most of them - Riot keeps them in their own layers (AnimProps_NightMarket, SRX_Duckies,
    /// Design_Base ...) - and on a map with none, where the map keeps its characters
    /// (<see cref="NewCharacterId"/>), since a prop names a character too.
    /// </summary>
    public static MapPlacementId NewAnimatedPropId(BinTree tree, uint seed)
    {
        ArgumentNullException.ThrowIfNull(tree);
        BinTreeObject? best = null;
        int bestCount = 0;
        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != ContainerClass || o.Properties.GetValueOrDefault(F_items) is not BinTreeMap m) continue;
            int count = m.Count(e => e.Value is BinTreeStruct st && st.ClassHash == AnimatedPropClass);
            if (count > bestCount) { best = o; bestCount = count; }
        }
        if (best is null) return NewCharacterId(tree, seed);

        var items = (BinTreeMap)best.Properties[F_items];
        var used = items.Where(e => e.Key is BinTreeHash).Select(e => ((BinTreeHash)e.Key).Value).ToHashSet();
        uint candidate = seed * 2654435761u + 0x9E3779B9u;
        while (candidate == 0 || used.Contains(candidate)) candidate++;
        return new MapPlacementId(best.PathHash, candidate);
    }

    /// <summary>
    /// M531: allocate MANY placement identities at once (any placement type - M575 sounds included).
    ///
    /// <para><see cref="NewParticleId"/> derives its key from the keys the tree holds RIGHT NOW, so
    /// calling it in a loop before applying anything hands out the same key repeatedly - and a batch
    /// write then silently drops all but the first, because TryApply refuses a key that is taken. A bulk
    /// import (Map2 ports 554 placements in one pass) has to reserve as it goes.</para>
    ///
    /// <para>Returns as many ids as there are seeds, in order, or an empty list when the map has no
    /// placeable container to put them in.</para>
    /// </summary>
    public static IReadOnlyList<MapPlacementId> NewPlacementIds(BinTree tree, IEnumerable<uint> seeds)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(seeds);

        var container = tree.Objects.Values.FirstOrDefault(o => o.ClassHash == ContainerClass
            && o.Properties.GetValueOrDefault(F_items) is BinTreeMap);
        if (container is null) return Array.Empty<MapPlacementId>();

        var items = (BinTreeMap)container.Properties[F_items];
        var used = items.Where(e => e.Key is BinTreeHash).Select(e => ((BinTreeHash)e.Key).Value).ToHashSet();

        var ids = new List<MapPlacementId>();
        foreach (uint seed in seeds)
        {
            uint candidate = seed * 2654435761u + 0x9E3779B9u;
            while (candidate == 0 || !used.Add(candidate)) candidate++;
            ids.Add(new MapPlacementId(container.PathHash, candidate));
        }
        return ids;
    }

    private static readonly uint MapAudioClass = HashAlgorithms.Fnv1a("MapAudio");
    private static readonly uint F_eventName = HashAlgorithms.Fnv1a("eventName");

    // M696: the decorative character placement. Its class has no name in the hash database; the value is
    // the one every such placement carries on the shipped maps (26 on Map453, 33 on Map12, 60 on Map11).
    private const uint CharacterItemClass = 0x9aa5b4bcu;
    // M746: the attackable character placement (turrets, inhibitors, nexus, camps) - also unnamed.
    private const uint UnitCharacterItemClass = 0xad65d8c4u;
    private static readonly uint F_Character = HashAlgorithms.Fnv1a("Character");
    private static readonly uint F_CharacterMesh = HashAlgorithms.Fnv1a("CharacterMesh");
    private static readonly uint F_IdleAnimationName = HashAlgorithms.Fnv1a("IdleAnimationName");
    // M723: the flag that makes a scenery prop animate at all. CharacterMeshGeComponentDef's schema
    // defaults it to FALSE, so a placement carrying only IdleAnimationName names a clip nobody plays -
    // which is why every prop this editor placed before now stood in bind pose in-game while the
    // viewport animated it. Riot writes it on exactly the props that are meant to move: 11 of Map11
    // bloom's 12 (the Gromp props), and on Map453 the 2 of 26 that are the S3Yonkeys - the other 24
    // there are turrets, inhibitors and the nexus, whose animation the server drives.
    private static readonly uint F_PlayIdleAnimation = HashAlgorithms.Fnv1a("PlayIdleAnimation");
    // M747: Riot's MapAnimatedProp, field for field from the schema database.
    private static readonly uint AnimatedPropClass = HashAlgorithms.Fnv1a("MapAnimatedProp");
    private static readonly uint F_PropName = HashAlgorithms.Fnv1a("PropName");
    private static readonly uint F_SkinID = HashAlgorithms.Fnv1a("SkinID");
    // "Dimension" (U8, schema default 0). Riot writes 6 on 2,906 of 3,058 props and nothing else when it
    // writes it at all; what the value means is not known, so it is copied rather than interpreted.
    private const uint F_Dimension = 0x670b6ae3u;
    private const byte RiotDimension = 6;
    // M748: the time gate, built from classes Riot ships.
    private static readonly uint F_VisibilityController = HashAlgorithms.Fnv1a("VisibilityController");
    private static readonly uint F_PathHash = HashAlgorithms.Fnv1a("PathHash");
    private static readonly uint F_VisibilityDriver = HashAlgorithms.Fnv1a("VisibilityDriver");
    private static readonly uint F_mValueA = HashAlgorithms.Fnv1a("mValueA");
    private static readonly uint F_mValueB = HashAlgorithms.Fnv1a("mValueB");
    private static readonly uint F_mOperator = HashAlgorithms.Fnv1a("mOperator");
    private static readonly uint F_mValue = HashAlgorithms.Fnv1a("mValue");
    private static readonly uint LogicDriverVisibilityControllerClass = HashAlgorithms.Fnv1a("LogicDriverVisibilityController");
    private static readonly uint FloatComparisonClass = HashAlgorithms.Fnv1a("FloatComparisonMaterialDriver");
    private static readonly uint TimeDriverClass = HashAlgorithms.Fnv1a("TimeMaterialDriver");
    private static readonly uint FloatLiteralClass = HashAlgorithms.Fnv1a("FloatLiteralMaterialDriver");
    private const uint GreaterThan = 1u;
    private static readonly uint SkinCharacterGeComponentDefClass = HashAlgorithms.Fnv1a("SkinCharacterGeComponentDef");
    private static readonly uint CharacterMeshGeComponentDefClass = HashAlgorithms.Fnv1a("CharacterMeshGeComponentDef");

    private static bool TryApply(BinTree tree, MapPlacementEdit edit)
    {
        if (!edit.Id.IsValid) return false;
        if (!tree.Objects.TryGetValue(edit.Id.ContainerHash, out var container)) return false;
        if (container.ClassHash != ContainerClass) return false;
        if (!container.Properties.TryGetValue(F_items, out var itemsProp) || itemsProp is not BinTreeMap items) return false;

        // M206: a clone inserts a deep copy of its source under this edit's (new) key first; everything
        // below then treats it as an ordinary placement, so the other verbs apply to the copy for free.
        if (edit.CloneOf is { } src)
        {
            if (items.Any(e => e.Key is BinTreeHash k && k.Value == edit.Id.ItemKey)) return false;  // key taken
            BinTreeProperty? sourceValue = null;
            foreach (var e in items)
                if (e.Key is BinTreeHash sk && sk.Value == src.ItemKey) { sourceValue = e.Value; break; }
            if (sourceValue is not BinTreeStruct sourceStruct) return false;

            var copy = BinTreeCloner.Clone(sourceStruct, 0);
            var withClone = items
                .Select(e => new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key, e.Value))
                .Append(new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, edit.Id.ItemKey), copy));
            items = new BinTreeMap(F_items, items.KeyType, items.ValueType, withClone);
            container.Properties[F_items] = items;
        }
        else if (edit.CreateParticle)
        {
            if (items.Any(e => e.Key is BinTreeHash k && k.Value == edit.Id.ItemKey)
                || edit.Transform is null || edit.SystemLink is null) return false;
            var properties = new List<BinTreeProperty>
            {
                new BinTreeString(F_name, edit.Name ?? "Workshop_Particle"),
                new BinTreeString(F_groupName, "Workshop"),
                new BinTreeMatrix44(F_transform, edit.Transform.Value),
                new BinTreeObjectLink(F_system, edit.SystemLink.Value),
            };
            // Absent is Riot's authored "ordinary/default visibility" state. Writing the editor's
            // permissive read fallback (255) would invent a value not present on shipped placements.
            if (edit.VisibilityFlags is { } authoredVisibility)
                properties.Add(new BinTreeU8(F_visibilityFlags, (byte)Math.Clamp(authoredVisibility, 0, 255)));
            var particle = new BinTreeStruct(0, ParticleClass, properties);
            items = new BinTreeMap(F_items, items.KeyType, items.ValueType,
                items.Select(e => new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key, e.Value))
                    .Append(new(new BinTreeHash(0, edit.Id.ItemKey), particle)));
            container.Properties[F_items] = items;
        }
        else if (edit.CreateSound)
        {
            if (items.Any(e => e.Key is BinTreeHash k && k.Value == edit.Id.ItemKey)
                || edit.Transform is null || string.IsNullOrWhiteSpace(edit.EventName)) return false;
            // Field for field what Riot ships: Map453's eight water emitters carry transform, name and
            // EventName and nothing else. No visibility flag - absent is the authored default.
            var sound = new BinTreeStruct(0, MapAudioClass, new BinTreeProperty[]
            {
                new BinTreeMatrix44(F_transform, edit.Transform.Value),
                new BinTreeString(F_name, edit.Name ?? "LegacyPortAudio"),
                new BinTreeString(F_eventName, edit.EventName!),
            });
            items = new BinTreeMap(F_items, items.KeyType, items.ValueType,
                items.Select(e => new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key, e.Value))
                    .Append(new(new BinTreeHash(0, edit.Id.ItemKey), sound)));
            container.Properties[F_items] = items;
        }
        else if (edit.CreateCharacter)
        {
            if (items.Any(e => e.Key is BinTreeHash k && k.Value == edit.Id.ItemKey)
                || edit.Transform is null || string.IsNullOrWhiteSpace(edit.CharacterRecord) || string.IsNullOrWhiteSpace(edit.Skin)) return false;
            // Field for field the shipped scenery placement: the name is a HASH (never a string on this
            // class), Character is a POINTER and CharacterMesh an EMBED - the client drops a property whose
            // wire form disagrees with the class, and a dropped Character is an invisible prop.
            bool plays = !string.IsNullOrWhiteSpace(edit.IdleAnimation);
            var mesh = new List<BinTreeProperty>();
            // M723: field order as the S3Yonkey placements carry it - the flag before the name.
            if (plays) mesh.Add(new BinTreeBool(F_PlayIdleAnimation, true));
            mesh.Add(new BinTreeString(F_IdleAnimationName, plays ? edit.IdleAnimation! : "Idle1"));
            var character = new BinTreeStruct(0, CharacterItemClass, new BinTreeProperty[]
            {
                new BinTreeMatrix44(F_transform, edit.Transform.Value),
                new BinTreeHash(F_name, HashAlgorithms.Fnv1a(edit.Name ?? edit.Skin!)),
                new BinTreeStruct(F_Character, SkinCharacterGeComponentDefClass, new BinTreeProperty[]
                {
                    new BinTreeString(F_characterRecord, edit.CharacterRecord!),
                    new BinTreeString(F_skin, edit.Skin!),
                }),
                new BinTreeEmbedded(F_CharacterMesh, CharacterMeshGeComponentDefClass, mesh),
            });
            items = new BinTreeMap(F_items, items.KeyType, items.ValueType,
                items.Select(e => new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key, e.Value))
                    .Append(new(new BinTreeHash(0, edit.Id.ItemKey), character)));
            container.Properties[F_items] = items;
        }
        else if (edit.CreateAnimatedProp)
        {
            if (items.Any(e => e.Key is BinTreeHash k && k.Value == edit.Id.ItemKey)
                || edit.Transform is null || string.IsNullOrWhiteSpace(edit.PropName)) return false;
            // Field order as Riot writes it (1,251 of 3,058 carry exactly this shape): transform, name,
            // PropName, PlayIdleAnimation, IdleAnimationName, SkinID, Dimension. Unlike the character
            // placement the name is a STRING on this class (3,058 of 3,058), and SkinID 0 is left out.
            bool plays = !string.IsNullOrWhiteSpace(edit.IdleAnimation);
            var fields = new List<BinTreeProperty>
            {
                new BinTreeMatrix44(F_transform, edit.Transform.Value),
                new BinTreeString(F_name, edit.Name ?? edit.PropName!),
                new BinTreeString(F_PropName, edit.PropName!),
            };
            if (plays)
            {
                fields.Add(new BinTreeBool(F_PlayIdleAnimation, true));
                fields.Add(new BinTreeString(F_IdleAnimationName, edit.IdleAnimation!));
            }
            if (edit.SkinId != 0) fields.Add(new BinTreeU32(F_SkinID, edit.SkinId));
            fields.Add(new BinTreeU8(F_Dimension, RiotDimension));

            items = new BinTreeMap(F_items, items.KeyType, items.ValueType,
                items.Select(e => new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key, e.Value))
                    .Append(new(new BinTreeHash(0, edit.Id.ItemKey), new BinTreeStruct(0, AnimatedPropClass, fields))));
            container.Properties[F_items] = items;
        }

        BinTreeProperty? key = null, value = null;
        foreach (var e in items)
            if (e.Key is BinTreeHash kh && kh.Value == edit.Id.ItemKey) { key = e.Key; value = e.Value; break; }
        if (key is null || value is not BinTreeStruct s) return false;

        if (edit.ConvertToAnimatedProp)
        {
            if (ConvertedFrom(s, edit.Id.ItemKey, out _) is not { } converted) return false;
            items = new BinTreeMap(F_items, items.KeyType, items.ValueType,
                items.Select(e => new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key,
                    e.Key is BinTreeHash ck && ck.Value == edit.Id.ItemKey ? converted : e.Value)));
            container.Properties[F_items] = items;
            s = converted;   // the other verbs of this edit apply to the new form
        }

        if (edit.Remove)
        {
            // Rebuild the map without this entry - BinTreeMap exposes no Remove.
            var kept = items.Where(e => !(e.Key is BinTreeHash h && h.Value == edit.Id.ItemKey))
                            .Select(e => new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key, e.Value));
            container.Properties[F_items] = new BinTreeMap(F_items, items.KeyType, items.ValueType, kept);
            return true;
        }

        if (edit.Transform is { } m) s.Properties[F_transform] = new BinTreeMatrix44(F_transform, m);
        if (edit.ColorModulate is { } c) s.Properties[F_colorModulate] = new BinTreeVector4(F_colorModulate, c);
        if (edit.SystemLink is { } link) s.Properties[F_system] = new BinTreeObjectLink(F_system, link);
        // The name's WIRE FORM belongs to the placement's class, not to the edit: particles and sounds
        // carry a string, the scenery character class carries a hash (119 of 119 measured). Writing the
        // wrong one is a property the client silently drops - the same rule as the visibility flags below.
        if (edit.Name is { } n)
            s.Properties[F_name] = s.Properties.GetValueOrDefault(F_name) is BinTreeHash
                ? new BinTreeHash(F_name, HashAlgorithms.Fnv1a(n))
                : new BinTreeString(F_name, n);
        if (edit.EventName is { } ev && !edit.CreateSound) s.Properties[F_eventName] = new BinTreeString(F_eventName, ev);
        if (edit.VisibilityFlags is { } visibility)
        {
            int maskValue = Math.Clamp(visibility, 0, 255);
            s.Properties[F_visibilityFlags] = s.Properties.GetValueOrDefault(F_visibilityFlags) switch
            {
                BinTreeU16 => new BinTreeU16(F_visibilityFlags, (ushort)maskValue),
                BinTreeU32 => new BinTreeU32(F_visibilityFlags, (uint)maskValue),
                _ => new BinTreeU8(F_visibilityFlags, (byte)maskValue),
            };
        }
        if (edit.AppearAfterSeconds is { } after && !ApplyAppearAfter(tree, edit.Id, s, after)) return false;
        if (edit.Skin is { } propSkin && s.ClassHash == AnimatedPropClass)
        {
            // M747: this class names its skin by NUMBER; "Characters/X/Skins/Skin3" becomes SkinID 3.
            if (!TrySkinNumber(propSkin, out uint number)) return false;
            if (number == 0) s.Properties.Remove(F_SkinID);
            else s.Properties[F_SkinID] = new BinTreeU32(F_SkinID, number);
        }
        else if (edit.Skin is { } skin)
        {
            var characterData = s.Properties.Values.OfType<BinTreeStruct>()
                .FirstOrDefault(x => x.Properties.ContainsKey(F_characterRecord));
            if (characterData is null) return false;
            characterData.Properties[F_skin] = new BinTreeString(F_skin, skin);
        }
        return true;
    }

    /// <summary>Null when the rewrite changed only what was asked for; otherwise a description of the first
    /// unintended difference. Objects are compared by path hash, then property-by-property; the containers
    /// holding edited placements are compared entry-by-entry so an edit does not excuse its neighbours.</summary>
    private static string? UnintendedChange(BinTree before, BinTree after, IReadOnlyList<MapPlacementEdit> edits)
    {
        var editedByContainer = edits.GroupBy(e => e.Id.ContainerHash)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Id.ItemKey).ToHashSet());
        // M206: a clone's key is absent from `before` on purpose, so it must not read as an intruder.
        var addedByContainer = edits.Where(e => e.CloneOf is not null || e.CreateParticle || e.CreateSound || e.CreateCharacter
                                                || e.CreateAnimatedProp)
            .GroupBy(e => e.Id.ContainerHash)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Id.ItemKey).ToHashSet());

        // M748: a time gate adds, replaces or removes one controller object per gated placement - exactly
        // those, and nothing else, may differ at the object level.
        var gateObjects = edits.Where(e => e.AppearAfterSeconds is not null)
            .Select(e => HashAlgorithms.Fnv1a(AppearAfterControllerPath(e.Id))).ToHashSet();
        foreach (var hash in after.Objects.Keys)
            if (!before.Objects.ContainsKey(hash) && !gateObjects.Contains(hash))
                return $"object 0x{hash:x8} appeared without being asked for";

        foreach (var (hash, a) in before.Objects)
        {
            if (gateObjects.Contains(hash)) continue;
            if (!after.Objects.TryGetValue(hash, out var b)) return $"object 0x{hash:x8} disappeared";
            if (a.ClassHash != b.ClassHash) return $"object 0x{hash:x8} changed class";

            if (!editedByContainer.TryGetValue(hash, out var editedKeys))
            {
                if (!BinPropEquality.DictsEqual(a.Properties, b.Properties))
                    return $"untouched object 0x{hash:x8} changed";
                continue;
            }

            // An edited container: every property except `items` must be untouched, and inside `items`
            // every entry except the edited keys must be untouched.
            foreach (var (ph, pa) in a.Properties)
            {
                if (ph == F_items) continue;
                if (!b.Properties.TryGetValue(ph, out var pb) || !BinPropEquality.PropsEqual(pa, pb))
                    return $"container 0x{hash:x8} property 0x{ph:x8} changed";
            }
            if (a.Properties[F_items] is not BinTreeMap ma || b.Properties[F_items] is not BinTreeMap mb)
                return $"container 0x{hash:x8} items is no longer a map";

            var afterByKey = mb.Where(e => e.Key is BinTreeHash)
                               .ToDictionary(e => ((BinTreeHash)e.Key).Value, e => e.Value);
            var added = addedByContainer.GetValueOrDefault(hash) ?? new HashSet<uint>();
            var beforeKeys = ma.Where(e => e.Key is BinTreeHash).Select(e => ((BinTreeHash)e.Key).Value).ToHashSet();
            foreach (var k in afterByKey.Keys)
                if (!beforeKeys.Contains(k) && !added.Contains(k))
                    return $"placement 0x{k:x8} appeared without being asked for";
            foreach (var e in ma)
            {
                if (e.Key is not BinTreeHash kh) continue;
                bool wasEdited = editedKeys.Contains(kh.Value);
                bool stillThere = afterByKey.TryGetValue(kh.Value, out var vb);
                if (!stillThere)
                {
                    if (!wasEdited) return $"placement 0x{kh.Value:x8} vanished without being edited";
                    continue;   // a requested Remove
                }
                if (!wasEdited && !BinPropEquality.PropsEqual(e.Value, vb))
                    return $"untouched placement 0x{kh.Value:x8} changed";
            }
        }
        return null;
    }
}
