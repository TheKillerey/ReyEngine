using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Characters;

/// <summary>What the game's own wiring says a VFX system is for.</summary>
public enum VfxSystemRole
{
    /// <summary>Nothing in the files names it. The name is all there is to go on.</summary>
    Unknown,
    /// <summary>A spell flies it: <c>mMissileEffectKey</c>.</summary>
    Missile,
    /// <summary>A spell plays it where it lands, usually on a bone of whatever it hit:
    /// <c>mHitEffectKey</c>, <c>mAfterEffectKey</c>.</summary>
    Target,
    /// <summary>It hangs on a bone of the champion carrying it - an idle effect, or one a buff turns on.</summary>
    Bone,
}

/// <summary>
/// One statement the game makes about a VFX system: what plays it, and how.
/// </summary>
/// <param name="Role">What it is for.</param>
/// <param name="Owner">Who plays it - the spell's script name, or the buff condition that turns it on.</param>
/// <param name="Motion">How its missile crosses the distance, when it is one.</param>
/// <param name="Width">The missile's authored width, or 0.</param>
/// <param name="Bone">The bone it hangs on, or that a hit effect plays at, when the record names one.</param>
public sealed record VfxSystemLink(
    VfxSystemRole Role,
    string Owner,
    MissileMotion Motion = default,
    float Width = 0f,
    string? Bone = null)
{
    /// <summary>One line for the editor to show, so the reader can see what the rig was decided from.</summary>
    public string Why => Role switch
    {
        VfxSystemRole.Missile => Motion.Kind switch
        {
            MissileMotionKind.ConstantSpeed when Motion.Value > 0f =>
                $"{Owner} flies this, at {Motion.Value:0} units a second. The spell record says so.",
            MissileMotionKind.FixedDuration when Motion.Value > 0f =>
                $"{Owner} flies this, over {Motion.Value:0.##} seconds however far it goes. The spell record says so.",
            _ => $"{Owner} flies this. Its movement authors no speed this reader can use, so the rig keeps its own.",
        },
        VfxSystemRole.Target => Bone is { Length: > 0 }
            ? $"{Owner} plays this where it lands, on the bone {Bone}. It does not travel."
            : $"{Owner} plays this where it lands. It does not travel.",
        VfxSystemRole.Bone => Bone is { Length: > 0 }
            ? $"It hangs on the bone {Bone}{(Owner.Length > 0 ? ", under " + Owner : "")}. It does not travel."
            : $"It hangs on the champion{(Owner.Length > 0 ? ", under " + Owner : "")}. It does not travel.",
        _ => "",
    };

    /// <summary>
    /// The rig this link asks for, built on <paramref name="basis"/> so the reader's own height and
    /// replay settings survive.
    ///
    /// <para>Only the missile moves. A target or bone effect is carried by something this preview does not
    /// have - the thing that was hit, or the champion that owns it - so it is stood still and the reason is
    /// shown rather than a motion being invented for it.</para>
    /// </summary>
    public VfxPreviewRig Rig(VfxPreviewRig basis)
    {
        if (Role != VfxSystemRole.Missile) return basis with { Mode = VfxRigMode.Still };

        float speed = Motion.Kind switch
        {
            MissileMotionKind.ConstantSpeed when Motion.Value > 0f => Motion.Value,
            MissileMotionKind.FixedDuration when Motion.Value > 0f => basis.Distance / Motion.Value,
            _ => basis.Speed,
        };
        // Authored speeds run from 0.5 to 2,500,000 units a second - Aurelion Sol's E is the top of it -
        // and the median is 1,750. A flight of half a millisecond is not something a preview can show, so
        // the speed is capped at whatever crosses the rig in a twentieth of a second and the note says the
        // spell's own number. The floor is the other end: Hwei's W is authored at 0.5, which would take
        // forty minutes to cross the rig.
        float fastest = basis.Distance / 0.05f;
        float slowest = basis.Distance / 30f;
        return basis with { Mode = VfxRigMode.Missile, Speed = Math.Clamp(speed, slowest, fastest) };
    }
}

/// <summary>
/// M713: read what the game says a VFX system is for, instead of guessing from its name.
///
/// <para><b>Why the guess was a fallback.</b> M712 opened the preview's rig on the system's name, which is
/// right about 84% of the time and wrong in ways that matter - a dash and a recall are written like a
/// missile. The game does not guess: a spell record names the system it flies, and hands over its speed
/// with it. Over 174 champion archives, 1,255 spell records name a missile effect and <b>1,254 of them
/// resolve end to end</b>. The single miss is a skin deliberately mapping the key to nothing.</para>
///
/// <para><b>The chain is three bins, and they are three different files.</b> The spell objects live in the
/// champion's root bin (5,885 of 5,888). The key they carry is skin-independent, and the skin bin's
/// <c>ResourceResolver.resourceMap</c> turns it into that skin's system - key a Hash, value an object
/// link, 14,361 of 14,361 in both cases. The system itself usually lives somewhere else again: 85% of
/// resolved links point into a <c>&lt;champ&gt;_multi_skins_…</c> dependency bin shared across skins.</para>
///
/// <para><b>Where the speed really is.</b> <c>mMissileSpec.movementComponent.mSpeed</c> is right for 1,114
/// of the 1,162 specs that author a speed and is NOT a rule: the component is polymorphic over twelve
/// classes. 141 author <c>mTravelTime</c> instead, on <c>FixedTimeMovement</c>; 69 are
/// <c>AcceleratingMovement</c> and author none of either. Ahri is the case that makes the point - her
/// passive, her Q, her Q-return and her W all read NOTHING under the claimed path, so a reader that knows
/// only <c>mSpeed</c> is silent on her signature ability. <see cref="ChampionSpellData"/> switches on the
/// FIELDS rather than the class for exactly this reason, and M713 taught it <c>mInitialSpeed</c> as
/// well.</para>
///
/// <para><b>What is not read here.</b> Clip particle events (<c>ParticleEventData</c>, 56,380 keys) are
/// the largest bone-attached population and live in the animation bins, which this does not open. Child
/// effects (<c>VfxChildIdentifier</c>, 39,663) are already recognised by name. Neither changes what the
/// preview can carry, because a bone rig needs the champion standing beside the effect and this window
/// has no champion.</para>
/// </summary>
public static class VfxSystemRoles
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly uint SkinClass = H("SkinCharacterDataProperties");
    private static readonly uint F_persistentConditions = H("PersistentEffectConditions");
    private static readonly uint F_persistentVfxs = H("PersistentVfxs");
    private static readonly uint F_ownerCondition = H("OwnerCondition");
    private static readonly uint F_spell = H("Spell");
    private static readonly uint F_effectKey = H("effectKey");
    private static readonly uint F_boneName = H("boneName");
    // Confirmed by dumping the real bins rather than guessed from the class name: the container is
    // `idleParticlesEffects` (Lux Skin0 carries two), and OwnerCondition.Spell is a HASH of a spell's
    // object path, not a string - reading it as one would have returned nothing, silently.
    private static readonly uint F_idleEffects = H("idleParticlesEffects");

    /// <summary>
    /// Every effect key a champion's spells name, and what naming it means.
    ///
    /// <para>Reads the champion's ROOT bin - <c>data/characters/&lt;champ&gt;/&lt;champ&gt;.bin</c> - through
    /// <see cref="ChampionSpellData"/>, so there is one reader of a spell record in this codebase rather
    /// than two.</para>
    /// </summary>
    public static IReadOnlyDictionary<uint, VfxSystemLink> FromSpells(byte[] championBin, string? characterName = null)
    {
        var byKey = new Dictionary<uint, VfxSystemLink>();
        IReadOnlyList<AbilitySlot> slots;
        try { slots = ChampionSpellData.Read(championBin, characterName); }
        catch { return byKey; }

        foreach (var slot in slots)
            foreach (var missile in slot.Missiles)
            {
                string owner = missile.SpellName.Length > 0 ? missile.SpellName : slot.Slot;
                // The missile wins over the hit effect when a key is somehow both: it is the one that moves.
                if (missile.MissileEffectKey != 0)
                    byKey[missile.MissileEffectKey] =
                        new VfxSystemLink(VfxSystemRole.Missile, owner, missile.Motion, missile.Width);
                if (missile.HitEffectKey != 0 && !byKey.ContainsKey(missile.HitEffectKey))
                    byKey[missile.HitEffectKey] = new VfxSystemLink(VfxSystemRole.Target, owner);
            }
        return byKey;
    }

    /// <summary>
    /// Every effect key a SKIN bin attaches to a bone: the idle effects it always plays, and the ones a
    /// buff condition turns on. Both name their bone in over 93% of records, and both live in the same bin
    /// as the resource map, so reading them costs nothing extra.
    /// </summary>
    public static IReadOnlyDictionary<uint, VfxSystemLink> FromSkin(byte[] skinBin, Func<uint, string?>? resolveBinName = null)
    {
        var byKey = new Dictionary<uint, VfxSystemLink>();
        BinTree tree;
        try { tree = SafeBinTree.Parse(skinBin); }
        catch { return byKey; }

        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != SkinClass) continue;

            // always-on idle effects
            if (o.Properties.GetValueOrDefault(F_idleEffects) is BinTreeContainer idles)
                foreach (var el in idles.Elements.OfType<BinTreeStruct>())
                    Add(byKey, el, owner: "");

            // effects a buff or a spell condition switches on
            if (o.Properties.GetValueOrDefault(F_persistentConditions) is not BinTreeContainer conditions) continue;
            foreach (var condition in conditions.Elements.OfType<BinTreeStruct>())
            {
                string owner = OwnerOf(condition, resolveBinName);
                if (condition.Properties.GetValueOrDefault(F_persistentVfxs) is not BinTreeContainer vfxs) continue;
                foreach (var el in vfxs.Elements.OfType<BinTreeStruct>()) Add(byKey, el, owner);
            }
        }
        return byKey;

        static void Add(Dictionary<uint, VfxSystemLink> into, BinTreeStruct s, string owner)
        {
            if (s.Properties.GetValueOrDefault(F_effectKey) is not BinTreeHash key || key.Value == 0) return;
            string? bone = (s.Properties.GetValueOrDefault(F_boneName) as BinTreeString)?.Value;
            // A spell record is the stronger statement, so it is never overwritten by a bone attachment.
            if (!into.ContainsKey(key.Value))
                into[key.Value] = new VfxSystemLink(VfxSystemRole.Bone, owner, Bone: bone);
        }
    }

    /// <summary>The name of whatever turns a persistent effect on - a spell path, trimmed to its last
    /// segment, or "" when the condition names none.</summary>
    private static string OwnerOf(BinTreeStruct condition, Func<uint, string?>? resolveBinName)
    {
        if (condition.Properties.GetValueOrDefault(F_ownerCondition) is not BinTreeStruct owner) return "";
        if (owner.Properties.GetValueOrDefault(F_spell) is not BinTreeHash spell || spell.Value == 0) return "";
        if (resolveBinName?.Invoke(spell.Value) is not { Length: > 0 } path) return "";
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }

    /// <summary>
    /// Turn effect-key links into SYSTEM links, through a skin's resource map.
    ///
    /// <para>The map is the only route: not one record in the corpus points at a system directly. A key
    /// that maps to the null link is a skin deliberately suppressing that effect, and is dropped rather
    /// than followed to another skin's system.</para>
    /// </summary>
    public static void Resolve(
        IReadOnlyDictionary<uint, VfxSystemLink> byEffectKey,
        IReadOnlyDictionary<uint, uint> resourceMap,
        IDictionary<uint, VfxSystemLink> into)
    {
        foreach (var (key, link) in byEffectKey)
        {
            if (!resourceMap.TryGetValue(key, out uint system) || system == 0) continue;
            // The first skin that maps a key wins. A missile is the same missile on every skin; what
            // differs is which system it points at, and each of those gets its own entry.
            if (!into.ContainsKey(system)) into[system] = link;
        }
    }
}
